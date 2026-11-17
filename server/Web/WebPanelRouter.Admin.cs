using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Админ панель веб-сайта: состояние, вход по коду этого запуска, кастомизация,
/// баны. Данные отдаёт <see cref="GameNetHost"/>, права проверяет затвор ниже.
/// </summary>
/// <remarks>
/// <para>
/// Тройной затвор на каждый эндпоинт: сессия сайта (кто ты) → <b>живой</b> конфиг
/// <c>admin.ids</c> (кто админ — перечитывание конфига сразу снимает доступ) →
/// админ-кука этого запуска (код из auth.txt, в памяти до рестарта). Любая ветка не
/// пройдена — 401/403, данных нет.
/// </para>
/// <para>
/// Анонимный <c>GET /api/admin</c> не ошибка, а честное «ты не админ»: страница на
/// <c>#/admin</c> должна открываться всем, содержимое показывается только после входа.
/// Все остальные эндпоинты (вход, поиск, кастомизация, баны) — затвор полностью.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    /// <summary>
    /// Тройной затвор админ-эндпоинта. Ответ-отказ (401/403/503) или <c>null</c>,
    /// если обе сессии получены и валидны.
    /// </summary>
    private WebResponse? AdminGate(WebRequestContext request, out SiteSession site, out AdminSession admin)
    {
        site = null!;
        admin = null!;

        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        if (BannedRefusal(session) is { } banned)
            return banned;

        if (_options.IsAdminLive?.Invoke(session.PlayerId) != true)
            return ApiError(403, "not_admin", "админ панель доступна только админам сервера");

        var adminSession = _options.AdminSessionStore?.Find(AdminCookie.ReadToken(request.Headers));
        if (adminSession is null || adminSession.PlayerId != session.PlayerId)
            return ApiError(403, "no_admin_auth", "введи код этого запуска из auth.txt");

        site = session;
        admin = adminSession;
        return null;
    }

    // ── Состояние ───────────────────────────────────────────────────────────────

    /// <summary>Состояние админ-панели для этой сессии сайта (без данных, если не админ).</summary>
    private WebResponse AdminState(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return AdminStateJson(null, null, authed: false);

        if (BannedRefusal(session) is { } banned)
            return banned;

        if (_options.IsAdminLive?.Invoke(session.PlayerId) != true)
            return AdminStateJson(null, null, authed: false);

        var adminSession = _options.AdminSessionStore?.Find(AdminCookie.ReadToken(request.Headers));
        if (adminSession is null || adminSession.PlayerId != session.PlayerId)
            return AdminStateJson(session.PlayerId, session.Nick, authed: false);

        return AdminStateJson(session.PlayerId, session.Nick, authed: true);
    }

    /// <summary>
    /// JSON состояния. <paramref name="id"/> — <c>null</c> для не-админа (данных нет:
    /// ни ника, ни факта админства).
    /// </summary>
    private WebResponse AdminStateJson(int? id, string? nick, bool authed)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("admin", id is not null);
            writer.WriteBoolean("authed", authed);

            if (id is not null)
            {
                writer.WriteNumber("id", id.Value);
                writer.WriteString("nick", nick ?? "");
                writer.WriteString("customId", _options.CustomIdGet?.Invoke(id.Value));

                var (enabled, minMinutes, maxDays) = _options.BanSettings?.Invoke() ?? (false, 10, 14);
                writer.WriteStartObject("bans");
                writer.WriteBoolean("enabled", enabled);
                writer.WriteNumber("minMinutes", minMinutes);
                writer.WriteNumber("maxDays", maxDays);
                writer.WriteEndObject();
            }

            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── Вход / выход ────────────────────────────────────────────────────────────

    /// <summary>Вход в админ панель: код этого запуска (тот же, что в auth.txt и в чате лобби).</summary>
    private WebResponse AdminLogin(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        if (BannedRefusal(session) is { } refused)
            return refused;

        // Не-админ даже код вводить не должен: спека — отклонять всех, кто не админ.
        if (_options.IsAdminLive?.Invoke(session.PlayerId) != true)
            return ApiError(403, "not_admin", "админ панель доступна только админам сервера");

        if (_options.AdminSessionStore is null || _options.AdminCodeMatches is null)
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

        var code = parsed?.Code?.Trim();
        if (string.IsNullOrEmpty(code))
            return ApiError(400, "empty_code", "введи код из auth.txt");

        // Константное сравнение внутри Matches: время ответа не выдаёт длину/близость кода.
        if (!_options.AdminCodeMatches(code))
        {
            Console.WriteLine(
                $"[web] админ-код отклонён: '{session.Nick}' (ID {session.PlayerId}) с {request.RemoteAddress}");
            return ApiError(403, "bad_code", "код не подходит — он в auth.txt рядом с config.json, и он новый после каждого рестарта");
        }

        var adminSession = _options.AdminSessionStore.Create(session.PlayerId, session.Nick, DateTime.UtcNow);
        if (adminSession is null)
            return ApiError(503, "too_many_sessions", "на сервере кончились админ-сессии — попробуй позже");

        Console.WriteLine(
            $"[web] админ-авторизация: '{session.Nick}' (ID {session.PlayerId}) с {request.RemoteAddress}");

        return WithCookie(
            AdminStateJson(session.PlayerId, session.Nick, authed: true),
            AdminCookie.Build(adminSession.Token, _options.CookiePath, _options.SecureCookies));
    }

    /// <summary>Выход из админ панели: гасим конкретную админ-куку.</summary>
    private WebResponse AdminLogout(WebRequestContext request)
    {
        var token = AdminCookie.ReadToken(request.Headers);
        if (token is { Length: > 0 })
            _options.AdminSessionStore?.Drop(token);

        // Куку гасим всегда — как у сессии сайта.
        return WithCookie(
            WebResponse.Json("{\"ok\":true}"),
            AdminCookie.BuildExpired(_options.CookiePath, _options.SecureCookies));
    }

    // ── Кастомизация ────────────────────────────────────────────────────────────

    /// <summary>Поставить/сбросить свой кастомный ID. Только себе — чужой не трогаем.</summary>
    private WebResponse AdminSetCustomId(WebRequestContext request)
    {
        if (AdminGate(request, out var session, out _) is { } denied)
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

        var error = _options.CustomIdSet(session.PlayerId, value.Length == 0 ? null : value);
        if (error is not null)
            return ApiError(400, "bad_custom_id", error);

        var action = value.Length == 0 ? "сбросил" : "поставил";
        Console.WriteLine($"[web] кастомный ID: '{session.Nick}' (ID {session.PlayerId}) {action} «{value}»");

        return AdminStateJson(session.PlayerId, session.Nick, authed: true);
    }

    // ── Баны ────────────────────────────────────────────────────────────────────

    /// <summary>Превью игрока по id: ник, статус (катка/лобби/не в сети), аватар, бан.</summary>
    private WebResponse AdminLookupPlayer(WebRequestContext request)
    {
        if (AdminGate(request, out _, out _) is { } denied)
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
        if (AdminGate(request, out _, out _) is { } denied)
            return denied;

        if (_options.AvatarById is null)
            return WebResponse.Text("нет аватара", 404);

        if (!TryQueryInt(request.Query, "id", out var id) || id <= 0)
            return WebResponse.Text("нет аватара", 404);

        var bytes = _options.AvatarById(id);
        var type = ImageContentType(bytes);
        if (type is null)
            return WebResponse.Text("нет аватара", 404);

        return new WebResponse(200, type, bytes!, 0,
            new List<(string, string)> { ("Cache-Control", "private, max-age=60") });
    }

    /// <summary>Активные баны — для списка с кнопкой «разбанить».</summary>
    private WebResponse AdminBansList(WebRequestContext request)
    {
        if (AdminGate(request, out _, out _) is { } denied)
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
        if (AdminGate(request, out var session, out _) is { } denied)
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

        // Причина необязательна, но безграничной быть не должна: в объявление всем.
        var reason = (parsed.Reason ?? "").Trim();
        if (reason.Length > 200)
            reason = reason[..200].Trim();

        var (error, record) = _options.WebBan(parsed.Id, $"{value}{unit}", reason, session.Nick);
        if (error is not null || record is null)
            return ApiError(400, "ban_refused", error ?? "бан не удался");

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
        if (AdminGate(request, out _, out _) is { } denied)
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

    private sealed record AdminLoginRequest(string? Code);
    private sealed record AdminCustomIdRequest(string? CustomId);
    private sealed record AdminBanRequest(int Id, string? Reason, int DurationValue, string? DurationUnit);
    private sealed record AdminUnbanRequest(int Id);
}
