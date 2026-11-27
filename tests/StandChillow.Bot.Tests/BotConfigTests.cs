using StandChillow.Bot;
using Xunit;

namespace StandChillow.Bot.Tests;

/// <summary>
/// Разбор .env: один сервер, адрес панели, ключ и адрес для игроков.
/// </summary>
public sealed class BotConfigTests
{
    private static Dictionary<string, string?> Base() => new()
    {
        ["TELEGRAM_BOT_TOKEN"] = "123456:ABC",
        ["BOT_KEY"] = new string('a', 64),
    };

    private static BotConfig Parse(Dictionary<string, string?> values) =>
        BotConfig.Parse(
            key => values.TryGetValue(key, out var value) ? value : null,
            "/tmp/test.env");

    [Fact]
    public void Defaults_PointToNovosibirsk()
    {
        var config = Parse(Base());

        Assert.Equal("https://skam.su/vlalchillow/api/bot", config.ApiBaseUrl);
        Assert.Equal("212.164.115.37:7778", config.GameAddress);
        Assert.Equal("Новосибирск", config.Label);
        Assert.Equal(new string('a', 64), config.BotKey);
    }

    [Fact]
    public void SiteUrl_IsNormalized()
    {
        var values = Base();
        values["SITE_URL"] = "https://skam.su/vlalchillow/";

        Assert.Equal(
            "https://skam.su/vlalchillow/api/bot", Parse(values).ApiBaseUrl);
    }

    [Fact]
    public void SiteUrl_AlreadyEndingWithApiBot_IsNotDoubled()
    {
        var values = Base();
        values["SITE_URL"] = "https://skam.su/vlalchillow/api/bot/";

        Assert.Equal(
            "https://skam.su/vlalchillow/api/bot", Parse(values).ApiBaseUrl);
    }

    [Fact]
    public void SiteUrl_MustBeHttp()
    {
        var values = Base();
        values["SITE_URL"] = "skam.su/vlalchillow";

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("SITE_URL", error.Message);
    }

    [Fact]
    public void GameAddress_DropsSchemeAndTrailingSlash()
    {
        var values = Base();
        values["GAME_ADDRESS"] = "https://212.164.115.37:7778/";

        Assert.Equal("212.164.115.37:7778", Parse(values).GameAddress);
    }

    [Fact]
    public void Label_CanBeOverridden()
    {
        var values = Base();
        values["BOT_LABEL"] = "Сибирь";

        Assert.Equal("Сибирь", Parse(values).Label);
    }

    [Fact]
    public void BotKey_IsRequired()
    {
        var values = Base();
        values.Remove("BOT_KEY");

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("BOT_KEY", error.Message);
    }

    [Fact]
    public void BadKey_IsAConfigError()
    {
        var values = Base();
        values["BOT_KEY"] = "not-hex";

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("BOT_KEY", error.Message);
        Assert.Contains("64 hex", error.Message);
    }

    [Fact]
    public void Key_IsNormalizedToLowercase()
    {
        var values = Base();
        values["BOT_KEY"] = new string('A', 64);

        Assert.Equal(new string('a', 64), Parse(values).BotKey);
    }

    [Fact]
    public void OldLocalKey_DoesNotReplaceBotKey()
    {
        var values = Base();
        values.Remove("BOT_KEY");
        values["LOCAL_BOT_KEY"] = new string('c', 64);

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("BOT_KEY", error.Message);
    }

    [Fact]
    public void OldServerVariables_AreIgnored()
    {
        var values = Base();
        values["LOCAL_BOT_KEY"] = new string('c', 64);
        values["LOCAL_GAME_ADDRESS"] = "209.25.141.16:6606";
        values["REMOTE_SITE_URL"] = "https://example.com";
        values["BOT_SERVERS"] = "local";

        var config = Parse(values);

        Assert.Equal("https://skam.su/vlalchillow/api/bot", config.ApiBaseUrl);
        Assert.Equal("212.164.115.37:7778", config.GameAddress);
    }

    [Fact]
    public void ApiTimeout_IsClamped()
    {
        var values = Base();
        values["BOT_API_TIMEOUT_SEC"] = "1";
        Assert.Equal(TimeSpan.FromSeconds(5), Parse(values).ApiTimeout);

        values["BOT_API_TIMEOUT_SEC"] = "999";
        Assert.Equal(TimeSpan.FromSeconds(120), Parse(values).ApiTimeout);
    }

    [Fact]
    public void Token_IsRequired()
    {
        var values = Base();
        values.Remove("TELEGRAM_BOT_TOKEN");

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("TELEGRAM_BOT_TOKEN", error.Message);
    }
}
