using System.Net;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Лимиты панели (PR 6 плана): частота, пауза за упорство, изоляция адресов.
/// Время подменено — тесты не спят.
/// </summary>
public sealed class WebRateLimiterTests
{
    private sealed class Clock
    {
        public DateTime Now { get; set; } =
            new(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);

        public void Advance(int seconds) => Now = Now.AddSeconds(seconds);
    }

    private static readonly IPAddress A = IPAddress.Parse("203.0.113.1");
    private static readonly IPAddress B = IPAddress.Parse("203.0.113.2");

    private static WebRateLimiter NewLimiter(Clock clock) => new(() => clock.Now)
    {
        RequestsPerSecond = 10,
        RefusalsBeforeBlock = 5,
        RefusalWindowSec = 10,
        BlockDurationSec = 60,
    };

    [Fact]
    public void AllowsUpToLimitPerSecond()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void DeniesEleventhRequestInSameSecond()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 10; i++)
            limiter.Check(A);

        var decision = limiter.Check(A);

        Assert.False(decision.Allowed);
        Assert.Equal(1, decision.RetryAfterSec);
        Assert.Equal("rate", decision.Reason);
    }

    [Fact]
    public void LimitRecoversNextSecond()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 10; i++)
            limiter.Check(A);
        Assert.False(limiter.Check(A).Allowed);

        clock.Advance(1);

        Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void FiveRefusalBurstsTriggerBlock()
    {
        // Пять секунд подряд, в каждой по залпу отказов. Один отказ в секунду — это
        // долбящий клиент, и он получает паузу; параллельный залп внутри секунды
        // считается одним штрафом (см. ParallelRefusalsAreOneStrike).
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 5; i++)
        {
            limiter.NoteRefusal(A);
            clock.Advance(1);
        }

        var blocked = limiter.Check(A);
        Assert.False(blocked.Allowed);
        Assert.Equal("blocked", blocked.Reason);
        Assert.True(blocked.RetryAfterSec > 1);
    }

    [Fact]
    public void BlockExpiresAfterItsDuration()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 5; i++)
        {
            limiter.NoteRefusal(A);
            clock.Advance(1);
        }
        Assert.Equal("blocked", limiter.Check(A).Reason);

        clock.Advance(61);

        Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void RefusalsOutsideWindowDoNotAccumulate()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        // Один отказ на окно и пауза больше окна: серии не набегает.
        for (var i = 0; i < 30; i++)
        {
            limiter.NoteRefusal(A); // отказ
            clock.Advance(11);      // уходим за окно отказов
        }

        // Никакого «накопленного штрафа» — следующий запрос проходит.
        var decision = limiter.Check(A);
        Assert.True(decision.Allowed);
        Assert.NotEqual("blocked", decision.Reason);
    }

    [Fact]
    public void AddressesAreIndependent()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 11; i++)
            limiter.Check(A);
        Assert.False(limiter.Check(A).Allowed);

        Assert.True(limiter.Check(B).Allowed);
    }

    [Fact]
    public void SuccessfulRequestResetsRefusalStreak()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        // 4 отказа: 10 проходов + 4 лишних.
        for (var i = 0; i < 14; i++)
            limiter.Check(A);

        clock.Advance(1);
        Assert.True(limiter.Check(A).Allowed); // сброс серии

        for (var i = 0; i < 10; i++)
            limiter.Check(A);

        // После сброса нужно снова 5 отказов, а не 1: один лишний запрос — только «rate»,
        // и адрес по-прежнему не заблокирован.
        var afterReset = limiter.Check(A);
        Assert.Equal("rate", afterReset.Reason);
        clock.Advance(1);
        Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void NoteRefusalCountsTowardBlock()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        // Упорный мусорный поток: по отказу в секунду — это долбящий клиент.
        for (var i = 0; i < 5; i++)
        {
            limiter.NoteRefusal(A);
            clock.Advance(1);
        }

        Assert.Equal("blocked", limiter.Check(A).Reason);
    }

    [Fact]
    public void ParallelRefusalsAreOneStrike()
    {
        // Одна загрузка страницы даёт несколько ответов сразу (аноним получает 401 от
        // /api/me и /api/avatar, плюс 404 от кривого пути). Это один человек и один
        // заход, а не пять попыток подряд: штраф за серию — один.
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 12; i++)
            limiter.NoteRefusal(A);

        Assert.True(limiter.Check(A).Allowed);
        Assert.NotEqual("blocked", limiter.Check(A).Reason);
    }

    [Fact]
    public void NeighboursBehindOneNatDoNotBlockEachOther()
    {
        // Пять человек из одного дома (один NAT) открыли сайт одновременно — каждый
        // получил 401 от профиля. Никто не должен попасть в блокировку из-за соседей.
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 5; i++)
            limiter.NoteRefusal(A);

        for (var i = 0; i < 10; i++)
            Assert.True(limiter.Check(A).Allowed);

        clock.Advance(1);
        Assert.True(limiter.Check(A).Allowed);
    }

    [Fact]
    public void SharedBucketNeverGetsTheLongPause()
    {
        // Панель открыта наружу мимо прокси: адрес у всех один, посетителей не различить.
        // Пусть его долбят непрерывно — остальные всё равно проходят: долгой паузы нет,
        // есть только посекундный лимит, который снимается через секунду.
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var second = 0; second < 10; second++)
        {
            for (var i = 0; i < 40; i++)
                limiter.Check(A, sharedBucket: true);
            clock.Advance(1);
        }

        var decision = limiter.Check(A, sharedBucket: true);
        Assert.True(decision.Allowed);
        Assert.NotEqual("blocked", decision.Reason);
    }

    [Fact]
    public void SharedBucketStillGetsThePerSecondLimit()
    {
        // Гарантия «сайт не залипнет» не значит «флуд бесплатен»: в пределах секунды
        // лишние запросы с общего адреса всё равно получают 429 и Retry-After 1.
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 10; i++)
            limiter.Check(A, sharedBucket: true);

        var decision = limiter.Check(A, sharedBucket: true);
        Assert.False(decision.Allowed);
        Assert.Equal("rate", decision.Reason);
        Assert.Equal(1, decision.RetryAfterSec);
    }

    [Fact]
    public void KnownVisitorStillGetsTheFullProtection()
    {
        // А вот когда посетитель известен (заголовок от своего прокси), долбящий адрес
        // получает обычную паузу — защита не ослабла.
        var clock = new Clock();
        var limiter = NewLimiter(clock);
        for (var i = 0; i < 5; i++)
        {
            limiter.NoteRefusal(A);
            clock.Advance(1);
        }

        Assert.Equal("blocked", limiter.Check(A).Reason);
    }

    [Fact]
    public void NullAddressIsAlwaysAllowed()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        for (var i = 0; i < 100; i++)
            Assert.True(limiter.Check(null).Allowed);
    }

    [Fact]
    public void IdleAddressesAreEvicted()
    {
        var clock = new Clock();
        var limiter = new WebRateLimiter(() => clock.Now) { IdleEvictionSec = 60 };

        limiter.Check(A);
        Assert.Equal(1, limiter.TrackedCount);

        clock.Advance(120);
        limiter.Check(B);

        Assert.Equal(1, limiter.TrackedCount);
    }

    [Fact]
    public void ParallelChecksDoNotExceedLimit()
    {
        var clock = new Clock();
        var limiter = NewLimiter(clock);

        var allowed = 0;
        Parallel.For(0, 200, _ =>
        {
            if (limiter.Check(A).Allowed)
                Interlocked.Increment(ref allowed);
        });

        Assert.Equal(10, allowed);
    }
}
