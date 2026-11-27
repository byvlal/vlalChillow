using StandChillow.Bot;
using Xunit;

namespace StandChillow.Bot.Tests;

/// <summary>
/// Разбор .env: какие серверы включаются, в каком порядке и с какими адресами.
/// </summary>
public sealed class BotConfigTests
{
    private static Dictionary<string, string?> Base() => new()
    {
        ["TELEGRAM_BOT_TOKEN"] = "123456:ABC",
        ["LOCAL_BOT_KEY"] = new string('a', 64),
        ["LOCAL_GAME_ADDRESS"] = "209.25.141.16:6606",
        ["REMOTE_BOT_KEY"] = new string('b', 64),
        ["REMOTE_SITE_URL"] = "https://skam.su/vlalchillow",
        ["REMOTE_GAME_ADDRESS"] = "212.164.115.37:7778",
    };

    private static BotConfig Parse(Dictionary<string, string?> values) =>
        BotConfig.Parse(
            key => values.TryGetValue(key, out var value) ? value : null,
            "/tmp/test.env");

    [Fact]
    public void BothKeys_EnableBothServers_RemoteFirst()
    {
        var config = Parse(Base());

        Assert.Equal(2, config.Servers.Count);
        Assert.Equal("remote", config.Servers[0].Id);
        Assert.Equal("local", config.Servers[1].Id);
        Assert.Equal(
            "https://skam.su/vlalchillow/api/bot", config.Servers[0].ApiBaseUrl);
        Assert.Equal("http://127.0.0.1:8080/api/bot", config.Servers[1].ApiBaseUrl);
        Assert.Equal("212.164.115.37:7778", config.Servers[0].GameAddress);
        Assert.Equal("209.25.141.16:6606", config.Servers[1].GameAddress);
    }

    [Fact]
    public void FindServer_ReturnsProfileOrNull()
    {
        var config = Parse(Base());

        Assert.NotNull(config.FindServer("remote"));
        Assert.NotNull(config.FindServer("local"));
        Assert.Null(config.FindServer("nope"));
        Assert.Null(config.FindServer(null));
    }

    [Fact]
    public void BotModeLocal_KeepsOnlyLocal()
    {
        var values = Base();
        values["BOT_MODE"] = "local";

        var config = Parse(values);

        var server = Assert.Single(config.Servers);
        Assert.Equal("local", server.Id);
    }

    [Fact]
    public void BotModeRemote_KeepsOnlyRemote()
    {
        var values = Base();
        values["BOT_MODE"] = "remote";

        var config = Parse(values);

        var server = Assert.Single(config.Servers);
        Assert.Equal("remote", server.Id);
    }

    [Fact]
    public void BotServers_WinsOverBotMode()
    {
        var values = Base();
        values["BOT_MODE"] = "local";
        values["BOT_SERVERS"] = "remote";

        var config = Parse(values);

        var server = Assert.Single(config.Servers);
        Assert.Equal("remote", server.Id);
    }

    [Fact]
    public void EmptyBotServers_MeansAllKeyedServers()
    {
        var values = Base();
        values["BOT_SERVERS"] = "";

        var config = Parse(values);

        Assert.Equal(2, config.Servers.Count);
    }

    [Fact]
    public void BotServersRemote_WithoutKey_IsAConfigError()
    {
        var values = Base();
        values.Remove("REMOTE_BOT_KEY");
        values["BOT_SERVERS"] = "remote";

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("REMOTE_BOT_KEY", error.Message);
    }

    [Fact]
    public void NoKeys_IsAConfigError()
    {
        var values = Base();
        values.Remove("LOCAL_BOT_KEY");
        values.Remove("REMOTE_BOT_KEY");

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("LOCAL_BOT_KEY", error.Message);
        Assert.Contains("REMOTE_BOT_KEY", error.Message);
    }

    [Fact]
    public void BadKey_IsAConfigError()
    {
        var values = Base();
        values["LOCAL_BOT_KEY"] = "not-hex";

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("LOCAL_BOT_KEY", error.Message);
        Assert.Contains("64 hex", error.Message);
    }

    [Fact]
    public void UnknownServerName_IsAConfigError()
    {
        var values = Base();
        values["BOT_SERVERS"] = "local,prod";

        var error = Assert.Throws<BotConfigException>(() => Parse(values));
        Assert.Contains("BOT_SERVERS", error.Message);
    }

    [Fact]
    public void SiteUrl_AlreadyEndingWithApiBot_IsNotDoubled()
    {
        var values = Base();
        values["REMOTE_SITE_URL"] = "https://skam.su/vlalchillow/api/bot/";

        var config = Parse(values);

        Assert.Equal(
            "https://skam.su/vlalchillow/api/bot", config.Servers[0].ApiBaseUrl);
    }

    [Fact]
    public void GameAddress_DropsSchemeAndTrailingSlash()
    {
        var values = Base();
        values["REMOTE_GAME_ADDRESS"] = "https://212.164.115.37:7778/";

        var config = Parse(values);

        Assert.Equal("212.164.115.37:7778", config.Servers[0].GameAddress);
    }

    [Fact]
    public void Labels_CanBeOverridden()
    {
        var values = Base();
        values["REMOTE_LABEL"] = "Новосибирск";
        values["LOCAL_LABEL"] = "стенд";

        var config = Parse(values);

        Assert.Equal("Новосибирск", config.Servers[0].Label);
        Assert.Equal("стенд", config.Servers[1].Label);
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
