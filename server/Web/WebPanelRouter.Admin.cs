using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Админ панель веб-сайта: состояние, вход по личному паролю, кастомизация, баны,
/// права. Данные отдаёт <see cref="GameNetHost"/>, доступ проверяет <see cref="AdminGate"/>.
/// </summary>
/// <remarks>
/// <para>
/// Затвор один: админ-кука с паролем → живой id из <c>admin.ids</c> → право на действие.
/// Сессия сайта для админки не нужна: вход в панель — это отдельный пароль, и он не
/// зависит от кода из лобби. Без куки не отдаётся ни один байт данных.
/// </para>
/// <para>
/// Права выдаёт только мега-админ: его id входит в <c>admin.megaIds</c>. Обычный админ
/// видит <c>dashboard</c> и <c>journal</c>, пока мега-админ не выдаст операционные.
/// IP панели на мега-статус не влияет.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private readonly AdminLoginLimiter _adminLoginLimiter = new();

    /// <summary>Затвор админ-эндпоинта: кука → живой админ → право.</summary>
    private WebResponse? AdminGate(
        WebRequestContext request, out AdminSession admin, string permission)
    {
        admin = null!;

        if (_options.AdminSessionStore is null
            || _options.AdminStore is null
            || _options.IsAdminLive is null)
            return ApiError(503, "admin_disabled", "админ панель не настроена на этом сервере");

        var now = DateTime.UtcNow;
        var session = _options.AdminSessionStore.Find(
            AdminCookie.ReadToken(request.Headers), now);
        if (session is null)
            return ApiError(401, "no_admin_auth", "войди в админ панель: id и личный пароль");

        if (!_options.IsAdminLive(session.PlayerId))
            return ApiError(403, "not_admin", "этого id больше нет в admin.ids");

        if (_options.IsBanned?.Invoke(session.PlayerId) == true)
        {
            _options.AdminSessionStore.DropByPlayer(session.PlayerId);
            return ApiError(403, "banned", "аккаунт забанен");
        }

        var record = _options.AdminStore.Find(session.PlayerId);
        if (record is null)
            return ApiError(403, "not_admin", "для этого id нет записи админа");

        if (!_options.AdminStore.HasPermission(session.PlayerId, permission))
            return ApiError(403, "no_permission",
                $"нет права «{permission}» — его выдаёт мега-админ");

        admin = session;
        return null;
    }

    /// <summary>Затвор, где достаточно любого из прав (поиск игрока нужен и банам).</summary>
    private WebResponse? AdminGateAny(
        WebRequestContext request, out AdminSession admin, params string[] permissions)
    {
        admin = null!;

        if (_options.AdminSessionStore is null
            || _options.AdminStore is null
            || _options.IsAdminLive is null)
            return ApiError(503, "admin_disabled", "админ панель не настроена на этом сервере");

        var session = _options.AdminSessionStore.Find(
            AdminCookie.ReadToken(request.Headers), DateTime.UtcNow);
        if (session is null)
            return ApiError(401, "no_admin_auth", "войди в админ панель: id и личный пароль");

        if (!_options.IsAdminLive(session.PlayerId))
            return ApiError(403, "not_admin", "этого id больше нет в admin.ids");

        if (_options.IsBanned?.Invoke(session.PlayerId) == true)
        {
            _options.AdminSessionStore.DropByPlayer(session.PlayerId);
            return ApiError(403, "banned", "аккаунт забанен");
        }

        if (_options.AdminStore.Find(session.PlayerId) is null)
            return ApiError(403, "not_admin", "для этого id нет записи админа");

        foreach (var permission in permissions)
        {
            if (_options.AdminStore.HasPermission(session.PlayerId, permission))
            {
                admin = session;
                return null;
            }
        }

        return ApiError(403, "no_permission",
            $"нет ни одного из прав: {string.Join(", ", permissions)}");
    }

    /// <summary>Только мега-админ из <c>admin.megaIds</c>; IP не важен.</summary>
    private WebResponse? MegaGate(WebRequestContext request, out AdminSession admin)
    {
        if (AdminGate(request, out admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        if (_options.AdminStore?.IsMega(admin.PlayerId) != true)
            return ApiError(403, "not_mega",
                "это действие доступно только мега-админу из admin.megaIds");

        return null;
    }

    // ── Состояние ───────────────────────────────────────────────────────────────

    /// <summary>Состояние админ-панели. Без входа — 401, без права — 403, данных нет.</summary>
    private WebResponse AdminState(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        var record = _options.AdminStore?.Find(admin.PlayerId);
        if (record is null)
            return ApiError(403, "not_admin", "для этого id нет записи админа");

        return AdminStateJson(record);
    }

    /// <summary>JSON состояния вошедшего админа: без записи сюда не попадают.</summary>
    private WebResponse AdminStateJson(AdminRecord record)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("admin", true);
            writer.WriteBoolean("authed", true);

            var permissions = _options.AdminStore?.PermissionsFor(record.Id)
                ?? AdminPermissions.Normalize(record.Permissions);
            var mega = _options.AdminStore?.IsMega(record.Id) == true;

            writer.WriteNumber("id", record.Id);
            writer.WriteString("nick", record.Nick ?? "");
            writer.WriteString("role", mega ? "mega" : "admin");
            writer.WriteBoolean("mega", mega);
            writer.WriteString("customId", _options.CustomIdGet?.Invoke(record.Id));

            writer.WriteStartArray("permissions");
            foreach (var permission in permissions)
                writer.WriteStringValue(permission);
            writer.WriteEndArray();

            var (enabled, minMinutes, maxDays) = _options.BanSettings?.Invoke() ?? (false, 10, 14);
            writer.WriteStartObject("bans");
            writer.WriteBoolean("enabled", enabled);
            writer.WriteNumber("minMinutes", minMinutes);
            writer.WriteNumber("maxDays", maxDays);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── Вход / выход ────────────────────────────────────────────────────────────

    /// <summary>Вход в админ панель по id и личному паролю. Сессия сайта не нужна.</summary>
    private WebResponse AdminLogin(WebRequestContext request)
    {
        if (_options.AdminSessionStore is null
            || _options.AdminStore is null
            || _options.IsAdminLive is null)
            return ApiError(503, "admin_disabled", "вход в админ панель не настроен на этом сервере");

        AdminLoginRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminLoginRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");
        if (string.IsNullOrWhiteSpace(parsed.Password))
            return ApiError(400, "empty_password", "введи личный пароль из auth/<id>/password.txt");

        var now = DateTime.UtcNow;
        if (!_adminLoginLimiter.TryBegin(request.RemoteAddress, now))
        {
            var wait = _adminLoginLimiter.RetryAfterSec(request.RemoteAddress, now);
            Console.WriteLine(
                $"[web] лимит попыток входа: {request.RemoteAddress} — ещё {wait} с");
            return ApiError(429, "too_many_attempts",
                $"слишком много попыток — подожди {wait} секунд");
        }

        var record = _options.AdminStore.Verify(parsed.Id, parsed.Password, now);
        if (record is null || !_options.IsAdminLive(record.Id))
        {
            _adminLoginLimiter.Fail(request.RemoteAddress, now);
            Console.WriteLine(
                $"[web] вход отклонён: id #{parsed.Id} с {request.RemoteAddress} " +
                "(неверный пароль или id не админ)");
            // Один и тот же ответ: не подсказываем, существует ли id.
            return ApiError(401, "bad_credentials", "id или пароль не подходят");
        }

        _adminLoginLimiter.Reset(request.RemoteAddress);
        var adminSession = _options.AdminSessionStore.Create(
            record.Id, record.Nick, record.AuthVersion, now);
        if (adminSession is null)
            return ApiError(503, "too_many_sessions",
                "на сервере кончились админ-сессии — попробуй позже");

        _options.AdminStore.Journal("auth", "login", record.Id, record.Id);
        Console.WriteLine(
            $"[web] админ-вход: #{record.Id} с {request.RemoteAddress} " +
            $"(мега: {_options.AdminStore.IsMega(record.Id)})");

        return WithCookie(
            AdminStateJson(record),
            AdminCookie.Build(adminSession.Token, _options.CookiePath, _options.SecureCookies));
    }

    /// <summary>Выход из админ панели: гасим конкретную админ-куку.</summary>
    private WebResponse AdminLogout(WebRequestContext request)
    {
        var token = AdminCookie.ReadToken(request.Headers);
        if (token is { Length: > 0 })
            _options.AdminSessionStore?.Drop(token);

        return WithCookie(
            WebResponse.Json("{\"ok\":true}"),
            AdminCookie.BuildExpired(_options.CookiePath, _options.SecureCookies));
    }

    // ── Админы и права (только мега) ────────────────────────────────────────────

    /// <summary>Список админов. Только с правом <c>admins</c>; мега получает его всегда.</summary>
    private WebResponse AdminList(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Admins) is { } denied)
            return denied;

        return AdminListJson(admin.PlayerId);
    }

    /// <summary>Список без повторной проверки: вызывающий уже прошёл затвор.</summary>
    private WebResponse AdminListJson(int viewerId)
    {
        var viewerMega = _options.AdminStore?.IsMega(viewerId) == true;
        var records = _options.AdminStore?.Records() ?? Array.Empty<AdminRecord>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("mega", viewerMega);
            writer.WriteStartArray("admins");
            foreach (var record in records)
            {
                var mega = _options.AdminStore?.IsMega(record.Id) == true;
                writer.WriteStartObject();
                writer.WriteNumber("id", record.Id);
                writer.WriteString("nick", record.Nick ?? "");
                writer.WriteString("role", mega ? "mega" : "admin");
                writer.WriteBoolean("mega", mega);
                writer.WriteString("addedBy", record.AddedBy);
                writer.WriteNumber("authVersion", record.AuthVersion);
                writer.WriteBoolean("live", _options.IsAdminLive?.Invoke(record.Id) == true);
                writer.WriteStartArray("permissions");
                foreach (var permission in mega
                             ? AdminPermissions.All
                             : AdminPermissions.Normalize(record.Permissions))
                    writer.WriteStringValue(permission);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Выдать/отнять операционные права. Только мега-админ.</summary>
    private WebResponse AdminSetPermissions(WebRequestContext request)
    {
        if (MegaGate(request, out var admin) is { } denied)
            return denied;

        AdminPermissionsRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminPermissionsRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");

        string? error = null;
        if (_options.AdminStore?.SetPermissions(
                admin.PlayerId, parsed.Id, parsed.Permissions, DateTime.UtcNow,
                out error) != true)
            return ApiError(400, "permissions_refused", error ?? "права не изменены");

        Console.WriteLine(
            $"[web] права #{parsed.Id} → {AdminPermissions.Describe(parsed.Permissions)} " +
            $"(кто: #{admin.PlayerId})");
        return AdminListJson(admin.PlayerId);
    }

    // ── Кастомизация ────────────────────────────────────────────────────────────

    /// <summary>Поставить/сбросить свой кастомный ID. Только себе — чужой не трогаем.</summary>
    private WebResponse AdminSetCustomId(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Customization) is { } denied)
            return denied;

        if (_options.CustomIdSet is null)
            return ApiError(503, "admin_disabled", "кастомизация не настроена на этом сервере");

        AdminCustomIdRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminCustomIdRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var value = parsed?.CustomId?.Trim() ?? string.Empty;
        if (value.Length > CustomIdStore.MaxLength)
            return ApiError(400, "bad_custom_id", $"максимум {CustomIdStore.MaxLength} символов");

        var error = _options.CustomIdSet(admin.PlayerId, value.Length == 0 ? null : value);
        if (error is not null)
            return ApiError(400, "bad_custom_id", error);

        var action = value.Length == 0 ? "сбросил" : "поставил";
        Console.WriteLine($"[web] кастомный ID: #{admin.PlayerId} {action} «{value}»");

        var record = _options.AdminStore?.Find(admin.PlayerId);
        if (record is null)
            return ApiError(403, "not_admin", "для этого id нет записи админа");
        return AdminStateJson(record);
    }

    // ── Баны ────────────────────────────────────────────────────────────────────

    /// <summary>Превью игрока по id: ник, статус (катка/лобби/не в сети), аватар, бан.</summary>
    private WebResponse AdminLookupPlayer(WebRequestContext request)
    {
        if (AdminGateAny(request, out _, AdminPermissions.Bans, AdminPermissions.Players) is { } denied)
            return denied;

        if (_options.AdminLookup is null)
            return ApiError(503, "admin_disabled", "поиск игроков не настроен на этом сервере");

        if (!TryQueryInt(request.Query, "id", out var id) || id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");

        var found = _options.AdminLookup(id);
        if (found is null)
            return ApiError(404, "not_found", $"никого с id {id} сервер не помнит");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("id", found.Id);
            writer.WriteString("nick", found.Nick);
            writer.WriteString("status", found.Status);
            writer.WriteBoolean("hasAvatar", found.HasAvatar);
            writer.WriteBoolean("banned", found.Banned);
            writer.WriteString("customId", found.CustomId);
            if (found.LastSeenUtc is { } seen)
                writer.WriteString("lastSeen", seen.ToString("O"));
            if (found.Note is not null)
                writer.WriteString("note", found.Note);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Аватар найденного игрока — те же байты, что клиент прислал в лобби.</summary>
    private WebResponse AdminAvatar(WebRequestContext request)
    {
        if (AdminGateAny(request, out _, AdminPermissions.Bans, AdminPermissions.Players,
                AdminPermissions.Dashboard) is { } denied)
            return denied;

        if (_options.AvatarById is null)
            return WebResponse.Text("нет аватара", 404);

        if (!TryQueryInt(request.Query, "id", out var id) || id <= 0)
            return WebResponse.Text("нет аватара", 404);

        var bytes = _options.AvatarById(id);
        var type = AvatarCache.ImageContentType(bytes);
        if (type is null)
            return WebResponse.Text("нет аватара", 404);

        return new WebResponse(200, type, bytes!, 0,
            new List<(string, string)> { ("Cache-Control", "private, max-age=60") });
    }

    /// <summary>Активные баны — для списка с кнопкой «разбанить».</summary>
    private WebResponse AdminBansList(WebRequestContext request)
    {
        if (AdminGate(request, out _, AdminPermissions.Bans) is { } denied)
            return denied;

        var now = DateTime.UtcNow;
        var active = _options.ActiveBans?.Invoke() ?? Array.Empty<BanRecord>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartArray("bans");
            foreach (var record in active)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", record.MatchIds.Count > 0 ? record.MatchIds[0] : 0);
                writer.WriteString("nick", record.DisplayNick);
                writer.WriteString("reason", string.IsNullOrWhiteSpace(record.Reason) ? "" : record.Reason.Trim());
                writer.WriteString("bannedBy", record.BannedBy);
                writer.WriteString("created", record.CreatedUtc.ToString("O"));
                writer.WriteString("expires", record.ExpiresUtc.ToString("O"));
                writer.WriteString("duration", BanDuration.FormatShort(record.Duration));
                writer.WriteString("remaining", BanDuration.FormatRemaining(record.Remaining(now)));
                writer.WriteNumber("ips", record.Ips.Count);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Забанить игрока по id: срок («6d»), причина, «кто банит» — из админ-сессии.</summary>
    private WebResponse AdminBan(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Bans) is { } denied)
            return denied;

        if (_options.WebBan is null)
            return ApiError(503, "admin_disabled", "баны не настроены на этом сервере");

        AdminBanRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminBanRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");

        var value = parsed.DurationValue;
        var unit = (parsed.DurationUnit ?? "").Trim().ToLowerInvariant();
        if (value <= 0 || unit is not ("m" or "h" or "d"))
            return ApiError(400, "bad_duration", "срок — число и единица: минуты, часы или дни");

        var reason = (parsed.Reason ?? "").Trim();
        if (reason.Length > 200)
            reason = reason[..200].Trim();

        var (error, record) = _options.WebBan(parsed.Id, $"{value}{unit}", reason, admin.Nick);
        if (error is not null || record is null)
            return ApiError(400, "ban_refused", error ?? "бан не удался");

        _options.AdminStore?.Journal("ban", "ban", admin.PlayerId, parsed.Id,
            after: new { duration = BanDuration.FormatShort(record.Duration), reason });

        Console.WriteLine(
            $"[web] бан из панели: '{record.DisplayNick}' на {BanDuration.FormatShort(record.Duration)} " +
            $"причиной '{(reason.Length == 0 ? "не указана" : reason)}' (кто: {record.BannedBy})");

        using var outStream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(outStream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("id", record.MatchIds.Count > 0 ? record.MatchIds[0] : 0);
            writer.WriteString("nick", record.DisplayNick);
            writer.WriteString("duration", BanDuration.FormatShort(record.Duration));
            writer.WriteString("expires", record.ExpiresUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss"));
            writer.WriteString("reason", reason);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(outStream.ToArray()));
    }

    /// <summary>Разбан по id: снимает все записи этого человека, объявление всем.</summary>
    private WebResponse AdminUnban(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Bans) is { } denied)
            return denied;

        if (_options.WebUnban is null)
            return ApiError(503, "admin_disabled", "баны не настроены на этом сервере");

        AdminUnbanRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminUnbanRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");

        var (error, removed) = _options.WebUnban(parsed.Id);
        if (error is not null)
            return ApiError(404, "not_found", error);

        _options.AdminStore?.Journal("ban", "unban", admin.PlayerId, parsed.Id,
            after: new { removed });
        return WebResponse.Json($"{{\"ok\":true,\"removed\":{removed}}}");
    }

    /// <summary>Число из query-строки (<c>id=123</c>): без URL-декода — цифры и так честные.</summary>
    private static bool TryQueryInt(string? query, string name, out int value)
    {
        value = 0;
        if (string.IsNullOrEmpty(query))
            return false;

        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            return int.TryParse(part[(eq + 1)..].Trim(), out value) && value > 0;
        }

        return false;
    }

    private sealed record AdminLoginRequest(int Id, string? Password);
    private sealed record AdminPermissionsRequest(int Id, List<string>? Permissions);
    private sealed record AdminCustomIdRequest(string? CustomId);
    private sealed record AdminBanRequest(int Id, string? Reason, int DurationValue, string? DurationUnit);
    private sealed record AdminUnbanRequest(int Id);
}
