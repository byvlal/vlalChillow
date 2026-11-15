using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Права на бан-команды: по умолчанию только админ, независимо от
/// <c>admin.adminOnlyCommands</c>; запуск матча при этом остаётся открытым.
/// </summary>
public sealed class BanCommandAccessTests
{
    private static ServerConfig Config(params int[] admins)
    {
        var cfg = new ServerConfig();
        cfg.Admin.Ids.AddRange(admins);
        cfg.Normalize();
        return cfg;
    }

    [Theory]
    [InlineData("/ban Ktulxy 30m софт")]
    [InlineData("/banid 671488 30m софт")]
    [InlineData("/банид 671488 30m софт")]
    [InlineData("/unban Ktulxy")]
    [InlineData("/unbanid 671488")]
    [InlineData("/bans")]
    [InlineData("/ids")]
    [InlineData("/бан Ktulxy 30m софт")]
    [InlineData("/разбан Ktulxy")]
    [InlineData("/баны")]
    [InlineData("/айди")]
    public void BanCommandsAreAdminOnlyByDefault(string message)
    {
        var cfg = Config(25_354_325);

        Assert.False(
            CommandAccessPolicy.IsAllowed(cfg, 65_054_804, message, out var reason));
        Assert.Contains("только для админов", reason);

        Assert.True(CommandAccessPolicy.IsAllowed(cfg, 25_354_325, message, out var adminReason));
        Assert.Equal(string.Empty, adminReason);
    }

    [Fact]
    public void AdminCheckUsesTheServerId()
    {
        var cfg = Config(MatchPublicId.ForNick("Влал"));

        // Тот же id независимо от регистра ника: его считает сервер.
        Assert.True(CommandAccessPolicy.IsAllowed(
            cfg, MatchPublicId.ForNick("  влАЛ "), "/bans", out _));
        Assert.False(CommandAccessPolicy.IsAllowed(
            cfg, 65_054_804, "/bans", out _));
    }

    [Fact]
    public void BanGateDoesNotNeedAdminOnlyCommandsList()
    {
        var cfg = Config(25_354_325);
        Assert.DoesNotContain("ban", cfg.Admin.AdminOnlyCommands);

        Assert.False(CommandAccessPolicy.IsAllowed(cfg, 65_054_804, "/ban X 30m", out _));
    }

    [Fact]
    public void AdminOnlyFalseOpensBanToEveryone()
    {
        var cfg = Config(25_354_325);
        cfg.Admin.Ban.AdminOnly = false;

        Assert.True(CommandAccessPolicy.IsAllowed(cfg, 65_054_804, "/ban Ktulxy 30m", out var reason));
        Assert.Equal(string.Empty, reason);
    }

    [Fact]
    public void ModeAndMapStayOpenWhenBanGateIsOn()
    {
        // Бановый гейт закрывает /ban и /ids — на выбор режима/карты он не влияет.
        // /play и /start из проверки убраны: этих команд больше нет.
        var cfg = Config(25_354_325);

        Assert.True(CommandAccessPolicy.IsAllowed(cfg, 65_054_804, "/mode Escalation", out _));
        Assert.True(CommandAccessPolicy.IsAllowed(cfg, 65_054_804, "/map Sandstone", out _));
    }
}
