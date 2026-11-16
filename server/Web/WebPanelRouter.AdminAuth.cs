using System.Net;
using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Гейт доступа к данным панели и админские маршруты.
/// </summary>
/// <remarks>
/// <para>
/// Владелец просил одно правило: <b>без входа сервер не отдаёт ничего</b>, ни список
/// комнат, ни онлайн. Поэтому чтение (<c>/api/status</c>, <c>/api/rooms</c>,
/// <c>/api/modes</c>) тоже требует сессии, а страница — нет: она должна открыться, чтобы
/// человек мог ввести код из лобби. Каталог режимов нужен форме создания комнаты уже
/// <i>после</i> входа, так что и он под гейтом.
/// </para>
/// <para>
/// Админские маршруты — третья створка. Мало быть авторизованным на сайте: нужно, чтобы
/// числовой id был в <c>admin.ids</c> <b>и</b> был предъявлен код текущего запуска
/// (<c>auth.txt</c> / консоль). Обе проверки брались из тех же мест, что и в игре, — это
/// удобно ровно потому, что для человека это <i>тот же</i> код, который он и так вводит
/// в чат лобби.
/// </para>
/// <para>
/// На чтение админские ответы тоже закрыты: без ключа любой <c>/api/admin/*</c> отвечает
/// <c>403 admin_required</c>, а не «пустым списком» — иначе страница выглядела бы так,
/// будто банов нет.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    /// <summary>Код сессии для гейта чтения.</summary>
    private const string NeedAuthMessage = "нужно ввести код из лобби";

    /// <summary>
    /// Ошибка «не админ» одна на все случаи: и «ты не админ», и «код не предъявлен».
    /// </summary>
    /// <remarks>
    /// Разница видна только в тексте: человеку, чей id в <c>admin.ids</c>, честно
    /// подсказывается, что нужен код, — иначе он будет искать проблему не там.
    /// </remarks>
    private WebResponse AdminRefusal(SiteSession session)
    {
        if (_options.IsAdminId?.Invoke(session.PlayerId) != true)
        {
            return ApiError(403, "admin_required",
                $"это админский раздел: id #{session.PlayerId} нет в admin.ids");
        }

        return ApiError(403, "admin_code_required",
            "id админский, но код этого запуска не предъявлен — " +
            "введи код из auth.txt рядом с config.json");
    }

    /// <summary>
    /// Прошедшая проверку админская сессия или <c>null</c> вместе с готовым отказом.
    /// </summary>
    /// <param name="request">Запрос.</param>
    /// <param name="session">Обычная сессия сайта (её нужно передать, чтобы не искать дважды).</param>
    private SiteAdminSession? AdminSessionOf(WebRequestContext request, SiteSession session, out WebResponse? refusal)
    {
        refusal = null;

        var store = _options.AdminSessions;
        if (store is null || _options.IsAdminId is null)
        {
            refusal = ApiError(503, "admin_disabled", "админка на этом сервере не настроена");
            return null;
        }

        // id пересчитывается из сессии каждый раз: убрали из admin.ids на ходу — ключ
        // перестал давать права, и повторный вход тоже не поможет.
        if (_options.IsAdminId(session.PlayerId) != true)
        {
            refusal = AdminRefusal(session);
            return null;
        }

        var admin = store.Find(SiteCookie.ReadAdminToken(request.Headers), request.RemoteAddress);
        if (admin is null || admin.PlayerId != session.PlayerId)
        {
            refusal = AdminRefusal(session);
            return null;
        }

        return admin;
    }

    // ── маршруты: вход в админку ──────────────────────────────────────────────

    /// <summary>
    /// <c>POST /api/admin/auth</c> — обменять код запуска на админский ключ.
    /// </summary>
    /// <remarks>
    /// Код — тот же, что печатает сервер в консоль и кладёт в <c>auth.txt</c>
    /// (<see cref="Net.AdminAuth"/>). Проверка кода делается вызывающим
    /// (<c>VerifyAdminCode</c>), сравнение — в постоянном времени, без учёта регистра.
    /// </remarks>
    private WebResponse AdminAuthenticate(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banned)
            return banned;

        if (_options.AdminSessions is null || _options.IsAdminId is null)
            return ApiError(503, "admin_disabled", "админка на этом сервере не настроена");

        // Права спрашиваются по id из сессии, а не из тела: тело приходит от браузера.
        if (_options.IsAdminId(session.PlayerId) != true)
        {
            _options.Log?.Invoke(
                $"[web] админка: отказ — id {session.PlayerId} ('{session.Nick}') не в admin.ids");
            return ApiError(403, "admin_required",
                $"это админский раздел: id #{session.PlayerId} нет в admin.ids");
        }

        if (_options.VerifyAdminCode is null)
            return ApiError(503, "admin_disabled", "проверка кода не настроена на этом сервере");

        AdminAuthRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AdminAuthRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        var code = (parsed?.Code ?? string.Empty).Trim();
        if (code.Length == 0)
            return ApiError(400, "empty_code", "введи код из auth.txt или из консоли сервера");

        if (!_options.VerifyAdminCode(code))
        {
            _options.Log?.Invoke(
                $"[web] админка: неверный код от ID {session.PlayerId} ('{session.Nick}') " +
                $"с {request.RemoteAddress}");
            return ApiError(403, "bad_admin_code",
                "код не подошёл — он меняется при каждом запуске сервера " +
                "(смотри auth.txt рядом с config.json)");
        }

        var admin = _options.AdminSessions.Create(
            session.PlayerId, session.Nick, request.RemoteAddress, DateTime.UtcNow);
        if (admin is null)
            return ApiError(503, "too_many_admins", "на сервере кончились админские сессии");

        // Тот же код, что вводится в чат лобби, — значит и адрес должен стать
        // авторизованным в игре: иначе «вошёл в админку, а лобби теневой» и человек
        // ищет причину в конфиге. Сайт не даёт новых прав: их даёт код, который он
        // только что проверил.
        _options.AuthorizeAddress?.Invoke(request.RemoteAddress);

        _options.Log?.Invoke(
            $"[web] админка: код принят — ID {session.PlayerId} ('{session.Nick}') " +
            $"с {request.RemoteAddress}");

        return WithCookie(
            WebResponse.Json(BuildAdminProfileJson(admin)),
            SiteCookie.BuildAdmin(admin.Token, _options.CookiePath, _options.SecureCookies));
    }

    /// <summary><c>POST /api/admin/logout</c> — снять админский ключ (вход на сайте остаётся).</summary>
    private WebResponse AdminLogout(WebRequestContext request)
    {
        _options.AdminSessions?.Drop(SiteCookie.ReadAdminToken(request.Headers));
        return WithCookie(
            WebResponse.Json("{\"ok\":true}"),
            SiteCookie.BuildAdminExpired(_options.CookiePath, _options.SecureCookies));
    }

    /// <summary>
    /// <c>GET /api/admin/me</c> — «я админ?». Ответ <c>{admin:false}</c> для обычного
    /// авторизованного игрока: странице нужно различать «не вошёл» и «вошёл, но не админ».
    /// </summary>
    private WebResponse AdminWhoAmI(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banned)
            return banned;

        var isAdminId = _options.IsAdminId?.Invoke(session.PlayerId) == true;
        var admin = AdminSessionOf(request, session, out _);
        var displayId = _options.PublicIdOf?.Invoke(session.PlayerId);

        return WebResponse.Json(
            BuildAdminProfileJson(admin, session.PlayerId, isAdminId, displayId));
    }

    /// <summary>Числовой id, подменённый публичный id и ник — для страницы и чата.</summary>
    private static string BuildAdminProfileJson(
        SiteAdminSession? admin, int playerId = 0, bool isAdminId = false, string? displayId = null)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("admin", admin is not null);
            writer.WriteBoolean("adminId", isAdminId);
            writer.WriteNumber("id", admin?.PlayerId ?? playerId);
            writer.WriteString("displayId",
                displayId ?? (admin?.PlayerId ?? playerId).ToString());
            if (admin is not null)
            {
                writer.WriteString("nick", admin.Nick);
                writer.WriteString("since", admin.IssuedUtc.ToString("O"));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Тело <c>POST /api/admin/auth</c>.</summary>
    private sealed record AdminAuthRequest(string? Code);
}
