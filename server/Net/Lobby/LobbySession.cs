using LiteNetLib;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match;

namespace StandChillow.LanServer.Net.Lobby;

public enum PlayerLobbyStatus
{
    Lobby,
    InMatch,
}

public sealed class ConnectedPlayer
{
    /// <summary>
    /// Current lobby socket. Not <c>init</c>: a phone that lost the network comes back on a new
    /// socket and takes over its existing membership instead of adding a second one.
    /// </summary>
    public required NetPeer Peer { get; set; }
    /// <summary>Stable lobby member id (Server=0; humans start at 1). Same on the wire for all peers.</summary>
    public required int ServerMemberId { get; init; }
    public required string Name { get; set; }
    public byte[]? Avatar { get; set; }
    public PlayerLobbyStatus Status { get; set; } = PlayerLobbyStatus.Lobby;
    public bool Joined { get; set; }
    /// <summary>
    /// Серверный id игрока. Считается хостом из ника сессии при входе; клиент его не
    /// присылает и подделать не может. Основа админских прав и ответа /whoami.
    /// </summary>
    public int PublicId { get; set; }
}

/// <summary>
/// Host-side LAN lobby session. В проводе лобби виден только Server + сам клиент:
/// клиент не переваривает больше ~10 подключённых игроков, поэтому состав держится
/// в состоянии сессии (дашборд, сайт, /ids, команды), но никуда не передаётся на 7778.
/// Комнаты матча (7777) этим не затрагиваются — там видны все актёры.
/// </summary>
public sealed class LobbySession
{
    public const string ServerPlayerName = "Server";
    public const int ServerMemberId = 0;
    /// <summary>
    /// Id «я» в приватной росписи клиента: раз чужих строк нет, глобальный member id
    /// не нужен и мог бы выйти за предел клиента. Server=0, клиент=1.
    /// </summary>
    public const int WireSelfMemberId = 1;
    /// <summary>Dedicated soft cap for snapshot maxMembers (live phone uses 4).</summary>
    public const byte DedicatedMaxMembers = 16;
    public const string WelcomeMessage = "это тест ради нехуй делать";

    /// <summary>
    /// Переопределения из <c>config.json</c> (блок server). Задаёт только
    /// выделенный сервер на старте; в пробе (<c>--client</c>) остаются дефолты.
    /// </summary>
    public static byte MaxMembersOverride { get; set; }

    /// <summary>null — использовать <see cref="WelcomeMessage"/>.</summary>
    public static string? WelcomeMessageOverride { get; set; }

    private readonly object _gate = new();
    private readonly Dictionary<NetPeer, ConnectedPlayer> _byPeer = new();
    private readonly List<ConnectedPlayer> _order = new();
    private int _nextMemberId = 1;
    private string _lobbyName;
    private readonly string _lobbyId;
    private string _gameModeId = LobbyPropKeys.DefaultGameModeId;
    private IReadOnlyList<string> _selectedLevels = new[] { LobbyPropKeys.DefaultSelectedLevel };
    private bool _joinable = true;
    private bool _gameInProgress;
    private bool _searching;

    public LobbySession(string lobbyName, string? lobbyId = null)
    {
        _lobbyName = lobbyName;
        // Real phone: discovery HostName == LobbyId property (32-char uppercase MD5 hex).
        _lobbyId = string.IsNullOrWhiteSpace(lobbyId)
            ? LobbyAuth.Md5Hex(Guid.NewGuid().ToString("N"))
            : lobbyId.Trim().ToUpperInvariant();
    }

    public string LobbyName
    {
        get { lock (_gate) return _lobbyName; }
        set { lock (_gate) _lobbyName = value; }
    }

    /// <summary>LobbyId custom prop + discovery HostName (live phone uses same hash).</summary>
    public string LobbyId
    {
        get { lock (_gate) return _lobbyId; }
    }

    public string GameModeId
    {
        get { lock (_gate) return _gameModeId; }
        set { lock (_gate) _gameModeId = value; }
    }

    public IReadOnlyList<string> SelectedLevels
    {
        get { lock (_gate) return _selectedLevels; }
        set
        {
            lock (_gate)
                _selectedLevels = value is { Count: > 0 }
                    ? value.ToList()
                    : new List<string> { LobbyPropKeys.DefaultSelectedLevel };
        }
    }

    public bool Searching
    {
        get { lock (_gate) return _searching; }
        set { lock (_gate) _searching = value; }
    }

    public bool Joinable
    {
        get { lock (_gate) return _joinable; }
        set { lock (_gate) _joinable = value; }
    }

    public bool GameInProgress
    {
        get { lock (_gate) return _gameInProgress; }
        set
        {
            lock (_gate)
            {
                _gameInProgress = value;
                // Do not force every peer InMatch — presence is per-player (JoinRoom Dedik).
                if (!value)
                {
                    foreach (var p in _order)
                        if (p.Joined)
                            p.Status = PlayerLobbyStatus.Lobby;
                }
            }
        }
    }

    public int JoinedCount
    {
        get { lock (_gate) return _order.Count(p => p.Joined); }
    }

    public IReadOnlyList<(string Key, LobbyVariant Value)> BuildLobbyProps()
    {
        lock (_gate)
        {
            return new List<(string, LobbyVariant)>
            {
                (LobbyPropKeys.LobbyId, LobbyVariant.FromString(_lobbyId)),
                // The client resolves the mode by its wire id (duel → «RankedDuel»),
                // so the catalog id never goes out as-is.
                (LobbyPropKeys.GameModeId, LobbyVariant.FromString(MatchModeIds.ToWire(_gameModeId))),
                (LobbyPropKeys.SelectedLevels, LobbyVariant.FromStrings(_selectedLevels)),
            };
        }
    }

    /// <summary>op7 CustomProperties bag — same shape as live Ranked2v2 + Sandstone mode change.</summary>
    public IReadOnlyList<(string Key, LobbyVariant Value)> BuildModeLevelProps()
    {
        lock (_gate)
        {
            return new List<(string, LobbyVariant)>
            {
                (LobbyPropKeys.GameModeId, LobbyVariant.FromString(MatchModeIds.ToWire(_gameModeId))),
                (LobbyPropKeys.SelectedLevels, LobbyVariant.FromStrings(_selectedLevels)),
            };
        }
    }

    public IReadOnlyList<(string Name, PlayerLobbyStatus Status)> SnapshotRoster()
    {
        lock (_gate)
        {
            var list = new List<(string, PlayerLobbyStatus)>(_order.Count + 1)
            {
                (ServerPlayerName, PlayerLobbyStatus.Lobby)
            };
            foreach (var p in _order.Where(p => p.Joined))
                list.Add((p.Name, p.Status));
            return list;
        }
    }

    public void OnPeerConnected(NetPeer peer)
    {
        lock (_gate)
        {
            if (_byPeer.ContainsKey(peer)) return;
            var p = new ConnectedPlayer
            {
                Peer = peer,
                ServerMemberId = _nextMemberId++,
                Name = "?",
            };
            _byPeer[peer] = p;
            _order.Add(p);
        }
    }

    public ConnectedPlayer? OnPeerDisconnected(NetPeer peer, out string? leftName)
    {
        lock (_gate)
        {
            leftName = null;
            if (!_byPeer.Remove(peer, out var p))
                return null;
            _order.Remove(p);
            if (p.Joined)
                leftName = p.Name;
            return p;
        }
    }

    public JoinResult HandleJoinRequest(
        NetPeer peer,
        JoinLobbyRequest req,
        string? matchHostingIp = null,
        ushort matchHostingPort = 0)
    {
        lock (_gate)
        {
            if (!_byPeer.TryGetValue(peer, out var player))
                return JoinResult.Fail(JoinFailReason.NotJoinable, "unknown peer");

            if (!_joinable)
                return JoinResult.Fail(JoinFailReason.NotJoinable, "not joinable");

            // Dedicated server intentionally does NOT enforce MaxMembersReached (live phone = 4).

            if (!LobbyAuth.HashEquals(req.VersionHash, LobbyAuth.VersionHash))
                return JoinResult.Fail(JoinFailReason.InvalidGameVersion, "version hash mismatch");

            if (!LobbyAuth.HashEquals(req.AuthHash, LobbyAuth.AuthHash))
                return JoinResult.Fail(JoinFailReason.InvalidAuthKey, "auth hash mismatch");

            if (string.IsNullOrWhiteSpace(req.Profile.Name))
                return JoinResult.Fail(JoinFailReason.ProfileValidationFailed, "empty name");

            // Ник сервера зарезервирован: копий «Server» в лобби быть не должно.
            if (ServerIdentity.IsServerNick(req.Profile.Name))
                return JoinResult.Fail(
                    JoinFailReason.ProfileValidationFailed, "name reserved for the server");

            if (req.Profile.Avatar is { Length: > 2_000_000 })
                return JoinResult.Fail(JoinFailReason.ProfileValidationFailed, "avatar too large");

            var nick = req.Profile.Name.Trim();
            var takenOver = FindRejoinMembership(peer, player, nick);
            if (takenOver is not null)
            {
                // Issue #39: same player, new lobby socket (the phone re-joins the lobby after a
                // network drop while the old socket is still inside its timeout). Before this the
                // roster kept both entries — the client showed «брат-близнец» next to itself, and
                // each reconnect added another one until the server restarted.
                var previousPeer = takenOver.Peer;
                takenOver.Peer = peer;
                takenOver.Name = nick;
                takenOver.Avatar = req.Profile.Avatar;
                takenOver.Status = PlayerLobbyStatus.Lobby;
                takenOver.Joined = true;
                _byPeer.Remove(previousPeer);
                _order.Remove(player);
                _byPeer[peer] = takenOver;
                player = takenOver;
                Console.WriteLine(
                    $"[lobby] member id={takenOver.ServerMemberId} '{nick}' перешёл к новому сокету " +
                    $"{peer.Address}:{peer.Port} (старый {previousPeer.Address}:{previousPeer.Port} " +
                    "отсоединён) — второй записи в лобби нет");
                try { previousPeer.Disconnect(); }
                catch { /* уже мёртв */ }
            }
            else
            {
                player.Name = nick;
                player.Avatar = req.Profile.Avatar;
                player.Status = PlayerLobbyStatus.Lobby;
                player.Joined = true;
            }

            // Приватная роспись: только Server(0) + сам клиент (selfMemberId=1).
            // Полный состав остаётся в состоянии сессии — его видят дашборд, сайт,
            // /ids и команды, но на 7778 чужие строки не уходят: клиент ломается,
            // когда в лобби больше ~10 подключённых игроков. Та же форма уже
            // проверена на теневом входе забаненного (Server + я).
            // PublicId не выставляем: аккаунт создаёт GameNetHost после успешного
            // JoinResponse — у сессии нет доступа к реестру аккаунтов.
            var visible = BuildHiddenLobbyRoster();

            string? hostIp = null;
            ushort hostPort = 0;
            if (_gameInProgress
                && !string.IsNullOrWhiteSpace(matchHostingIp)
                && matchHostingPort != 0)
            {
                hostIp = matchHostingIp;
                hostPort = matchHostingPort;
            }

            var response = LobbyCodec.BuildJoinSuccess(
                selfMemberId: WireSelfMemberId,
                lobbyName: _lobbyName,
                maxMembers: MaxMembersOverride > 0 ? MaxMembersOverride : DedicatedMaxMembers,
                members: visible,
                lobbyProps: BuildLobbyProps(),
                hostingIp: hostIp,
                hostingPort: hostPort);

            return JoinResult.Ok(player, response);
        }
    }

    /// <summary>
    /// Вход забаненного: ответ успешный, но игрок остаётся «тенью» — <c>Joined = false</c>,
    /// поэтому его нет ни в росписи, ни в рассылках, ни в чате.
    /// </summary>
    /// <remarks>
    /// Клиент рисует себя сам по <c>selfMemberId</c>, поэтому в списке остаётся только Server:
    /// «Server первым, я вторым», без единого имени других игроков. <c>hasHosting</c> не
    /// выставляется — для забаненного матч как будто не запущен.
    /// </remarks>
    public JoinResult HandleShadowJoinRequest(NetPeer peer, JoinLobbyRequest req)
    {
        lock (_gate)
        {
            if (!_byPeer.TryGetValue(peer, out var player))
                return JoinResult.Fail(JoinFailReason.NotJoinable, "unknown peer");

            if (!LobbyAuth.HashEquals(req.VersionHash, LobbyAuth.VersionHash))
                return JoinResult.Fail(JoinFailReason.InvalidGameVersion, "version hash mismatch");

            if (!LobbyAuth.HashEquals(req.AuthHash, LobbyAuth.AuthHash))
                return JoinResult.Fail(JoinFailReason.InvalidAuthKey, "auth hash mismatch");

            if (string.IsNullOrWhiteSpace(req.Profile.Name))
                return JoinResult.Fail(JoinFailReason.ProfileValidationFailed, "empty name");

            if (ServerIdentity.IsServerNick(req.Profile.Name))
                return JoinResult.Fail(
                    JoinFailReason.ProfileValidationFailed, "name reserved for the server");

            player.Name = req.Profile.Name.Trim();
            player.Avatar = req.Profile.Avatar;
            player.Status = PlayerLobbyStatus.Lobby;
            player.Joined = false;
            // Теневой вход ничего не значит: аккаунта тут не выдаём и справочник не пишем.

            var response = LobbyCodec.BuildJoinSuccess(
                selfMemberId: WireSelfMemberId,
                lobbyName: _lobbyName,
                maxMembers: MaxMembersOverride > 0 ? MaxMembersOverride : DedicatedMaxMembers,
                members: BuildHiddenLobbyRoster(),
                lobbyProps: BuildLobbyProps(),
                hostingIp: null,
                hostingPort: 0);

            return JoinResult.Ok(player, response);
        }
    }

    /// <summary>
    /// Issue #39: другая запись того же игрока в лобби — тот же ник с того же адреса (телефон
    /// переподключился, старый сокет ещё живёт до таймаута).
    /// </summary>
    /// <remarks>
    /// Сравнение по нику+IP, потому что wire-uid у лобби нет (в <c>JoinLobbyRequest</c> только
    /// профиль с ником). Другой адрес при том же нике — это, возможно, другой человек, его не
    /// трогаем.
    /// </remarks>
    private ConnectedPlayer? FindRejoinMembership(NetPeer peer, ConnectedPlayer self, string nick)
    {
        lock (_gate)
        {
            foreach (var p in _order)
            {
                if (ReferenceEquals(p, self) || !p.Joined)
                    continue;
                if (!string.Equals(p.Name, nick, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (p.Peer.Address is not { } oldAddress
                    || peer.Address is not { } newAddress
                    || !oldAddress.Equals(newAddress))
                    continue;
                return p;
            }
            return null;
        }
    }

    public ConnectedPlayer? TryGet(NetPeer peer)
    {
        lock (_gate) return _byPeer.TryGetValue(peer, out var p) ? p : null;
    }

    /// <summary>
    /// Все joined-игроки с этим IP. Нужен, чтобы не угадывать автора команды: за NAT/Radmin
    /// несколько телефонов сидят на одном адресе, и «первый попавшийся» — не ответ.
    /// </summary>
    public List<ConnectedPlayer> JoinedPlayersByIp(System.Net.IPAddress ip)
    {
        var hits = new List<ConnectedPlayer>();
        lock (_gate)
        {
            foreach (var p in _order)
            {
                if (!p.Joined || p.Peer.Address is null) continue;
                if (p.Peer.Address.Equals(ip))
                    hits.Add(p);
            }
        }

        return hits;
    }

    public IReadOnlyList<ConnectedPlayer> JoinedPlayers()
    {
        lock (_gate) return _order.Where(p => p.Joined).ToList();
    }

    /// <summary>Wire member for OpLobbyNewMemberEvent / JoinResponse rows.</summary>
    public static LobbyMember ToLobbyMember(ConnectedPlayer p) => new()
    {
        Id = p.ServerMemberId,
        Name = p.Name,
        Avatar = p.Avatar,
    };

    /// <summary>
    /// Роспись лобби на проводе: одна строка Server. Чужие игроки не передаются —
    /// клиент не переваривает больше ~10 подключённых игроков в лобби. Чистая
    /// функция, без сокетов: покрыта тестом и переиспользуется теневым входом.
    /// </summary>
    public static IReadOnlyList<LobbyMember> BuildHiddenLobbyRoster() =>
        new List<LobbyMember>
        {
            new() { Id = ServerMemberId, Name = ServerPlayerName, Avatar = null },
        };

    /// <summary>
    /// Чужой чат в лобби подписывается автором Server с префиксом ника: раз в росписи
    /// нет чужих строк, чужие member id на провод вообще не должны уходить.
    /// </summary>
    public static string FormatRelayedChat(string name, string text) => $"{name}: {text}";

    public bool TrySetPlayerStatus(ConnectedPlayer player, PlayerLobbyStatus status)
    {
        lock (_gate)
        {
            if (!player.Joined) return false;
            if (player.Status == status) return false;
            player.Status = status;
            return true;
        }
    }

    public void ResetAllToLobby()
    {
        lock (_gate)
        {
            foreach (var p in _order)
                if (p.Joined)
                    p.Status = PlayerLobbyStatus.Lobby;
        }
    }

    public static string FormatStatus(PlayerLobbyStatus s) => s switch
    {
        PlayerLobbyStatus.InMatch => "В матче",
        _ => "Лобби",
    };

    /// <summary>
    /// Сколько людей в лобби и сколько ушло в катку. Цифры, а не список ников.
    /// </summary>
    /// <remarks>
    /// Раньше на каждое изменение онлайна в чат уходила вся роспись
    /// (<c>«Текущие игроки в лобби:»</c> + строка на человека): вход, выход, вход в катку и
    /// выход из катки — четыре рассылки полного состава на одно событие, и чат тонул в
    /// списках (#51). Теперь состав никуда не рассылается: сводку цифрами получает только
    /// тот, кто вошёл, и только один раз — в той же строке, где его ник и ID.
    /// </remarks>
    public (int Lobby, int InMatch) CountPresence()
    {
        lock (_gate)
        {
            var lobby = 0;
            var match = 0;
            foreach (var p in _order)
            {
                if (!p.Joined) continue;
                if (p.Status == PlayerLobbyStatus.InMatch) match++;
                else lobby++;
            }
            return (lobby, match);
        }
    }
}

public readonly struct JoinResult
{
    public bool Success { get; init; }
    public JoinFailReason? FailReason { get; init; }
    public string? Detail { get; init; }
    public ConnectedPlayer? Player { get; init; }
    public byte[]? ResponseBytes { get; init; }

    public static JoinResult Ok(ConnectedPlayer player, byte[] response) => new()
    {
        Success = true,
        Player = player,
        ResponseBytes = response,
    };

    public static JoinResult Fail(JoinFailReason reason, string detail) => new()
    {
        Success = false,
        FailReason = reason,
        Detail = detail,
        ResponseBytes = LobbyCodec.BuildJoinFail(reason),
    };
}
