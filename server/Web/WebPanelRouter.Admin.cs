using System.Security.Cryptography;
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

    /// <summary>Тестовый режим: пароль админам не нужен.</summary>
    private bool NoAdminPassword => _options.TestNoAdminPassword?.Invoke() == true;

    /// <summary>Тестовый режим: аккаунту без админки выдаётся мега-админка.</summary>
    private bool AutoMegaAdmin => _options.TestAutoMegaAdmin?.Invoke() == true;

    /// <summary>
    /// Найти админ-сессию по двум кукам: токену сессии и токену устройства. Вторая
    /// кука привязывает сессию к браузеру: скопированная админ-кука без неё не работает.
    /// </summary>
    private AdminSession? FindAdminSession(WebRequestContext request, DateTime now)
    {
        var store = _options.AdminSessionStore;
        if (store is null)
            return null;

        var session = store.Find(
            AdminCookie.ReadToken(request.Headers),
            AdminCookie.ReadDeviceToken(request.Headers),
            now);

        // Тестовый автовход живёт ровно пока включён test.noAdminPassword.
        // Выключили флаг — сессия снимается на первом же запросе, а не через 30 дней.
        if (session is { TestAuto: true } && !NoAdminPassword)
        {
            store.DropBySessionId(session.SessionId, session.PlayerId);
            return null;
        }

        return session;
    }

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
        var session = FindAdminSession(request, now);
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

        var session = FindAdminSession(request, DateTime.UtcNow);
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

    /// <summary>
    /// Единый затвор подтверждения паролем для всех изменяющих админ-запросов.
    /// Проверяет живую сессию, привязку к устройству и свежий step-up. В тестовом
    /// режиме <c>test.noAdminPassword</c> подтверждение не требуется, но вход,
    /// CSRF, права и запреты остаются на месте.
    /// </summary>
    private WebResponse? AdminMutationGate(WebRequestContext request)
    {
        if (AdminGate(request, out var admin, AdminPermissions.Dashboard) is { } denied)
            return denied;

        if (NoAdminPassword)
            return null;

        if (admin.StepUpUntilUtc is { } until && until > DateTime.UtcNow)
            return null;

        return ApiError(403, "step_up_required",
            "подтверди пароль заново: это действие меняет данные сервера");
    }

    /// <summary>
    /// Защита изменяющих запросов: сессия есть, но токен не тот — 403. Без сессии
    /// пропускаем дальше: обработчик сам ответит 401, а не подскажет, что кука жива.
    /// </summary>
    private WebResponse? AdminCsrfGate(WebRequestContext request)
    {
        var sessions = _options.AdminSessionStore;
        if (sessions is null)
            return null;

        var session = FindAdminSession(request, DateTime.UtcNow);
        if (session is null)
            return null;

        if (!OriginAllowed(request))
            return ApiError(403, "bad_origin", "запрос пришёл с чужого сайта");

        var token = HeaderOf(request, "X-CSRF-Token");
        if (string.IsNullOrEmpty(token)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(session.CsrfToken)))
            return ApiError(403, "bad_csrf",
                "нет или неверный CSRF-токен — обнови страницу и войди заново");

        return null;
    }

    /// <summary>Origin/Referer, если он есть, обязан указывать на этот же хост.</summary>
    private static bool OriginAllowed(WebRequestContext request)
    {
        var origin = HeaderOf(request, "Origin");
        if (origin.Length == 0)
            return true; // не браузерный запрос: токен уже проверен
        if (string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
            return false;

        var host = HeaderOf(request, "X-Forwarded-Host");
        if (host.Length == 0)
            host = HeaderOf(request, "Host");
        if (host.Length == 0)
            return false;

        var colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(']', colon) < 0)
            host = host[..colon];
        return string.Equals(uri.Host, host.Trim('[', ']'), StringComparison.OrdinalIgnoreCase);
    }

    private static string HeaderOf(WebRequestContext request, string name)
    {
        foreach (var (key, value) in request.Headers)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
                return value ?? "";
        }
        return "";
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

        // Заход в панель продлевает сессию: активный админ не должен заново вводить
        // пароль каждые 30 дней. Куки перевыставляются с полным сроком.
        _options.AdminSessionStore?.Renew(admin.TokenHash, DateTime.UtcNow);
        var response = AdminStateJson(record, admin);
        var token = AdminCookie.ReadToken(request.Headers);
        var device = AdminCookie.ReadDeviceToken(request.Headers);
        return token is { Length: > 0 } && device is { Length: > 0 }
            ? WithCookies(
                response,
                AdminCookie.Build(token, _options.CookiePath, _options.SecureCookies),
                AdminCookie.BuildDevice(device, _options.CookiePath, _options.SecureCookies))
            : response;
    }

    /// <summary>JSON состояния вошедшего админа: без записи сюда не попадают.</summary>
    private WebResponse AdminStateJson(AdminRecord record, AdminSession? session = null)
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
            writer.WriteBoolean("testNoPassword", NoAdminPassword);
            writer.WriteBoolean("testAutoMega", AutoMegaAdmin);
            writer.WriteBoolean("testAutoLogin", session?.TestAuto == true);
            writer.WriteString("customId", _options.CustomIdGet?.Invoke(record.Id));
            if (session is not null)
            {
                writer.WriteString("csrf", session.CsrfToken);
                var stepUp = session.StepUpUntilUtc is { } until && until > DateTime.UtcNow;
                writer.WriteBoolean("stepUp", stepUp);
                writer.WriteString("stepUpUntil", stepUp ? session.StepUpUntilUtc!.Value.ToString("O") : "");
            }

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

    /// <summary>
    /// Подсказка для экрана входа: включены ли тестовые флаги. Ничего, кроме двух
    /// булевых значений, не отдаём — иначе анонимный посетитель узнал бы состав
    /// админов или настройки сервера.
    /// </summary>
    private WebResponse AdminLoginInfo()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteBoolean("testNoPassword", NoAdminPassword);
            writer.WriteBoolean("testAutoMega", AutoMegaAdmin);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

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

        // В обычном режиме пароль обязателен всегда. Тестовый флаг снимает только
        // это требование; id всё равно обязан быть живым админом или аккаунтом,
        // которому тестовый режим выдаёт мега-права.
        var passwordless = NoAdminPassword;
        if (string.IsNullOrWhiteSpace(parsed.Password) && !passwordless)
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

        var ip = request.RemoteAddress?.ToString() ?? "";
        AdminRecord? record;
        var testMegaGranted = false;

        if (passwordless)
        {
            record = _options.AdminStore.Find(parsed.Id);

            // Тестовая автовыдача: только существующему аккаунту и только если
            // админки у него нет. Запись, оставшаяся от прежнего конфига, не
            // считается «нет админки» — её владелец мог снять намеренно.
            if ((record is null || AdminStore.IsTestAutoRecord(record))
                && _options.AccountExists?.Invoke(parsed.Id) == true)
            {
                testMegaGranted = TryGrantTestMega(
                    parsed.Id, record, "", ip, now,
                    "тестовый режим: вход без пароля, автовыдача мега-админки");
                record = _options.AdminStore.Find(parsed.Id);
            }

            if (record is not null && !_options.IsAdminLive(record.Id))
                record = null;
        }
        else
        {
            record = _options.AdminStore.Verify(parsed.Id, parsed.Password, now);
            if (record is not null && !_options.IsAdminLive(record.Id))
                record = null;
        }

        if (record is null)
        {
            _adminLoginLimiter.Fail(request.RemoteAddress, now);
            Console.WriteLine(
                $"[web] вход отклонён: id #{parsed.Id} с {request.RemoteAddress} " +
                "(неверный пароль или id не админ)");
            _options.AdminStore.Journal(
                "security", "login_failed", parsed.Id, parsed.Id,
                context: new AdminJournalContext(
                    parsed.Id, "", ip,
                    "admin", parsed.Id, "", Result: "fail",
                    Note: passwordless
                        ? "тестовый режим: id не админ и не аккаунт"
                        : "неверный id или пароль"));
            // Один и тот же ответ: не подсказываем, существует ли id.
            return ApiError(401, "bad_credentials", "id или пароль не подходят");
        }

        _adminLoginLimiter.Reset(request.RemoteAddress);
        var adminSession = _options.AdminSessionStore.Create(
            record.Id, record.Nick, record.AuthVersion, now, ip,
            HeaderOf(request, "User-Agent"));
        if (adminSession is null)
            return ApiError(503, "too_many_sessions",
                "на сервере кончились админ-сессии — попробуй позже");

        var loginNote = passwordless
            ? (testMegaGranted
                ? "тестовый режим: вход без пароля, выдана мега-админка"
                : "тестовый режим: вход без пароля")
            : null;
        _options.AdminStore.Journal(
            "auth", "login", record.Id, record.Id,
            context: new AdminJournalContext(
                record.Id, record.Nick, ip,
                "admin", record.Id, record.Nick, Note: loginNote));
        Console.WriteLine(
            $"[web] админ-вход: #{record.Id} с {request.RemoteAddress} " +
            $"(мега: {_options.AdminStore.IsMega(record.Id)}" +
            (passwordless ? ", ТЕСТ: без пароля" : "") + ")");

        return WithCookies(
            AdminStateJson(record, adminSession),
            AdminCookie.Build(adminSession.Token, _options.CookiePath, _options.SecureCookies),
            AdminCookie.BuildDevice(
                adminSession.DeviceToken, _options.CookiePath, _options.SecureCookies));
    }

    /// <summary>
    /// Тестовый автовход: экран «id + пароль» пропускается, сервер сам выбирает
    /// админа и открывает обычную сессию — те же куки, CSRF и журнал, что при входе.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Работает только при <c>test.noAdminPassword = true</c>. Выключили флаг —
    /// эндпоинт отвечает 403, а уже выданные тестовые сессии снимаются в
    /// <see cref="FindAdminSession"/>.
    /// </para>
    /// <para>
    /// Кого пускаем: первый живой id из <c>admin.megaIds</c> (порядок конфига),
    /// иначе первый из <c>admin.ids</c>, иначе — при <c>test.autoMegaAdmin</c> —
    /// самый первый аккаунт с мега-правами в памяти. Права не раздаются всем:
    /// обычный админ остаётся обычным, забаненный кандидат пропускается.
    /// </para>
    /// </remarks>
    private WebResponse AdminTestLogin(WebRequestContext request)
    {
        if (_options.AdminSessionStore is null
            || _options.AdminStore is null
            || _options.IsAdminLive is null)
            return ApiError(503, "admin_disabled", "админ панель не настроена на этом сервере");

        if (!NoAdminPassword)
            return ApiError(403, "test_disabled",
                "тестовый режим выключен: вход только по id и личному паролю");

        // Автовход меняет состояние браузера. Чужой сайт не должен уметь подсунуть
        // свою сессию: Origin, если он есть, обязан указывать на этот же хост.
        if (!OriginAllowed(request))
            return ApiError(403, "bad_origin", "запрос пришёл с чужого сайта");

        var now = DateTime.UtcNow;
        var ip = request.RemoteAddress?.ToString() ?? "";

        // Уже вошли — новую сессию не плодим.
        var existing = FindAdminSession(request, now);
        if (existing is not null
            && _options.AdminStore.Find(existing.PlayerId) is { } existingRecord)
            return AdminStateJson(existingRecord, existing);

        if (!TryPickTestAdmin(out var id, out var nick, out var reason))
        {
            Console.WriteLine(
                $"[web] тестовый автовход невозможен: {reason} ({request.RemoteAddress})");
            return ApiError(409, "no_test_admin", reason);
        }

        if (_options.IsBanned?.Invoke(id) == true)
            return ApiError(403, "banned", "аккаунт забанен");

        var record = _options.AdminStore.Find(id);
        if (record is null || AdminStore.IsTestAutoRecord(record))
        {
            TryGrantTestMega(id, record, nick, ip, now,
                "тестовый режим: автовход без экрана входа, выдана мега-админка");
            record = _options.AdminStore.Find(id);
        }

        if (record is null || !_options.IsAdminLive(record.Id))
            return ApiError(409, "no_test_admin",
                $"id #{id} не админ: впиши его в admin.ids или включи test.autoMegaAdmin");

        var session = _options.AdminSessionStore.Create(
            record.Id, record.Nick ?? "", record.AuthVersion, now, ip,
            HeaderOf(request, "User-Agent"), testAuto: true);
        if (session is null)
            return ApiError(503, "too_many_sessions",
                "на сервере кончились админ-сессии — попробуй позже");

        _options.AdminStore.Journal(
            "auth", "login", record.Id, record.Id,
            context: new AdminJournalContext(
                record.Id, record.Nick ?? "", ip,
                "admin", record.Id, record.Nick ?? "",
                Note: "тестовый режим: автовход без экрана входа"));
        Console.WriteLine(
            $"[web] тестовый автовход: #{record.Id} с {request.RemoteAddress} " +
            $"(мега: {_options.AdminStore.IsMega(record.Id)})");

        return WithCookies(
            AdminStateJson(record, session),
            AdminCookie.Build(session.Token, _options.CookiePath, _options.SecureCookies),
            AdminCookie.BuildDevice(
                session.DeviceToken, _options.CookiePath, _options.SecureCookies));
    }

    /// <summary>
    /// Кандидат тестового автовхода: живой админ из конфига, а если админов нет —
    /// первый аккаунт при <c>test.autoMegaAdmin</c>.
    /// </summary>
    private bool TryPickTestAdmin(out int id, out string nick, out string reason)
    {
        var sawBanned = false;

        foreach (var candidate in _options.MegaIds?.Invoke() ?? Array.Empty<int>())
        {
            if (IsTestCandidate(candidate, out var banned))
            {
                id = candidate;
                nick = _options.AdminStore?.Find(candidate)?.Nick ?? "";
                reason = "";
                return true;
            }
            sawBanned |= banned;
        }

        foreach (var candidate in _options.AdminIds?.Invoke() ?? Array.Empty<int>())
        {
            if (IsTestCandidate(candidate, out var banned))
            {
                id = candidate;
                nick = _options.AdminStore?.Find(candidate)?.Nick ?? "";
                reason = "";
                return true;
            }
            sawBanned |= banned;
        }

        if (AutoMegaAdmin
            && _options.FirstAccount?.Invoke() is { } account
            && _options.AccountExists?.Invoke(account.Id) == true)
        {
            id = account.Id;
            nick = account.Nick;
            reason = "";
            return true;
        }

        id = 0;
        nick = "";
        reason = sawBanned
            ? "все админы из admin.ids забанены — автовход некуда"
            : "в admin.ids нет ни одного живого админа — впиши свой id в config.json " +
              "(admin.megaIds) или включи test.autoMegaAdmin";
        return false;
    }

    /// <summary>Живой админ с записью, которого не забанили.</summary>
    private bool IsTestCandidate(int id, out bool banned)
    {
        banned = _options.IsBanned?.Invoke(id) == true;
        return !banned
               && id > 0
               && _options.IsAdminLive?.Invoke(id) == true
               && _options.AdminStore?.Find(id) is not null;
    }

    /// <summary>
    /// Тестовая автовыдача мега-прав: только существующему аккаунту и только если
    /// админки у него нет (или она выдана этим же тестовым режимом). Запись,
    /// которую владелец вписал сам, тестовый режим не трогает.
    /// </summary>
    private bool TryGrantTestMega(
        int id, AdminRecord? record, string nick, string ip, DateTime nowUtc, string note)
    {
        if (!AutoMegaAdmin || _options.AdminStore is null)
            return false;
        if (record is not null && !AdminStore.IsTestAutoRecord(record))
            return false;
        if (_options.AccountExists?.Invoke(id) != true)
            return false;

        var display = string.IsNullOrWhiteSpace(record?.Nick) ? nick : record!.Nick;
        return _options.AdminStore.GrantRuntimeMega(
            id, display, nowUtc,
            new AdminJournalContext(
                id, display, ip, "admin", id, display, Note: note));
    }

    /// <summary>Выход из админ панели: гасим админ-куку и куку устройства.</summary>
    private WebResponse AdminLogout(WebRequestContext request)
    {
        var token = AdminCookie.ReadToken(request.Headers);
        if (token is { Length: > 0 })
            _options.AdminSessionStore?.Drop(token);

        return WithCookies(
            WebResponse.Json("{\"ok\":true}"),
            AdminCookie.BuildExpired(_options.CookiePath, _options.SecureCookies),
            AdminCookie.BuildDeviceExpired(_options.CookiePath, _options.SecureCookies));
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
        if (MegaStepUpGate(request, out var admin) is { } denied)
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

        var target = _options.AdminStore?.Find(parsed.Id);
        var context = new AdminJournalContext(
            admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
            "admin", parsed.Id, target?.Nick ?? "", Reversible: true);
        string? error = null;
        if (_options.AdminStore?.SetPermissions(
                admin.PlayerId, parsed.Id, parsed.Permissions, DateTime.UtcNow,
                out error, context) != true)
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

        var before = _options.CustomIdGet?.Invoke(admin.PlayerId) ?? "";
        var error = _options.CustomIdSet(admin.PlayerId, value.Length == 0 ? null : value);
        if (error is not null)
            return ApiError(400, "bad_custom_id", error);

        _options.AdminStore?.Journal("customid", "set", admin.PlayerId, admin.PlayerId,
            before: new { value = before }, after: new { value },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "admin", admin.PlayerId, admin.Nick, Reversible: true));

        var action = value.Length == 0 ? "сбросил" : "поставил";
        Console.WriteLine($"[web] кастомный ID: #{admin.PlayerId} {action} «{value}»");

        var record = _options.AdminStore?.Find(admin.PlayerId);
        if (record is null)
            return ApiError(403, "not_admin", "для этого id нет записи админа");
        return AdminStateJson(record, admin);
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
        if (parsed.Id == admin.PlayerId)
            return ApiError(400, "self_ban", "себя забанить нельзя");
        if (_options.AdminStore?.IsMega(parsed.Id) == true)
            return ApiError(403, "target_mega", "мега-админа забанить нельзя");

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
            before: new { banned = false },
            after: new
            {
                banned = true,
                banId = record.BanId,
                nick = record.DisplayNick,
                duration = BanDuration.FormatShort(record.Duration),
                expires = record.ExpiresUtc.ToString("O"),
                reason,
                ips = record.Ips,
                devices = record.Devices,
            },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "player", parsed.Id, record.DisplayNick,
                Reversible: true, Note: reason));

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
            before: new { banned = true },
            after: new { banned = false, removed },
            context: new AdminJournalContext(
                admin.PlayerId, admin.Nick, request.RemoteAddress?.ToString() ?? "",
                "player", parsed.Id, ""));
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
