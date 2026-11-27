using System.Net;
using System.Text;
using StandChillow.Bot.Contracts;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Reports;
using StandChillow.LanServer.Stats;
using StandChillow.LanServer.Web;
using Xunit;
using WebResponse = StandChillow.LanServer.Web.WebResponse;

namespace StandChillow.Bot.Contracts.Tests;

/// <summary>
/// Контракт бота с панелью: настоящий <see cref="WebPanelRouter"/> в процессе,
/// запросы идут через <see cref="PanelClient"/> и поддельный HTTP-обработчик.
/// </summary>
/// <remarks>
/// Переименовали поле в JSON — падает этот тест, а не бот в бою. DTO помечены
/// <c>[JsonRequired]</c>, поэтому пропавшее поле — тоже ошибка, а не тихий ноль.
/// </remarks>
public sealed class PanelContractTests : IDisposable
{
    private const string StatusJson =
        """
        {"onlineLobby":1,"onlineMatch":2,"onlineTotal":3,"roomsCount":0,
         "roomsMax":20,"nextNumber":1,"gameListening":true,"gamePortFailed":false,
         "matchRunning":false,"mode":"","levels":"","publicAddress":"",
         "publicLobbyPort":7778,"publicMatchPort":7777,"localLobbyPort":7778,
         "localMatchPort":7777,"serverUtc":"2026-11-27T00:00:00.0000000Z"}
        """;

    private readonly string _dir;
    private readonly BotKeyStore _keys;
    private readonly SiteSessions _sessions = new();
    private readonly StatusSnapshotCache _status = new();
    private readonly MatchHistoryStore _history;
    private readonly ReportStore _reports;

    public PanelContractTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-bot-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _keys = new BotKeyStore(Path.Combine(_dir, "auth", BotKeyStore.DirectoryName));
        Assert.True(_keys.LoadOrCreate());

        _history = new MatchHistoryStore(Path.Combine(_dir, "matches.json"));
        _reports = new ReportStore(Path.Combine(_dir, "reports.json"));
        _status.Refresh(() => StatusJson);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch
        {
            // Тестовый мусор не должен ронять прогон.
        }
    }

    // ── стенд ───────────────────────────────────────────────────────────────

    private WebPanelRouter NewRouter()
    {
        return new WebPanelRouter(
            new RoomRegistry(),
            () => "<!doctype html><html><body></body></html>",
            _status,
            new WebPanelRouterOptions
            {
                BotKey = _keys,
                Sessions = _sessions,
                RedeemCode = (code, now) => code == "123456"
                    ? new SiteLoginCode(1000, "влал", code, now, now.AddMinutes(30))
                    : null,
                OnlinePlayers = () => new List<(int Id, string Nick)>
                {
                    (1000, "влал"),
                    (1001, "a"),
                },
                OnRoomCreated = _ => null,
                EndRoom = (_, _) => null,
                Matches = _history,
                Reports = _reports,
                ReportPlayerLookup = query => query switch
                {
                    "1000" => Target(1000, "влал", admin: true),
                    "1001" => Target(1001, "a", admin: false),
                    _ => null,
                },
                AdministrationList = () => new List<ReportTarget>
                {
                    Target(1001, "a", admin: true),
                },
                IsAdminById = id => id == 1001,
                CustomIdGet = id => id == 1000 ? "vlal" : null,
                DefaultModeId = "Ranked2v2",
            });
    }

    private static ReportTarget Target(int id, string nick, bool admin) => new(
        id, nick, null, id.ToString(), admin, "online", false, false, null,
        Array.Empty<string>(), null);

    private PanelClient NewClient(WebPanelRouter router, bool correctKey = true)
    {
        return new PanelClient(
            new PanelClientOptions
            {
                BaseUrl = new Uri("http://bot.test/api/bot"),
                BotKey = correctKey ? _keys.Current! : new string('0', 64),
            },
            new RouterHandler(router));
    }

    private async Task<(PanelClient Client, string Token)> AuthAsync()
    {
        var client = NewClient(NewRouter());
        var auth = await client.AuthenticateAsync("123456");
        return (client, auth.Token);
    }

    // ── рукопожатие, статус, каталог ────────────────────────────────────────

    [Fact]
    public async Task HandshakeStatusAndModesParse()
    {
        using var client = NewClient(NewRouter());

        var handshake = await client.HandshakeAsync();
        Assert.True(handshake.Ok);
        Assert.Equal("vlalchillow", handshake.Service);
        Assert.Equal(1, handshake.Api);

        var status = await client.GetStatusAsync();
        Assert.Equal(1, status.OnlineLobby);
        Assert.Equal(2, status.OnlineMatch);
        Assert.Equal(20, status.RoomsMax);
        Assert.True(status.GameListening);
        Assert.Equal(7778, status.LocalLobbyPort);

        var modes = await client.GetModesAsync();
        var ranked = Assert.Single(modes.Modes, mode => mode.Id == "Ranked2v2");
        Assert.True(ranked.Implemented);
        Assert.Contains("Prison 2x2", ranked.Levels);
        Assert.Contains(2, ranked.TeamSizes);
    }

    [Fact]
    public async Task BadBotKeyIsRejected()
    {
        using var client = NewClient(NewRouter(), correctKey: false);

        var error = await Assert.ThrowsAsync<PanelApiException>(
            () => client.HandshakeAsync());

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("bad_bot_key", error.Code);
    }

    [Fact]
    public async Task MissingRequiredFieldIsAnError()
    {
        using var client = new PanelClient(
            new PanelClientOptions
            {
                BaseUrl = new Uri("http://bot.test/api/bot"),
                BotKey = _keys.Current!,
            },
            new StaticHandler("{}"));

        var error = await Assert.ThrowsAsync<PanelApiException>(
            () => client.GetStatusAsync());

        Assert.Equal("bad_response", error.Code);
    }

    // ── вход и профиль ──────────────────────────────────────────────────────

    [Fact]
    public async Task AuthProfileAndLogout()
    {
        using var client = NewClient(NewRouter());

        var auth = await client.AuthenticateAsync("123456");
        Assert.Equal(1000, auth.Profile.Id);
        Assert.Equal("влал", auth.Profile.Nick);
        Assert.Equal("vlal", auth.Profile.DisplayId);
        Assert.False(string.IsNullOrWhiteSpace(auth.Token));

        var me = await client.WhoAmIAsync(auth.Token);
        Assert.Equal(1000, me.Id);
        Assert.Equal("влал", me.Nick);

        await client.LogoutAsync(auth.Token);

        var error = await Assert.ThrowsAsync<PanelApiException>(
            () => client.WhoAmIAsync(auth.Token));
        Assert.Equal(401, error.StatusCode);
        Assert.Equal("no_auth", error.Code);
    }

    [Fact]
    public async Task BadLobbyCodeIs401()
    {
        using var client = NewClient(NewRouter());

        var error = await Assert.ThrowsAsync<PanelApiException>(
            () => client.AuthenticateAsync("000000"));

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("bad_code", error.Code);
    }

    // ── комнаты ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateListCardAndEndRoom()
    {
        using var client = NewClient(NewRouter());
        var auth = await client.AuthenticateAsync("123456");

        var created = await client.CreateRoomAsync(
            auth.Token,
            new CreateRoomRequest
            {
                Mode = "Ranked2v2",
                Level = "Prison 2x2",
                Name = "тест",
                PerTeam = 2,
            });

        Assert.Equal(1, created.Number);
        Assert.Equal("Ranked2v2", created.Mode);
        Assert.Equal("тест", created.Name);
        Assert.Contains("/join", created.JoinCommand);

        var rooms = await client.GetRoomsAsync(auth.Token);
        var room = Assert.Single(rooms.Rooms);
        Assert.True(room.IsMine);
        Assert.Equal(1, room.Number);
        Assert.Equal(4, room.MaxPlayers);

        var card = await client.GetRoomAsync(created.Number);
        Assert.Equal(1, card.Number);
        Assert.Equal("тест", card.Name);
        Assert.Equal("Prison 2x2", card.Map);
        Assert.Equal(1000, card.Owner.Id);
        Assert.Equal(2, card.PerTeam);

        await client.EndRoomAsync(auth.Token, created.Number);
    }

    // ── матчи ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MatchesListAndDetailParse()
    {
        var now = DateTime.UtcNow;
        _history.Add(new MatchHistoryRecord
        {
            StartedUtc = now.AddMinutes(-40),
            EndedUtc = now,
            ModeId = "Ranked2v2",
            Map = "Prison 2x2",
            ScoreCt = 16,
            ScoreTr = 14,
            Winner = "CT",
            Rounds = 30,
            RoomNumber = 1,
            RoomName = "тест",
            Players =
            {
                new MatchHistoryPlayer
                {
                    Id = 1000, Nick = "влал", Team = "CT",
                    Kills = 20, Assists = 5, Deaths = 10, Score = 42,
                },
                new MatchHistoryPlayer
                {
                    Id = 1001, Nick = "a", Team = "T",
                    Kills = 14, Assists = 3, Deaths = 16, Score = 30,
                },
            },
        });

        using var client = NewClient(NewRouter());
        var list = await client.GetMatchesAsync(1000);

        var summary = Assert.Single(list.Matches);
        Assert.Equal(16, summary.ScoreCt);
        Assert.NotNull(summary.You);
        Assert.Equal(20, summary.You!.Kills);

        var detail = await client.GetMatchAsync(summary.Id);
        Assert.Equal(2, detail.Players.Count);
        Assert.Contains(detail.Players, player => player.Id == 1000 && player.Nick == "влал");
    }

    // ── игрок, жалобы, администрация ────────────────────────────────────────

    [Fact]
    public async Task PlayerReportAndAdministrationParse()
    {
        using var client = NewClient(NewRouter());
        var auth = await client.AuthenticateAsync("123456");

        var player = await client.GetPlayerAsync("1001");
        Assert.Equal(1001, player.Id);
        Assert.Equal("a", player.Nick);
        Assert.Equal("online", player.Status);

        var report = await client.SubmitReportAsync(
            auth.Token, 1001, "cheat", "тестовая жалоба");
        Assert.True(report.Ok);
        Assert.Equal(1001, report.Report.TargetId);
        Assert.Equal("cheat", report.Report.Category);

        var mine = await client.GetMyReportsAsync(auth.Token);
        var item = Assert.Single(mine.Items);
        Assert.Equal(1001, item.TargetId);
        Assert.Equal("влал", item.ReporterNick);

        var admins = await client.GetAdministrationAsync();
        var admin = Assert.Single(admins.Admins);
        Assert.Equal(1001, admin.Id);
        Assert.True(admin.Admin);
    }

    // ── поддельный HTTP ─────────────────────────────────────────────────────

    private sealed class RouterHandler : HttpMessageHandler
    {
        private readonly WebPanelRouter _router;

        public RouterHandler(WebPanelRouter router) => _router = router;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
                headers[header.Key] = string.Join(", ", header.Value);
            if (request.Content is not null)
            {
                foreach (var header in request.Content.Headers)
                    headers[header.Key] = string.Join(", ", header.Value);
            }

            var uri = request.RequestUri!;
            var context = new WebRequestContext(
                request.Method.Method,
                uri.AbsolutePath,
                uri.Query.TrimStart('?'),
                body,
                IPAddress.Loopback,
                headers);

            var response = _router.Route(context);
            return ToHttp(response);
        }
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly string _json;

        public StaticHandler(string json) => _json = json;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
            return Task.FromResult(response);
        }
    }

    private static HttpResponseMessage ToHttp(WebResponse response)
    {
        var message = new HttpResponseMessage((HttpStatusCode)response.Status)
        {
            Content = new ByteArrayContent(response.Body),
        };
        message.Content.Headers.TryAddWithoutValidation("Content-Type", response.ContentType);

        foreach (var (name, value) in response.ExtraHeaders)
            message.Headers.TryAddWithoutValidation(name, value);

        if (response.RetryAfterSec > 0)
        {
            message.Headers.RetryAfter =
                new System.Net.Http.Headers.RetryConditionHeaderValue(
                    TimeSpan.FromSeconds(response.RetryAfterSec));
        }

        return message;
    }
}
