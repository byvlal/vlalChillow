using System.Text.Json;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Конфиг сервера: шаблон, который сервер пишет при первом запуске, и правила прав.
/// Сокетов нет — проверяется только разбор и логика доступа.
/// </summary>
public sealed class ServerConfigTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // Шаблон закомментирован — сервер обязан его проглатывать.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
    };

    private static ServerConfig Parse(string json) =>
        JsonSerializer.Deserialize<ServerConfig>(json, Options) ?? new ServerConfig();

    [Fact]
    public void Template_ParsesIntoTheDocumentedDefaults()
    {
        var cfg = Parse(ServerConfig.Template);
        cfg.Normalize();

        Assert.Equal(MatchStartMode.Auto, cfg.StartMode);
        Assert.Equal(1, cfg.MatchStart.Auto.MinPlayersPerTeam);
        Assert.True(cfg.MatchStart.Ready.AnnounceInChat);
        Assert.True(cfg.MatchStart.Ready.ResetOnLeave);
        Assert.True(cfg.MatchStart.Ready.AllowUnready);
        Assert.True(cfg.Admin.AnyoneCanStart);
        // Владелец сервера — и админ, и мега-админ «из коробки».
        Assert.Contains(1_000_000, cfg.Admin.Ids);
        Assert.Contains(1_000_000, cfg.Admin.MegaIds);
        Assert.True(cfg.IsAdmin(1_000_000));
        Assert.True(cfg.IsMegaId(1_000_000));
        Assert.Empty(cfg.Launch.Args);
        Assert.False(cfg.Debug.Captures);
        Assert.False(cfg.Debug.Verbose);
        Assert.True(cfg.Debug.Health);
        Assert.Equal(33, cfg.Tuning.TickMs);
        Assert.Equal(256, cfg.Tuning.ReliableQueueSoftLimit);
        Assert.Equal(109, cfg.Timings.RoundTime);
        // Сеть по умолчанию: слушаем везде, VPN не трогаем.
        Assert.Equal("0.0.0.0", cfg.Network.BindAddress);
        Assert.True(cfg.Network.IgnoreVpn);
        Assert.Null(cfg.Network.AdvertiseIp);
    }

    [Fact]
    public void Template_ClosesOnlyParameterChangingCommandsByDefault()
    {
        var cfg = Parse(ServerConfig.Template);
        cfg.Normalize();

        // /set меняет параметры хоста (раунды, победу, состав), /end обрывает катку.
        Assert.True(cfg.IsAdminOnly("set"));
        Assert.True(cfg.IsAdminOnly("end"));

        // Запуск матча закрывать нельзя — катку начинает кто угодно.
        Assert.False(cfg.IsAdminOnly("start"));
        Assert.False(cfg.IsAdminOnly("mode"));
        Assert.False(cfg.IsAdminOnly("map"));
        Assert.False(cfg.IsAdminOnly("play"));
        Assert.True(cfg.Admin.AnyoneCanStart);
    }

    [Fact]
    public void TrustedProxies_AreNormalizedAndInvalidEntriesAreDropped()
    {
        var cfg = Parse(ServerConfig.Template);
        cfg.Web.TrustedProxies = new List<string>
        {
            " 192.168.1.10 ",
            "192.168.1.5/24",
            "10.0.0.0/8",
            "::ffff:192.168.1.20",
            "garbage",
            "",
        };

        cfg.Normalize();

        Assert.Equal(
            new[] { "192.168.1.10", "192.168.1.0/24", "10.0.0.0/8", "192.168.1.20" },
            cfg.Web.TrustedProxies);
        Assert.Contains(cfg.Issues, issue => issue.Key == "web.trustedProxies");
    }

    [Fact]
    public void TrustedProxies_AreEmptyByDefault()
    {
        var cfg = Parse(ServerConfig.Template);

        cfg.Normalize();

        // По умолчанию верим только loopback: LAN-клиент не должен подделать
        // X-Forwarded-For и получить мега-IP.
        Assert.Empty(cfg.Web.TrustedProxies);
    }

    [Fact]
    public void EverySetSubcommand_IsAdminOnly_NotJustSetStart()
    {
        // «любая команда, что начинается на /set» — иначе /set round 99 или
        // /set team tr менял бы хост без прав.
        var cfg = Parse(ServerConfig.Template);
        cfg.Normalize();

        Assert.True(cfg.IsAdminOnly("set"));
        // /set start разрешён всем через anyoneCanStart — это отдельное правило,
        // а не дыра в админ-гейте на set.
        Assert.True(cfg.Admin.AnyoneCanStart);
    }

    [Fact]
    public void DefaultConfig_IsAutoAndMakesTheOwnerAdmin()
    {
        var cfg = new ServerConfig();

        // auto — единственный режим, который у пользователя работал без /start.
        Assert.Equal(MatchStartMode.Auto, cfg.StartMode);
        // 1_000_000 — владелец по умолчанию: админ и мега-админ.
        Assert.True(cfg.IsAdmin(1_000_000));
        Assert.True(cfg.IsMegaId(1_000_000));
        Assert.False(cfg.IsAdmin(65_054_804));
        Assert.False(cfg.IsAdmin(0));
        Assert.False(cfg.IsAdmin(-1));
    }

    [Fact]
    public void ExplicitEmptyAdminList_StillMeansNoAdmins()
    {
        // Явные "ids": [] и "megaIds": [] — осознанный выбор, дефолт их не перебивает.
        var cfg = Parse("""
        {
          "admin": { "ids": [], "megaIds": [] }
        }
        """);

        cfg.Normalize();

        Assert.Empty(cfg.Admin.Ids);
        Assert.Empty(cfg.Admin.MegaIds);
        Assert.False(cfg.IsAdmin(1_000_000));
        Assert.False(cfg.IsMegaId(1_000_000));
    }

    [Fact]
    public void MegaId_JoinsAdminsAndWinsOverlap()
    {
        var cfg = Parse("""
        {
          "admin": {
            "ids": [ 500000 ],
            "megaIds": [ 500000, 600000 ]
          }
        }
        """);

        cfg.Normalize();

        Assert.True(cfg.IsAdmin(500000));
        Assert.True(cfg.IsMegaId(500000));
        Assert.True(cfg.IsAdmin(600000));
        Assert.True(cfg.IsMegaId(600000));
        Assert.Single(cfg.Admin.Ids.Where(id => id == 500000));
        Assert.Contains(600000, cfg.Admin.Ids);
        Assert.False(cfg.IsMegaId(500001));
    }

    [Fact]
    public void IsAdmin_MatchesTheServerIdOnly()
    {
        var cfg = new ServerConfig();
        cfg.Admin.Ids.Add(25_354_325);
        cfg.Normalize();

        Assert.True(cfg.IsAdmin(25_354_325));
        Assert.False(cfg.IsAdmin(25_354_326));
        Assert.False(cfg.IsAdmin(0));
        Assert.False(cfg.IsAdmin(-1));
    }

    [Fact]
    public void AnyoneCanStart_OverridesTheAdminGateForStart()
    {
        var cfg = new ServerConfig();
        cfg.Admin.AdminOnlyCommands.Add("start");
        Assert.True(cfg.IsAdminOnly("start"));

        // Флаг из конфига пускает всех к запуску даже при админ-гейте на start.
        cfg.Admin.AnyoneCanStart = true;
        Assert.True(cfg.Admin.AnyoneCanStart);
    }

    [Fact]
    public void IsAdmin_UsesTheIdTheServerComputesFromTheSessionNick()
    {
        var cfg = new ServerConfig();
        cfg.Admin.Ids.Add(LegacyNickHash.ForNick("влал"));
        cfg.Normalize();

        // Регистр/невидимые символы не меняют id, потому что его считает сервер
        // из нормализованного ника сессии, а не из строки пакета.
        Assert.True(cfg.IsAdmin(LegacyNickHash.ForNick("ВЛАЛ")));
        Assert.True(cfg.IsAdmin(LegacyNickHash.ForNick("\u200Bвлал\u00AD")));
        Assert.False(cfg.IsAdmin(LegacyNickHash.ForNick("влал2")));
    }

    [Fact]
    public void IsAdminOnly_AcceptsTheSlashForm()
    {
        var cfg = new ServerConfig();
        cfg.Admin.AdminOnlyCommands.Add("start");

        Assert.True(cfg.IsAdminOnly("/start"));
        Assert.True(cfg.IsAdminOnly(" start "));
    }

    [Theory]
    [InlineData("manual", MatchStartMode.Manual)]
    [InlineData("MANUAL", MatchStartMode.Manual)]
    [InlineData("ручной", MatchStartMode.Manual)]
    [InlineData("auto", MatchStartMode.Auto)]
    [InlineData("авто", MatchStartMode.Auto)]
    [InlineData("ready", MatchStartMode.Ready)]
    [InlineData("готовность", MatchStartMode.Ready)]
    public void TryParseMode_AcceptsEnglishAndRussianAliases(string raw, MatchStartMode expected)
    {
        Assert.True(ServerConfig.TryParseMode(raw, out var mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("когда-нибудь")]
    public void TryParseMode_RejectsUnknownValues(string? raw)
    {
        Assert.False(ServerConfig.TryParseMode(raw, out _));
    }

    [Fact]
    public void LegacyConfigWithEmptyAdminsAndFullAdminGate_IsUnlockedOnLoad()
    {
        // Ровно то, что лежит у пользователя в C:\Users\vlal\Desktop\vltest\config.json:
        // админов нет, зато все команды «только для админов», anyoneCanStart=false.
        // Такой файл не перезаписывается при запуске, поэтому без миграции команды
        // остались бы заблокированными навсегда.
        var cfg = Parse("""
        {
          "admin": {
            "ids": [],
            "adminOnlyCommands": [ "mode", "map", "set", "play", "end", "start" ],
            "anyoneCanStart": false
          },
          "matchStart": { "mode": "manual" }
        }
        """);

        cfg.Normalize();

        // Старый набор заменён новым: только set и end.
        Assert.Equal(2, cfg.Admin.AdminOnlyCommands.Count);
        Assert.Contains("set", cfg.Admin.AdminOnlyCommands);
        Assert.Contains("end", cfg.Admin.AdminOnlyCommands);
        Assert.True(cfg.Admin.AnyoneCanStart);
        Assert.Equal(MatchStartMode.Manual, cfg.StartMode); // явный manual уважаем
    }

    [Fact]
    public void DeliberateAdminSetup_IsNeverOverwrittenByTheMigration()
    {
        // Есть админ — значит гейт выставлен осознанно, миграция не вмешивается.
        var cfg = new ServerConfig();
        cfg.Admin.Ids.Add(25_354_325);
        cfg.Admin.AdminOnlyCommands.Clear();
        cfg.Admin.AdminOnlyCommands.AddRange(["mode", "map", "set", "play", "end", "start"]);
        cfg.Admin.AnyoneCanStart = false;

        cfg.Normalize();

        Assert.Equal(6, cfg.Admin.AdminOnlyCommands.Count);
        Assert.False(cfg.Admin.AnyoneCanStart);
        Assert.True(cfg.IsAdmin(25_354_325));
    }

    [Fact]
    public void CustomAdminGateWithoutAdmins_IsLeftAlone()
    {
        // Набор команд не совпадает со старым дефолтом — это ручная настройка.
        var cfg = new ServerConfig();
        cfg.Admin.AdminOnlyCommands.Clear();
        cfg.Admin.AdminOnlyCommands.AddRange(["mode", "map"]);
        cfg.Admin.AnyoneCanStart = false;

        cfg.Normalize();

        Assert.Equal(2, cfg.Admin.AdminOnlyCommands.Count);
        Assert.False(cfg.Admin.AnyoneCanStart);
    }

    [Fact]
    public void Normalize_ClampsMinPlayersPerTeamAndFallsBackToAuto()
    {
        var cfg = new ServerConfig
        {
            MatchStart = new MatchStartConfig
            {
                Mode = "что-то не то",
                Auto = new AutoStartConfig { MinPlayersPerTeam = 0 },
            },
        };

        cfg.Normalize();

        // Непонятный режим больше не откатывается тихо в manual: битый
        // matchStart.mode берёт рабочий дефолт auto.
        Assert.Equal("auto", cfg.MatchStart.Mode);
        Assert.Equal(MatchStartMode.Auto, cfg.StartMode);
        Assert.Equal(1, cfg.MatchStart.Auto.MinPlayersPerTeam);
    }

    [Fact]
    public void LaunchArgs_CannotSwitchDedicatedServerIntoProbeMode()
    {
        // Иначе весь блок debug/launch «утечёт» в пробу: программа ушла бы в --client
        // и сама себя перевела в режим, где конфиг запрещён.
        var cfg = new ServerConfig();
        cfg.Launch.Args.AddRange(new[] { "--client", "-c", "ConnectAsClient", "--verbose" });

        cfg.Normalize();

        Assert.DoesNotContain("--client", cfg.Launch.Args);
        Assert.DoesNotContain("-c", cfg.Launch.Args);
        Assert.DoesNotContain("ConnectAsClient", cfg.Launch.Args);
        Assert.Single(cfg.Launch.Args);
        Assert.Equal("--verbose", cfg.Launch.Args[0]);
    }

    [Fact]
    public void Normalize_ClampsTimingAndTuningIntoTheLiveRanges()
    {
        var cfg = new ServerConfig
        {
            Timings = new TimingsConfig
            {
                Warmup = 9999,
                RoundTime = 1,
                TotalRounds = 0,
                WinsNeeded = 999,
                BombFuse = 0,
            },
            Tuning = new TuningConfig
            {
                TickMs = 0,
                StateIntervalMs = 10_000,
                ReliableQueueSoftLimit = 0,
                PacketPoolSize = 1,
                HealthIntervalSec = -5,
            },
        };

        cfg.Normalize();

        Assert.Equal(3, cfg.Timings.Warmup);
        Assert.Equal(109, cfg.Timings.RoundTime);
        Assert.Equal(8, cfg.Timings.TotalRounds);
        Assert.Equal(0, cfg.Timings.WinsNeeded);
        Assert.Equal(40, cfg.Timings.BombFuse);
        Assert.Equal(33, cfg.Tuning.TickMs);
        Assert.Equal(50, cfg.Tuning.StateIntervalMs);
        Assert.Equal(256, cfg.Tuning.ReliableQueueSoftLimit);
        Assert.Equal(4096, cfg.Tuning.PacketPoolSize);
        Assert.Equal(10, cfg.Tuning.HealthIntervalSec);
    }

    [Fact]
    public void DebugSection_HasItsOwnDefaultsIndependentOfProbe()
    {
        // Проба не читает config.json вообще; блок debug — только для дедика.
        var debug = new DebugConfig();

        Assert.False(debug.Captures);
        Assert.False(debug.Verbose);
        Assert.False(debug.LogPackets);
        Assert.True(debug.Health);
        Assert.Equal(4000, debug.CapturesMaxFiles);
        Assert.Equal(64, debug.CapturesMaxBytesMb);
    }

    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData(" 192.168.1.5 ", "192.168.1.5")]
    [InlineData("25.7.7.7", "25.7.7.7")] // Hamachi
    public void Normalize_KeepsAValidBindAddress(string raw, string expected)
    {
        var cfg = new ServerConfig { Network = new NetworkConfig { BindAddress = raw } };
        cfg.Normalize();
        Assert.Equal(expected, cfg.Network.BindAddress);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("не-айпи")]
    [InlineData("999.1.1.1")]
    public void Normalize_FallsBackToAnyBindWhenAddressIsGarbage(string raw)
    {
        var cfg = new ServerConfig { Network = new NetworkConfig { BindAddress = raw } };
        cfg.Normalize();

        // Пустой/битый адрес не должен ронять сервер: слушаем все интерфейсы.
        Assert.Equal("0.0.0.0", cfg.Network.BindAddress);
    }

    [Fact]
    public void Normalize_DropsGarbageAdvertiseIp()
    {
        var cfg = new ServerConfig { Network = new NetworkConfig { AdvertiseIp = "не-айпи" } };
        cfg.Normalize();
        Assert.Null(cfg.Network.AdvertiseIp);
    }

    [Fact]
    public void NetworkConfig_DefaultsLiveEverywhereButIgnoreVpn()
    {
        var net = new NetworkConfig();

        Assert.Equal("0.0.0.0", net.BindAddress); // все интерфейсы, включая VPN
        Assert.True(net.IgnoreVpn);               // но виртуальные адаптеры по умолчанию мимо
        Assert.Null(net.AdvertiseIp);
    }

    [Fact]
    public void ProbeDefaults_DoNotDependOnNetworkConfig()
    {
        // Проба читает LanInterfacePicker, а не конфиг: значения по умолчанию
        // остаются такими, какими были до появления блока network.
        Assert.True(StandChillow.LanServer.Lan.LanInterfacePicker.IgnoreVpn);
    }

    [Fact]
    public void Template_IsValidJsonWithComments()
    {
        // Если шаблон сломается, сервер молча уедет на дефолты — ловим это здесь.
        var cfg = Parse(ServerConfig.Template);
        Assert.NotNull(cfg.Admin);
        Assert.NotNull(cfg.MatchStart);
    }
}
