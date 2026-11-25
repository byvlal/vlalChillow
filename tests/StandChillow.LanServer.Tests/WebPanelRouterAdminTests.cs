using System.Net;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Reports;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Вход в админку и право-затворы: без пароля — 401, без права — 403, мега — только
/// id из <c>admin.megaIds</c>. Ни один админ-эндпоинт не отдаёт данные «просто админу».
/// </summary>
public sealed class WebPanelRouterAdminTests : IDisposable
{
    private const int AdminId = 1000;
    private const int SecondId = 1001;
    private const int MegaId = 1002;
    private static readonly IPAddress ClientIp = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress OtherIp = IPAddress.Parse("203.0.113.11");

    private readonly string _dir;
    private readonly AdminStore _store;
    private readonly AdminSessions _sessions;
    private readonly ReportStore _reports;
    private readonly HashSet<int> _admins = new() { AdminId, SecondId, MegaId };
    private readonly HashSet<int> _mega = new() { MegaId };

    public WebPanelRouterAdminTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-adminrouter-" + Guid.NewGuid().ToString("N"));
        _store = new AdminStore(Path.Combine(_dir, "auth"));
        _store.Initialize(_admins, _mega, DateTime.UtcNow);
        _sessions = new AdminSessions(Path.Combine(_dir, "sessions.json"), _store);
        _reports = new ReportStore(Path.Combine(_dir, "reports.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    private string PasswordOf(int id) =>
        File.ReadAllLines(_store.PasswordPathOf(id)).First();

    private WebPanelRouter NewRouter(
        Func<bool>? testNoPassword = null,
        Func<bool>? testAutoMega = null,
        Func<int, bool>? accountExists = null,
        Func<int, bool>? isAdminLive = null) =>
        new(
            new RoomRegistry(),
            () => "<!doctype html><title>тест</title>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                AdminStore = _store,
                AdminSessionStore = _sessions,
                IsAdminLive = isAdminLive ?? (id => _admins.Contains(id)),
                TestNoAdminPassword = testNoPassword,
                TestAutoMegaAdmin = testAutoMega,
                AccountExists = accountExists,
                CookiePath = "/",
                BanSettings = () => (true, 10, 14),
                Reports = _reports,
            });

    private static Dictionary<string, string> Headers(string? cookie = null)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (cookie is not null)
            map["Cookie"] = cookie;
        return map;
    }

    private static WebRequestContext Get(
        string path, string? cookie = null, IPAddress? ip = null) =>
        new("GET", path, "", Array.Empty<byte>(), ip ?? ClientIp, Headers(cookie));

    private static WebRequestContext GetQuery(
        string path, string query, string? cookie = null, IPAddress? ip = null) =>
        new("GET", path, query, Array.Empty<byte>(), ip ?? ClientIp, Headers(cookie));

    private static WebRequestContext Post(
        string path, string json, string? cookie = null, IPAddress? ip = null) =>
        new("POST", path, "", Encoding.UTF8.GetBytes(json), ip ?? ClientIp, Headers(cookie));

    /// <summary>POST от вошедшего админа: без CSRF-токена сервер обязан отказать.</summary>
    private static WebRequestContext PostAs(
        string path, string json, string cookie, string csrf, IPAddress? ip = null)
    {
        var headers = Headers(cookie);
        headers["X-CSRF-Token"] = csrf;
        return new WebRequestContext(
            "POST", path, "", Encoding.UTF8.GetBytes(json), ip ?? ClientIp, headers);
    }

    private static string Csrf(string loginBody) =>
        JsonDocument.Parse(loginBody).RootElement.GetProperty("csrf").GetString() ?? "";

    private static string Body(WebResponse response) =>
        Encoding.UTF8.GetString(response.Body);

    private static string LoginJson(int id, string password) =>
        JsonSerializer.Serialize(new { id, password });

    /// <summary>Ответ логина: статус, тело и куки админ-сессии + устройства.</summary>
    private static (int Status, string Body, string? Cookie) Login(
        WebPanelRouter router, int id, string password, IPAddress? ip = null)
    {
        var response = router.Route(Post(
            "/api/admin/login", LoginJson(id, password), ip: ip));
        var cookies = response.ExtraHeaders
            .Where(header => header.Item1 == "Set-Cookie")
            .Select(header => header.Item2.Split(';')[0])
            .Where(cookie => cookie.Length > 0)
            .ToArray();
        var cookie = cookies.Length == 0 ? null : string.Join("; ", cookies);
        return (response.Status, Body(response), cookie);
    }

    /// <summary>Подтвердить пароль заново: без step-up изменяющие POST теперь закрыты.</summary>
    private static WebResponse StepUp(
        WebPanelRouter router, (int Status, string Body, string? Cookie) login, string password)
    {
        var response = router.Route(PostAs(
            "/api/admin/stepup",
            JsonSerializer.Serialize(new { password }),
            login.Cookie!,
            Csrf(login.Body)));
        Assert.Equal(200, response.Status);
        return response;
    }

    [Fact]
    public void WithoutPassword_EveryAdminEndpointReturns401()
    {
        var router = NewRouter();

        foreach (var request in new[]
                 {
                     Get("/api/admin"),
                     Get("/api/admin/bans"),
                     Get("/api/admin/admins"),
                     GetQuery("/api/admin/lookup", "id=1001"),
                     Get("/api/admin/reports"),
                     Post("/api/admin/permissions", """{"id":1001,"permissions":["bans"]}"""),
                     Post("/api/admin/customid", """{"customId":"x"}"""),
                     Post("/api/admin/ban", """{"id":1}"""),
                     Post("/api/admin/unban", """{"id":1}"""),
                 })
        {
            var response = router.Route(request);
            Assert.True(
                response.Status == 401,
                $"{request.Method} {request.Path}: {response.Status} {Body(response)}");
            Assert.Contains("no_admin_auth", Body(response));
            Assert.DoesNotContain("nick", Body(response));
        }
    }

    [Fact]
    public void WrongPassword_AndUnknownId_LookExactlyTheSame()
    {
        var router = NewRouter();

        var unknown = router.Route(Post(
            "/api/admin/login", LoginJson(9999, PasswordOf(AdminId))));
        var wrong = router.Route(Post(
            "/api/admin/login", LoginJson(AdminId, "неверный")));

        Assert.Equal(401, unknown.Status);
        Assert.Equal(401, wrong.Status);
        Assert.Equal(Body(unknown), Body(wrong));
        Assert.Contains("bad_credentials", Body(wrong));
    }

    [Fact]
    public void Login_WithPersonalPassword_OpensThePanel()
    {
        var router = NewRouter();

        var login = Login(router, AdminId, PasswordOf(AdminId));

        Assert.Equal(200, login.Status);
        Assert.NotNull(login.Cookie);
        Assert.Contains("\"admin\":true", login.Body);
        Assert.Contains("\"authed\":true", login.Body);
        Assert.Contains("\"mega\":false", login.Body);
        Assert.Contains("dashboard", login.Body);
        Assert.Contains("journal", login.Body);

        var state = router.Route(Get("/api/admin", login.Cookie));
        Assert.Equal(200, state.Status);
        Assert.Contains($"\"id\":{AdminId}", Body(state));
    }

    [Fact]
    public void AuthenticatedAdminWithoutPermission_IsRejectedEverywhere()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));

        Assert.Equal(403, router.Route(Get("/api/admin/bans", login.Cookie)).Status);
        Assert.Equal(403, router.Route(Get("/api/admin/reports", login.Cookie)).Status);
        Assert.Equal(403, router.Route(Get("/api/admin/admins", login.Cookie)).Status);
        Assert.Equal(403, router.Route(GetQuery("/api/admin/lookup", "id=1001", login.Cookie)).Status);
        Assert.Contains(
            "no_permission",
            Body(router.Route(Get("/api/admin/bans", login.Cookie))));

        // Выдали только баны — открылись только баны, остальное по-прежнему закрыто.
        _store.SetPermissions(null, AdminId, new[] { "bans" }, DateTime.UtcNow, out _);

        Assert.Equal(200, router.Route(Get("/api/admin/bans", login.Cookie)).Status);
        Assert.Equal(403, router.Route(Get("/api/admin/reports", login.Cookie)).Status);
    }

    [Fact]
    public void PostWithoutCsrfToken_IsRejectedEvenWithValidSession()
    {
        var router = NewRouter();
        var login = Login(router, MegaId, PasswordOf(MegaId));

        var response = router.Route(Post(
            "/api/admin/customid", """{"customId":"x"}""", login.Cookie));

        Assert.Equal(403, response.Status);
        Assert.Contains("bad_csrf", Body(response));
    }

    [Fact]
    public void PostFromAnotherOrigin_IsRejectedEvenWithCsrfToken()
    {
        var router = NewRouter();
        var login = Login(router, MegaId, PasswordOf(MegaId));

        var headers = Headers(login.Cookie);
        headers["X-CSRF-Token"] = Csrf(login.Body);
        headers["Origin"] = "https://evil.example";
        headers["Host"] = "panel.example";
        var request = new WebRequestContext(
            "POST", "/api/admin/customid", "", Encoding.UTF8.GetBytes("""{"customId":"x"}"""),
            ClientIp, headers);

        var response = router.Route(request);

        Assert.Equal(403, response.Status);
        Assert.Contains("bad_origin", Body(response));
    }

    [Fact]
    public void OrdinaryAdmin_CanNotChangePermissionsEvenWithAdminsRight()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));
        _store.SetPermissions(null, AdminId, new[] { "admins" }, DateTime.UtcNow, out _);
        StepUp(router, login, PasswordOf(AdminId));

        var response = router.Route(PostAs(
            "/api/admin/permissions",
            JsonSerializer.Serialize(new { id = SecondId, permissions = new[] { "bans" } }),
            login.Cookie!,
            Csrf(login.Body)));

        Assert.Equal(403, response.Status);
        Assert.Contains("not_mega", Body(response));
    }

    [Fact]
    public void MegaFromConfig_GetsEveryRequestFromAnyIp()
    {
        var megaRouter = NewRouter();
        var mega = Login(megaRouter, MegaId, PasswordOf(MegaId), ClientIp);

        Assert.Equal(200, mega.Status);
        Assert.Contains("\"mega\":true", mega.Body);
        Assert.Contains("\"role\":\"mega\"", mega.Body);
        Assert.Equal(200, megaRouter.Route(Get("/api/admin/admins", mega.Cookie, ClientIp)).Status);
        Assert.Equal(200, megaRouter.Route(Get("/api/admin/reports", mega.Cookie, ClientIp)).Status);

        // Тот же пароль с другого IP остаётся мега: статус даёт только config.
        var otherRouter = NewRouter();
        var other = Login(otherRouter, MegaId, PasswordOf(MegaId), OtherIp);

        Assert.Equal(200, other.Status);
        Assert.Contains("\"mega\":true", other.Body);
        Assert.Equal(200, otherRouter.Route(Get("/api/admin/admins", other.Cookie, OtherIp)).Status);
    }

    [Fact]
    public void MegaIpEndpoint_IsGone()
    {
        var router = NewRouter();
        var login = Login(router, MegaId, PasswordOf(MegaId));
        StepUp(router, login, PasswordOf(MegaId));

        var response = router.Route(PostAs(
            "/api/admin/mega",
            """{"id":1001,"ip":"1.2.3.4","action":"add"}""",
            login.Cookie!,
            Csrf(login.Body)));

        Assert.Equal(404, response.Status);
    }

    [Fact]
    public void PasswordRegen_KillsExistingSession()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));
        Assert.Equal(200, router.Route(Get("/api/admin", login.Cookie)).Status);

        _store.RegeneratePassword(null, AdminId, DateTime.UtcNow, out _, out _);

        var after = router.Route(Get("/api/admin", login.Cookie));
        Assert.Equal(401, after.Status);
        Assert.Contains("no_admin_auth", Body(after));
    }

    [Fact]
    public void LoginRateLimit_BlocksTheSixthAttempt()
    {
        var router = NewRouter();

        for (var i = 0; i < AdminLoginLimiter.MaxAttempts; i++)
        {
            var failed = router.Route(Post(
                "/api/admin/login", LoginJson(AdminId, "неверный")));
            Assert.Equal(401, failed.Status);
        }

        var limited = router.Route(Post(
            "/api/admin/login", LoginJson(AdminId, PasswordOf(AdminId))));

        Assert.Equal(429, limited.Status);
        Assert.Contains("too_many_attempts", Body(limited));
    }

    [Fact]
    public void Logout_DropsTheSessionAndCookie()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));

        var logout = router.Route(PostAs(
            "/api/admin/logout", "{}", login.Cookie!, Csrf(login.Body)));

        Assert.Equal(200, logout.Status);
        Assert.Equal(401, router.Route(Get("/api/admin", login.Cookie)).Status);
    }

    [Fact]
    public void RemovedFromAdminIds_LosesAccessImmediately()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));
        Assert.Equal(200, router.Route(Get("/api/admin", login.Cookie)).Status);

        _admins.Remove(AdminId);

        var after = router.Route(Get("/api/admin", login.Cookie));
        Assert.Equal(403, after.Status);
        Assert.Contains("not_admin", Body(after));
    }

    [Fact]
    public void EveryMutationWithoutStepUp_IsRejected_EvenForMega()
    {
        var router = NewRouter();
        var login = Login(router, MegaId, PasswordOf(MegaId));
        var csrf = Csrf(login.Body);

        var posts = new[]
        {
            ("/api/admin/kick", """{"id":1001}"""),
            ("/api/admin/rooms/close", """{"number":1}"""),
            ("/api/admin/ban", """{"id":1001,"durationValue":1,"durationUnit":"d"}"""),
            ("/api/admin/unban", """{"id":1001}"""),
            ("/api/admin/reports/status", """{"id":1,"status":"accepted"}"""),
            ("/api/admin/customid", """{"customId":"x"}"""),
            ("/api/admin/permissions", """{"id":1001,"permissions":["bans"]}"""),
            ("/api/admin/add", """{"id":1001}"""),
            ("/api/admin/remove", """{"id":1001}"""),
            ("/api/admin/password", """{"id":1001}"""),
            ("/api/admin/config/set", """{"key":"web.port","value":8080}"""),
            ("/api/admin/reload", "{}"),
            ("/api/admin/sessions/revoke", """{"sessionId":"x"}"""),
            ("/api/admin/journal/undo", """{"id":"J-0"}"""),
        };

        foreach (var (path, json) in posts)
        {
            var response = router.Route(PostAs(path, json, login.Cookie!, csrf));
            Assert.True(
                response.Status == 403,
                $"{path}: {response.Status} {Body(response)}");
            Assert.Contains("step_up_required", Body(response));
        }
    }

    [Fact]
    public void OrdinaryAdminMutation_AlsoNeedsStepUp_AndPassesAfterIt()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));
        _store.SetPermissions(
            null, AdminId, new[] { "customization" }, DateTime.UtcNow, out _);

        var body = """{"customId":"x"}""";
        var denied = router.Route(PostAs(
            "/api/admin/customid", body, login.Cookie!, Csrf(login.Body)));

        Assert.Equal(403, denied.Status);
        Assert.Contains("step_up_required", Body(denied));

        StepUp(router, login, PasswordOf(AdminId));

        var allowed = router.Route(PostAs(
            "/api/admin/customid", body, login.Cookie!, Csrf(login.Body)));

        Assert.DoesNotContain("step_up_required", Body(allowed));
        // Сам кастомный ID в этом тесте не настроен — важен только пройденный затвор.
        Assert.Equal(503, allowed.Status);
    }

    [Fact]
    public void AdminSession_CopiedWithoutDeviceCookie_DoesNotWork()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));
        var parts = login.Cookie!.Split("; ");
        var sessionCookie = parts.Single(cookie =>
            cookie.StartsWith("sc_admin=", StringComparison.Ordinal));
        var deviceCookie = parts.Single(cookie =>
            cookie.StartsWith("sc_admin_dev=", StringComparison.Ordinal));

        Assert.Equal(401, router.Route(Get("/api/admin", sessionCookie)).Status);
        Assert.Equal(401, router.Route(Get("/api/admin", deviceCookie)).Status);
        Assert.Equal(200, router.Route(Get("/api/admin", login.Cookie)).Status);
    }

    [Fact]
    public void LoginInfo_IsPublicAndReflectsTestFlags()
    {
        var normal = NewRouter();
        var plain = normal.Route(Get("/api/admin/login-info"));

        Assert.Equal(200, plain.Status);
        Assert.Contains("\"testNoPassword\":false", Body(plain));
        Assert.Contains("\"testAutoMega\":false", Body(plain));

        var test = NewRouter(() => true, () => true);
        var flags = test.Route(Get("/api/admin/login-info"));

        Assert.Equal(200, flags.Status);
        Assert.Contains("\"testNoPassword\":true", Body(flags));
        Assert.Contains("\"testAutoMega\":true", Body(flags));
    }

    [Fact]
    public void TestMode_NoAdminPassword_LogsInWithoutPassword_AndSkipsStepUp()
    {
        var router = NewRouter(() => true, () => false);
        var login = Login(router, MegaId, "");

        Assert.Equal(200, login.Status);
        Assert.Contains("\"testNoPassword\":true", login.Body);
        Assert.Contains("\"testAutoMega\":false", login.Body);

        var body = """{"customId":"x"}""";
        var mutation = router.Route(PostAs(
            "/api/admin/customid", body, login.Cookie!, Csrf(login.Body)));

        Assert.NotEqual(403, mutation.Status);
        Assert.DoesNotContain("step_up_required", Body(mutation));

        // Тестовый режим снимает только пароль: CSRF остаётся обязательным.
        var noCsrf = router.Route(Post("/api/admin/customid", body, login.Cookie));
        Assert.Equal(403, noCsrf.Status);
        Assert.Contains("bad_csrf", Body(noCsrf));
    }

    [Fact]
    public void TestMode_AutoMega_GrantsRuntimeMegaOnlyToExistingAccounts()
    {
        const int PlayerId = 2000;
        var router = NewRouter(
            () => true,
            () => true,
            accountExists: id => id == PlayerId,
            isAdminLive: id => _admins.Contains(id) || _store.Find(id) is not null);

        var login = Login(router, PlayerId, "");

        Assert.Equal(200, login.Status);
        Assert.Contains("\"mega\":true", login.Body);
        Assert.Contains("\"role\":\"mega\"", login.Body);
        Assert.Contains("\"testAutoMega\":true", login.Body);
        Assert.True(_store.IsRuntimeMega(PlayerId));
        Assert.True(_store.IsMega(PlayerId));
        Assert.Equal(200, router.Route(Get("/api/admin", login.Cookie)).Status);

        var unknown = Login(router, PlayerId + 1, "");
        Assert.Equal(401, unknown.Status);
        Assert.Contains("bad_credentials", unknown.Body);
    }

    [Fact]
    public void TestMode_AutoMegaAlone_DoesNotBypassPassword()
    {
        const int PlayerId = 2000;
        var router = NewRouter(
            () => false,
            () => true,
            accountExists: id => id == PlayerId,
            isAdminLive: id => _admins.Contains(id) || _store.Find(id) is not null);

        var login = Login(router, PlayerId, "любой-пароль");

        Assert.Equal(401, login.Status);
        Assert.Contains("bad_credentials", login.Body);
        Assert.False(_store.IsRuntimeMega(PlayerId));
    }
}
