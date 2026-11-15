using System.Net;
using System.Text;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Сессии панели (#51): обмен кода из лобби, кука, профиль, выход. Сокетов нет —
/// проверяются статусы, заголовки и JSON, то есть всё, что раньше смотрели в браузере.
/// </summary>
public sealed class SiteSessionTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    // ── хранилище сессий ─────────────────────────────────────────────────────

    [Fact]
    public void Session_TokenIsLongAndUrlSafe()
    {
        var sessions = new SiteSessions();

        var session = sessions.Create(4242, "влал", Now);

        Assert.NotNull(session);
        Assert.Equal(4242, session!.PlayerId);
        Assert.Equal("влал", session.Nick);
        Assert.True(session.Token.Length >= 32);
        Assert.DoesNotContain("+", session.Token);
        Assert.DoesNotContain("/", session.Token);
        Assert.DoesNotContain("=", session.Token);
    }

    [Fact]
    public void Find_ReturnsSessionAndRefreshesLastSeen()
    {
        var sessions = new SiteSessions();
        var session = sessions.Create(4242, "влал", Now)!;

        var found = sessions.Find(session.Token, Now.AddMinutes(5));

        Assert.NotNull(found);
        Assert.Equal(Now.AddMinutes(5), found!.LastSeenUtc);
        Assert.Equal(Now, found.IssuedUtc);
    }

    [Fact]
    public void Find_UnknownTokenIsNull()
    {
        var sessions = new SiteSessions();
        sessions.Create(4242, "влал", Now)!;

        Assert.Null(sessions.Find("чужой", Now));
        Assert.Null(sessions.Find(null, Now));
        Assert.Null(sessions.Find("", Now));
    }

    [Fact]
    public void Drop_KillsOnlyThatSession()
    {
        var sessions = new SiteSessions();
        var first = sessions.Create(4242, "влал", Now)!;
        var second = sessions.Create(4242, "влал", Now)!;

        Assert.True(sessions.Drop(first.Token));

        Assert.Null(sessions.Find(first.Token, Now));
        Assert.NotNull(sessions.Find(second.Token, Now));
        Assert.Equal(1, sessions.Count);
    }

    [Fact]
    public void DropByPlayer_KillsEverySessionOfPlayer()
    {
        var sessions = new SiteSessions();
        sessions.Create(4242, "влал", Now);
        sessions.Create(4242, "влал", Now);
        sessions.Create(777, "другой", Now);

        // Бан или выход «откуда угодно» должен снять все токены человека, а не один.
        var dropped = sessions.DropByPlayer(4242);

        Assert.Equal(2, dropped);
        Assert.Equal(1, sessions.Count);
    }

    [Fact]
    public void Create_RejectsEmptyPlayer()
    {
        var sessions = new SiteSessions();
        Assert.Null(sessions.Create(0, "", Now));
        Assert.Equal(0, sessions.Count);
    }

    // ── маршруты ─────────────────────────────────────────────────────────────

    private static Dictionary<string, string> Headers(params (string Name, string Value)[] headers)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            map[name] = value;
        return map;
    }

    private static WebRequestContext Get(string path, params (string, string)[] headers) =>
        new("GET", path, "", Array.Empty<byte>(), IPAddress.Parse("203.0.113.1"), Headers(headers));

    private static WebRequestContext Post(string path, string json, params (string, string)[] headers) =>
        new("POST", path, "", Encoding.UTF8.GetBytes(json), IPAddress.Parse("203.0.113.1"),
            Headers(headers));

    private static string Body(WebResponse response) => Encoding.UTF8.GetString(response.Body);

    private static (WebPanelRouter Router, SiteSessions Sessions, SiteLoginCodes Codes) NewAuthRouter(
        string basePath = "")
    {
        var sessions = new SiteSessions();
        var codes = new SiteLoginCodes();
        var router = new WebPanelRouter(
            new RoomRegistry(),
            () => "<!doctype html>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                BasePath = basePath,
                Sessions = sessions,
                RedeemCode = (code, now) => codes.TryFind(code, now, out var entry) ? entry : null,
                CookiePath = WebPanelHost.CookiePathFor(basePath),
            });

        return (router, sessions, codes);
    }

    [Fact]
    public void Auth_ValidCodeGivesCookieAndProfile()
    {
        var (router, sessions, codes) = NewAuthRouter();
        var code = codes.Issue(4242, "влал", DateTime.UtcNow)!;

        var response = router.Route(Post("/api/auth", $"{{\"code\":\"{code}\"}}"));

        Assert.Equal(200, response.Status);
        Assert.Contains("\"nick\":\"влал\"", Body(response));
        Assert.Contains("\"id\":4242", Body(response));

        var cookie = Assert.Single(response.ExtraHeaders, h => h.Item1 == "Set-Cookie");
        Assert.Contains("sc_session=", cookie.Item2);
        Assert.Contains("HttpOnly", cookie.Item2);
        Assert.Contains("Path=/", cookie.Item2);
        Assert.Equal(1, sessions.Count);
    }

    [Fact]
    public void Auth_DeadOrUnknownCodeIsTheSameRefusal()
    {
        var (router, _, codes) = NewAuthRouter();
        var issued = DateTime.UtcNow;
        codes.Issue(4242, "влал", issued);
        var dead = codes.Issue(4242, "влал", issued)!;
        codes.Expire(4242);

        var unknown = router.Route(Post("/api/auth", "{\"code\":\"123456\"}"));
        var expired = router.Route(Post("/api/auth", $"{{\"code\":\"{dead}\"}}"));

        Assert.Equal(401, unknown.Status);
        Assert.Equal(401, expired.Status);
        // Текст один и тот же: сайт не подсказывает, какие коды существуют.
        Assert.Equal(Body(unknown), Body(expired));
        Assert.Contains("перезайдите в лобби", Body(unknown));
    }

    [Fact]
    public void Auth_EmptyAndBrokenBodyAreBadRequest()
    {
        var (router, _, _) = NewAuthRouter();

        Assert.Equal(400, router.Route(Post("/api/auth", "{\"code\":\"\"}")).Status);
        Assert.Equal(400, router.Route(Post("/api/auth", "не json")).Status);
    }

    [Fact]
    public void Me_WithoutCookieIsUnauthorized()
    {
        var (router, _, _) = NewAuthRouter();

        var response = router.Route(Get("/api/me"));

        Assert.Equal(401, response.Status);
        Assert.Contains("no_auth", Body(response));
    }

    [Fact]
    public void Me_WithCookieReturnsProfile()
    {
        var (router, sessions, _) = NewAuthRouter();
        var session = sessions.Create(4242, "влал", DateTime.UtcNow)!;

        var response = router.Route(
            Get("/api/me", ("Cookie", $"sc_session={session.Token}")));

        Assert.Equal(200, response.Status);
        Assert.Contains("\"nick\":\"влал\"", Body(response));
    }

    [Fact]
    public void Me_IgnoresForeignCookies()
    {
        var (router, sessions, _) = NewAuthRouter();
        sessions.Create(4242, "влал", DateTime.UtcNow);

        // Чужие куки в том же заголовке не должны нас сбить.
        var response = router.Route(
            Get("/api/me", ("Cookie", "a=1; sc_session=чужой; b=2")));

        Assert.Equal(401, response.Status);
    }

    [Fact]
    public void Logout_DropsSessionAndClearsCookie()
    {
        var (router, sessions, _) = NewAuthRouter();
        var session = sessions.Create(4242, "влал", DateTime.UtcNow)!;
        var headers = ("Cookie", $"sc_session={session.Token}");

        var response = router.Route(Post("/api/logout", "{}", headers));

        Assert.Equal(200, response.Status);
        var cookie = Assert.Single(response.ExtraHeaders, h => h.Item1 == "Set-Cookie");
        Assert.Contains("Max-Age=0", cookie.Item2);
        Assert.Equal(0, sessions.Count);
        Assert.Equal(401, router.Route(Get("/api/me", headers)).Status);
    }

    [Fact]
    public void CookiePath_RespectsBasePath()
    {
        var (router, sessions, codes) = NewAuthRouter("vlalchillow");
        var code = codes.Issue(4242, "влал", DateTime.UtcNow)!;

        var response = router.Route(Post("/api/auth", $"{{\"code\":\"{code}\"}}"));

        var cookie = Assert.Single(response.ExtraHeaders, h => h.Item1 == "Set-Cookie");
        Assert.Contains("Path=/vlalchillow", cookie.Item2);
        Assert.Equal(1, sessions.Count);
    }

    [Fact]
    public void Auth_WrongMethodIs405WithAllow()
    {
        var (router, _, _) = NewAuthRouter();

        var response = router.Route(Get("/api/auth"));

        Assert.Equal(405, response.Status);
        Assert.Contains(response.ExtraHeaders, h => h.Item1 == "Allow" && h.Item2 == "POST");
    }
}
