using System.Collections.Concurrent;
using System.Net;
using LiteNetLib;
using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Dashboard;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Авторизация админского id личным паролем и выдача прав на команды.
/// </summary>
/// <remarks>
/// <para>
/// Права даёт серверный id из <c>admin.ids</c>, посчитанный сервером из ника сессии.
/// Пока адрес не ввёл личный пароль из <c>auth/&lt;id&gt;/password.txt</c>, такой игрок
/// не попадает в лобби (видит только Server, как забаненный), не может зайти в матч и
/// не может выполнить ни одной команды.
/// </para>
/// <para>
/// Пара «id + IP» запоминается до перезапуска сервера. Смена пароля поднимает
/// <c>authVersion</c>, и все старые авторизации перестают действовать.
/// </para>
/// </remarks>
public sealed partial class GameNetHost
{
    /// <summary>Сколько неверных паролей терпим, прежде чем отключить сокет.</summary>
    private const int AdminAuthMaxAttempts = 5;

    private readonly ConcurrentDictionary<string, int> _gameAdminAuth =
        new(StringComparer.Ordinal);
    private readonly object _pendingAdminGate = new();
    private readonly Dictionary<NetPeer, PendingAdmin> _pendingAdmins = new();

    private sealed class PendingAdmin
    {
        public int Id;
        public string Nick = string.Empty;
        public int Attempts;
    }

    /// <summary>Хранилище админов (пароли, права, megaIps). Может быть <c>null</c> в тестах.</summary>
    internal AdminStore? Admins => _adminStore;

    /// <summary>
    /// Id в <c>admin.ids</c> И этот адрес уже ввёл пароль — только тогда есть права.
    /// </summary>
    internal bool IsAdminAuthorized(int id, IPAddress? ip)
    {
        if (!_config.IsAdmin(id) || _adminStore is null || ip is null)
            return false;

        var key = AuthKey(id, ip);
        if (!_gameAdminAuth.TryGetValue(key, out var version))
            return false;

        var current = _adminStore.AuthVersionOf(id);
        if (current != version)
        {
            _gameAdminAuth.TryRemove(key, out _);
            return false;
        }

        return true;
    }

    /// <summary>
    /// Админский id, но этот адрес пароль ещё не вводил: в игру пускать нельзя.
    /// </summary>
    internal bool IsUnauthorizedAdmin(int id, IPAddress? ip) =>
        _config.IsAdmin(id) && !IsAdminAuthorized(id, ip);

    /// <summary>
    /// Живая проверка админства для веб-панели: читает текущий <c>_config</c>, поэтому
    /// перечитывание конфига (admin.ids) сразу меняет ответ.
    /// </summary>
    internal bool IsAdminLive(int id) => _config.IsAdmin(id);

    /// <summary>Есть ли право на действие. Мега-админ получает любое.</summary>
    internal bool HasAdminPermission(int id, IPAddress? ip, string permission) =>
        IsAdminAuthorized(id, ip)
        && _adminStore?.HasPermission(id, ip, permission) == true;

    /// <summary>Путь к личному паролю — для подсказки в чате и консоли.</summary>
    internal string PasswordPathOf(int id) =>
        _adminStore?.PasswordPathOf(id) ?? "auth/<id>/password.txt";

    /// <summary>
    /// Id написавшего из ника сессии (никогда из поля пакета клиента): account ID, если
    /// человек уже входил, иначе старый хеш ника — пока аккаунт не создан, <c>admin.ids</c>
    /// ещё хранит хеш.
    /// </summary>
    internal int IdOf(string? nick) =>
        _accounts?.FindByNick(nick)?.Id ?? LegacyNickHash.ForNick(nick);

    /// <summary>Что происходит с админами на старте: пароли уже лежат в auth/.</summary>
    private void LogAdminAuthStartup()
    {
        var directory = _adminStore?.Directory ?? "auth/";
        Console.WriteLine(
            $"[auth] личные пароли админов: {directory}/<id>/password.txt " +
            "(вида <id>-<hex64>; вводится в чат лобби)");

        if (_config.Admin.Ids.Count == 0)
        {
            Console.WriteLine(
                "[auth] админских id нет — пока в config.json admin.ids пусто, " +
                "админ-команд ни у кого нет");
            return;
        }

        Console.WriteLine(
            $"[auth] админские id: {_config.DescribeAdmins()} — " +
            "без пароля они видят только Server, как забаненные");
        foreach (var id in _config.Admin.Ids)
        {
            var record = _adminStore?.Find(id);
            var mega = record is { MegaIps.Count: > 0 }
                ? $" · MEGA IP: {string.Join(", ", record.MegaIps)}"
                : "";
            Console.WriteLine(
                $"[auth]   #{id}: {PasswordPathOf(id)} · права: " +
                $"{AdminPermissions.Describe(record?.Permissions)}{mega}");
        }
    }

    /// <summary>
    /// Вход под админским id без пароля: теневой лобби (роспись из одного Server) и
    /// просьба ввести личный пароль. Никаких команд, никакого матча.
    /// </summary>
    private void HandleAdminAuthJoin(NetPeer peer, JoinLobbyRequest req)
    {
        var id = IdOf(req.Profile.Name);
        var result = _session.HandleShadowJoinRequest(peer, req);
        if (result.ResponseBytes is not null)
            Send(peer, result.ResponseBytes);

        if (!result.Success)
        {
            Console.WriteLine(
                $"[auth] теневой вход отклонён: {result.FailReason} ({result.Detail})");
            return;
        }

        var nick = ServerConfig.NormalizeNick(req.Profile.Name);
        lock (_pendingAdminGate)
        {
            _pendingAdmins[peer] = new PendingAdmin { Id = id, Nick = nick };
        }

        Console.WriteLine(
            $"[auth] '{nick}' id #{id} {peer.Address} — админский id без пароля: " +
            "лобби не выдан, команды закрыты, ждём пароль в чате");

        DashboardHub.PostChat(
            LobbySession.ServerPlayerName,
            $"[auth] {nick} (id #{id}, {peer.Address}) — админ без пароля, ждём авторизацию",
            fromServer: true);

        SendServerChatTo(peer, $"id #{id} в admin.ids — нужна авторизация");
        if (_adminStore is null)
        {
            SendServerChatTo(peer, "хранилище паролей не подключено — админ-вход закрыт");
            return;
        }

        SendServerChatTo(
            peer,
            $"твой личный пароль лежит в {PasswordPathOf(id)} — строка вида <id>-<hex64>");
        SendServerChatTo(peer, "напиши пароль сюда, в чат целиком (он начинается с твоего id)");
    }

    private bool IsPendingAdmin(NetPeer peer)
    {
        lock (_pendingAdminGate)
            return _pendingAdmins.ContainsKey(peer);
    }

    /// <summary>
    /// Сообщение от неавторизованного админа: принимаем только пароль. Возвращает
    /// <c>true</c>, если сообщение обработано здесь (дальше его никто не видит).
    /// </summary>
    private bool HandlePendingAdminChat(NetPeer peer, string message)
    {
        PendingAdmin? pending;
        lock (_pendingAdminGate)
        {
            if (!_pendingAdmins.TryGetValue(peer, out pending))
                return false;
        }

        // Пароль можно прислать и как «/auth <пароль>» — так рука сама тянется после /help.
        message = StripAuthPrefix(message);

        var record = _adminStore?.Verify(pending.Id, message, DateTime.UtcNow);
        if (record is not null)
        {
            AuthorizeAdmin(pending.Id, peer.Address, record.AuthVersion);
            _adminStore?.SetNick(pending.Id, pending.Nick, DateTime.UtcNow);
            lock (_pendingAdminGate)
                _pendingAdmins.Remove(peer);

            Console.WriteLine(
                $"[auth] пароль принят: '{pending.Nick}' id #{pending.Id} {peer.Address} — " +
                $"адрес авторизован до перезапуска (версия {record.AuthVersion})");
            DashboardHub.PostChat(
                LobbySession.ServerPlayerName,
                $"[auth] {pending.Nick} ({peer.Address}) авторизован",
                fromServer: true);

            SendServerChatTo(peer, "пароль принят — ты авторизован до перезапуска сервера");
            SendServerChatTo(peer, "зайди в лобби заново");
            Schedule(400, () =>
            {
                try { peer.Disconnect(); }
                catch { /* уже мёртв */ }
            });
            return true;
        }

        pending.Attempts++;
        Console.WriteLine(
            $"[auth] неверный пароль от '{pending.Nick}' (#{pending.Id}) {peer.Address} " +
            $"(попытка {pending.Attempts}/{AdminAuthMaxAttempts})");

        if (pending.Attempts >= AdminAuthMaxAttempts)
        {
            lock (_pendingAdminGate)
                _pendingAdmins.Remove(peer);
            SendServerChatTo(peer, "слишком много неверных паролей — соединение закрыто");
            Schedule(300, () =>
            {
                try { peer.Disconnect(); }
                catch { /* уже мёртв */ }
            });
            return true;
        }

        SendServerChatTo(peer, $"неверный пароль (попытка {pending.Attempts} из " +
                               $"{AdminAuthMaxAttempts}), попробуй ещё");
        return true;
    }

    /// <summary>
    /// «/auth пароль» и «пароль» — одно и то же: снимаем слэш и слово-префикс, чтобы
    /// игрок не тратил попытки на оформление.
    /// </summary>
    private static string StripAuthPrefix(string message)
    {
        var text = message.Trim();
        if (!text.StartsWith('/'))
            return text;

        text = text[1..].TrimStart();
        var space = text.IndexOf(' ');
        if (space <= 0)
            return text;

        var head = text[..space];
        return head.Equals("auth", StringComparison.OrdinalIgnoreCase)
               || head.Equals("код", StringComparison.OrdinalIgnoreCase)
            ? text[(space + 1)..].Trim()
            : text;
    }

    private void ForgetPendingAdmin(NetPeer peer)
    {
        lock (_pendingAdminGate)
            _pendingAdmins.Remove(peer);
    }

    private int PendingAdminCount
    {
        get { lock (_pendingAdminGate) return _pendingAdmins.Count; }
    }

    private void AuthorizeAdmin(int id, IPAddress? ip, int authVersion)
    {
        if (ip is null)
            return;
        _gameAdminAuth[AuthKey(id, ip)] = authVersion;
    }

    private static string AuthKey(int id, IPAddress ip) =>
        $"{id}|{AdminStore.NormalizeIp(ip)}";

    /// <summary>
    /// <c>/whoami</c>: что сервер реально знает о написавшем — ник после нормализации, id,
    /// адрес и статус админа.
    /// </summary>
    private void HandleWhoAmICommand(Action<string> reply, ConnectedPlayer? player)
    {
        if (player is null)
        {
            reply("whoami — только из чата игрока");
            return;
        }

        var nick = ServerConfig.NormalizeNick(player.Name);
        var id = player.PublicId > 0 ? player.PublicId : IdOf(nick);
        var isAdmin = _config.IsAdmin(id);
        var authorized = IsAdminAuthorized(id, player.Peer.Address);

        reply($"ник: '{nick}' · id: #{id} · IP: {player.Peer.Address}");
        reply(isAdmin
            ? (authorized ? "админ: да (id в admin.ids), пароль принят"
                          : "админ: id в admin.ids, пароль не введён")
            : "админ: нет");
        if (isAdmin && authorized)
        {
            var permissions = _adminStore?.PermissionsFor(id, player.Peer.Address)
                ?? Array.Empty<string>();
            var mega = _adminStore?.IsMega(id, player.Peer.Address) == true;
            reply($"права: {string.Join(", ", permissions)}" + (mega ? " (MEGA)" : ""));
        }
        reply(_config.Admin.Ids.Count == 0
            ? "в config.json admin.ids пусто — админ-команд ни у кого нет"
            : $"админы в конфиге (id): {_config.DescribeAdmins()}");
    }

    /// <summary>
    /// <c>/auth</c> в чате: пароль вводится один раз при входе. Консоль шлём к
    /// <c>admin</c>: пароли и права — только оттуда.
    /// </summary>
    private void HandleAuthCommand(Action<string> reply, string args, ConnectedPlayer? player)
    {
        if (player is null)
        {
            reply("пароли и права: admin help (admin passwd regen <id|all>, admin mega, admin perm)");
            return;
        }

        if (_config.IsAdmin(IdOf(player.Name))
            && IsAdminAuthorized(IdOf(player.Name), player.Peer.Address))
        {
            reply("пароль уже принят на этом запуске");
            return;
        }

        reply("пароль вводится при входе в лобби: сервер сам попросит его в чате");
        reply("он лежит в auth/<id>/password.txt и выглядит как <id>-<hex64>");
    }

    /// <summary>
    /// Консольная команда <c>admin</c>: пароли, мега-IP, права. Только консоль/панель.
    /// </summary>
    private void HandleAdminCommand(Action<string> reply, string args, ConnectedPlayer? player)
    {
        if (player is not null)
        {
            reply("команда admin — только из консоли сервера");
            return;
        }

        var parts = (args ?? "").Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0 || parts[0].Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            reply("admin list — кто есть и какие права");
            reply("admin passwd regen <id|all> — новый личный пароль");
            reply("admin mega add|remove <id> <ip> — мега-права по IP");
            reply("admin perm grant|revoke|set <id> <права...> — выдать/отнять права");
            return;
        }

        var sub = parts[0].ToLowerInvariant();
        if (sub is "list" or "ls")
        {
            var records = _adminStore?.Records() ?? Array.Empty<AdminRecord>();
            if (records.Count == 0)
            {
                reply($"админов нет — впиши id в admin.ids ({ServerConfig.LastLoadedPath})");
                return;
            }

            foreach (var record in records)
            {
                var live = _config.IsAdmin(record.Id) ? "в admin.ids" : "не в admin.ids";
                var mega = record.MegaIps.Count > 0
                    ? $" · MEGA IP: {string.Join(", ", record.MegaIps)}"
                    : "";
                reply($"#{record.Id} '{record.Nick}' · {live} · v{record.AuthVersion} · " +
                      $"права: {AdminPermissions.Describe(record.Permissions)}{mega}");
            }
            return;
        }

        if (sub is "passwd" or "password")
        {
            if (parts.Length < 3 || !parts[1].Equals("regen", StringComparison.OrdinalIgnoreCase))
            {
                reply("admin passwd regen <id|all>");
                return;
            }

            if (_adminStore is null)
            {
                reply("хранилище паролей не подключено");
                return;
            }

            if (parts[2].Equals("all", StringComparison.OrdinalIgnoreCase))
            {
                var all = _adminStore.RegenerateAllPasswords(actorId: null, DateTime.UtcNow);
                foreach (var (adminId, path) in all)
                    reply($"#{adminId}: новый пароль — {path}");
                reply("старые веб-сессии и игровые авторизации мертвы");
                return;
            }

            if (!int.TryParse(parts[2], out var id) || id <= 0)
            {
                reply($"id — число или all, а не '{parts[2]}'");
                return;
            }

            if (!_adminStore.RegeneratePassword(
                    actorId: null, id, DateTime.UtcNow, out var passwordPath, out var error))
            {
                reply(error ?? "пароль не изменён");
                return;
            }

            reply($"#{id}: новый пароль — {passwordPath}");
            reply("старые веб-сессии и игровые авторизации мертвы");
            return;
        }

        if (sub == "mega")
        {
            if (parts.Length < 4
                || (parts[1] is not ("add" or "remove"))
                || !int.TryParse(parts[2], out var id) || id <= 0)
            {
                reply("admin mega add|remove <id> <ip>");
                return;
            }

            if (_adminStore is null)
            {
                reply("хранилище админов не подключено");
                return;
            }

            var ok = parts[1] == "add"
                ? _adminStore.AddMegaIp(null, id, parts[3], DateTime.UtcNow, out var error)
                : _adminStore.RemoveMegaIp(null, id, parts[3], DateTime.UtcNow, out error);
            reply(ok
                ? $"{parts[1]}: #{id} / {parts[3]} — готово"
                : error ?? "не удалось");
            return;
        }

        if (sub == "perm" || sub == "permission")
        {
            if (parts.Length < 4
                || parts[1] is not ("grant" or "revoke" or "set")
                || !int.TryParse(parts[2], out var id) || id <= 0)
            {
                reply("admin perm grant|revoke|set <id> <права...>");
                return;
            }

            if (_adminStore is null)
            {
                reply("хранилище админов не подключено");
                return;
            }

            var record = _adminStore.Find(id);
            if (record is null)
            {
                reply($"id #{id} нет среди админов");
                return;
            }

            var current = AdminPermissions.Normalize(record.Permissions);
            var requested = parts.Skip(3).ToList();
            var next = parts[1] switch
            {
                "grant" => current.Concat(requested).ToList(),
                "revoke" => current.Where(p => !requested.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList(),
                _ => requested,
            };

            if (!_adminStore.SetPermissions(
                    null, id, next, DateTime.UtcNow, out var error))
            {
                reply(error ?? "права не изменены");
                return;
            }

            reply($"#{id}: права → {AdminPermissions.Describe(next)}");
            return;
        }

        reply($"неизвестно: admin {sub} (admin help)");
    }
}
