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
        _store.Initialize(new[] { PlayerId }, Array.Empty<int>(), Now);
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
        var found = reloaded.Find(session.Token, session.DeviceToken, Now.AddDays(1));

        Assert.NotNull(found);
        Assert.Equal(PlayerId, found!.PlayerId);
        Assert.Equal("влал", found.Nick);
        Assert.Equal(1, found.AuthVersion);
    }

    [Fact]
    public void AdminSession_RequiresTheSameDeviceCookie()
    {
        var sessions = new AdminSessions(Path.Combine(_dir, "sessions.json"), _store);
        var session = sessions.Create(PlayerId, "влал", 1, Now)!;

        Assert.Null(sessions.Find(session.Token, "чужое-устройство", Now));
        Assert.Null(sessions.Find(session.Token, null, Now));
        Assert.Null(sessions.Find(null, session.DeviceToken, Now));
        Assert.NotNull(sessions.Find(session.Token, session.DeviceToken, Now));
    }

    [Fact]
    public void AdminSession_HasThirtyDayTtlAndSlidesOnRenew()
    {
        Assert.Equal(30, AdminSessions.TtlDays);

        var path = Path.Combine(_dir, "sessions.json");
        var sessions = new AdminSessions(path, _store);
        var session = sessions.Create(PlayerId, "влал", 1, Now)!;

        var renewed = sessions.Renew(session.TokenHash, Now.AddDays(20));
        Assert.NotNull(renewed);
        Assert.Equal(Now.AddDays(50), renewed!.ExpiresUtc);
        Assert.NotNull(sessions.Find(session.Token, session.DeviceToken, Now.AddDays(45)));

        // Отдельная сессия: проверка истечения не должна убивать продлённую.
        var expiring = sessions.Create(PlayerId, "влал", 1, Now)!;
        Assert.Null(sessions.Find(expiring.Token, expiring.DeviceToken, Now.AddDays(31)));
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

        Assert.NotNull(sessions.Find(session.Token, session.DeviceToken, Now.AddMinutes(4)));
        Assert.Null(sessions.Find(session.Token, session.DeviceToken, Now.AddMinutes(6)));
        Assert.Equal(0, sessions.Count);
    }

    [Fact]
    public void AdminSession_DiesWhenPasswordVersionChanges()
    {
        var path = Path.Combine(_dir, "sessions.json");
        var sessions = new AdminSessions(path, _store);
        var session = sessions.Create(PlayerId, "влал", 1, Now)!;

        _store.RegeneratePassword(null, PlayerId, Now.AddMinutes(1), out _, out _);

        Assert.Null(sessions.Find(session.Token, session.DeviceToken, Now.AddMinutes(2)));
    }

    [Fact]
    public void AdminSession_DropByPlayerKillsEverySession()
    {
        var sessions = new AdminSessions(Path.Combine(_dir, "sessions.json"), _store);
        var first = sessions.Create(PlayerId, "влал", 1, Now)!;
        var second = sessions.Create(PlayerId, "влал", 1, Now)!;

        Assert.Equal(2, sessions.DropByPlayer(PlayerId));
        Assert.Null(sessions.Find(first.Token, first.DeviceToken, Now));
        Assert.Null(sessions.Find(second.Token, second.DeviceToken, Now));
    }

    [Fact]
    public void TestAutoSession_KeepsItsMarkAcrossReload()
    {
        var path = Path.Combine(_dir, "sessions.json");
        var sessions = new AdminSessions(path, _store);
        var session = sessions.Create(
            PlayerId, "влал", 1, Now, "203.0.113.10", "", testAuto: true)!;

        var reloaded = new AdminSessions(path, _store);
        var found = reloaded.Find(session.Token, session.DeviceToken, Now.AddMinutes(1));

        Assert.NotNull(found);
        Assert.True(found!.TestAuto);

        var normal = sessions.Create(PlayerId, "влал", 1, Now)!;
        Assert.False(normal.TestAuto);
    }

    [Fact]
    public void TestAutoSessions_AreCappedPerAddress()
    {
        var sessions = new AdminSessions(null, _store);

        for (var i = 0; i < AdminSessions.MaxTestSessionsPerIp + 2; i++)
            Assert.NotNull(sessions.Create(
                PlayerId, "влал", 1, Now.AddSeconds(i), "203.0.113.10", "", testAuto: true));

        Assert.Equal(AdminSessions.MaxTestSessionsPerIp, sessions.Count);

        // Другой адрес не вытесняет чужие тестовые сессии.
        Assert.NotNull(sessions.Create(
            PlayerId, "влал", 1, Now, "203.0.113.11", "", testAuto: true));
        Assert.Equal(AdminSessions.MaxTestSessionsPerIp + 1, sessions.Count);
    }

    [Fact]
    public void FullStore_EvictsTestSessionInsteadOfRefusingALogin()
    {
        var sessions = new AdminSessions(null, _store);
        var test = sessions.Create(
            PlayerId, "тест", 1, Now, "203.0.113.10", "", testAuto: true)!;

        while (sessions.Count < AdminSessions.MaxSessions)
            Assert.NotNull(sessions.Create(PlayerId, "влал", 1, Now));

        var login = sessions.Create(PlayerId, "влал", 1, Now);

        Assert.NotNull(login);
        Assert.Equal(AdminSessions.MaxSessions, sessions.Count);
        Assert.Null(sessions.Find(test.Token, test.DeviceToken, Now));
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
        // Find больше не пишет LastSeen: сессию продлевает только Renew.
        Assert.Equal(Now, found.LastSeenUtc);

        var renewed = reloaded.Renew(session.Token, Now.AddDays(1))!;
        Assert.Equal(Now.AddDays(1), renewed.LastSeenUtc);
        Assert.Equal(Now.AddDays(31), renewed.ExpiresUtc);
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
