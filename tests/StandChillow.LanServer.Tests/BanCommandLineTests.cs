using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Разбор <c>/ban &lt;id&gt; &lt;срок&gt; [причина…]</c>.
/// </summary>
/// <remarks>
/// Пользователь: «сделай лучше баны только по айди, не по никам, нет отдельного /banid,
/// просто /ban &lt;id&gt; 6d».
/// </remarks>
public sealed class BanCommandLineTests
{
    [Fact]
    public void ParsesIdDurationAndFullReason()
    {
        Assert.True(BanCommandLine.TryParse(
            "1000001 6d лох сасал ахахах", out var id, out var duration, out var reason, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal(1000001, id);
        Assert.Equal("6d", duration);
        Assert.Equal("лох сасал ахахах", reason);
    }

    [Fact]
    public void ReasonKeepsCommasAndQuotes()
    {
        Assert.True(BanCommandLine.TryParse(
            "1000042 6h удали чит, а то забаню", out _, out _, out var reason, out _));
        Assert.Equal("удали чит, а то забаню", reason);
    }

    [Fact]
    public void ReasonIsOptional()
    {
        Assert.True(BanCommandLine.TryParse("1000042 30m", out var id, out var duration, out var reason, out _));
        Assert.Equal(1000042, id);
        Assert.Equal("30m", duration);
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void HashPrefixFromTheGameIsAccepted()
    {
        Assert.True(BanCommandLine.TryParse("#1000001 10m", out var id, out _, out _, out var error), error);
        Assert.Equal(1000001, id);
    }

    [Fact]
    public void NickIsRejectedAndPointsAtTheId()
    {
        Assert.False(BanCommandLine.TryParse("Ktulxy 30m софт", out _, out _, out _, out var error));

        Assert.Contains("Ktulxy", error);
        Assert.Contains("id", error);
        Assert.Contains("/ids", error);
    }

    [Fact]
    public void QuotedNickGetsTheSameHint()
    {
        Assert.False(BanCommandLine.TryParse("\"влал chillow\" 3d", out _, out _, out _, out var error));
        Assert.Contains("/ids", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1000001")]
    [InlineData("0 30m")]              // 0 — «id нет», не игрок
    [InlineData("100000001 30m")]      // выше диапазона
    [InlineData("999999 30m")]         // старый хеш ника — больше не адрес бана
    [InlineData("-5 30m")]
    [InlineData("1000001  ")]
    public void BadLinesAreRejected(string args)
    {
        Assert.False(BanCommandLine.TryParse(args, out _, out _, out _, out var error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Theory]
    [InlineData("1000000 30m", 1000000)]
    [InlineData("1000042 10m причина", 1000042)]
    [InlineData("100000000 14d", 100000000)]
    public void EdgeIdsAreAccepted(string args, int expected)
    {
        Assert.True(BanCommandLine.TryParse(args, out var id, out _, out _, out var error), error);
        Assert.Equal(expected, id);
    }

    [Fact]
    public void IdRangeMatchesTheAccountIdRange()
    {
        Assert.Equal(AccountStore.FirstId, BanCommandLine.MinId);
        Assert.Equal(AccountStore.MaxId, BanCommandLine.MaxId);
    }
}
