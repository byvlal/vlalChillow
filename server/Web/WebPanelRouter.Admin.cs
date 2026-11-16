using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Bans;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Данные админки: кто это по id, баны, выдача и снятие бана, публичный id, сервер.
/// </summary>
/// <remarks>
/// <para>
/// Каждый маршрут начинается с <see cref="AdminSessionOf"/>: обычная сессия сайта плюс
/// ключ администратора. Иначе «просто авторизованный» игрок мог бы прочитать баны,
/// а это личные данные других людей.
/// </para>
/// <para>
/// Разделения на «чтение можно, запись нельзя» здесь нет намеренно: владелец просил
/// отклонять <b>любые</b> запросы к админке без ключа, включая чтение. Поэтому
/// <c>/api/admin/*</c> целиком закрыт.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    // ── GET /api/admin/players?id=… ──────────────────────────────────────────

    /// <summary>
    /// Кого именно собираются забанить: ник, id, публичный id, аватар и текущий бан.
    /// </summary>
    /// <remarks>
    /// Ищется по номеру, который человек видит в игре и в <c>/ids</c>. Ответ отдаёт
    /// <b>ник и аватар</b> — админ обязан видеть, кого банит, а не только цифры.
    /// </remarks>
    /// <summary>
    /// Раскладка запроса: путь и query приходят раздельно, а страница присылает
    /// <c>?id=…</c>. Склеиваем здесь, чтобы разбор не зависел от того, кто вызвал.
    /// </summary>
    private WebResponse AdminPlayer(WebRequestContext request) =>
        AdminPlayerWith(WithQuery(request), SessionOf(request));

    private static WebRequestContext WithQuery(WebRequestContext request) =>
        string.IsNullOrEmpty(request.Query)
            ? request
            : request with { Path = request.Path + "?" + request.Query };

    private WebResponse AdminPlayerWith(WebRequestContext request, SiteSession? session)
    {
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banRefusal)
            return banRefusal;

        var who = session!;
        if (AdminSessionOf(request, who, out var refusal) is null)
            return refusal!;

        var query = QueryValue(request.Query, "id");
        if (!int.TryParse(query, out var playerId) || playerId <= 0)
            return ApiError(400, "bad_id", "нужен числовой id игрока (он же в /ids и в игре)");

        var options = _options;
        var seen = options.LookupPlayer?.Invoke(playerId);
        var isBanned = options.IsBanned?.Invoke(playerId) == true;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", playerId);
            writer.WriteString("displayId", options.PublicIdOf?.Invoke(playerId) ?? playerId.ToString());

            if (seen is null)
            {
                // Важно не врать: «не знаю такого» — это не «чистый игрок».
                writer.WriteBoolean("known", false);
                writer.WriteEndObject();
                return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
            }

            writer.WriteBoolean("known", true);
            writer.WriteString("nick", seen.Nick);
            writer.WriteBoolean("banned", isBanned);
            writer.WriteBoolean("online", options.LobbyAddressOf?.Invoke(playerId) is not null);
            writer.WriteString("lastSeen", seen.LastSeenUtcMs <= 0
                ? null
                : DateTimeOffset.FromUnixTimeMilliseconds(seen.LastSeenUtcMs).UtcDateTime.ToString("O"));

            writer.WriteStartArray("ips");
            foreach (var ip in seen.Ips)
                writer.WriteStringValue(ip);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── GET /api/admin/bans ──────────────────────────────────────────────────

    /// <summary>Активные баны: кто, на сколько, за что и кто забанил.</summary>
    private WebResponse AdminBans(WebRequestContext request) => AdminBansWith(request, SessionOf(request));

    private WebResponse AdminBansWith(WebRequestContext request, SiteSession? session)
    {
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banRefusal)
            return banRefusal;

        var who = session!;
        if (AdminSessionOf(request, who, out var refusal) is null)
            return refusal!;

        var now = DateTime.UtcNow;
        var options = _options;
        var bans = options.ActiveBans?.Invoke() ?? Array.Empty<BanRecord>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("bans");
            foreach (var record in bans)
            {
                writer.WriteStartObject();
                // id берётся из тега записи: по нему же работает /unban.
                writer.WriteNumber("id", record.MatchIds.Count > 0 ? record.MatchIds[0] : 0);
                writer.WriteString("nick", record.Nick);
                writer.WriteString("displayId", record.MatchIds.Count > 0
                    ? options.PublicIdOf?.Invoke(record.MatchIds[0]) ?? record.MatchIds[0].ToString()
                    : "");
                writer.WriteString("reason", record.Reason);
                writer.WriteString("bannedBy", record.BannedBy);
                writer.WriteString("expires", record.ExpiresUtc.ToString("O"));
                writer.WriteNumber("remainingSec", Math.Max(0, (long)record.Remaining(now).TotalSeconds));
                writer.WriteString("duration", BanDuration.FormatShort(record.Duration));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── POST /api/admin/bans ─────────────────────────────────────────────────

    /// <summary>
    /// Забанить игрока с сайта: id, срок, причина.
    /// </summary>
    /// <remarks>
    /// Ник автора берётся из админской сессии, а не из тела запроса: иначе «кто забанил»
    /// можно было бы подписать чужим ником, а объявление видят все игроки.
    /// </remarks>
    private WebResponse AdminBan(WebRequestContext request) => AdminBanWith(request, SessionOf(request));

    private WebResponse AdminBanWith(WebRequestContext request, SiteSession? session)
    {
        var who = session!;
        var admin = AdminSessionOf(request, who, out var refusal);
        if (admin is null)
            return refusal!;

        AdminBanRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminBanRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null)
            return ApiError(400, "bad_request", "пустое тело запроса");

        if (_options.BanPlayer is null)
            return ApiError(503, "admin_disabled", "баны не подключены на этом сервере");

        var record = _options.BanPlayer(
            parsed.Id, parsed.Duration, parsed.Reason, admin.Nick, out var error);

        if (record is null)
            return ApiError(400, "ban_refused", error ?? "не получилось забанить");

        _options.Log?.Invoke(
            $"[web] админка: {admin.Nick} (ID {admin.PlayerId}) забанил " +
            $"{record.DisplayNick} на {BanDuration.FormatShort(record.Duration)}: {record.Reason}");

        return WebResponse.Json(BuildBanJson(record, _options), 201);
    }

    // ── POST /api/admin/unban ────────────────────────────────────────────────

    /// <summary>Снять баны, выданные под этим id.</summary>
    private WebResponse AdminUnban(WebRequestContext request) => AdminUnbanWith(request, SessionOf(request));

    private WebResponse AdminUnbanWith(WebRequestContext request, SiteSession? session)
    {
        var who = session!;
        var admin = AdminSessionOf(request, who, out var refusal);
        if (admin is null)
            return refusal!;

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
            return ApiError(400, "bad_id", "нужен числовой id игрока");

        if (_options.UnbanPlayer is null)
            return ApiError(503, "admin_disabled", "баны не подключены на этом сервере");

        var ok = _options.UnbanPlayer(parsed.Id, out var removed, out var error);
        if (!ok)
            return ApiError(400, "unban_refused", error ?? "не получилось разбанить");

        _options.Log?.Invoke(
            $"[web] админка: {admin.Nick} (ID {admin.PlayerId}) снял баны под id #{parsed.Id} ({removed})");

        return WebResponse.Json($"{{\"ok\":true,\"removed\":{removed}}}");
    }

    // ── POST /api/admin/public-id ────────────────────────────────────────────

    /// <summary>
    /// Назначить себе (или указанному id) буквенно-цифровой публичный id вместо числового.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Ключ проверок остаётся числовым: <c>admin.ids</c>, баны и <c>/ids</c> считают его из
    /// ника. Меняется только то, что человек видит в игре и на сайте (см.
    /// <see cref="PublicIdOverrides"/>).
    /// </para>
    /// <para>
    /// Пустое значение снимает подмену — вернуться к числовому id можно той же кнопкой.
    /// </para>
    /// </remarks>
    private WebResponse AdminPublicId(WebRequestContext request) => AdminPublicIdWith(request, SessionOf(request));

    private WebResponse AdminPublicIdWith(WebRequestContext request, SiteSession? session)
    {
        var who = session!;
        var admin = AdminSessionOf(request, who, out var refusal);
        if (admin is null)
            return refusal!;

        AdminPublicIdRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminPublicIdRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null)
            return ApiError(400, "bad_request", "пустое тело запроса");

        // Параметр id необязателен: по умолчанию — себе. Это удобно и безопасно ровно
        // потому, что подмена не даёт прав: она меняет только отображение.
        var targetId = parsed.Id > 0 ? parsed.Id : admin.PlayerId;

        var overrides = _options.PublicIds;
        if (overrides is null)
            return ApiError(503, "admin_disabled", "подмена id не подключена на этом сервере");

        if (string.IsNullOrWhiteSpace(parsed.Value))
        {
            var cleared = overrides.Clear(targetId);
            _options.Log?.Invoke(cleared
                ? $"[web] админка: публичный id #{targetId} снят (снова числовой)"
                : $"[web] админка: у #{targetId} и не было подмены");

            return WebResponse.Json(
                $"{{\"ok\":true,\"id\":{targetId},\"displayId\":{Escape(overrides.DisplayOf(targetId))}}}");
        }

        if (!overrides.TrySet(targetId, parsed.Value, out var value, out var error))
            return ApiError(400, "bad_public_id", error);

        _options.Log?.Invoke(
            $"[web] админка: {admin.Nick} назначил публичный id '{value}' игроку #{targetId}");

        return WebResponse.Json(
            $"{{\"ok\":true,\"id\":{targetId},\"displayId\":{Escape(value)}}}");
    }

    // ── GET /api/admin/status ────────────────────────────────────────────────

    /// <summary>Список id из <c>admin.ids</c> — только для админского раздела сайта.</summary>
    private WebResponse AdminIds(WebRequestContext request) => AdminIdsWith(request, SessionOf(request));

    private WebResponse AdminIdsWith(WebRequestContext request, SiteSession? session)
    {
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banRefusal)
            return banRefusal;

        var who = session!;
        if (AdminSessionOf(request, who, out var refusal) is null)
            return refusal!;

        var ids = _options.AdminIdList?.Invoke() ?? Array.Empty<int>();
        return WebResponse.Json(
            $"{{\"ids\":{JsonSerializer.Serialize(ids)}}}");
    }

    /// <summary>Сводка сервера для админки: то же, что видит страница, плюс баны и админы.</summary>
    private WebResponse AdminStatus(WebRequestContext request) => AdminStatusWith(request, SessionOf(request));

    private WebResponse AdminStatusWith(WebRequestContext request, SiteSession? session)
    {
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banRefusal)
            return banRefusal;

        var who = session!;
        if (AdminSessionOf(request, who, out var refusal) is null)
            return refusal!;

        var bans = _options.ActiveBans?.Invoke() ?? Array.Empty<BanRecord>();
        return WebResponse.Json(
            $"{{\"status\":{_status.Current},\"bans\":{bans.Count}," +
            $"\"adminIds\":{JsonSerializer.Serialize(_options.AdminIdList?.Invoke() ?? Array.Empty<int>())}}}");
    }

    /// <summary>Ответ по выданному бану — тот же набор полей, что в списке.</summary>
    private static string BuildBanJson(BanRecord record, WebPanelRouterOptions options)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("nick", record.Nick);
            writer.WriteString("reason", record.Reason);
            writer.WriteString("bannedBy", record.BannedBy);
            writer.WriteString("expires", record.ExpiresUtc.ToString("O"));
            writer.WriteString("duration", BanDuration.FormatShort(record.Duration));
            writer.WriteNumber("remainingSec", Math.Max(0, (long)record.Remaining(DateTime.UtcNow).TotalSeconds));
            if (record.MatchIds.Count > 0)
            {
                writer.WriteNumber("id", record.MatchIds[0]);
                writer.WriteString("displayId",
                    options.PublicIdOf?.Invoke(record.MatchIds[0]) ?? record.MatchIds[0].ToString());
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Значение из строки запроса <c>a=1&amp;b=2</c>; пусто, если его нет.</summary>
    internal static string QueryValue(string? query, string name)
    {
        if (string.IsNullOrEmpty(query))
            return string.Empty;

        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!part[..eq].Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;
            return Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }

        return string.Empty;
    }

    /// <summary>Экранирование строки для ручной сборки JSON (короткие ответы).</summary>
    /// <remarks>
    /// Возвращает значение <b>с</b> кавычками — так оно и подставляется в шаблон.
    /// </remarks>
    private static string Escape(string text) =>
        JsonSerializer.Serialize(text);

    /// <summary>Тело <c>POST /api/admin/bans</c>.</summary>
    private sealed record AdminBanRequest(int Id, string? Duration, string? Reason);

    /// <summary>Тело <c>POST /api/admin/unban</c>.</summary>
    private sealed record AdminUnbanRequest(int Id);

    /// <summary>Тело <c>POST /api/admin/public-id</c>. <c>Id</c> = 0 — себе.</summary>
    private sealed record AdminPublicIdRequest(int Id, string? Value);
}
