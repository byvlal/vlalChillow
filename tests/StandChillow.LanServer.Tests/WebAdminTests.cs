using System.Net;
using System.Text;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Админка сайта и гейт чтения: без входа — ничего, без кода — только «не админ».
/// </summary>
/// <remarks>
/// Сокетов нет: проверяются статусы и JSON. Главное, что здесь фиксируется, — что
/// «просто авторизован на сайте» не даёт ни банов, ни чужих id, ни права банить.
/// </remarks>
public sealed class WebAdminTests
{
    private readonly SiteSessions _sessions = new();
    private readonly SiteAdminSessions _adminSessions = new();
    private readonly PublicIdOverrides _overrides = new();

    private const int AdminPlayer = 1000;
    private const int PlainPlayer = 2000;
    private const string AdminCode = "TESTCODE123";
    private const string RemoteIp = "203.0.113.7";

    private readonly List<(int Id, string? Duration, string? Reason, string? By)> _bans = new();
    private readonly List<string> _log = new();

    private WebPanelRouter NewRouter()
    {
        var rooms = new RoomRegistry();
        return new WebPanelRouter(
            rooms,
            () => "<!doctype html><title>т</title>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                OnlinePlayers = () => new List<(int, string)> { (AdminPlayer, "влал") },
                Sessions = _sessions,
                RedeemCode = (_, _) => null,

                AdminSessions = _adminSessions,
                IsAdminId = id => id == AdminPlayer,
                VerifyAdminCode = code => string.Equals(
                    code.Trim(), AdminCode, StringComparison.OrdinalIgnoreCase),
                AuthorizeAddress = _ => true,
                PublicIds = _overrides,
                PublicIdOf = id => _overrides.DisplayOf(id),
                LookupPlayer = id => id == 67776161
                    ? new SeenPlayer { Id = id, Nick = "Ktulxy", Ips = { "192.168.1.50" } }
                    : null,
                ActiveBans = () => Array.Empty<BanRecord>(),
                AdminIdList = () => new[] { AdminPlayer },
                Log = _log.Add,

                // Тестовый бан: только запоминаем, что попросили.
                BanPlayer = (id, duration, reason, by, out string? error) =>
                {
                    if (id != 67776161)
                    {
                        error = "сервер не знает такого id";
                        return null;
                    }

                    error = null;
                    _bans.Add((id, duration, reason, by));
                    return new BanRecord
                    {
                        Nick = "Ktulxy",
                        Reason = reason ?? "",
                        BannedBy = by ?? "",
                        DurationSeconds = 259200,
                        ExpiresUtcMs = BanRecord.ToUnixMs(DateTime.UtcNow.AddDays(3)),
                        MatchIds = { id },
                    };
                },

                UnbanPlayer = (id, out int removed, out string? error) =>
                {
                    if (id != 67776161)
                    {
                        removed = 0;
                        error = "под id нет банов";
                        return false;
                    }

                    removed = 1;
                    error = null;
                    return true;
                },
            });
    }

    private static WebRequestContext Request(
        string method, string path, string? json = null, string? cookie = null,
        string query = "", string origin = "", string requestedWith = "vlalchillow")
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Host"] = "192.168.1.10:8080",
        };
        if (cookie is { Length: > 0 })
            headers["Cookie"] = cookie;
        if (origin.Length > 0)
            headers["Origin"] = origin;
        if (requestedWith.Length > 0)
            headers["X-Requested-With"] = requestedWith;

        return new WebRequestContext(
            method,
            path,
            query,
            json is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json),
            IPAddress.Parse(RemoteIp),
            headers);
    }

    private string SessionCookie(int playerId, string nick) =>
        "sc_session=" + _sessions.Create(playerId, nick, DateTime.UtcNow)!.Token;

    private string AdminCookie(int playerId, string nick) =>
        "sc_admin=" + _adminSessions.Create(playerId, nick, IPAddress.Parse(RemoteIp), DateTime.UtcNow)!.Token;

    private static string Body(WebResponse response) => Encoding.UTF8.GetString(response.Body);

    // ── гейт чтения ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/rooms")]
    [InlineData("/api/modes")]
    public void ReadEndpointsRequireSession(string path)
    {
        var router = NewRouter();
        var response = router.Route(Request("GET", path));

        Assert.Equal(401, response.Status);
        Assert.Contains("no_auth", Body(response));
    }

    [Theory]
    [InlineData("/api/status")]
    [InlineData("/api/rooms")]
    [InlineData("/api/modes")]
    public void ReadEndpointsOpenWithSession(string path)
    {
        var router = NewRouter();
        var response = router.Route(Request("GET", path, cookie: SessionCookie(PlainPlayer, "игрок")));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public void PageItselfIsOpenWithoutSession()
    {
        var router = NewRouter();
        foreach (var path in new[] { "/", "/play", "/admin" })
            Assert.Equal(200, router.Route(Request("GET", path)).Status);
    }

    // ── CSRF ────────────────────────────────────────────────────────────────

    [Fact]
    public void ForeignOriginPostIsRejected()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/auth", "{\"code\":\"123456\"}",
            origin: "http://evil.example", requestedWith: ""));

        Assert.Equal(403, response.Status);
        Assert.Contains("cross_origin", Body(response));
    }

    [Fact]
    public void SameOriginPostIsAllowed()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/auth", "{\"code\":\"" + AdminCode + "\"}",
            cookie: SessionCookie(AdminPlayer, "влал"),
            origin: "http://192.168.1.10:8080", requestedWith: ""));

        Assert.Equal(200, response.Status);
    }

    [Fact]
    public void NonBrowserClientWithHeaderIsAllowed()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/auth", "{\"code\":\"123456\"}"));

        // Проходит CSRF-гейт и упирается в обычную авторизацию (505 → код неверный/нет хоста).
        Assert.NotEqual(403, response.Status);
    }

    // ── админка: вход ───────────────────────────────────────────────────────

    [Fact]
    public void AdminAuthRejectsNonAdminId()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/auth", "{\"code\":\"" + AdminCode + "\"}",
            cookie: SessionCookie(PlainPlayer, "игрок")));

        Assert.Equal(403, response.Status);
        Assert.Contains("admin_required", Body(response));
    }

    [Fact]
    public void AdminAuthRejectsWrongCode()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/auth", "{\"code\":\"nope\"}",
            cookie: SessionCookie(AdminPlayer, "влал")));

        Assert.Equal(403, response.Status);
        Assert.Contains("bad_admin_code", Body(response));
    }

    [Fact]
    public void AdminAuthAcceptsCodeAndSetsSeparateCookie()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/auth", "{\"code\":\"" + AdminCode.ToLowerInvariant() + "\"}",
            cookie: SessionCookie(AdminPlayer, "влал")));

        Assert.Equal(200, response.Status);
        var cookie = Assert.Single(
            response.ExtraHeaders.Where(h => h.Item1 == "Set-Cookie"),
            header => header.Item2.StartsWith("sc_admin="));
        Assert.Contains("HttpOnly", cookie.Item2);
        // Обычная сессия не подменяется: админский ключ — отдельная кука.
        Assert.DoesNotContain("sc_session=", cookie.Item2);
    }

    [Fact]
    public void AdminAuthNeedsSiteSession()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/auth", "{\"code\":\"" + AdminCode + "\"}"));

        Assert.Equal(401, response.Status);
        Assert.Contains("no_auth", Body(response));
    }

    // ── админка: данные закрыты ─────────────────────────────────────────────

    [Theory]
    [InlineData("GET", "/api/admin/bans", "")]
    [InlineData("GET", "/api/admin/players?id=67776161", "")]
    [InlineData("GET", "/api/admin/ids", "")]
    [InlineData("GET", "/api/admin/status", "")]
    public void AdminReadEndpointsNeedAdminKey(string method, string path, string body)
    {
        var router = NewRouter();
        var query = path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : "";
        var clean = path.Contains('?') ? path[..path.IndexOf('?')] : path;

        // Только сессия, без ключа — отказ.
        var withSession = router.Route(Request(
            method, clean, body.Length == 0 ? null : body,
            cookie: SessionCookie(AdminPlayer, "влал"), query: query));
        Assert.Equal(403, withSession.Status);

        // И даже админский id без кода — тоже отказ (вторая створка).
        Assert.Contains("admin_code_required", Body(withSession));
    }

    [Fact]
    public void AdminKeyFromAnotherPlayerDoesNotCount()
    {
        var router = NewRouter();

        // Ключ выдан одному игроку, а запрос идёт с сессией другого.
        var response = router.Route(Request(
            "GET", "/api/admin/bans",
            cookie: SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(PlainPlayer, "игрок")));

        Assert.Equal(403, response.Status);
    }

    [Fact]
    public void AdminKeyFromAnotherAddressDoesNotCount()
    {
        var _ = NewRouter();
        var token = _adminSessions.Create(
            AdminPlayer, "влал", IPAddress.Parse("10.0.0.1"), DateTime.UtcNow)!.Token;

        Assert.Null(_adminSessions.Find(token, IPAddress.Parse(RemoteIp)));
        Assert.NotNull(_adminSessions.Find(token, IPAddress.Parse("10.0.0.1")));
    }

    [Fact]
    public void AdminBansOpenWithBothKeyAndSession()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "GET", "/api/admin/bans",
            cookie: SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал")));

        Assert.Equal(200, response.Status);
        Assert.Contains("\"bans\"", Body(response));
    }

    // ── админка: поиск игрока ───────────────────────────────────────────────

    [Fact]
    public void PlayerLookupReturnsNickAndIps()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "GET", "/api/admin/players", query: "id=67776161",
            cookie: SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал")));

        var body = Body(response);
        Assert.Equal(200, response.Status);
        Assert.Contains("Ktulxy", body);
        Assert.Contains("192.168.1.50", body);
        Assert.Contains("\"known\":true", body);
    }

    [Fact]
    public void UnknownPlayerIsReportedAsUnknownNotClean()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "GET", "/api/admin/players", query: "id=123",
            cookie: SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал")));

        Assert.Contains("\"known\":false", Body(response));
    }

    // ── админка: бан и разбан ───────────────────────────────────────────────

    [Fact]
    public void BanUsesAdminNickFromSessionNotFromBody()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/bans",
            "{\"id\":67776161,\"duration\":\"3d\",\"reason\":\"читы\",\"bannedBy\":\"подделка\"}",
            cookie: SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал")));

        Assert.Equal(201, response.Status);
        var ban = Assert.Single(_bans);
        Assert.Equal("влал", ban.By);
        Assert.Equal("3d", ban.Duration);
        Assert.Equal("читы", ban.Reason);
    }

    [Fact]
    public void BanRequiresAdminKey()
    {
        var router = NewRouter();
        var response = router.Route(Request(
            "POST", "/api/admin/bans",
            "{\"id\":67776161,\"duration\":\"3d\",\"reason\":\"читы\"}",
            cookie: SessionCookie(AdminPlayer, "влал")));

        Assert.Equal(403, response.Status);
        Assert.Empty(_bans);
    }

    [Fact]
    public void UnbanRequiresAdminKey()
    {
        var router = NewRouter();
        var denied = router.Route(Request(
            "POST", "/api/admin/unban", "{\"id\":67776161}",
            cookie: SessionCookie(AdminPlayer, "влал")));
        Assert.Equal(403, denied.Status);

        var allowed = router.Route(Request(
            "POST", "/api/admin/unban", "{\"id\":67776161}",
            cookie: SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал")));
        Assert.Equal(200, allowed.Status);
        Assert.Contains("\"removed\":1", Body(allowed));
    }

    // ── админка: публичный id ───────────────────────────────────────────────

    [Fact]
    public void PublicIdIsSavedAndShownInsteadOfNumeric()
    {
        var router = NewRouter();
        var cookie = SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал");

        var saved = router.Route(Request(
            "POST", "/api/admin/public-id", "{\"id\":0,\"value\":\"vlal\"}", cookie: cookie));

        Assert.Equal(200, saved.Status);
        Assert.Equal("vlal", _overrides.CustomOf(AdminPlayer));

        // /api/me отдаёт подменённый id, но числовой остаётся в поле id.
        var me = router.Route(Request("GET", "/api/me", cookie: SessionCookie(AdminPlayer, "влал")));
        var body = Body(me);
        Assert.Contains("\"displayId\":\"vlal\"", body);
        Assert.Contains("\"id\":" + AdminPlayer, body);
    }

    [Fact]
    public void PublicIdRejectsSpecialCharactersAndDigitsOnly()
    {
        var router = NewRouter();
        var cookie = SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал");

        foreach (var bad in new[] { "vl_al", "влал", "vlal!", "12345", "a" })
        {
            var response = router.Route(Request(
                "POST", "/api/admin/public-id",
                "{\"id\":0,\"value\":\"" + bad + "\"}", cookie: cookie));
            Assert.Equal(400, response.Status);
        }

        Assert.Equal(0, _overrides.Count);
    }

    [Fact]
    public void EmptyPublicIdClearsOverride()
    {
        var router = NewRouter();
        var cookie = SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал");

        router.Route(Request("POST", "/api/admin/public-id", "{\"id\":0,\"value\":\"vlal\"}", cookie: cookie));
        Assert.Equal("vlal", _overrides.DisplayOf(AdminPlayer));

        router.Route(Request("POST", "/api/admin/public-id", "{\"id\":0,\"value\":\"\"}", cookie: cookie));
        Assert.Equal(AdminPlayer.ToString(), _overrides.DisplayOf(AdminPlayer));
    }

    [Fact]
    public void PublicIdsAreUniqueAcrossPlayers()
    {
        var overrides = new PublicIdOverrides();
        Assert.True(overrides.TrySet(1, "vlal", out _, out _));
        Assert.False(overrides.TrySet(2, "VLAL", out _, out var error));
        Assert.Contains("занят", error);

        // Тот же человек может переназначить себе — это не конфликт.
        Assert.True(overrides.TrySet(1, "vlal2", out _, out _));
    }

    [Fact]
    public void AdminLogoutDropsOnlyAdminKey()
    {
        var router = NewRouter();
        var cookie = SessionCookie(AdminPlayer, "влал") + "; " + AdminCookie(AdminPlayer, "влал");

        var response = router.Route(Request("POST", "/api/admin/logout", "{}", cookie: cookie));

        Assert.Equal(200, response.Status);
        Assert.Equal(0, _adminSessions.Count);
        // Обычная сессия жива: админка снимается отдельно.
        Assert.Equal(1, _sessions.Count);
    }
}
