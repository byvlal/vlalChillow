using System.Net;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Web;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Админские операции из веб-панели: поиск игрока по id, бан, разбан, список банов.
/// </summary>
/// <remarks>
/// <para>
/// Права проверяет <see cref="Web.WebPanelRouter"/> (тройной затвор: сессия сайта →
/// админ из живого конфига → админ-кука этого запуска). Здесь только сама работа:
/// резолв цели, запись в реестр, кик и объявление — тем же путём, что и <c>/ban</c>
/// из чата, чтобы «видят все: кого, кем, почему, на сколько» работало одинаково.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>Справочник личностей — для веб-панели (ник и статус по id).</summary>
    internal PlayerDirectory Directory => _directory;

    /// <summary>Диапазон сроков бана из конфига — для формы в веб-админке.</summary>
    internal (bool Enabled, int MinMinutes, int MaxDays) BanRange
    {
        get
        {
            var s = _config.Admin.Ban;
            return (_bans is not null && s.Enabled, s.MinMinutes, s.MaxDays);
        }
    }

    /// <summary>Активные баны прямо сейчас — для списка в веб-админке.</summary>
    internal IReadOnlyList<BanRecord> ActiveBanRecords()
    {
        if (_bans is null)
            return Array.Empty<BanRecord>();

        return _bans.Active(DateTime.UtcNow);
    }

    /// <summary>
    /// Найти игрока по серверному id для превью: ник, где он сейчас (катка / лобби /
    /// не в сети), аватар, активный бан. Не нашли никого — <c>null</c>.
    /// </summary>
    internal PlayerLookupResult? LookupPlayerForAdmin(int id)
    {
        if (id < BanCommandLine.MinId || id > BanCommandLine.MaxId)
            return null;

        var utc = DateTime.UtcNow;
        var banned = _bans is not null && _config.Admin.Ban.Enabled
            && new PlayerBanLookup(_directory, _bans, IsBanExemptNick).IsBanned(id, utc);
        var custom = CustomIdOf(id);

        // 1) катка (7777): адрес и ник из ростера комнат.
        var inMatch = _match.SnapshotPublicIds().Where(e => e.Id == id).ToList();
        if (inMatch.Count > 1)
        {
            return new PlayerLookupResult(
                id, inMatch[0].Nick, "match", HasAvatarInLobby(id), banned, null, custom,
                $"id {id} одновременно у {inMatch.Count} акторов катки — ошибка сервера");
        }
        if (inMatch.Count == 1)
            return new PlayerLookupResult(id, inMatch[0].Nick, "match", HasAvatarInLobby(id), banned, null, custom, null);

        // 2) лобби (7778): статус и аватар живут в сессии лобби.
        var inLobby = _session.JoinedPlayers()
            .Where(p => p.PublicId == id)
            .ToList();
        if (inLobby.Count > 1)
        {
            return new PlayerLookupResult(
                id, ServerConfig.NormalizeNick(inLobby[0].Name), "lobby", HasAvatarInLobby(id), banned, null, custom,
                $"id {id} одновременно у {inLobby.Count} игроков лобби — ошибка сервера");
        }
        if (inLobby.Count == 1)
        {
            return new PlayerLookupResult(
                id, ServerConfig.NormalizeNick(inLobby[0].Name), "lobby", HasAvatarInLobby(id), banned, null, custom, null);
        }

        // 3) не в сети: справочник личностей помнит ник и когда видел последний раз.
        var seen = _directory.Resolve(id);
        if (seen is null)
            return null;

        var lastSeen = DateTimeOffset.FromUnixTimeMilliseconds(seen.LastSeenUtcMs).UtcDateTime;
        return new PlayerLookupResult(
            id, seen.Nick, "offline", _avatars.Has(id), banned, lastSeen, custom, null);
    }

    /// <summary>Есть ли у игрока аватар (живые байты из лобби или кэш на диске).</summary>
    private bool HasAvatarInLobby(int id)
    {
        foreach (var player in _session.JoinedPlayers())
        {
            if (player.PublicId != id)
                continue;
            return player.Avatar is { Length: > 0 } || _avatars.Has(id);
        }

        return _avatars.Has(id);
    }

    /// <summary>
    /// Бан из веб-админки: тот же путь, что <c>/ban</c> из чата — проверки, запись,
    /// кик из катки и лобби, объявление всем (кого, кем, почему, на сколько).
    /// </summary>
    /// <returns>Текст ошибки или <c>null</c> при успехе (тогда заполнена запись).</returns>
    internal (string? Error, BanRecord? Record) TryWebBan(
        int id, string durationToken, string reason, string bannedBy)
    {
        if (_bans is null)
            return ("баны не инициализированы", null);
        if (!_config.Admin.Ban.Enabled)
            return ("баны выключены (admin.ban.enabled = false)", null);
        if (id < BanCommandLine.MinId || id > BanCommandLine.MaxId)
            return ("неверный id игрока", null);
        if (string.IsNullOrWhiteSpace(bannedBy))
            return ("неизвестно, кто банит", null);

        var target = ResolveBanTargetById(id);
        if (target.Error is not null)
            return (target.Error, null);

        // Себя не баним: админ-панель знает свой ID из сессии сайта.
        if (id == IdOf(bannedBy))
            return ("себя банить нельзя", null);

        var messages = new List<string>();
        var record = TryBanTarget(
            messages.Add, target.AccountId, target.Nick, target.Address, durationToken, reason,
            player: null, target.KnownIps, bannedBy: bannedBy);

        if (record is null)
            return (messages.Count > 0 ? messages[0] : "бан не удался", null);

        return (null, record);
    }

    /// <summary>Разбан из веб-админки по серверному id: снимает все записи этого человека.</summary>
    /// <returns>Текст ошибки или <c>null</c> при успехе (тогда — сколько записей снято).</returns>
    internal (string? Error, int Removed) TryWebUnban(int id)
    {
        if (_bans is null)
            return ("баны не инициализированы", 0);

        var messages = new List<string>();
        var removed = RemoveBansById(messages.Add, id);
        if (removed == 0)
            return (messages.Count > 0 ? messages[0] : $"под id {id} активных банов нет", 0);

        return (null, removed);
    }

    /// <summary>Текущий конфиг глазами панели (после reload — уже новый).</summary>
    internal ServerConfig CurrentConfig => _config;

    /// <summary>Сколько личностей сервер вообще помнит.</summary>
    internal int KnownPlayersCount => _directory.Count;

    /// <summary>Кто сейчас в лобби и катке — для дашборда.</summary>
    internal (int Lobby, int Match) Presence => _session.CountPresence();

    /// <summary>Выбранные карты лобби — для дашборда.</summary>
    internal IReadOnlyList<string> SelectedLevels => _session.SelectedLevels;

    /// <summary>
    /// Поиск игрока для консоли: id, ник (в том числе прежний), IP и ключ устройства.
    /// Ищет и в живых каналах, и в справочнике, и в реестре аккаунтов; пустой запрос
    /// отдаёт тех, кто сейчас на сервере.
    /// </summary>
    internal IReadOnlyList<AdminPlayerRow> SearchPlayersForAdmin(string query, int limit)
    {
        var text = (query ?? "").Trim();
        var max = Math.Clamp(limit, 1, 100);
        var now = DateTime.UtcNow;
        var ids = new List<int>();
        var seen = new HashSet<int>();

        void Add(int id)
        {
            if (id is >= AccountStore.FirstId and <= AccountStore.MaxId && seen.Add(id))
                ids.Add(id);
        }

        var match = _match.SnapshotPublicIds()
            .Where(e => e.Id > 0)
            .GroupBy(e => e.Id)
            .ToDictionary(g => g.Key, g => g.ToList());
        var lobby = _session.JoinedPlayers()
            .Where(p => p.PublicId > 0)
            .GroupBy(p => p.PublicId)
            .ToDictionary(g => g.Key, g => g.ToList());

        if (text.Length == 0)
        {
            // Пустой запрос — «кто сейчас». Сначала катка, потом лобби: так полезнее.
            foreach (var id in match.Keys.OrderBy(x => x))
                Add(id);
            foreach (var id in lobby.Keys.OrderBy(x => x))
                Add(id);
        }
        else
        {
            if (int.TryParse(text, out var numeric))
                Add(numeric);

            if (_accounts is not null)
            {
                foreach (var account in _accounts.FindAccountsByNick(text))
                    Add(account.Id);
                foreach (var account in _accounts.FindAccountsByDevice(text))
                    Add(account.Id);

                // Частичное совпадение по нику и прежним никам. Реестр ограничен
                // несколькими тысячами записей, поэтому проход дешевле индексов.
                foreach (var account in _accounts.Export())
                {
                    if (account.AllNicks.Any(n => n.Contains(text, StringComparison.OrdinalIgnoreCase)))
                        Add(account.Id);
                }
            }

            if (_directory.FindByNick(text) is { } exact)
                Add(exact.Id);

            foreach (var person in _directory.Export())
            {
                var nickHit = person.Nick.Contains(text, StringComparison.OrdinalIgnoreCase);
                var ipHit = person.Ips.Any(ip => ip.Contains(text, StringComparison.OrdinalIgnoreCase));
                var deviceHit = person.Devices.Any(d =>
                    d.Contains(text, StringComparison.OrdinalIgnoreCase));
                if (nickHit || ipHit || deviceHit)
                    Add(person.Id);
            }
        }

        var bans = ActiveBanRecords();
        var rows = new List<AdminPlayerRow>(ids.Count);
        foreach (var id in ids)
        {
            var account = _accounts?.FindById(id);
            var person = _directory.Resolve(id);
            match.TryGetValue(id, out var matchRows);
            lobby.TryGetValue(id, out var lobbyRows);

            var status = matchRows is { Count: > 0 }
                ? "match"
                : lobbyRows is { Count: > 0 }
                    ? "lobby"
                    : "offline";
            var nick = matchRows?.FirstOrDefault().Nick
                       ?? (lobbyRows is { Count: > 0 }
                           ? ServerConfig.NormalizeNick(lobbyRows[0].Name)
                           : "");
            if (nick.Length == 0)
                nick = account?.Nick ?? person?.Nick ?? "";

            var ips = new List<string>();
            foreach (var ip in person?.Ips ?? new List<string>())
            {
                if (!ips.Contains(ip, StringComparer.OrdinalIgnoreCase))
                    ips.Add(ip);
            }
            foreach (var entry in matchRows ?? new List<MatchPublicEntry>())
            {
                var ip = entry.Address?.ToString() ?? "";
                if (ip.Length > 0 && !ips.Contains(ip, StringComparer.OrdinalIgnoreCase))
                    ips.Add(ip);
            }
            foreach (var player in lobbyRows ?? new List<Lobby.ConnectedPlayer>())
            {
                var ip = player.Peer.Address?.ToString() ?? "";
                if (ip.Length > 0 && !ips.Contains(ip, StringComparer.OrdinalIgnoreCase))
                    ips.Add(ip);
            }

            var devices = new List<string>();
            foreach (var device in account?.Devices ?? new List<string>())
                devices.Add(device);
            foreach (var device in _directory.DevicesForAccount(id))
            {
                if (!devices.Contains(device, StringComparer.OrdinalIgnoreCase))
                    devices.Add(device);
            }

            var lastSeenMs = Math.Max(account?.LastSeenUtcMs ?? 0, person?.LastSeenUtcMs ?? 0);
            var createdMs = account?.CreatedUtcMs ?? 0;
            var note = "";
            if (matchRows is { Count: > 1 } || lobbyRows is { Count: > 1 })
                note = "id одновременно у нескольких подключений — проверь";

            rows.Add(new AdminPlayerRow(
                id,
                nick,
                status,
                bans.Any(b => b.MatchIds.Contains(id)),
                CustomIdOf(id),
                lastSeenMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(lastSeenMs).UtcDateTime
                    : null,
                ips,
                devices,
                account?.Aliases ?? new List<string>(),
                createdMs > 0
                    ? DateTimeOffset.FromUnixTimeMilliseconds(createdMs).UtcDateTime
                    : null,
                IsAdminLive(id),
                _config.Admin.MegaIds.Contains(id),
                note));
        }

        return rows
            .OrderBy(r => r.Status switch { "match" => 0, "lobby" => 1, _ => 2 })
            .ThenByDescending(r => r.LastSeenUtc ?? DateTime.MinValue)
            .ThenBy(r => r.Nick, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToList();
    }

    /// <summary>Кик из лобби и катки по серверному id. Бан не ставится.</summary>
    /// <returns>Ошибка или <c>null</c>, если хотя бы одно соединение закрыто.</returns>
    internal string? KickPlayerForAdmin(int id, string reason)
    {
        var text = string.IsNullOrWhiteSpace(reason) ? "без причины" : reason.Trim();
        var kicked = _match.DisconnectAccount(id);

        foreach (var player in _session.JoinedPlayers())
        {
            if (player.PublicId != id)
                continue;
            try
            {
                SendServerChatTo(player.Peer, $"кик: {text}");
                DisconnectSoon(player.Peer);
                kicked++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[admin] kick lobby {player.Peer.Address}: {ex.Message}");
            }
        }

        if (kicked == 0)
            return $"игрок #{id} сейчас не на сервере";
        Console.WriteLine($"[admin] кик #{id}: {text} (сокетов {kicked})");
        return null;
    }

}
