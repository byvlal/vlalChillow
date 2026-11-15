using System.Net;
using System.Text;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// «Моя комната» (#51): чья комната — видно только её владельцу, и завершить катку
/// может только он. Сокетов нет: проверяются права и ответы роутера.
/// </summary>
public sealed class WebRoomOwnerTests
{
    private readonly SiteSessions _sessions = new();
    private readonly Dictionary<int, string> _tokens = new();
    private readonly List<(int Number, int PlayerId)> _endCalls = new();
    private string? _endError;

    private WebPanelRouter NewRouter(out RoomRegistry rooms)
    {
        rooms = new RoomRegistry();
        return new WebPanelRouter(
            rooms,
            () => "<!doctype html>",
            new StatusSnapshotCache(),
            new WebPanelRouterOptions
            {
                OnlinePlayers = () => new List<(int Id, string Nick)>
                {
                    (1000, "влал"),
                    (1001, "a"),
                },
                Sessions = _sessions,
                RedeemCode = (_, _) => null,
                EndRoom = (number, playerId) =>
                {
                    _endCalls.Add((number, playerId));
                    return _endError;
                },
            });
    }

    private string Token(int playerId)
    {
        if (_tokens.TryGetValue(playerId, out var token))
            return token;

        token = _sessions.Create(playerId, "игрок " + playerId, DateTime.UtcNow)!.Token;
        _tokens[playerId] = token;
        return token;
    }

    private Dictionary<string, string> HeadersFor(int asPlayer)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (asPlayer > 0)
            map["Cookie"] = $"sc_session={Token(asPlayer)}";
        return map;
    }

    private WebRequestContext Get(string path, int asPlayer = 0) => new(
        "GET", path, "", Array.Empty<byte>(), IPAddress.Parse("203.0.113.1"), HeadersFor(asPlayer));

    private WebRequestContext Post(string path, string json, int asPlayer = 0) => new(
        "POST", path, "", Encoding.UTF8.GetBytes(json), IPAddress.Parse("203.0.113.1"),
        HeadersFor(asPlayer));

    private static string Body(WebResponse response) => Encoding.UTF8.GetString(response.Body);

    private static void CreateRoom(WebPanelRouter router, int asPlayer)
    {
        var response = router.Route(Post("/api/rooms", """{"mode":"2v2"}""", asPlayer));
        Assert.Equal(201, response.Status);
    }

    [Fact]
    public void OnlyOwnerSeesHisRoomAsMine()
    {
        var router = NewRouter(out _);
        CreateRoom(router, 1000);

        Assert.Contains("\"isMine\":true", Body(router.Route(Get("/api/rooms", 1000))));
        Assert.Contains("\"isMine\":false", Body(router.Route(Get("/api/rooms", 1001))));

        // Анонимный зритель не должен узнать, чья это комната.
        Assert.Contains("\"isMine\":false", Body(router.Route(Get("/api/rooms"))));
    }

    [Fact]
    public void EndRoom_WithoutSessionIsRejected()
    {
        var router = NewRouter(out _);
        CreateRoom(router, 1000);

        var response = router.Route(Post("/api/rooms/1/end", "{}"));

        Assert.Equal(401, response.Status);
        Assert.Contains("no_auth", Body(response));
        Assert.Empty(_endCalls);
    }

    [Fact]
    public void EndRoom_ForeignRoomIsForbidden()
    {
        var router = NewRouter(out _);
        CreateRoom(router, 1000);

        var response = router.Route(Post("/api/rooms/1/end", "{}", 1001));

        Assert.Equal(403, response.Status);
        Assert.Contains("not_owner", Body(response));
        Assert.Empty(_endCalls);
    }

    [Fact]
    public void EndRoom_OwnerFinishesHisOwnRoom()
    {
        var router = NewRouter(out _);
        CreateRoom(router, 1000);

        var response = router.Route(Post("/api/rooms/1/end", "{}", 1000));

        Assert.Equal(200, response.Status);
        Assert.Contains("\"ok\":true", Body(response));
        Assert.Equal(new[] { (1, 1000) }, _endCalls);
    }

    [Fact]
    public void EndRoom_UnknownNumberIs404()
    {
        var router = NewRouter(out _);

        var response = router.Route(Post("/api/rooms/77/end", "{}", 1000));

        Assert.Equal(404, response.Status);
        Assert.Contains("no_room", Body(response));
        Assert.Empty(_endCalls);
    }

    [Fact]
    public void EndRoom_HostRefusalIs409WithReason()
    {
        var router = NewRouter(out _);
        CreateRoom(router, 1000);
        _endError = "эту катку уже завершили — обнови страницу";

        var response = router.Route(Post("/api/rooms/1/end", "{}", 1000));

        Assert.Equal(409, response.Status);
        Assert.Contains("not_ended", Body(response));
        Assert.Contains("уже завершили", Body(response));
    }

    [Fact]
    public void EndRoom_WrongMethodIs405()
    {
        var router = NewRouter(out _);

        var response = router.Route(Get("/api/rooms/1/end"));

        Assert.Equal(405, response.Status);
        Assert.Contains(response.ExtraHeaders, h => h.Item1 == "Allow" && h.Item2 == "POST");
        Assert.Empty(_endCalls);
    }

    [Theory]
    [InlineData("/api/rooms/1/end", true, 1)]
    [InlineData("/api/rooms/42/end", true, 42)]
    [InlineData("/api/rooms/end", false, 0)]
    [InlineData("/api/rooms/-1/end", false, 0)]
    [InlineData("/api/rooms/x/end", false, 0)]
    [InlineData("/api/rooms/1/finish", false, 0)]
    public void EndRoom_PathParsing(string path, bool expected, int number)
    {
        Assert.Equal(expected, WebPanelRouter.TryParseRoomEndPath(path, out var parsed));
        Assert.Equal(number, parsed);
    }
}
