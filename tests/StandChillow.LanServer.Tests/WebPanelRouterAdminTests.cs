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

    private WebPanelRouter NewRouter() =>
        new(
            new RoomRegistry(),
            () => "<!doctype html><title>тест</title>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                AdminStore = _store,
                AdminSessionStore = _sessions,
                IsAdminLive = id => _admins.Contains(id),
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

    private static string Body(WebResponse response) =>
        Encoding.UTF8.GetString(response.Body);

    private static string LoginJson(int id, string password) =>
        JsonSerializer.Serialize(new { id, password });

    /// <summary>Ответ логина: статус, тело и токен админ-куки (если вход удался).</summary>
    private static (int Status, string Body, string? Cookie) Login(
        WebPanelRouter router, int id, string password, IPAddress? ip = null)
    {
        var response = router.Route(Post(
            "/api/admin/login", LoginJson(id, password), ip: ip));
        var setCookie = response.ExtraHeaders
            .FirstOrDefault(header => header.Item1 == "Set-Cookie").Item2;
        var cookie = setCookie is null
            ? null
            : setCookie.Split(';')[0];
        return (response.Status, Body(response), cookie);
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
    public void OrdinaryAdmin_CanNotChangePermissionsEvenWithAdminsRight()
    {
        var router = NewRouter();
        var login = Login(router, AdminId, PasswordOf(AdminId));
        _store.SetPermissions(null, AdminId, new[] { "admins" }, DateTime.UtcNow, out _);

        var response = router.Route(Post(
            "/api/admin/permissions",
            JsonSerializer.Serialize(new { id = SecondId, permissions = new[] { "bans" } }),
            login.Cookie));

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

        var response = router.Route(Post(
            "/api/admin/mega",
            """{"id":1001,"ip":"1.2.3.4","action":"add"}""",
            login.Cookie));

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

        var logout = router.Route(Post("/api/admin/logout", "{}", login.Cookie));

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
}
