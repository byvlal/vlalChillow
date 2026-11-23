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
/// Жалобы, публичные профили и администрация в веб-панели: кто что видит, какие
/// лимиты стоят на отправке и как работает вход в панель и право «reports».
/// </summary>
public sealed class WebPanelRouterReportsTests : IDisposable
{
    private const string DeviceA = "LB143C7D41FAE30EB14F49B1678E3933C9";

    private readonly SiteSessions _sessions = new();
    private readonly AdminSessions _adminSessions = new();
    private readonly AdminStore _adminStore;
    private readonly Dictionary<int, string> _siteTokens = new();
    private readonly Dictionary<int, string> _adminTokens = new();
    private readonly HashSet<int> _admins = new() { 1000 };
    private readonly string _dir;
    private readonly ReportStore _reports;

    private static readonly Dictionary<int, (string Nick, bool Admin, string? Custom)> People = new()
    {
        [1000] = ("влал", true, "Vlal2026"),
        [1001] = ("sapper", false, null),
        [1002] = ("roommaker", false, null),
    };

    public WebPanelRouterReportsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-webreports-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _reports = new ReportStore(Path.Combine(_dir, "reports.json"));
        _adminStore = new AdminStore(Path.Combine(_dir, "auth"));
        _adminStore.Initialize(_admins, DateTime.UtcNow);
        _adminStore.SetPermissions(
            actorId: null, 1000, AdminPermissions.All, DateTime.UtcNow, out _);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    private WebPanelRouter NewRouter()
    {
        return new WebPanelRouter(
            new RoomRegistry(),
            () => "<!doctype html><title>тест</title>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                Sessions = _sessions,
                IsAdminById = id => _admins.Contains(id),
                IsAdminLive = id => _admins.Contains(id),
                AdminSessionStore = _adminSessions,
                AdminStore = _adminStore,
                CustomIdGet = id =>
                    People.TryGetValue(id, out var p) ? p.Custom : null,
                Reports = _reports,
                ReportPlayerLookup = Lookup,
                AdministrationList = () => _admins
                    .Where(People.ContainsKey)
                    .OrderBy(id => id)
                    .Select(id => Lookup(id.ToString())!)
                    .ToList(),
            });
    }

    private static ReportTarget? Lookup(string raw)
    {
        var text = (raw ?? "").Trim();
        if (text.Length == 0)
            return null;

        int id;
        if (!int.TryParse(text, out id))
        {
            var match = People.FirstOrDefault(p =>
                p.Value.Custom is not null &&
                string.Equals(p.Value.Custom, text, StringComparison.OrdinalIgnoreCase));
            if (match.Value.Custom is null)
                return null;
            id = match.Key;
        }

        if (!People.TryGetValue(id, out var person))
            return null;

        return new ReportTarget(
            id,
            person.Nick,
            person.Custom,
            person.Custom ?? id.ToString(),
            person.Admin,
            id == 1001 ? "lobby" : "offline",
            HasAvatar: false,
            Banned: false,
            id == 1001 ? DateTime.UtcNow.AddMinutes(-5) : null,
            id == 1001 ? new[] { DeviceA } : Array.Empty<string>(),
            Note: null);
    }

    private string SiteToken(int playerId)
    {
        if (_siteTokens.TryGetValue(playerId, out var token))
            return token;

        token = _sessions.Create(playerId, "игрок " + playerId, DateTime.UtcNow)!.Token;
        _siteTokens[playerId] = token;
        return token;
    }

    private string AdminToken(int playerId)
    {
        if (_adminTokens.TryGetValue(playerId, out var token))
            return token;

        token = _adminSessions.Create(
            playerId, "игрок " + playerId,
            _adminStore.Find(playerId)?.AuthVersion ?? 1, DateTime.UtcNow)!.Token;
        _adminTokens[playerId] = token;
        return token;
    }

    private IReadOnlyDictionary<string, string> Headers(int asPlayer, bool asAdmin)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cookies = new List<string>();
        if (asPlayer > 0)
            cookies.Add("sc_session=" + SiteToken(asPlayer));
        if (asAdmin)
            cookies.Add("sc_admin=" + AdminToken(asPlayer));
        if (cookies.Count > 0)
            map["Cookie"] = string.Join("; ", cookies);
        return map;
    }

    private WebRequestContext Get(
        string path, string query = "", int asPlayer = 0, bool asAdmin = false) => new(
        "GET", path, query, Array.Empty<byte>(), IPAddress.Parse("203.0.113.1"),
        Headers(asPlayer, asAdmin));

    private WebRequestContext Post(
        string path, string json, int asPlayer = 0, bool asAdmin = false) => new(
        "POST", path, "", Encoding.UTF8.GetBytes(json), IPAddress.Parse("203.0.113.1"),
        Headers(asPlayer, asAdmin));

    private static string Body(WebResponse response) =>
        Encoding.UTF8.GetString(response.Body);

    private static string IdOf(WebResponse response)
    {
        using var doc = JsonDocument.Parse(Body(response));
        return doc.RootElement.GetProperty("report").GetProperty("id").GetString()!;
    }

    // ── публичный профиль и список администрации ─────────────────────────────

    [Fact]
    public void PublicProfileIsOpenToAnonymous()
    {
        var router = NewRouter();
        var response = router.Route(Get("/api/player", "q=1001"));

        Assert.Equal(200, response.Status);
        var json = Body(response);
        Assert.Contains("\"ok\":true", json);
        Assert.Contains("\"nick\":\"sapper\"", json);
        Assert.Contains("\"displayId\":\"1001\"", json);
        Assert.Contains("\"admin\":false", json);
        Assert.Contains("\"total\":0", json);
        Assert.DoesNotContain(DeviceA, json);
    }

    [Fact]
    public void PublicProfileShowsCustomIdForAdmin()
    {
        var router = NewRouter();
        var response = router.Route(Get("/api/player", "q=Vlal2026"));

        Assert.Equal(200, response.Status);
        var json = Body(response);
        Assert.Contains("\"nick\":\"влал\"", json);
        Assert.Contains("\"admin\":true", json);
        Assert.Contains("\"customId\":\"Vlal2026\"", json);
        Assert.Contains("\"displayId\":\"Vlal2026\"", json);
    }

    [Fact]
    public void PublicProfileUnknownAndMissingQuery()
    {
        var router = NewRouter();

        Assert.Equal(404, router.Route(Get("/api/player", "q=999999")).Status);
        Assert.Equal(400, router.Route(Get("/api/player")).Status);
    }

    [Fact]
    public void AdministrationIsPublicAndShowsOnlyAdmins()
    {
        // В People двое админов не нужны: список строится из живого admin.ids.
        _admins.Add(1002);

        var router = NewRouter();
        var response = router.Route(Get("/api/administration"));

        Assert.Equal(200, response.Status);
        var json = Body(response);
        Assert.Contains("\"nick\":\"влал\"", json);
        Assert.Contains("\"nick\":\"roommaker\"", json);
        Assert.DoesNotContain("sapper", json);
        Assert.Contains("\"displayId\":\"Vlal2026\"", json);
    }

    // ── отправка жалоб ───────────────────────────────────────────────────────

    [Fact]
    public void SubmitRequiresLogin()
    {
        var router = NewRouter();
        var response = router.Route(Post("/api/reports",
            """{"target":"1000","category":"cheat","reason":"test"}"""));

        Assert.Equal(401, response.Status);
        Assert.Contains("no_auth", Body(response));
    }

    [Fact]
    public void SubmitRejectsBadTargetCategoryAndReason()
    {
        var router = NewRouter();

        Assert.Equal(400, router.Route(Post("/api/reports",
            """{"target":"1001","category":"cheat","reason":"test"}""", asPlayer: 1001)).Status);

        Assert.Equal(400, router.Route(Post("/api/reports",
            """{"target":"1000","category":"nope","reason":"test"}""", asPlayer: 1001)).Status);

        var longReason = new string('я', ReportStore.MaxReasonLength + 1);
        var tooLong = router.Route(Post("/api/reports",
            JsonSerializer.Serialize(new { target = "1000", category = "cheat", reason = longReason }),
            asPlayer: 1001));
        Assert.Equal(400, tooLong.Status);
        Assert.Contains("reason_too_long", Body(tooLong));

        Assert.Equal(404, router.Route(Post("/api/reports",
            """{"target":"999999","category":"cheat","reason":"test"}""", asPlayer: 1001)).Status);
    }

    [Fact]
    public void SubmitAcceptsReportAndRejectsDuplicate()
    {
        var router = NewRouter();
        var body = """{"target":"1000","category":"cheat","reason":"стреляет через стену"}""";

        var first = router.Route(Post("/api/reports", body, asPlayer: 1001));
        Assert.Equal(200, first.Status);
        var json = Body(first);
        Assert.Contains("\"targetNick\":\"влал\"", json);
        Assert.Contains("\"targetAdmin\":true", json);
        Assert.Contains("\"status\":\"open\"", json);
        Assert.Contains("\"total\":1", json);

        var second = router.Route(Post("/api/reports", body, asPlayer: 1001));
        Assert.Equal(409, second.Status);
        Assert.Contains("already_reported", Body(second));
        Assert.True(second.RetryAfterSec > 0);
    }

    [Fact]
    public void MyReportsAreVisibleOnlyToTheirAuthor()
    {
        var router = NewRouter();
        router.Route(Post("/api/reports",
            """{"target":"1000","category":"insult","reason":"обижает"}""", asPlayer: 1001));

        Assert.Equal(401, router.Route(Get("/api/reports/mine")).Status);

        var mine = router.Route(Get("/api/reports/mine", asPlayer: 1001));
        Assert.Equal(200, mine.Status);
        var json = Body(mine);
        Assert.Contains("\"targetNick\":\"влал\"", json);
        Assert.Contains("\"categoryLabel\":\"оскорбления\"", json);

        var other = router.Route(Get("/api/reports/mine", asPlayer: 1002));
        Assert.Equal(200, other.Status);
        Assert.Contains("\"items\":[]", Body(other));
    }

    // ── модерация: вход в панель → живой admin.ids → право reports ─────────────

    [Fact]
    public void ModerationRequiresAdminLogin()
    {
        var router = NewRouter();

        // Сессия сайта ничего не даёт: без админ-куки данные не отдаются.
        Assert.Equal(401, router.Route(Get("/api/admin/reports")).Status);
        Assert.Equal(401, router.Route(Get("/api/admin/reports", asPlayer: 1001)).Status);
        Assert.Contains("no_admin_auth",
            Body(router.Route(Get("/api/admin/reports", asPlayer: 1001))));

        // Админ по конфигу, но личный пароль не введён — тот же отказ.
        var noCookie = router.Route(Get("/api/admin/reports", asPlayer: 1000));
        Assert.Equal(401, noCookie.Status);
        Assert.Contains("no_admin_auth", Body(noCookie));
    }

    [Fact]
    public void AdminCanAcceptReportAndCountersChange()
    {
        var router = NewRouter();
        var submitted = router.Route(Post("/api/reports",
            """{"target":"1000","category":"cheat","reason":"аимбот"}""", asPlayer: 1001));
        var id = IdOf(submitted);

        var list = router.Route(Get("/api/admin/reports", "status=open", asPlayer: 1000, asAdmin: true));
        Assert.Equal(200, list.Status);
        Assert.Contains("\"open\":1", Body(list));
        Assert.Contains(id, Body(list));

        var accepted = router.Route(Post("/api/admin/reports/status",
            JsonSerializer.Serialize(new { id, status = "accepted" }),
            asPlayer: 1000, asAdmin: true));
        Assert.Equal(200, accepted.Status);
        Assert.Contains("\"status\":\"accepted\"", Body(accepted));
        Assert.Contains("\"accepted\":1", Body(accepted));

        // Принятая жалоба считается в публичном счётчике, но не отклонённая.
        var profile = router.Route(Get("/api/player", "q=1000"));
        Assert.Contains("\"total\":1", Body(profile));
        Assert.Contains("\"accepted\":1", Body(profile));

        var rejected = router.Route(Post("/api/admin/reports/status",
            JsonSerializer.Serialize(new { id, status = "rejected" }),
            asPlayer: 1000, asAdmin: true));
        Assert.Equal(200, rejected.Status);

        var after = router.Route(Get("/api/player", "q=1000"));
        Assert.Contains("\"total\":0", Body(after));
    }

    [Fact]
    public void ModerationRejectsBadStatusAndUnknownReport()
    {
        var router = NewRouter();

        var badStatus = router.Route(Post("/api/admin/reports/status",
            """{"id":"x","status":"open"}""", asPlayer: 1000, asAdmin: true));
        Assert.Equal(400, badStatus.Status);
        Assert.Contains("bad_status", Body(badStatus));

        var unknown = router.Route(Post("/api/admin/reports/status",
            """{"id":"нет-такой","status":"accepted"}""", asPlayer: 1000, asAdmin: true));
        Assert.Equal(400, unknown.Status);
        Assert.Contains("report_not_updated", Body(unknown));
    }

    // ── displayId в собственной сессии ───────────────────────────────────────

    [Fact]
    public void MeReturnsCustomIdAndDisplayId()
    {
        var router = NewRouter();

        var admin = router.Route(Get("/api/me", asPlayer: 1000));
        Assert.Equal(200, admin.Status);
        Assert.Contains("\"customId\":\"Vlal2026\"", Body(admin));
        Assert.Contains("\"displayId\":\"Vlal2026\"", Body(admin));

        var plain = router.Route(Get("/api/me", asPlayer: 1001));
        Assert.Equal(200, plain.Status);
        Assert.Contains("\"customId\":null", Body(plain));
        Assert.Contains("\"displayId\":\"1001\"", Body(plain));
    }

    [Fact]
    public void WrongMethodsGet405()
    {
        var router = NewRouter();
        Assert.Equal(405, router.Route(Post("/api/player", "{}")).Status);
        Assert.Equal(405, router.Route(Get("/api/reports")).Status);
        Assert.Equal(405, router.Route(Get("/api/admin/reports/status")).Status);
    }
}
