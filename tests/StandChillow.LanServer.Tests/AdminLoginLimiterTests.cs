using System.Net;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Лимит попыток входа в админку: 5 неудач за 5 минут на адрес. Успех сбрасывает счётчик,
/// окно скользящее — старые попытки не должны блокировать навсегда.
/// </summary>
public sealed class AdminLoginLimiterTests
{
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);
    private static readonly IPAddress Client = IPAddress.Parse("203.0.113.10");
    private static readonly IPAddress Other = IPAddress.Parse("203.0.113.11");

    [Fact]
    public void FiveFailures_AllowFiveAttempts_ThenBlock()
    {
        var limiter = new AdminLoginLimiter();

        for (var i = 0; i < AdminLoginLimiter.MaxAttempts; i++)
        {
            Assert.True(limiter.TryBegin(Client, Now), $"попытка {i + 1} должна быть разрешена");
            limiter.Fail(Client, Now);
        }

        Assert.False(limiter.TryBegin(Client, Now));
        Assert.True(limiter.RetryAfterSec(Client, Now) > 0);
    }

    [Fact]
    public void Success_ResetsTheCounter()
    {
        var limiter = new AdminLoginLimiter();
        for (var i = 0; i < AdminLoginLimiter.MaxAttempts; i++)
            limiter.Fail(Client, Now);

        limiter.Reset(Client);

        Assert.True(limiter.TryBegin(Client, Now));
        Assert.Equal(0, limiter.RetryAfterSec(Client, Now));
    }

    [Fact]
    public void TheOtherAddressKeepsItsOwnCounter()
    {
        var limiter = new AdminLoginLimiter();
        for (var i = 0; i < AdminLoginLimiter.MaxAttempts; i++)
            limiter.Fail(Client, Now);

        Assert.False(limiter.TryBegin(Client, Now));
        Assert.True(limiter.TryBegin(Other, Now));
    }

    [Fact]
    public void FailuresExpireAfterTheWindow()
    {
        var limiter = new AdminLoginLimiter();
        for (var i = 0; i < AdminLoginLimiter.MaxAttempts; i++)
            limiter.Fail(Client, Now);

        Assert.False(limiter.TryBegin(Client, Now.AddSeconds(1)));

        var afterWindow = Now + AdminLoginLimiter.Window + TimeSpan.FromSeconds(1);
        Assert.True(limiter.TryBegin(Client, afterWindow));
        Assert.Equal(0, limiter.RetryAfterSec(Client, afterWindow));
    }

    [Fact]
    public void NullAddressHasItsOwnBucket()
    {
        var limiter = new AdminLoginLimiter();
        for (var i = 0; i < AdminLoginLimiter.MaxAttempts; i++)
            limiter.Fail(null, Now);

        Assert.False(limiter.TryBegin(null, Now));
        Assert.True(limiter.TryBegin(Client, Now));
    }
}
