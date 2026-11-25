using System.Globalization;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Reports;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Полная админ-консоль: дашборд, журнал, игроки, комнаты, конфиг, безопасность и
/// откат действий. Каждый обработчик начинается с затвора прав; записи в журнал
/// получают контекст актора и отметку «можно откатить».
/// </summary>
public sealed partial class WebPanelRouter
{
    private readonly AdminLoginLimiter _adminStepUpLimiter = new();

    /// <summary>Сколько действует подтверждение паролем для опасных действий.</summary>
    private static readonly TimeSpan StepUpWindow = TimeSpan.FromMinutes(15);

    /// <summary>Сколько хранится возможность отката.</summary>
    private static readonly TimeSpan RollbackWindow = TimeSpan.FromDays(30);

    // ── Подтверждение паролем ──────────────────────────────────────────────────

    /// <summary>Повторная проверка пароля: выдаёт сессии право на опасные действия.</summary>
    private WebResponse AdminStepUp(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        if (_options.AdminStore is null || _options.AdminSessionStore is null)
            return ApiError(503, "admin_disabled", "админ панель не настроена на этом сервере");

        // Тестовый режим: пароля нет, подтверждать нечего. Окно всё равно
        // выставляем, чтобы state и журнал выглядели как при обычном step-up.
        if (NoAdminPassword)
        {
            var testNow = DateTime.UtcNow;
            var testUntil = testNow.Add(StepUpWindow);
            if (!_options.AdminSessionStore.SetStepUp(
                    AdminCookie.ReadToken(request.Headers), testUntil))
                return ApiError(401, "no_admin_auth", "сессия уже недействительна — войди заново");

            _options.AdminStore.Journal(
                "security", "step_up", admin.PlayerId, admin.PlayerId,
                context: new AdminJournalContext(
                    admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                    "admin", admin.PlayerId, admin.Nick,
                    Note: $"тестовый режим: без пароля, до {testUntil:O}"));

            using var testStream = new MemoryStream();
            using (var testWriter = new Utf8JsonWriter(testStream, JsonText.WriterOptions))
            {
                testWriter.WriteStartObject();
                testWriter.WriteBoolean("ok", true);
                testWriter.WriteString("until", testUntil.ToString("O"));
                testWriter.WriteEndObject();
            }
            return WebResponse.Json(Encoding.UTF8.GetString(testStream.ToArray()));
        }

        AdminStepUpRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminStepUpRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Password))
            return ApiError(400, "empty_password", "введи свой личный пароль");

        var now = DateTime.UtcNow;
        if (!_adminStepUpLimiter.TryBegin(request.RemoteAddress, now))
        {
            var wait = _adminStepUpLimiter.RetryAfterSec(request.RemoteAddress, now);
            return ApiError(429, "too_many_attempts",
                $"слишком много попыток — подожди {wait} секунд");
        }

        var record = _options.AdminStore.Verify(admin.PlayerId, parsed.Password, now);
        if (record is null)
        {
            _adminStepUpLimiter.Fail(request.RemoteAddress, now);
            _options.AdminStore.Journal(
                "security", "step_up_failed", admin.PlayerId, admin.PlayerId,
                context: new AdminJournalContext(
                    admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                    "admin", admin.PlayerId, admin.Nick, Result: "fail",
                    Note: "неверный пароль при подтверждении"));
            return ApiError(401, "bad_credentials", "пароль не подходит");
        }

        _adminStepUpLimiter.Reset(request.RemoteAddress);
        var until = now.Add(StepUpWindow);
        if (!_options.AdminSessionStore.SetStepUp(
                AdminCookie.ReadToken(request.Headers), until))
            return ApiError(401, "no_admin_auth", "сессия уже недействительна — войди заново");

        _options.AdminStore.Journal(
            "security", "step_up", admin.PlayerId, admin.PlayerId,
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "admin", admin.PlayerId, admin.Nick,
                Note: $"до {until:O}"));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("until", until.ToString("O"));
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Мега-админ + свежее подтверждение пароля — только для опасных правок.</summary>
    private WebResponse? MegaStepUpGate(WebRequestContext request, out AdminSession admin)
    {
        if (MegaGate(request, out admin) is { } denied)
            return denied;

        // test.noAdminPassword убирает и это подтверждение: иначе после единого
        // затвора опасная правка всё равно упёрлась бы в step_up_required.
        if (NoAdminPassword)
            return null;

        if (admin.StepUpUntilUtc is not { } until || until <= DateTime.UtcNow)
            return ApiError(403, "step_up_required",
                "подтверди пароль заново: это действие меняет конфиг или админов");

        return null;
    }

    // ── Дашборд ────────────────────────────────────────────────────────────────

    /// <summary>Сводка для первого экрана консоли: онлайн, комнаты, баны, журнал.</summary>
    private WebResponse AdminDashboard(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        var now = DateTime.UtcNow;
        var started = _options.StartedUtc ?? now;
        var state = _options.AdminServerState?.Invoke();
        var store = _options.AdminStore;
        var records = store?.Records() ?? Array.Empty<AdminRecord>();
        var admins = records.Count(r => _options.IsAdminLive?.Invoke(r.Id) != false);
        var megas = records.Count(r =>
            store?.IsMega(r.Id) == true && _options.IsAdminLive?.Invoke(r.Id) != false);
        var bans = _options.ActiveBans?.Invoke().Count ?? 0;
        var reports = _options.Reports?.Summary() ?? new ReportSummary(0, 0, 0);
        var journal = store?.ReadJournal(new AdminJournalQuery { Limit = 8 });
        var failures = store?.ReadJournal(new AdminJournalQuery
        {
            Category = "security",
            Action = "login_failed",
            Limit = 100,
            FromUtc = now.AddHours(-24),
        });
        var sessions = _options.AdminSessionStore?.List(now) ?? Array.Empty<AdminSession>();
        var rooms = _rooms.Snapshot();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("now", now.ToString("O"));
            writer.WriteString("started", started.ToString("O"));
            writer.WriteNumber("uptimeSec", Math.Max(0, (long)(now - started).TotalSeconds));

            writer.WriteStartObject("server");
            writer.WriteNumber("lobbyOnline", state?.LobbyOnline ?? 0);
            writer.WriteNumber("matchOnline", state?.MatchOnline ?? 0);
            writer.WriteNumber("onlineTotal", (state?.LobbyOnline ?? 0) + (state?.MatchOnline ?? 0));
            writer.WriteNumber("rooms", state?.RoomsCount ?? rooms.Count);
            writer.WriteNumber("roomsMax", state?.RoomsMax ?? _rooms.MaxRooms);
            writer.WriteBoolean("matchRunning", state?.MatchRunning ?? false);
            writer.WriteBoolean("gameListening", state?.GameListening ?? false);
            writer.WriteBoolean("gamePortFailed", state?.GamePortFailed ?? false);
            writer.WriteString("mode", state?.Mode ?? "");
            writer.WriteStartArray("levels");
            foreach (var level in state?.Levels ?? Array.Empty<string>())
                writer.WriteStringValue(level);
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WriteStartObject("counts");
            writer.WriteNumber("admins", admins);
            writer.WriteNumber("megas", megas);
            writer.WriteNumber("bans", bans);
            writer.WriteNumber("reportsOpen", reports.Open);
            writer.WriteNumber("reportsAccepted", reports.Accepted);
            writer.WriteNumber("reportsRejected", reports.Rejected);
            writer.WriteNumber("playersKnown", _options.KnownPlayersCount?.Invoke() ?? 0);
            writer.WriteNumber("matches", _options.Matches?.Count ?? 0);
            writer.WriteNumber("staffSessions", sessions.Count);
            writer.WriteNumber("failedLogins24h", failures?.Entries.Count ?? 0);
            writer.WriteEndObject();

            writer.WriteStartObject("memory");
            writer.WriteNumber("managedBytes", GC.GetTotalMemory(false));
            writer.WriteNumber("workingSetBytes", Environment.WorkingSet);
            writer.WriteNumber("gen0", GC.CollectionCount(0));
            writer.WriteNumber("gen2", GC.CollectionCount(2));
            writer.WriteEndObject();

            writer.WriteStartObject("you");
            writer.WriteNumber("id", admin.PlayerId);
            writer.WriteString("nick", admin.Nick);
            writer.WriteBoolean("mega", store?.IsMega(admin.PlayerId) == true);
            writer.WriteBoolean("stepUp",
                admin.StepUpUntilUtc is { } until && until > now);
            writer.WriteEndObject();

            writer.WriteStartArray("journal");
            foreach (var entry in journal?.Entries ?? Array.Empty<AdminJournalEntry>())
                WriteJournalEntry(writer, entry, store?.IsMega(admin.PlayerId) == true, true);
            writer.WriteEndArray();

            writer.WriteStartArray("rooms");
            foreach (var room in rooms.OrderBy(r => r.Number).Take(6))
                WriteAdminRoom(writer, room, admin.PlayerId);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    // ── Журнал ─────────────────────────────────────────────────────────────────

    /// <summary>Страница журнала с фильтрами и курсором «новее/старее».</summary>
    private WebResponse AdminJournal(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Journal) is { } denied)
            return denied;

        var store = _options.AdminStore;
        if (store is null)
            return ApiError(503, "journal_disabled", "журнал не настроен на этом сервере");

        var limit = Math.Clamp(QueryInt(request.Query, "limit", 50), 1, 200);
        var query = new AdminJournalQuery
        {
            Limit = limit,
            BeforeId = QueryValue(request.Query, "before"),
            Category = QueryValue(request.Query, "category"),
            Action = QueryValue(request.Query, "action"),
            Search = QueryValue(request.Query, "search"),
            ReversibleOnly = QueryValue(request.Query, "reversible") is "1" or "true",
            ActorId = TryParseQueryInt(request.Query, "actor", out var actor) ? actor : null,
            TargetId = TryParseQueryInt(request.Query, "target", out var target) ? target : null,
            FromUtc = ParseQueryDate(request.Query, "from"),
            ToUtc = ParseQueryDate(request.Query, "to"),
            BeforeTsUtc = ParseQueryDate(request.Query, "beforeTs"),
        };

        var page = store.ReadJournal(query);
        var mega = store.IsMega(admin.PlayerId);
        var canRollback = store.HasPermission(admin.PlayerId, AdminPermissions.Rollback);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("mega", mega);
            writer.WriteBoolean("canRollback", canRollback);
            writer.WriteStartArray("entries");
            foreach (var entry in page.Entries)
                WriteJournalEntry(writer, entry, mega, canRollback);
            writer.WriteEndArray();
            if (page.NextId is { Length: > 0 })
            {
                writer.WriteString("nextId", page.NextId);
                if (page.NextTsUtc is { } nextTs)
                    writer.WriteString("nextTs", nextTs.ToString("O"));
            }
            writer.WriteBoolean("hasMore", page.HasMore);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Откатить обратное действие из журнала (только разрешённые типы).</summary>
    private WebResponse AdminJournalUndo(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Rollback) is { } denied)
            return denied;

        var store = _options.AdminStore;
        if (store is null)
            return ApiError(503, "journal_disabled", "журнал не настроен на этом сервере");

        AdminUndoRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminUndoRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var id = (parsed?.Id ?? "").Trim();
        if (id.Length == 0)
            return ApiError(400, "bad_journal_id", "не указана запись журнала");

        var entry = store.ReadJournalEntry(id);
        if (entry is null)
            return ApiError(404, "journal_not_found", $"записи {id} нет в журнале");
        if (entry.Reverted)
            return ApiError(409, "already_reverted",
                $"это действие уже отменено записью {entry.RevertedBy}");
        if (!entry.Reversible)
            return ApiError(409, "not_reversible", "это действие нельзя откатить");
        if (DateTime.UtcNow - entry.TsUtc > RollbackWindow)
            return ApiError(409, "too_old", "откат доступен только 30 дней");

        // Опасные категории откатывает только мега-админ, и только после подтверждения.
        if (entry.Category is "permission" or "admin" or "config")
        {
            if (MegaStepUpGate(request, out var mega) is { } megaDenied)
                return megaDenied;
            admin = mega;
            store = _options.AdminStore!;
        }

        var now = DateTime.UtcNow;
        var context = new AdminJournalContext(
            admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
            entry.TargetType, entry.TargetId, entry.TargetNick,
            Note: $"откат {entry.Id}", UndoOf: entry.Id);

        var result = entry.Category switch
        {
            "permission" => UndoPermissions(entry, admin.PlayerId, now, context),
            "admin" => UndoAdminIds(entry, admin.PlayerId, now, context),
            "config" => UndoConfig(entry, admin.PlayerId, now, context),
            "ban" => UndoBan(entry, admin.PlayerId, now, context),
            "report" => UndoReport(entry, admin.PlayerId, now, context),
            "customid" => UndoCustomId(entry, admin.PlayerId, now, context),
            _ => new UndoResult(false, "для этой категории откат не поддерживается"),
        };

        if (!result.Ok)
            return ApiError(result.Status, result.Code, result.Message);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("undoOf", entry.Id);
            writer.WriteString("message", result.Message);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private readonly record struct UndoResult(bool Ok, string Message, int Status = 400, string Code = "undo_refused");

    private UndoResult UndoPermissions(
        AdminJournalEntry entry, int actorId, DateTime now, AdminJournalContext context)
    {
        if (entry.TargetId is not { } targetId)
            return new(false, "в записи нет id админа");
        var store = _options.AdminStore;
        if (store is null)
            return new(false, "хранилище админов недоступно");
        if (store.IsMega(targetId))
            return new(false, "цель уже мега-админ — права откатывать нельзя", 409, "conflict");

        var record = store.Find(targetId);
        if (record is null)
            return new(false, "админа уже нет", 409, "conflict");

        var before = ReadStringArray(entry.Before);
        var after = ReadStringArray(entry.After);
        if (after is null || before is null)
            return new(false, "в записи нет прежних прав");
        if (!AdminPermissions.Normalize(record.Permissions).SequenceEqual(after))
            return new(false, "права уже менялись после этой записи — откат небезопасен", 409, "conflict");

        string? error = null;
        if (!store.SetPermissions(actorId, targetId, before, now, out error, context))
            return new(false, error ?? "права не откатились");
        return new(true, $"права #{targetId} возвращены к прежним");
    }

    private UndoResult UndoAdminIds(
        AdminJournalEntry entry, int actorId, DateTime now, AdminJournalContext context)
    {
        var snapshot = _options.AdminConfig?.Invoke();
        if (snapshot is null || _options.AdminSetIds is null)
            return new(false, "конфиг недоступен", 503, "config_disabled");

        var before = ReadIntArray(entry.Before, "ids");
        var after = ReadIntArray(entry.After, "ids");
        if (before is null || after is null)
            return new(false, "в записи нет списка админов");
        if (!snapshot.AdminIds.OrderBy(x => x).SequenceEqual(after.OrderBy(x => x)))
            return new(false, "admin.ids уже менялся после этой записи — откат небезопасен", 409, "conflict");

        var error = _options.AdminSetIds(before);
        if (error is not null)
            return new(false, error);

        foreach (var removed in snapshot.AdminIds.Except(before))
            _options.AdminSessionStore?.DropByPlayer(removed);
        _options.AdminStore?.Journal(
            "admin", entry.Action == "add" ? "remove" : "add", actorId,
            entry.TargetId, before: new { ids = after }, after: new { ids = before }, context);
        return new(true, "состав админов возвращён");
    }

    private UndoResult UndoConfig(
        AdminJournalEntry entry, int actorId, DateTime now, AdminJournalContext context)
    {
        var snapshot = _options.AdminConfig?.Invoke();
        if (snapshot is null || _options.AdminConfigSet is null)
            return new(false, "конфиг недоступен", 503, "config_disabled");

        var key = ReadString(entry.Before, "key") ?? ReadString(entry.After, "key");
        var before = ReadString(entry.Before, "value");
        var after = ReadString(entry.After, "value");
        if (key is null || before is null || after is null)
            return new(false, "в записи нет значения конфига");

        var option = snapshot.Options.FirstOrDefault(o =>
            string.Equals(o.Key, key, StringComparison.Ordinal));
        if (option is null)
            return new(false, "этот ключ больше не разрешён в панели", 409, "conflict");
        if (!string.Equals(option.Value, after, StringComparison.Ordinal))
            return new(false, "значение конфига уже менялось после этой записи", 409, "conflict");

        var error = _options.AdminConfigSet(option.Key, option.Kind, before);
        if (error is not null)
            return new(false, error);
        _options.AdminStore?.Journal(
            "config", "set", actorId, null,
            before: new { key, value = after }, after: new { key, value = before }, context);
        return new(true, $"{key} возвращён к {before}");
    }

    private UndoResult UndoBan(
        AdminJournalEntry entry, int actorId, DateTime now, AdminJournalContext context)
    {
        if (entry.TargetId is not { } targetId)
            return new(false, "в записи нет id игрока");
        if (_options.WebUnban is null || _options.ActiveBans is null)
            return new(false, "баны не настроены", 503, "bans_disabled");

        var banId = ReadString(entry.After, "banId");
        if (banId is null)
            return new(false, "в записи нет номера бана");
        var active = _options.ActiveBans().Where(b =>
            string.Equals(b.BanId, banId, StringComparison.Ordinal)).ToList();
        if (active.Count == 0)
            return new(false, "этот бан уже снят или истёк", 409, "conflict");
        var allForTarget = _options.ActiveBans().Count(b => b.MatchIds.Contains(targetId));
        if (allForTarget > active.Count)
            return new(false, "у игрока есть другой активный бан — сними его вручную", 409, "conflict");

        var (error, removed) = _options.WebUnban(targetId);
        if (error is not null)
            return new(false, error);
        _options.AdminStore?.Journal(
            "ban", "unban", actorId, targetId,
            before: new { banId, removed }, after: new { banId, removed = 0 }, context);
        return new(true, $"бан {banId} снят ({removed})");
    }

    private UndoResult UndoReport(
        AdminJournalEntry entry, int actorId, DateTime now, AdminJournalContext context)
    {
        var reportId = ReadString(entry.After, "reportId");
        var before = ReadString(entry.Before, "status");
        var after = ReadString(entry.After, "status");
        if (reportId is null || before is null || after is null)
            return new(false, "в записи нет статуса жалобы");
        if (_options.Reports is null)
            return new(false, "жалобы не настроены", 503, "reports_disabled");

        var current = _options.Reports.Export()
            .FirstOrDefault(r => string.Equals(r.Id, reportId, StringComparison.Ordinal));
        if (current is null)
            return new(false, "жалоба не найдена", 404, "not_found");
        if (!string.Equals(current.Status, after, StringComparison.Ordinal))
            return new(false, "статус жалобы уже менялся после этой записи", 409, "conflict");

        var (error, _) = _options.Reports.SetStatus(
            reportId, before, "откат панели", $"возврат к {before}", now);
        if (error is not null)
            return new(false, error);
        _options.AdminStore?.Journal(
            "report", "status", actorId, entry.TargetId,
            before: new { reportId, status = after }, after: new { reportId, status = before }, context);
        return new(true, $"жалоба {reportId} возвращена в «{before}»");
    }

    private UndoResult UndoCustomId(
        AdminJournalEntry entry, int actorId, DateTime now, AdminJournalContext context)
    {
        if (entry.TargetId is not { } targetId)
            return new(false, "в записи нет id");
        if (_options.CustomIdSet is null || _options.CustomIdGet is null)
            return new(false, "кастомизация не настроена", 503, "customid_disabled");
        if (_options.IsAdminLive?.Invoke(targetId) != true)
            return new(false, "игрок уже не админ — кастомный ID откатывать некуда", 409, "conflict");

        var before = ReadString(entry.Before, "value") ?? "";
        var after = ReadString(entry.After, "value") ?? "";
        if (!string.Equals(_options.CustomIdGet(targetId) ?? "", after, StringComparison.Ordinal))
            return new(false, "кастомный ID уже менялся после этой записи", 409, "conflict");

        var error = _options.CustomIdSet(targetId, before);
        if (error is not null)
            return new(false, error);
        _options.AdminStore?.Journal(
            "customid", "set", actorId, targetId,
            before: new { value = after }, after: new { value = before }, context);
        return new(true, "кастомный ID возвращён");
    }

    // ── Игроки ─────────────────────────────────────────────────────────────────

    /// <summary>Поиск по id, нику, IP и устройству — онлайн и офлайн.</summary>
    private WebResponse AdminPlayers(WebRequestContext request)
    {
        if (AdminGateAny(request, out var admin,
                AdminPermissions.Players, AdminPermissions.Bans) is { } denied)
            return denied;

        if (_options.AdminSearchPlayers is null)
            return ApiError(503, "players_disabled", "поиск игроков не настроен на этом сервере");

        var query = (QueryValue(request.Query, "q") ?? "").Trim();
        if (query.Length > 80)
            return ApiError(400, "query_too_long", "слишком длинный запрос");
        var limit = Math.Clamp(QueryInt(request.Query, "limit", 30), 1, 100);
        var rows = _options.AdminSearchPlayers(query, limit);
        var mega = _options.AdminStore?.IsMega(admin.PlayerId) == true;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("query", query);
            writer.WriteBoolean("fullPrivacy", mega);
            writer.WriteStartArray("players");
            foreach (var row in rows)
                WriteAdminPlayer(writer, row, mega);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Кик из лобби и катки. Себя и мега-админа кикнуть нельзя.</summary>
    private WebResponse AdminKick(WebRequestContext request)
    {
        if (AdminGateAny(request, out var admin,
                AdminPermissions.Players, AdminPermissions.Bans) is { } denied)
            return denied;

        AdminKickRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminKickRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");
        if (parsed.Id == admin.PlayerId)
            return ApiError(400, "self_kick", "себя кикнуть нельзя");
        if (_options.AdminStore?.IsMega(parsed.Id) == true)
            return ApiError(403, "target_mega", "мега-админа кикнуть нельзя");
        if (_options.AdminKickPlayer is null)
            return ApiError(503, "players_disabled", "кик не настроен на этом сервере");

        var reason = CleanReason(parsed.Reason);
        var error = _options.AdminKickPlayer(parsed.Id, reason);
        if (error is not null)
            return ApiError(404, "kick_refused", error);

        var nick = _options.AdminLookup?.Invoke(parsed.Id)?.Nick ?? "";
        _options.AdminStore?.Journal(
            "player", "kick", admin.PlayerId, parsed.Id,
            after: new { reason },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "player", parsed.Id, nick, Note: reason));
        return WebResponse.Json("{\"ok\":true}");
    }

    // ── Комнаты ────────────────────────────────────────────────────────────────

    /// <summary>Все комнаты с живым составом и счётом.</summary>
    private WebResponse AdminRooms(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Rooms) is { } denied)
            return denied;

        var rooms = _rooms.Snapshot().OrderBy(r => r.Number).ToList();
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("count", rooms.Count);
            writer.WriteNumber("max", _rooms.MaxRooms);
            writer.WriteStartArray("rooms");
            foreach (var room in rooms)
                WriteAdminRoom(writer, room, admin.PlayerId);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Принудительно закрыть комнату: завершить катку и освободить номер.</summary>
    private WebResponse AdminRoomClose(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Rooms) is { } denied)
            return denied;

        AdminRoomCloseRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminRoomCloseRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Number <= 0)
            return ApiError(400, "bad_room", "номер комнаты — число больше нуля");
        if (_options.AdminCloseRoom is null)
            return ApiError(503, "rooms_disabled", "закрытие комнат не настроено на этом сервере");

        var room = _rooms.Snapshot().FirstOrDefault(r => r.Number == parsed.Number);
        if (room is null)
            return ApiError(404, "room_not_found", $"комнаты #{parsed.Number} уже нет");

        var reason = CleanReason(parsed.Reason);
        var error = _options.AdminCloseRoom(parsed.Number, reason);
        if (error is not null)
            return ApiError(409, "room_close_refused", error);

        _options.AdminStore?.Journal(
            "room", "close", admin.PlayerId, parsed.Number,
            after: new { number = parsed.Number, name = room.Name, reason },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "room", parsed.Number, room.Name, Note: reason));
        return WebResponse.Json("{\"ok\":true}");
    }

    // ── Конфиг ─────────────────────────────────────────────────────────────────

    /// <summary>Безопасная часть конфига: только разрешённые ключи и списки админов.</summary>
    private WebResponse AdminConfigView(WebRequestContext request)
    {
        if (AdminGate(request, out _, AdminPermissions.Config) is { } denied)
            return denied;

        var snapshot = _options.AdminConfig?.Invoke();
        if (snapshot is null)
            return ApiError(503, "config_disabled", "просмотр конфига не настроен на этом сервере");

        return AdminConfigJson(snapshot);
    }

    /// <summary>Изменить разрешённый ключ. Только мега-админ, только после подтверждения.</summary>
    private WebResponse AdminConfigSet(WebRequestContext request)
    {
        if (MegaStepUpGate(request, out var admin) is { } denied)
            return denied;
        if (_options.AdminStore?.HasPermission(admin.PlayerId, AdminPermissions.Config) != true)
            return ApiError(403, "no_permission", "нет права «config»");

        AdminConfigSetRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminConfigSetRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var key = (parsed?.Key ?? "").Trim();
        if (key.Length == 0)
            return ApiError(400, "bad_key", "не указан ключ конфига");

        var snapshot = _options.AdminConfig?.Invoke();
        if (snapshot is null || _options.AdminConfigSet is null)
            return ApiError(503, "config_disabled", "редактор конфига не настроен на этом сервере");

        var option = snapshot.Options.FirstOrDefault(o =>
            string.Equals(o.Key, key, StringComparison.Ordinal));
        if (option is null)
            return ApiError(400, "bad_key", "этот ключ не разрешён в панели");

        if (!TryReadConfigValue(parsed?.Value ?? default, option, out var value, out var bad))
            return ApiError(400, "bad_value", bad);

        var error = _options.AdminConfigSet(option.Key, option.Kind, value);
        if (error is not null)
            return ApiError(400, "config_refused", error);

        _options.AdminStore?.Journal(
            "config", "set", admin.PlayerId, null,
            before: new { key, value = option.Value },
            after: new { key, value },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "config", null, key, Reversible: true));

        var fresh = _options.AdminConfig?.Invoke() ?? snapshot;
        return AdminConfigJson(fresh);
    }

    /// <summary>Перечитать config.json немедленно. Только мега-админ.</summary>
    private WebResponse AdminReloadConfig(WebRequestContext request)
    {
        if (MegaGate(request, out var admin) is { } denied)
            return denied;
        if (_options.AdminReloadConfig is null)
            return ApiError(503, "config_disabled", "перечитывание конфига не настроено");

        var error = _options.AdminReloadConfig();
        if (error is not null)
            return ApiError(400, "config_reload_refused", error);

        _options.AdminStore?.Journal(
            "config", "reload", admin.PlayerId, null,
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "config", null, "config.json"));
        return WebResponse.Json("{\"ok\":true}");
    }

    // ── Состав админов ─────────────────────────────────────────────────────────

    /// <summary>Добавить обычного админа в config.json. Только мега-админ.</summary>
    private WebResponse AdminAdd(WebRequestContext request)
    {
        if (MegaStepUpGate(request, out var admin) is { } denied)
            return denied;

        AdminIdRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminIdRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null
            || parsed.Id < StandChillow.LanServer.Config.AdminConfig.MinId
            || parsed.Id > StandChillow.LanServer.Config.AdminConfig.MaxId)
            return ApiError(400, "bad_id", "id вне допустимого диапазона");

        var snapshot = _options.AdminConfig?.Invoke();
        if (snapshot is null || _options.AdminSetIds is null)
            return ApiError(503, "config_disabled", "конфиг недоступен");

        var store = _options.AdminStore;
        if (store?.IsMega(parsed.Id) == true || snapshot.MegaIds.Contains(parsed.Id))
            return ApiError(400, "already_mega", "этот id уже мега-админ — он всегда админ");
        if (store?.Find(parsed.Id) is not null && _options.IsAdminLive?.Invoke(parsed.Id) == true)
            return ApiError(409, "already_admin", "этот id уже админ");

        var found = _options.AdminLookup?.Invoke(parsed.Id);
        if (_options.AdminLookup is not null && found is null)
            return ApiError(404, "unknown_player",
                $"сервер не помнит игрока #{parsed.Id} — проверь id, опечатка добавит чужого");

        var ids = snapshot.AdminIds.Concat(new[] { parsed.Id }).Distinct().OrderBy(x => x).ToList();
        var error = _options.AdminSetIds(ids);
        if (error is not null)
            return ApiError(400, "config_refused", error);

        // Пароль новому админу создаёт Sync; подсказываем, где он лежит.
        var passwordPath = store?.PasswordPathOf(parsed.Id) ?? $"auth/{parsed.Id}/password.txt";
        var nick = found?.Nick ?? store?.Find(parsed.Id)?.Nick ?? "";
        _options.AdminStore?.Journal(
            "admin", "add", admin.PlayerId, parsed.Id,
            before: new { ids = snapshot.AdminIds },
            after: new { ids },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "admin", parsed.Id, nick, Reversible: true,
                Note: $"пароль: {passwordPath}"));

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("id", parsed.Id);
            writer.WriteString("nick", nick);
            writer.WriteString("passwordPath", passwordPath);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Убрать обычного админа из config.json. Мега-админов не трогаем.</summary>
    private WebResponse AdminRemove(WebRequestContext request)
    {
        if (MegaStepUpGate(request, out var admin) is { } denied)
            return denied;

        AdminIdRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminIdRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");
        if (parsed.Id == admin.PlayerId)
            return ApiError(400, "self_remove", "себя убрать нельзя — сними права или смени пароль");
        if (_options.AdminStore?.IsMega(parsed.Id) == true)
            return ApiError(400, "target_mega",
                "мега-админ задаётся только в config.json → admin.megaIds");

        var snapshot = _options.AdminConfig?.Invoke();
        if (snapshot is null || _options.AdminSetIds is null)
            return ApiError(503, "config_disabled", "конфиг недоступен");
        if (!snapshot.AdminIds.Contains(parsed.Id))
            return ApiError(404, "not_admin", "этого id нет в admin.ids");

        var ids = snapshot.AdminIds.Where(x => x != parsed.Id).OrderBy(x => x).ToList();
        if (ids.Count == 0 && snapshot.MegaIds.Count == 0)
            return ApiError(409, "last_admin", "нельзя оставить сервер без админов");

        var nick = _options.AdminLookup?.Invoke(parsed.Id)?.Nick
                   ?? _options.AdminStore?.Find(parsed.Id)?.Nick ?? "";
        var error = _options.AdminSetIds(ids);
        if (error is not null)
            return ApiError(400, "config_refused", error);

        _options.AdminSessionStore?.DropByPlayer(parsed.Id);
        _options.AdminStore?.Journal(
            "admin", "remove", admin.PlayerId, parsed.Id,
            before: new { ids = snapshot.AdminIds },
            after: new { ids },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "admin", parsed.Id, nick, Reversible: true));
        return WebResponse.Json("{\"ok\":true}");
    }

    /// <summary>Новый личный пароль. Старые сессии цели умирают сразу.</summary>
    private WebResponse AdminRegeneratePassword(WebRequestContext request)
    {
        if (MegaStepUpGate(request, out var admin) is { } denied)
            return denied;

        AdminIdRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminIdRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || parsed.Id <= 0)
            return ApiError(400, "bad_id", "id — число больше нуля");

        var store = _options.AdminStore;
        if (store?.Find(parsed.Id) is null || _options.IsAdminLive?.Invoke(parsed.Id) != true)
            return ApiError(404, "not_admin", "этого id нет среди живых админов");

        if (!store.RegeneratePassword(
                admin.PlayerId, parsed.Id, DateTime.UtcNow, out var path, out var error,
                new AdminJournalContext(
                    admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                    "admin", parsed.Id, store.Find(parsed.Id)?.Nick ?? "")))
            return ApiError(400, "password_refused", error ?? "пароль не изменён");

        _options.AdminSessionStore?.DropByPlayer(parsed.Id);

        var password = "";
        try
        {
            password = File.ReadLines(path).FirstOrDefault()?.Trim() ?? "";
        }
        catch
        {
            // Файл только что записан; если прочитать не удалось — отдаём путь.
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("id", parsed.Id);
            writer.WriteString("passwordPath", path);
            writer.WriteString("password", password);
            writer.WriteEndObject();
        }

        return new WebResponse(
            200, "application/json; charset=utf-8",
            Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(stream.ToArray())),
            0,
            new List<(string, string)> { ("Cache-Control", "no-store") });
    }

    // ── Безопасность и сессии ───────────────────────────────────────────────────

    /// <summary>Живые сессии и последние неудачные входы. Свои — всегда, чужие — мега.</summary>
    private WebResponse AdminSecurity(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        if (_options.AdminSessionStore is null || _options.AdminStore is null)
            return ApiError(503, "admin_disabled", "сессии не настроены на этом сервере");

        var now = DateTime.UtcNow;
        var mega = _options.AdminStore.IsMega(admin.PlayerId);
        var all = _options.AdminSessionStore.List(now);
        var own = all.Count(s => s.PlayerId == admin.PlayerId);
        var visible = mega ? all : all.Where(s => s.PlayerId == admin.PlayerId).ToList();
        var failures = _options.AdminStore.ReadJournal(new AdminJournalQuery
        {
            Category = "security",
            Action = "login_failed",
            Limit = 20,
            FromUtc = now.AddDays(-7),
        });

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("mega", mega);
            writer.WriteNumber("activeSessions", all.Count);
            writer.WriteNumber("activeAdmins", all.Select(s => s.PlayerId).Distinct().Count());
            writer.WriteNumber("ownSessions", own);
            writer.WriteNumber("hiddenSessions", all.Count - visible.Count);
            writer.WriteString("stepUpUntil",
                admin.StepUpUntilUtc is { } until && until > now ? until.ToString("O") : "");
            writer.WriteStartArray("sessions");
            foreach (var session in visible.OrderByDescending(s => s.IssuedUtc))
            {
                var isOwn = session.PlayerId == admin.PlayerId;
                writer.WriteStartObject();
                writer.WriteString("sessionId", session.SessionId);
                writer.WriteNumber("id", session.PlayerId);
                writer.WriteString("nick", session.Nick);
                writer.WriteString("issued", session.IssuedUtc.ToString("O"));
                writer.WriteString("expires", session.ExpiresUtc.ToString("O"));
                writer.WriteString("ip", mega || isOwn ? session.Ip : MaskIp(session.Ip));
                writer.WriteString("userAgent", mega || isOwn ? session.UserAgent : "");
                writer.WriteBoolean("current",
                    string.Equals(session.SessionId, admin.SessionId, StringComparison.Ordinal));
                writer.WriteBoolean("stepUp",
                    session.StepUpUntilUtc is { } su && su > now);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("failures");
            foreach (var entry in failures.Entries)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.Id);
                writer.WriteString("ts", entry.TsUtc.ToString("O"));
                if (entry.ActorId is { } actorId)
                    writer.WriteNumber("attemptedId", actorId);
                writer.WriteString("ip", mega ? entry.ActorIp : MaskIp(entry.ActorIp));
                writer.WriteString("note", entry.Note ?? "");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Снять чужую (или свою другую) сессию. Текущую снимает только выход.</summary>
    private WebResponse AdminSessionRevoke(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        if (_options.AdminSessionStore is null)
            return ApiError(503, "admin_disabled", "сессии не настроены на этом сервере");

        AdminSessionRevokeRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminSessionRevokeRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var sessionId = (parsed?.SessionId ?? "").Trim();
        if (sessionId.Length == 0)
            return ApiError(400, "bad_session", "не указана сессия");

        var now = DateTime.UtcNow;
        var target = _options.AdminSessionStore.List(now)
            .FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.Ordinal));
        if (target is null)
            return ApiError(404, "session_not_found", "сессии уже нет");
        if (string.Equals(target.SessionId, admin.SessionId, StringComparison.Ordinal))
            return ApiError(400, "current_session", "это текущая сессия — используй кнопку «Выйти»");

        var mega = _options.AdminStore?.IsMega(admin.PlayerId) == true;
        if (!mega && target.PlayerId != admin.PlayerId)
            return ApiError(403, "not_mega", "чужие сессии снимает только мега-админ");

        if (!_options.AdminSessionStore.DropBySessionId(sessionId, mega ? null : admin.PlayerId))
            return ApiError(404, "session_not_found", "сессия уже закрыта");

        _options.AdminStore?.Journal(
            "security", "session_revoke", admin.PlayerId, target.PlayerId,
            before: new { sessionId }, after: new { revoked = true },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "admin", target.PlayerId, target.Nick,
                Note: $"сессия {sessionId}"));
        return WebResponse.Json("{\"ok\":true}");
    }

    // ── JSON-мелочи ────────────────────────────────────────────────────────────

    private static WebResponse AdminConfigJson(AdminConfigSnapshot snapshot)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("path", snapshot.Path);
            if (snapshot.LastWriteUtc is { } write)
                writer.WriteString("lastWrite", write.ToString("O"));
            writer.WriteStartArray("options");
            foreach (var option in snapshot.Options)
            {
                writer.WriteStartObject();
                writer.WriteString("key", option.Key);
                writer.WriteString("label", option.Label);
                writer.WriteString("section", option.Section);
                writer.WriteString("type", option.Kind switch
                {
                    AdminConfigValueKind.Bool => "bool",
                    AdminConfigValueKind.Int => "int",
                    _ => "string",
                });
                writer.WriteString("value", option.Value);
                if (option.Min is { } min)
                    writer.WriteNumber("min", min);
                if (option.Max is { } max)
                    writer.WriteNumber("max", max);
                if (option.Choices is { Count: > 0 })
                {
                    writer.WriteStartArray("choices");
                    foreach (var choice in option.Choices)
                        writer.WriteStringValue(choice);
                    writer.WriteEndArray();
                }
                if (!string.IsNullOrEmpty(option.Help))
                    writer.WriteString("help", option.Help);
                writer.WriteBoolean("restart", option.RestartRequired);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteStartArray("adminIds");
            foreach (var id in snapshot.AdminIds)
                writer.WriteNumberValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray("megaIds");
            foreach (var id in snapshot.MegaIds)
                writer.WriteNumberValue(id);
            writer.WriteEndArray();
            writer.WriteStartArray("issues");
            foreach (var issue in snapshot.Issues)
                writer.WriteStringValue(issue);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private void WriteJournalEntry(
        Utf8JsonWriter writer, AdminJournalEntry entry, bool mega, bool canRollback)
    {
        writer.WriteStartObject();
        writer.WriteString("id", entry.Id);
        writer.WriteString("ts", entry.TsUtc.ToString("O"));
        writer.WriteString("category", entry.Category);
        writer.WriteString("action", entry.Action);
        if (entry.ActorId is { } actor)
            writer.WriteNumber("actorId", actor);
        writer.WriteString("actorNick", entry.ActorNick);
        writer.WriteString("actorIp", mega ? entry.ActorIp : MaskIp(entry.ActorIp));
        writer.WriteString("targetType", entry.TargetType);
        if (entry.TargetId is { } target)
            writer.WriteNumber("targetId", target);
        writer.WriteString("targetNick", entry.TargetNick);
        writer.WriteString("result", entry.Result);
        writer.WriteBoolean("reversible", entry.Reversible);
        writer.WriteBoolean("reverted", entry.Reverted);
        if (entry.RevertedBy is { Length: > 0 } by)
            writer.WriteString("revertedBy", by);
        if (entry.UndoOf is { Length: > 0 } undo)
            writer.WriteString("undoOf", undo);
        if (entry.Note is { Length: > 0 } note)
            writer.WriteString("note", note);
        if (entry.Before is { } before)
        {
            writer.WritePropertyName("before");
            before.WriteTo(writer);
        }
        if (entry.After is { } after)
        {
            writer.WritePropertyName("after");
            after.WriteTo(writer);
        }
        writer.WriteBoolean("canUndo", canRollback
            && entry.Reversible
            && !entry.Reverted
            && DateTime.UtcNow - entry.TsUtc <= RollbackWindow
            && (mega || entry.Category is not ("permission" or "admin" or "config")));
        writer.WriteEndObject();
    }

    private static void WriteAdminPlayer(
        Utf8JsonWriter writer, AdminPlayerRow row, bool fullPrivacy)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", row.Id);
        writer.WriteString("nick", row.Nick);
        writer.WriteString("status", row.Status);
        writer.WriteString("statusLabel", row.StatusLabel);
        writer.WriteBoolean("banned", row.Banned);
        if (!string.IsNullOrEmpty(row.CustomId))
            writer.WriteString("customId", row.CustomId);
        if (row.LastSeenUtc is { } seen)
            writer.WriteString("lastSeen", seen.ToString("O"));
        if (row.CreatedUtc is { } created)
            writer.WriteString("created", created.ToString("O"));
        writer.WriteBoolean("admin", row.Admin);
        writer.WriteBoolean("mega", row.Mega);
        if (!string.IsNullOrEmpty(row.Note))
            writer.WriteString("note", row.Note);
        writer.WriteStartArray("ips");
        foreach (var ip in row.Ips)
            writer.WriteStringValue(fullPrivacy ? ip : MaskIp(ip));
        writer.WriteEndArray();
        writer.WriteNumber("devices", row.Devices.Count);
        if (fullPrivacy)
        {
            writer.WriteStartArray("deviceKeys");
            foreach (var device in row.Devices)
                writer.WriteStringValue(device);
            writer.WriteEndArray();
        }
        writer.WriteStartArray("aliases");
        foreach (var alias in row.Aliases)
            writer.WriteStringValue(alias);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private void WriteAdminRoom(Utf8JsonWriter writer, RoomDescriptor room, int viewerId)
    {
        writer.WriteStartObject();
        writer.WriteNumber("number", room.Number);
        writer.WriteString("name", room.Name);
        writer.WriteString("mode", room.ModeId);
        writer.WriteString("modeName", DisplayModeName(room.ModeId));
        writer.WriteString("level", room.Level);
        writer.WriteNumber("ownerId", room.OwnerId);
        writer.WriteString("ownerNick", room.OwnerNick);
        writer.WriteString("state", room.State.ToString().ToLowerInvariant());
        writer.WriteNumber("players", room.Players);
        writer.WriteNumber("perTeam", room.PerTeam);
        writer.WriteNumber("maxPlayers", RoomCapacity.TotalFor(room.ModeId, room.PerTeam));
        writer.WriteNumber("scoreCt", room.ScoreCt);
        writer.WriteNumber("scoreTr", room.ScoreTr);
        writer.WriteNumber("round", room.Round);
        writer.WriteString("created", room.CreatedUtc.ToString("O"));
        writer.WriteString("joinCommand", FormatJoinCommand(room));
        writer.WriteBoolean("isMine", room.OwnerId == viewerId);
        writer.WriteStartArray("live");
        foreach (var player in room.LivePlayers ?? Array.Empty<RoomLivePlayer>())
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", player.Id);
            writer.WriteString("nick", player.Nick);
            writer.WriteString("team", player.Team);
            writer.WriteNumber("kills", player.Kills);
            writer.WriteNumber("assists", player.Assists);
            writer.WriteNumber("deaths", player.Deaths);
            writer.WriteNumber("score", player.Score);
            writer.WriteNumber("ping", player.Ping);
            writer.WriteBoolean("left", player.Left);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static bool TryReadConfigValue(
        JsonElement value, AdminConfigOption option, out string raw, out string error)
    {
        raw = "";
        error = "";
        switch (option.Kind)
        {
            case AdminConfigValueKind.Bool:
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    error = "ожидается true или false";
                    return false;
                }
                raw = value.GetBoolean() ? "true" : "false";
                return true;

            case AdminConfigValueKind.Int:
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
                {
                    error = "ожидается целое число";
                    return false;
                }
                if (option.Min is { } min && number < min)
                {
                    error = $"минимум {min}";
                    return false;
                }
                if (option.Max is { } max && number > max)
                {
                    error = $"максимум {max}";
                    return false;
                }
                raw = number.ToString(CultureInfo.InvariantCulture);
                return true;

            default:
                if (value.ValueKind != JsonValueKind.String)
                {
                    error = "ожидается строка";
                    return false;
                }
                var text = (value.GetString() ?? "").Trim();
                if (text.Length > 220)
                {
                    error = "слишком длинное значение";
                    return false;
                }
                if (text.Any(ch => char.IsControl(ch) && ch is not '\t'))
                {
                    error = "управляющие символы не разрешены";
                    return false;
                }
                if (option.Choices is { Count: > 0 } choices)
                {
                    var match = choices.FirstOrDefault(c =>
                        string.Equals(c, text, StringComparison.OrdinalIgnoreCase));
                    if (match is null)
                    {
                        error = "допустимые значения: " + string.Join(", ", choices);
                        return false;
                    }
                    text = match;
                }
                raw = text;
                return true;
        }
    }

    private static string CleanReason(string? reason)
    {
        var value = (reason ?? "").Trim();
        if (value.Length > 200)
            value = value[..200].Trim();
        return value;
    }

    private static string MaskIp(string ip)
    {
        var value = (ip ?? "").Trim();
        if (value.Length == 0)
            return "";
        if (value.Contains(':'))
        {
            var colon = value.IndexOf(':');
            return colon <= 0 ? "…" : value[..colon] + ":…";
        }
        var parts = value.Split('.');
        return parts.Length == 4 ? $"{parts[0]}.{parts[1]}.•.•" : "•";
    }

    private static int? ReadInt(JsonElement? node, string name)
    {
        if (node is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(name, out var value))
            return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static string? ReadString(JsonElement? node, string name)
    {
        if (node is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String)
            return null;
        return value.GetString();
    }

    private static List<string>? ReadStringArray(JsonElement? node)
    {
        if (node is not { ValueKind: JsonValueKind.Array } array)
            return null;
        var list = new List<string>();
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                return null;
            list.Add(item.GetString() ?? "");
        }
        return list;
    }

    private static List<int>? ReadIntArray(JsonElement? node, string name)
    {
        if (node is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Array)
            return null;
        var list = new List<int>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var number))
                return null;
            list.Add(number);
        }
        return list;
    }

    private static bool TryParseQueryInt(string? query, string name, out int value)
    {
        value = 0;
        var raw = QueryValue(query, name);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)
               && value > 0;
    }

    private static DateTime? ParseQueryDate(string? query, string name)
    {
        var raw = QueryValue(query, name);
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        return DateTime.TryParse(
            raw, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var value)
            ? value
            : null;
    }

    private sealed record AdminStepUpRequest(string? Password);
    private sealed record AdminUndoRequest(string? Id);
    private sealed record AdminIdRequest(int Id);
    private sealed record AdminKickRequest(int Id, string? Reason);
    private sealed record AdminRoomCloseRequest(int Number, string? Reason);
    private sealed record AdminConfigSetRequest(string? Key, JsonElement Value);
    private sealed record AdminSessionRevokeRequest(string? SessionId);
}
