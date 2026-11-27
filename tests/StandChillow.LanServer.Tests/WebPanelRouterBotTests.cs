using System.Net;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Вход бота: <c>/api/bot/*</c> без ключа не существует, с ключом — та же панель
/// с теми же JSON и теми же правилами сессии. Админка боту закрыта.
/// </summary>
public sealed class WebPanelRouterBotTests : IDisposable
{
    private readonly string _dir;
    private readonly BotKeyStore _keys;
    private readonly SiteSessions _sessions = new();

    public WebPanelRouterBotTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-botrouter-" + Guid.NewGuid().ToString("N"));
        _keys = new BotKeyStore(Path.Combine(_dir, "auth", BotKeyStore.DirectoryName));
        Assert.True(_keys.LoadOrCreate());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    private WebPanelRouter NewRouter(
        string basePath = "", bool withKey = true, BotKeyStore? store = null)
    {
        var rooms = new RoomRegistry();
        return new WebPanelRouter(
            rooms,
            () => "<!doctype html><html><head></head><body></body></html>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                BasePath = basePath,
                BotKey = store ?? (withKey ? _keys : null),
                OnlinePlayers = () => new List<(int Id, string Nick)>
                {
                    (1000, "влал"),
                    (1001, "a"),
                },
                Sessions = _sessions,
                RedeemCode = (_, _) => null,
            });
    }

    private string Token(int playerId) =>
        _sessions.Create(playerId, "игрок " + playerId, DateTime.UtcNow)!.Token;

    private Dictionary<string, string> Headers(string? key = null, int asPlayer = 0)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (key is not null)
            map[WebPanelRouter.BotKeyHeader] = key;
        if (asPlayer > 0)
            map["Cookie"] = $"sc_session={Token(asPlayer)}";
        return map;
    }

    private WebRequestContext Get(
        string path, string? key = null, int asPlayer = 0) => new(
        "GET", path, "", Array.Empty<byte>(), IPAddress.Parse("203.0.113.1"),
        Headers(key, asPlayer));

    private WebRequestContext Post(
        string path, string json, string? key = null, int asPlayer = 0) => new(
        "POST", path, "", Encoding.UTF8.GetBytes(json), IPAddress.Parse("203.0.113.1"),
        Headers(key, asPlayer));

    private static string Body(WebResponse response) => Encoding.UTF8.GetString(response.Body);

    private static string CodeOf(WebResponse response)
    {
        using var doc = JsonDocument.Parse(response.Body);
        return doc.RootElement.GetProperty("code").GetString() ?? "";
    }

    [Fact]
    public void HandshakeWithoutKeyIsRejectedBeforeAnyData()
    {
        var router = NewRouter();

        var missing = router.Route(Get("/api/bot"));
        Assert.Equal(403, missing.Status);
        Assert.Equal("bad_bot_key", CodeOf(missing));

        var wrong = router.Route(Get("/api/bot", key: new string('0', 64)));
        Assert.Equal(403, wrong.Status);
        Assert.Equal("bad_bot_key", CodeOf(wrong));
    }

    [Fact]
    public void HandshakeWithKeyDescribesTheApi()
    {
        var router = NewRouter();
        var response = router.Route(Get("/api/bot", key: _keys.Current));

        Assert.Equal(200, response.Status);
        using var doc = JsonDocument.Parse(response.Body);
        Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("vlalchillow", doc.RootElement.GetProperty("service").GetString());
        Assert.Equal(WebPanelRouter.BotApiVersion, doc.RootElement.GetProperty("api").GetInt32());
    }

    [Fact]
    public void HandshakeIsGetOnly()
    {
        var router = NewRouter();
        var response = router.Route(Post("/api/bot", "{}", key: _keys.Current));

        Assert.Equal(405, response.Status);
        Assert.Contains(response.ExtraHeaders, h => h.Item1 == "Allow" && h.Item2 == "GET");
    }

    [Fact]
    public void StatusIsHiddenWithoutKeyAndIdenticalWithKey()
    {
        var router = NewRouter();

        var denied = router.Route(Get("/api/bot/api/status"));
        Assert.Equal(403, denied.Status);

        var normal = router.Route(Get("/api/status"));
        var bot = router.Route(Get("/api/bot/api/status", key: _keys.Current));

        Assert.Equal(200, bot.Status);
        Assert.Equal(Body(normal), Body(bot));
    }

    [Fact]
    public void RoomCreationGoesThroughTheSameSessionRules()
    {
        var router = NewRouter();

        var anonymous = router.Route(Post(
            "/api/bot/api/rooms", """{"mode":"2v2"}""", key: _keys.Current));
        Assert.Equal(401, anonymous.Status);
        Assert.Equal("no_auth", CodeOf(anonymous));

        var created = router.Route(Post(
            "/api/bot/api/rooms", """{"mode":"2v2","ownerId":1000}""",
            key: _keys.Current, asPlayer: 1000));
        Assert.Equal(201, created.Status);
        Assert.Contains("\"number\":1", Body(created));
        Assert.Contains("\"ownerId\":1000", Body(created));
    }

    [Fact]
    public void RoomsListIsIdenticalForSiteAndBot()
    {
        var router = NewRouter();
        router.Route(Post("/api/rooms", """{"mode":"2v2"}""", asPlayer: 1000));

        var site = router.Route(Get("/api/rooms", asPlayer: 1000));
        var bot = router.Route(Get("/api/bot/api/rooms", key: _keys.Current, asPlayer: 1000));

        Assert.Equal(200, bot.Status);
        Assert.Equal(Body(site), Body(bot));
        Assert.Contains("\"isMine\":true", Body(bot));
    }

    [Fact]
    public void AdminAreaIsClosedEvenForTheBot()
    {
        var router = NewRouter();

        var admin = router.Route(Get("/api/bot/api/admin", key: _keys.Current));
        Assert.Equal(403, admin.Status);
        Assert.Equal("bot_admin_forbidden", CodeOf(admin));

        var login = router.Route(Post(
            "/api/bot/api/admin/login", "{}", key: _keys.Current));
        Assert.Equal(403, login.Status);
        Assert.Equal("bot_admin_forbidden", CodeOf(login));
    }

    [Fact]
    public void UnknownBotPathStays404()
    {
        var router = NewRouter();
        Assert.Equal(404, router.Route(Get("/api/bot/api/nope", key: _keys.Current)).Status);
    }

    [Fact]
    public void BotEntryIsDisabledWhenNoKeyIsConfigured()
    {
        var router = NewRouter(withKey: false);
        var response = router.Route(Get("/api/bot", key: _keys.Current));

        Assert.Equal(503, response.Status);
        Assert.Equal("bot_disabled", CodeOf(response));
    }

    [Fact]
    public void BrokenKeyStoreIs503Not403()
    {
        var broken = new BotKeyStore(Path.Combine(_dir, "auth", "nokey"));
        var router = NewRouter(store: broken);

        var response = router.Route(Get("/api/bot", key: _keys.Current));

        Assert.Equal(503, response.Status);
        Assert.Equal("bot_key_unavailable", CodeOf(response));
    }

    [Fact]
    public void BasePathIsStrippedBeforeTheBotGate()
    {
        var router = NewRouter(basePath: "vlalchillow");
        var response = router.Route(Get(
            "/vlalchillow/api/bot/api/status", key: _keys.Current));

        Assert.Equal(200, response.Status);
    }
}
