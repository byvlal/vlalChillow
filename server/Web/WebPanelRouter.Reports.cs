using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Reports;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Жалобы игроков, публичные профили и список администрации.
/// </summary>
/// <remarks>
/// <para>
/// Отправка и «мои жалобы» — только по сессии сайта: меньше спама и видно автора.
/// Публичный профиль и список администрации открыты всем: это витрина сервера, а не
/// личные данные. IP и устройства наружу не отдаются никогда.
/// </para>
/// <para>
/// Модерация — в той же вкладке «Репорты», но только после тройного затвора
/// (сессия → живой <c>admin.ids</c> → админ-кука этого запуска). Просто быть в
/// <c>admin.ids</c> недостаточно: код из auth.txt обязателен.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private const int ReportsPageSize = 20;

    // ── публичные страницы ───────────────────────────────────────────────────

    /// <summary>Публичный профиль игрока: ник, displayId, статистика, счётчик жалоб.</summary>
    private WebResponse PublicPlayer(WebRequestContext request)
    {
        var query = QueryValue(request.Query, "q");
        if (string.IsNullOrWhiteSpace(query))
            return ApiError(400, "bad_query", "укажи ID игрока");

        if (_options.ReportPlayerLookup is null)
            return ApiError(503, "not_ready", "сервер ещё не готов — попробуй через пару секунд");

        var target = _options.ReportPlayerLookup(query);
        if (target is null)
            return ApiError(404, "not_found", "никого с таким ID сервер не помнит");

        var snap = _options.Stats?.Get(target.Id);
        var kills = snap?.Kills ?? 0;
        var deaths = snap?.Deaths ?? 0;
        var kd = deaths > 0
            ? Math.Round((double)kills / deaths, 2)
            : kills > 0 ? (double)kills : 0.0;
        var counts = _options.Reports?.CountsFor(
            target.Id, target.Devices, DateTime.UtcNow) ?? new ReportCounts(0, 0, 0);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("id", target.Id);
            writer.WriteString("nick", target.Nick);
            writer.WriteString("customId", target.CustomId);
            writer.WriteString("displayId", target.DisplayId);
            writer.WriteBoolean("admin", target.IsAdmin);
            writer.WriteString("status", target.Status);
            writer.WriteBoolean("hasAvatar", target.HasAvatar);
            writer.WriteBoolean("banned", target.Banned);
            if (target.LastSeenUtc is { } seen)
                writer.WriteString("lastSeen", seen.ToString("O"));
            else
                writer.WriteNull("lastSeen");
            if (!string.IsNullOrWhiteSpace(target.Note))
                writer.WriteString("note", target.Note);

            writer.WriteStartObject("stats");
            writer.WriteNumber("kills", kills);
            writer.WriteNumber("deaths", deaths);
            writer.WriteNumber("kd", kd);
            writer.WriteNumber("seconds", snap?.Seconds ?? 0);
            writer.WriteNumber("matches", snap?.Matches ?? 0);
            writer.WriteEndObject();

            WriteCounts(writer, counts);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Список всех админов из живого <c>admin.ids</c>.</summary>
    private WebResponse Administration(WebRequestContext request)
    {
        _ = request;
        var admins = _options.AdministrationList?.Invoke();
        if (admins is null)
            return ApiError(503, "not_ready", "сервер ещё не готов — попробуй через пару секунд");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartArray("admins");
            foreach (var target in admins)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", target.Id);
                writer.WriteString("nick", target.Nick);
                writer.WriteString("customId", target.CustomId);
                writer.WriteString("displayId", target.DisplayId);
                writer.WriteBoolean("admin", true);
                writer.WriteString("status", target.Status);
                writer.WriteBoolean("hasAvatar", target.HasAvatar);
                if (target.LastSeenUtc is { } seen)
                    writer.WriteString("lastSeen", seen.ToString("O"));
                else
                    writer.WriteNull("lastSeen");

                var counts = _options.Reports?.CountsFor(
                    target.Id, target.Devices, DateTime.UtcNow) ?? new ReportCounts(0, 0, 0);
                WriteCounts(writer, counts);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── жалобы игроков ───────────────────────────────────────────────────────

    /// <summary>Отправить жалобу. Ник и устройства цели сервер определяет сам.</summary>
    private WebResponse SubmitReport(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        if (BannedRefusal(session) is { } banned)
            return banned;

        if (_options.Reports is null || _options.ReportPlayerLookup is null)
            return ApiError(503, "reports_disabled", "жалобы не настроены на этом сервере");

        ReportSubmitRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<ReportSubmitRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var targetText = (parsed?.Target ?? "").Trim();
        if (targetText.Length == 0)
            return ApiError(400, "bad_target", "укажи ID игрока");

        var category = (parsed?.Category ?? "").Trim().ToLowerInvariant();
        if (!ReportCategories.IsValid(category))
            return ApiError(400, "bad_category", "неизвестная категория жалобы");

        var reason = (parsed?.Reason ?? "").Trim();
        if (reason.Length > ReportStore.MaxReasonLength)
            return ApiError(400, "reason_too_long",
                $"причина — максимум {ReportStore.MaxReasonLength} символов");

        var target = _options.ReportPlayerLookup(targetText);
        if (target is null)
            return ApiError(404, "not_found", "никого с таким ID сервер не помнит");

        if (target.Id == session.PlayerId)
            return ApiError(400, "self_report", "на себя жаловаться нельзя");

        var result = _options.Reports.Add(
            target.Id,
            target.Nick,
            target.IsAdmin,
            target.Devices,
            session.PlayerId,
            session.Nick,
            category,
            reason,
            DateTime.UtcNow);

        switch (result.Error)
        {
            case ReportSubmitError.Self:
                return ApiError(400, "self_report", "на себя жаловаться нельзя");
            case ReportSubmitError.Duplicate:
                return ApiErrorRetry(409, "already_reported",
                    "ты уже жаловался на него за последние 24 часа", result.RetryAfterSec);
            case ReportSubmitError.DailyLimit:
                return ApiErrorRetry(429, "daily_limit",
                    $"не больше {ReportStore.DailyLimit} жалоб в сутки", result.RetryAfterSec);
            case ReportSubmitError.Cooldown:
                return ApiErrorRetry(429, "cooldown",
                    $"подожди {result.RetryAfterSec} с перед следующей жалобой",
                    result.RetryAfterSec);
            case ReportSubmitError.Storage:
                return ApiError(503, "reports_storage",
                    "не удалось сохранить жалобу — попробуй позже");
        }

        var record = result.Record!;
        var counts = _options.Reports.CountsFor(
            target.Id, target.Devices, DateTime.UtcNow);

        Console.WriteLine(
            $"[report] '{session.Nick}' (ID {session.PlayerId}) пожаловался на " +
            $"'{target.Nick}' (ID {target.Id}) — {ReportCategories.Label(category)}" +
            (reason.Length > 0 ? $": {reason}" : ""));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("report");
            WriteReport(writer, record);
            WriteCounts(writer, counts);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Мои жалобы и их статусы — чтобы автор видел, что с ними стало.</summary>
    private WebResponse MyReports(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        if (BannedRefusal(session) is { } banned)
            return banned;

        if (_options.Reports is null)
            return ApiError(503, "reports_disabled", "жалобы не настроены на этом сервере");

        var page = QueryInt(request.Query, "page", 1);
        var data = _options.Reports.ListByReporter(session.PlayerId, page, ReportsPageSize);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            WritePage(writer, data);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── модерация (тройной затвор) ───────────────────────────────────────────

    /// <summary>Страница жалоб для админа с подтверждённым кодом этого запуска.</summary>
    private WebResponse AdminReports(WebRequestContext request)
    {
        if (AdminGate(request, out _, out _) is { } denied)
            return denied;

        if (_options.Reports is null)
            return ApiError(503, "reports_disabled", "жалобы не настроены на этом сервере");

        var statusText = QueryValue(request.Query, "status");
        string? status;
        if (string.IsNullOrWhiteSpace(statusText) || statusText == ReportStatuses.Open)
            status = ReportStatuses.Open;
        else if (statusText == "all")
            status = null;
        else if (ReportStatuses.IsValid(statusText))
            status = statusText;
        else
            return ApiError(400, "bad_status",
                "статус — open, accepted, rejected или all");

        var page = QueryInt(request.Query, "page", 1);
        var data = _options.Reports.List(status, page, ReportsPageSize);
        var summary = _options.Reports.Summary();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("status", status ?? "all");
            writer.WriteStartObject("summary");
            writer.WriteNumber("open", summary.Open);
            writer.WriteNumber("accepted", summary.Accepted);
            writer.WriteNumber("rejected", summary.Rejected);
            writer.WriteEndObject();
            WritePage(writer, data);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Принять или отклонить жалобу. Возвращает обновлённую запись и счётчики.</summary>
    private WebResponse AdminReportStatus(WebRequestContext request)
    {
        if (AdminGate(request, out var session, out _) is { } denied)
            return denied;

        if (_options.Reports is null)
            return ApiError(503, "reports_disabled", "жалобы не настроены на этом сервере");

        AdminReportStatusRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminReportStatusRequest>(
                request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var id = (parsed?.Id ?? "").Trim();
        var status = (parsed?.Status ?? "").Trim().ToLowerInvariant();
        if (id.Length == 0)
            return ApiError(400, "bad_report", "не указана жалоба");
        if (status is not (ReportStatuses.Accepted or ReportStatuses.Rejected))
            return ApiError(400, "bad_status", "статус — accepted или rejected");

        var (error, record) = _options.Reports.SetStatus(
            id, status, session.Nick, parsed?.Note, DateTime.UtcNow);
        if (error is not null || record is null)
            return ApiError(400, "report_not_updated", error ?? "жалоба не обновлена");

        Console.WriteLine(
            $"[report] '{session.Nick}' (ID {session.PlayerId}) " +
            $"{(status == ReportStatuses.Accepted ? "принял" : "отклонил")} жалобу " +
            $"{record.Id} на '{record.TargetNick}' (ID {record.TargetId})");

        var summary = _options.Reports.Summary();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WritePropertyName("report");
            WriteReport(writer, record);
            writer.WriteStartObject("summary");
            writer.WriteNumber("open", summary.Open);
            writer.WriteNumber("accepted", summary.Accepted);
            writer.WriteNumber("rejected", summary.Rejected);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── JSON-мелочи ──────────────────────────────────────────────────────────

    private static void WriteReport(Utf8JsonWriter writer, ReportRecord record)
    {
        writer.WriteStartObject();
        writer.WriteString("id", record.Id);
        writer.WriteNumber("targetId", record.TargetId);
        writer.WriteString("targetNick", record.TargetNick);
        writer.WriteBoolean("targetAdmin", record.TargetAdmin);
        writer.WriteNumber("reporterId", record.ReporterId);
        writer.WriteString("reporterNick", record.ReporterNick);
        writer.WriteString("category", record.Category);
        writer.WriteString("categoryLabel", ReportCategories.Label(record.Category));
        writer.WriteString("reason", record.Reason);
        writer.WriteString("status", record.Status);
        writer.WriteString("statusLabel", record.Status switch
        {
            ReportStatuses.Accepted => "принята",
            ReportStatuses.Rejected => "отклонена",
            _ => "на рассмотрении",
        });
        writer.WriteString("created", FromMs(record.CreatedUtcMs).ToString("O"));
        if (record.ReviewedUtcMs is { } reviewed)
            writer.WriteString("reviewed", FromMs(reviewed).ToString("O"));
        else
            writer.WriteNull("reviewed");
        writer.WriteString("reviewedBy", record.ReviewedBy);
        writer.WriteString("adminNote", record.AdminNote);
        writer.WriteEndObject();
    }

    private static void WriteCounts(Utf8JsonWriter writer, ReportCounts counts)
    {
        writer.WriteStartObject("reports");
        writer.WriteNumber("total", counts.Total);
        writer.WriteNumber("open", counts.Open);
        writer.WriteNumber("accepted", counts.Accepted);
        writer.WriteNumber("windowDays", ReportStore.BadgeWindowDays);
        writer.WriteEndObject();
    }

    private static void WritePage(Utf8JsonWriter writer, ReportPage page)
    {
        writer.WriteNumber("page", page.Page);
        writer.WriteNumber("pageSize", page.PageSize);
        writer.WriteNumber("total", page.Total);
        writer.WriteNumber("pages", page.Pages);
        writer.WriteStartArray("items");
        foreach (var item in page.Items)
            WriteReport(writer, item);
        writer.WriteEndArray();
    }

    private static DateTime FromMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;

    /// <summary>Значение из query-строки с URL-декодом. Нет — null.</summary>
    private static string? QueryValue(string? query, string name)
    {
        if (string.IsNullOrEmpty(query))
            return null;

        foreach (var part in query.Split('&'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            if (!part[..eq].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var raw = part[(eq + 1)..].Trim();
            try
            {
                return Uri.UnescapeDataString(raw.Replace('+', ' '));
            }
            catch
            {
                return raw;
            }
        }

        return null;
    }

    /// <summary>Число из query, с дефолтом и защитой от нуля/мусора.</summary>
    private static int QueryInt(string? query, string name, int fallback)
    {
        var raw = QueryValue(query, name);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    /// <summary>Ошибка с Retry-After: клиент не должен долбить сервер сразу же.</summary>
    private static WebResponse ApiErrorRetry(
        int status, string code, string message, int retryAfterSec)
    {
        var response = ApiError(status, code, message);
        return new WebResponse(
            response.Status,
            response.ContentType,
            response.Body,
            Math.Max(1, retryAfterSec),
            response.ExtraHeaders);
    }

    private sealed record ReportSubmitRequest(string? Target, string? Category, string? Reason);

    private sealed record AdminReportStatusRequest(string? Id, string? Status, string? Note);
}
