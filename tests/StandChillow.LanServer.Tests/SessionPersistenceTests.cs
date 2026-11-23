using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Сессии переживают рестарт: на диске только SHA-256 токена, кука с TTL. Смена пароля
/// поднимает <c>authVersion</c> и убивает старые админ-сессии.
/// </summary>
public sealed class SessionPersistenceTests : IDisposable
{
    private const int PlayerId = 1000;
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly AdminStore _store;

    public SessionPersistenceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-sessions-" + Guid.NewGuid().ToString("N"));
        _store = new AdminStore(Path.Combine(_dir, "auth"));
        _store.Initialize(new[] { PlayerId }, Now);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    [Fact]
    public void AdminSession_SurvivesReloadAndKeepsTheCookieToken()
    {
        var path = Path.Combine(_dir, "sessions.json");
        var sessions = new AdminSessions(path, _store);
        var session = sessions.Create(PlayerId, "влал", 1, Now)!;

        var reloaded = new AdminSessions(path, _store);
        var found = reloaded.Find(session.Token, Now.AddDays(1));

        Assert.NotNull(found);
        Assert.Equal(PlayerId, found!.PlayerId);
        Assert.Equal("влал", found.Nick);
        Assert.Equal(1, found.AuthVersion);
    }

    [Fact]
    public void AdminSession_FileHoldsHashNotTheToken()
    {
        var path = Path.Combine(_dir, "sessions.json");
        var session = new AdminSessions(path, _store)
            .Create(PlayerId, "влал", 1, Now)!;

        var json = File.ReadAllText(path);

        Assert.DoesNotContain(session.Token, json);
        Assert.Contains(session.TokenHash, json);
    }

    [Fact]
    public void AdminSession_ExpiresAfterTtl()
    {
        var path = Path.Combine(_dir, "sessions.json");
        var sessions = new AdminSessions(path, _store, TimeSpan.FromMinutes(5));
        var session = sessions.Create(PlayerId, "влал", 1, Now)!;

        Assert.NotNull(sessions.Find(session.Token, Now.AddMinutes(4)));
        Assert.Null(sessions.Find(session.Token, Now.AddMinutes(6)));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public void AdminSession_DiesWhenPasswordVersionChanges()
    {
        var path = Path.Combine(_dir, "sessions.json");
        var sessions = new AdminSessions(path, _store);
        var session = sessions.Create(PlayerId, "влал", 1, Now)!;

        _store.RegeneratePassword(null, PlayerId, Now.AddMinutes(1), out _, out _);

        Assert.Null(sessions.Find(session.Token, Now.AddMinutes(2)));
    }

    [Fact]
    public void AdminSession_DropByPlayerKillsEverySession()
    {
        var sessions = new AdminSessions(Path.Combine(_dir, "sessions.json"), _store);
        var first = sessions.Create(PlayerId, "влал", 1, Now)!;
        var second = sessions.Create(PlayerId, "влал", 1, Now)!;

        Assert.Equal(2, sessions.DropByPlayer(PlayerId));
        Assert.Null(sessions.Find(first.Token, Now));
        Assert.Null(sessions.Find(second.Token, Now));
    }

    [Fact]
    public void SiteSession_SurvivesReloadAndStoresOnlyHash()
    {
        var path = Path.Combine(_dir, "site-sessions.json");
        var sessions = new SiteSessions(path);
        var session = sessions.Create(PlayerId, "влал", Now)!;

        var json = File.ReadAllText(path);
        Assert.DoesNotContain(session.Token, json);
        Assert.Contains(session.TokenHash, json);

        var reloaded = new SiteSessions(path);
        var found = reloaded.Find(session.Token, Now.AddDays(1));

        Assert.NotNull(found);
        Assert.Equal(PlayerId, found!.PlayerId);
        Assert.Equal(Now.AddDays(1), found.LastSeenUtc);
    }

    [Fact]
    public void SiteSession_HasThirtyDayTtl()
    {
        Assert.Equal(30, SiteSessions.TtlDays);

        var sessions = new SiteSessions();
        var session = sessions.Create(PlayerId, "влал", Now)!;

        Assert.NotNull(sessions.Find(session.Token, Now.AddDays(29)));
        Assert.Null(sessions.Find(session.Token, Now.AddDays(31)));
    }
}
