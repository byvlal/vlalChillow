using StandChillow.LanServer.Net.Lobby;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Коды входа на сайт (#51): выдача в лобби, один код на игрока, срок 30 минут и
/// мгновенное протухание при выходе из лобби. Сокетов нет — проверяется только решение.
/// </summary>
public sealed class SiteLoginCodeTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Issue_ReturnsSixDigits()
    {
        var codes = new SiteLoginCodes();

        var code = codes.Issue(4242, "влал", Now);

        Assert.NotNull(code);
        Assert.Equal(6, code!.Length);
        Assert.All(code, c => Assert.True(c is >= '0' and <= '9'));
    }

    [Fact]
    public void Issue_RejectsNonPositivePlayerId()
    {
        var codes = new SiteLoginCodes();

        Assert.Null(codes.Issue(0, "", Now));
        Assert.Null(codes.Issue(-7, "кто-то", Now));
        Assert.Equal(0, codes.Count);
    }

    [Fact]
    public void Reissue_KillsPreviousCodeImmediately()
    {
        var codes = new SiteLoginCodes();
        var first = codes.Issue(4242, "влал", Now);
        Assert.NotNull(first);

        var second = codes.Issue(4242, "влал", Now.AddMinutes(1));

        // «Если код пересоздался — старый сразу удаляется и невозможно его поюзать» (#51).
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
        Assert.False(codes.TryFind(first, Now.AddMinutes(1), out _));
        Assert.True(codes.TryFind(second, Now.AddMinutes(1), out var entry));
        Assert.Equal(4242, entry!.PlayerId);
        Assert.Equal(1, codes.Count);
    }

    [Fact]
    public void Code_LivesThirtyMinutesAndNoLonger()
    {
        var codes = new SiteLoginCodes();
        var code = codes.Issue(4242, "влал", Now);

        Assert.True(codes.TryFind(code, Now.AddMinutes(29), out _));
        Assert.False(codes.TryFind(code, Now.AddMinutes(31), out _));

        // Протухший код не находится и у самого игрока.
        Assert.False(codes.TryGet(4242, Now.AddMinutes(31), out _));
    }

    [Fact]
    public void Expire_KillsCodeBeforeItsTime()
    {
        var codes = new SiteLoginCodes();
        var code = codes.Issue(4242, "влал", Now);
        Assert.True(codes.TryFind(code, Now, out _));

        codes.Expire(4242);

        Assert.False(codes.TryFind(code, Now, out _));
        Assert.Equal(0, codes.Count);
    }

    [Fact]
    public void Expire_UnknownPlayerIsNoOp()
    {
        var codes = new SiteLoginCodes();
        codes.Issue(4242, "влал", Now);

        codes.Expire(999);
        codes.Expire(0);

        Assert.Equal(1, codes.Count);
    }

    [Fact]
    public void TryFind_UnknownCodeIsSameAsDeadOne()
    {
        var codes = new SiteLoginCodes();
        codes.Issue(4242, "влал", Now);

        // Сайт не различает «не было» и «протух»: ответ человеку один и тот же.
        Assert.False(codes.TryFind("000000", Now, out _));
        Assert.False(codes.TryFind(null, Now, out _));
        Assert.False(codes.TryFind("   ", Now, out _));
    }

    [Theory]
    [InlineData("123456", "123456")]
    [InlineData(" 123456 ", "123456")]
    [InlineData("", null)]
    [InlineData("12345", null)]
    [InlineData("1234567", null)]
    [InlineData("12a456", null)]
    [InlineData("12 456", null)]
    public void Normalize_AcceptsOnlySixDigits(string? input, string? expected)
    {
        Assert.Equal(expected, SiteLoginCodes.Normalize(input));
    }

    [Fact]
    public void Prune_DropsOnlyExpired()
    {
        var codes = new SiteLoginCodes();
        var old = codes.Issue(1, "первый", Now);
        var fresh = codes.Issue(2, "второй", Now.AddMinutes(20));
        Assert.Equal(2, codes.Count);

        var dropped = codes.Prune(Now.AddMinutes(31));

        Assert.Equal(1, dropped);
        Assert.Equal(1, codes.Count);
        Assert.False(codes.TryFind(old, Now.AddMinutes(31), out _));
        Assert.True(codes.TryFind(fresh, Now.AddMinutes(31), out _));
    }

    [Fact]
    public void DifferentPlayers_GetDifferentCodes()
    {
        var codes = new SiteLoginCodes();

        var a = codes.Issue(1, "первый", Now);
        var b = codes.Issue(2, "второй", Now);

        Assert.NotEqual(a, b);
        Assert.True(codes.TryFind(a, Now, out var first));
        Assert.True(codes.TryFind(b, Now, out var second));
        Assert.Equal("первый", first!.Nick);
        Assert.Equal("второй", second!.Nick);
    }

    [Fact]
    public void Ttl_ComesFromConfiguredValue()
    {
        var codes = new SiteLoginCodes { Ttl = TimeSpan.FromMinutes(5) };
        var code = codes.Issue(4242, "влал", Now);

        Assert.True(codes.TryFind(code, Now.AddMinutes(4), out _));
        Assert.False(codes.TryFind(code, Now.AddMinutes(6), out _));
    }
}
