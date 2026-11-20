using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Матрица прав на команды из чата.
/// </summary>
/// <remarks>
/// Требование: <c>/set</c> (любая подкоманда) и <c>/end</c> — админские; запуск катки
/// (<c>/mode</c>, <c>/map</c>, <c>/start</c>, <c>/play</c>) — доступен всем. Тесты держат
/// это поведение и не дают новому правилу незаметно закрыть запуск матча.
/// </remarks>
public sealed class CommandAccessPolicyTests
{
    private const int AdminId = 25_354_325;
    private const int OtherId = 65_054_804;

    private static ServerConfig Default()
    {
        var cfg = new ServerConfig();
        cfg.Normalize();
        return cfg;
    }

    private static bool Allowed(ServerConfig cfg, int id, string message, out string reason) =>
        CommandAccessPolicy.IsAllowed(cfg, id, message, out reason);

    [Theory]
    [InlineData("/mode")]
    [InlineData("/mode Escalation")]
    [InlineData("/map")]
    [InlineData("/map Sandstone")]
    public void ChoosingModeAndMap_IsAllowedForEveryoneByDefault(string message)
    {
        var cfg = Default();

        Assert.True(Allowed(cfg, OtherId, message, out var reason),
            $"'{message}' должен быть доступен без админки");
        Assert.Equal(string.Empty, reason);
    }

    [Theory]
    [InlineData("/set start")]
    [InlineData("set start")]
    public void SetStart_IsStillRecognisedAsAMatchStartByThePolicy(string message)
    {
        var cfg = Default();

        // Запуска катки командой больше нет — диспетчер на «/set start» отвечает
        // «убран». Но разбор строки и права остались прежними, и тест держит эту
        // границу: политика не должна начать считать запуск админской настройкой.
        Assert.True(Allowed(cfg, OtherId, message, out _));
    }

    [Theory]
    [InlineData("/set round 16")]
    [InlineData("/set wins 9")]
    [InlineData("/set team ct")]
    [InlineData("/set status")]
    [InlineData("/set end")]
    [InlineData("/set")]
    public void EveryOtherSetSubcommand_NeedsAdmin(string message)
    {
        var cfg = Default();

        Assert.False(Allowed(cfg, OtherId, message, out var reason),
            $"'{message}' меняет параметры хоста — должен требовать админа");
        Assert.Contains("set", reason);
    }

    [Theory]
    [InlineData("/end")]
    [InlineData("/stopmatch")]
    [InlineData("/стопматч")]
    public void EndingTheMatch_NeedsAdmin(string message)
    {
        var cfg = Default();

        Assert.False(Allowed(cfg, OtherId, message, out _));
    }

    [Theory]
    [InlineData("/help")]
    [InlineData("/?")]
    [InlineData("/команды")]
    [InlineData("/ready")]
    [InlineData("/unready")]
    [InlineData("/готов")]
    [InlineData("/нг")]
    public void PublicCommands_AreAlwaysAllowed(string message)
    {
        Assert.True(Allowed(Default(), OtherId, message, out _));
    }

    /// <summary>
    /// <c>/chattest</c> шлёт кадры ChatManager, подписанные чужим актором, — это диагностика
    /// для админа. Гейт не зависит от <c>admin.adminOnlyCommands</c>, иначе он снимался бы
    /// правкой конфига (так же сделано для банов).
    /// </summary>
    [Theory]
    [InlineData("/chattest")]
    [InlineData("/чаттест")]
    [InlineData("chattest")]
    public void ChatTestDiagnostic_NeedsAdmin_EvenWithoutConfig(string message)
    {
        var cfg = Default();

        Assert.False(Allowed(cfg, OtherId, message, out var reason),
            $"'{message}' подписывает чат чужим актором — только админ");
        Assert.Contains("только для админов", reason);

        cfg.Admin.Ids.Add(AdminId);
        Assert.True(Allowed(cfg, AdminId, message, out _));
    }

    [Theory]
    [InlineData("/set round 16")]
    [InlineData("/end")]
    public void AdminCommands_AreAllowedForConfiguredAdmins(string message)
    {
        var cfg = Default();
        cfg.Admin.Ids.Add(AdminId);

        Assert.True(Allowed(cfg, AdminId, message, out _));
        // И регистр/невидимые символы не мешают.
        Assert.True(Allowed(cfg, LegacyNickHash.ForNick("ВЛАЛ"), message, out _));
    }

    [Fact]
    public void StartCommandsAreGoneButSetItselfIsStillAdminOnly()
    {
        // Запуска катки командой больше нет: /play и /start не разбирает диспетчер.
        // Гейт прав остался прежним и по-прежнему закрывает /set от не-админов.
        // («/set start» сюда не годится: он считается запуском матча и открыт всем
        // пока anyoneCanStart=true — см. SetStart_IsStillRecognisedAsAMatchStartByThePolicy.)
        var cfg = Default();
        cfg.Admin.Ids.Add(AdminId);

        Assert.False(Allowed(cfg, OtherId, "/set round 8", out _));
        Assert.True(Allowed(cfg, AdminId, "/set round 8", out _));
    }

    [Fact]
    public void ModeAndMapStayOpenEvenWhenSetIsAdminOnly()
    {
        // Смысл настройки: /set меняет параметры, но выбирать режим и карту может любой.
        var cfg = Default();

        Assert.True(Allowed(cfg, OtherId, "/mode Duel", out _));
        Assert.True(Allowed(cfg, OtherId, "/map Sandstone", out _));
        Assert.False(Allowed(cfg, OtherId, "/set round 4", out _));
    }

    [Fact]
    public void CustomAdminOnlyList_IsRespectedForOtherCommands()
    {
        // Список админских команд — общий механизм конфига, а не перечень
        // существующих команд. Запуск катки из него убран, поэтому проверяем
        // на команде, которая осталась: /mode закрыли — она закрыта только админу.
        var cfg = Default();
        cfg.Admin.AdminOnlyCommands.Add("mode");
        cfg.Admin.Ids.Add(AdminId);

        Assert.False(Allowed(cfg, OtherId, "/mode", out _));
        Assert.True(Allowed(cfg, AdminId, "/mode", out _));
        Assert.True(Allowed(cfg, OtherId, "/help", out _));
    }

    [Fact]
    public void Parse_RecognisesSetStartAsMatchStart()
    {
        var parsed = CommandAccessPolicy.Parse("/set start");

        Assert.Equal("set", parsed.Command);
        Assert.True(parsed.StartsMatch);
    }

    [Fact]
    public void Parse_TreatsSetRoundAsAPlainSet()
    {
        var parsed = CommandAccessPolicy.Parse("/set round 16");

        Assert.Equal("set", parsed.Command);
        Assert.Equal("round 16", parsed.Args);
        Assert.False(parsed.StartsMatch);
    }

    [Fact]
    public void DenialReason_AlwaysNamesTheCommandAndTheId()
    {
        Assert.False(Allowed(Default(), OtherId, "/set round 8", out var reason));

        // Не «молча проигнорировали»: в причине есть и команда, и id игрока.
        Assert.Contains("/set", reason);
        Assert.Contains($"#{OtherId}", reason);
    }

    /// <summary>
    /// Id из <c>admin.ids</c> — ещё не права: адрес обязан предъявить код этого
    /// запуска (<c>AdminAuth</c>). Гейт спрашивает <c>hasAdminRights</c>, а не список
    /// напрямую, иначе любой клиент с админским id получил бы <c>/set</c> и <c>/ban</c>
    /// — при том что <c>/whoami</c> отвечал бы «код не введён».
    /// </summary>
    [Theory]
    [InlineData("/set round 16")]
    [InlineData("/end")]
    [InlineData("/ban 12345678 1h тест")]
    [InlineData("/chattest")]
    public void AdminId_WithoutTheRunCode_HasNoAdminRights(string message)
    {
        var cfg = Default();
        cfg.Admin.Ids.Add(AdminId);

        var allowed = CommandAccessPolicy.IsAllowed(
            cfg, AdminId, message, out var reason, hasAdminRights: _ => false);

        Assert.False(allowed, $"'{message}' не должен исполняться без кода запуска");
        // Отказ объясняет именно этот случай: id в списке есть, прав нет.
        Assert.Contains("код", reason);
        Assert.Contains($"#{AdminId}", reason);
        Assert.Contains("/" + CommandAccessPolicy.Parse(message).Command, reason);
    }

    [Theory]
    [InlineData("/set round 16")]
    [InlineData("/end")]
    [InlineData("/chattest")]
    public void AdminId_WithTheRunCode_KeepsAdminRights(string message)
    {
        var cfg = Default();
        cfg.Admin.Ids.Add(AdminId);

        var allowed = CommandAccessPolicy.IsAllowed(
            cfg, AdminId, message, out var reason, hasAdminRights: id => cfg.IsAdmin(id));

        Assert.True(allowed, $"'{message}' обязан исполняться для авторизованного админа");
        Assert.Equal(string.Empty, reason);
    }

    /// <summary>
    /// Справка и открытый запуск матча прав не спрашивают вовсе — делегат не вызывается.
    /// Иначе игрок, которого админ-гейт не касается, потерял бы ещё и <c>/help</c>.
    /// </summary>
    [Theory]
    [InlineData("/help")]
    [InlineData("/ready")]
    [InlineData("/join 2v2 #1")]
    public void PublicCommands_NeverAskForAdminRights(string message)
    {
        Assert.True(CommandAccessPolicy.IsAllowed(
            Default(),
            OtherId,
            message,
            out _,
            hasAdminRights: _ => throw new InvalidOperationException(
                $"'{message}' не должна спрашивать админ-права")));
    }
}
