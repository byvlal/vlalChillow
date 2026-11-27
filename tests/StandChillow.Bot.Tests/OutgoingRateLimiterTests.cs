using StandChillow.Bot;
using Xunit;

namespace StandChillow.Bot.Tests;

/// <summary>
/// Лимиты Telegram: 1 сообщение в секунду в чат, 20 в минуту в группу,
/// 25 в секунду на всего бота. Часы поддельные, поэтому тесты не спят.
/// </summary>
public sealed class OutgoingRateLimiterTests
{
    private sealed class FakeClock
    {
        public DateTime Now { get; set; } =
            new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public OutgoingRateLimiter Create() => new(
            clock: () => Now,
            delay: (wait, _) =>
            {
                Now += wait;
                return Task.CompletedTask;
            });
    }

    [Fact]
    public async Task FirstCall_DoesNotWait()
    {
        var clock = new FakeClock();
        var limiter = clock.Create();
        var start = clock.Now;

        await limiter.WaitAsync(1, isGroup: false, CancellationToken.None);

        Assert.Equal(start, clock.Now);
    }

    [Fact]
    public async Task SecondCall_InSameChat_WaitsAtLeastOneSecond()
    {
        var clock = new FakeClock();
        var limiter = clock.Create();

        await limiter.WaitAsync(1, isGroup: false, CancellationToken.None);
        var first = clock.Now;
        await limiter.WaitAsync(1, isGroup: false, CancellationToken.None);

        Assert.True(clock.Now - first >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task DifferentChats_DoNotWaitForEachOther()
    {
        var clock = new FakeClock();
        var limiter = clock.Create();
        var start = clock.Now;

        await limiter.WaitAsync(1, isGroup: false, CancellationToken.None);
        await limiter.WaitAsync(2, isGroup: false, CancellationToken.None);

        Assert.Equal(start, clock.Now);
    }

    [Fact]
    public async Task Group_MoreThan20PerMinute_IsDelayed()
    {
        var clock = new FakeClock();
        var limiter = clock.Create();
        var start = clock.Now;

        for (var i = 0; i < 20; i++)
            await limiter.WaitAsync(10, isGroup: true, CancellationToken.None);

        var after20 = clock.Now;
        await limiter.WaitAsync(10, isGroup: true, CancellationToken.None);

        Assert.True(clock.Now - after20 >= TimeSpan.FromSeconds(39));
        Assert.True(clock.Now - start >= TimeSpan.FromSeconds(59));
    }

    [Fact]
    public async Task PrivateChat_IsNotLimitedTo20PerMinute()
    {
        var clock = new FakeClock();
        var limiter = clock.Create();
        var start = clock.Now;

        for (var i = 0; i < 21; i++)
            await limiter.WaitAsync(10, isGroup: false, CancellationToken.None);

        Assert.True(clock.Now - start < TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Global_MoreThan25PerSecond_IsDelayed()
    {
        var clock = new FakeClock();
        var limiter = clock.Create();
        var start = clock.Now;

        for (var i = 0; i < 25; i++)
            await limiter.WaitAsync(100 + i, isGroup: false, CancellationToken.None);

        Assert.Equal(start, clock.Now);

        await limiter.WaitAsync(999, isGroup: false, CancellationToken.None);

        Assert.True(clock.Now - start >= TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Cancellation_StopsTheWait()
    {
        var clock = new FakeClock();
        var limiter = new OutgoingRateLimiter(
            clock: () => clock.Now,
            delay: (_, ct) => Task.FromCanceled(ct));

        await limiter.WaitAsync(1, isGroup: false, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => limiter.WaitAsync(1, isGroup: false, cts.Token));
    }
}
