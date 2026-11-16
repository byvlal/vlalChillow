using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Отдача встроенных файлов оформления с поддержкой <c>Range</c>.
/// </summary>
/// <remarks>
/// Видео-fon без <c>206 Partial Content</c> не играет на части телефонов, поэтому
/// разбор диапазона проверяется отдельно, без сети и без бинарника.
/// </remarks>
public sealed class WebAssetsTests
{
    /// <summary>
    /// Экземпляр без файлов: разбор <c>Range</c> — чистая функция, ему содержимое не нужно.
    /// </summary>
    private static WebAssets NewAssets() =>
        new(typeof(WebAssetsTests).Assembly, new Dictionary<string, (string, string)>());

    [Theory]
    [InlineData("bytes=0-9", 100, 0, 9)]
    [InlineData("bytes=90-99", 100, 90, 99)]
    [InlineData("bytes=50-", 100, 50, 99)]
    [InlineData("bytes=-10", 100, 90, 99)]
    [InlineData("bytes=0-1000", 100, 0, 99)]
    [InlineData("bytes=-1000", 100, 0, 99)]
    [InlineData("Bytes=5-6", 100, 5, 6)]
    public void ParsesValidRanges(string header, int total, int expectedStart, int expectedEnd)
    {
        Assert.True(WebAssets.TryParseRange(header, total, out var start, out var end));
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData("")]
    [InlineData("bytes=")]
    [InlineData("bytes=abc-def")]
    [InlineData("bytes=100-200")]
    [InlineData("items=0-5")]
    [InlineData("bytes=-")]
    public void IgnoresInvalidRanges(string header)
    {
        // Кривой Range по стандарту игнорируется: файл отдаётся целиком.
        Assert.False(WebAssets.TryParseRange(header, 100, out var start, out var end));
        Assert.Equal(0, start);
        Assert.Equal(99, end);
    }

    [Fact]
    public void UnknownAssetIs404()
    {
        var assets = NewAssets();
        Assert.Equal(404, assets.Serve("/assets/media/nope.mp4", "").Status);
    }
}
