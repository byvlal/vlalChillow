using StandChillow.LanServer.Config;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Байтовая миграция config.json: секция admin.megaIds появляется один раз, а всё
/// остальное — комментарии, порядок ключей, переводы строк — остаётся как было.
/// </summary>
[Collection("config-file")]
public sealed class ConfigMigrationTests : IDisposable
{
    private readonly string _dir;

    public ConfigMigrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-migration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string Write(string json)
    {
        var path = Path.Combine(_dir, "config.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void AddsMegaAfterIds_AndKeepsCommentsAndCustomIds()
    {
        const string source = """
        {
          // шапка
          "admin": {
            // обычные админы
            "ids": [ 500000 ],
            // команды
            "adminOnlyCommands": [ "set" ]
          }
        }
        """;
        var path = Write(source);

        Assert.True(ConfigMigration.EnsureMegaSection(path, out var message));
        Assert.Contains("megaIds", message);

        var text = File.ReadAllText(path);
        Assert.Contains("\"megaIds\": [ 1000000 ]", text);
        Assert.Contains("// шапка", text);
        Assert.Contains("// обычные админы", text);
        Assert.Contains("// команды", text);
        Assert.Contains("\"ids\": [ 500000 ]", text);
        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.True(cfg.IsMegaId(1_000_000));
        Assert.True(cfg.IsAdmin(500000));
    }

    [Fact]
    public void EmptyOrLegacyIds_BecomeOwnerBothInIdsAndMega()
    {
        foreach (var legacy in new[] { "[]", "[ 25354325 ]" })
        {
            var path = Write($$"""
            {
              "admin": {
                "ids": {{legacy}},
                "adminOnlyCommands": [ "set" ]
              }
            }
            """);

            Assert.True(ConfigMigration.EnsureMegaSection(path, out _));

            var text = File.ReadAllText(path);
            Assert.Contains("\"ids\": [ 1000000 ]", text);
            Assert.Contains("\"megaIds\": [ 1000000 ]", text);
            Assert.DoesNotContain("25354325", text);
            Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
            Assert.True(cfg.IsAdmin(1_000_000));
            Assert.True(cfg.IsMegaId(1_000_000));
        }
    }

    [Fact]
    public void ExplicitMegaSection_IsNeverTouched()
    {
        const string source = """
        {
          "admin": {
            "ids": [],
            "megaIds": []
          }
        }
        """;
        var path = Write(source);

        Assert.False(ConfigMigration.EnsureMegaSection(path, out _));
        Assert.Equal(source, File.ReadAllText(path));
    }

    [Fact]
    public void Migration_IsIdempotent()
    {
        var path = Write("""
        {
          "admin": { "ids": [ 500000 ] }
        }
        """);

        Assert.True(ConfigMigration.EnsureMegaSection(path, out _));
        var once = File.ReadAllText(path);
        Assert.False(ConfigMigration.EnsureMegaSection(path, out _));
        Assert.Equal(once, File.ReadAllText(path));
    }

    [Fact]
    public void BrokenJson_IsNotTouched()
    {
        const string source = """{ "admin": { "ids": [ 1, """;
        var path = Write(source);

        Assert.False(ConfigMigration.EnsureMegaSection(path, out _));
        Assert.Equal(source, File.ReadAllText(path));
    }

    [Fact]
    public void MissingAdminSection_IsAddedWithIdsAndMega()
    {
        var path = Write("""
        {
          "web": { "port": 8080 }
        }
        """);

        Assert.True(ConfigMigration.EnsureMegaSection(path, out _));

        var text = File.ReadAllText(path);
        Assert.Contains("\"admin\":", text);
        Assert.Contains("\"ids\": [ 1000000 ]", text);
        Assert.Contains("\"megaIds\": [ 1000000 ]", text);
        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.True(cfg.IsMegaId(1_000_000));
    }

    [Fact]
    public void AdminWithoutIds_GetsMegaAtTheTop()
    {
        var path = Write("""
        {
          "admin": {
            "anyoneCanStart": false
          }
        }
        """);

        Assert.True(ConfigMigration.EnsureMegaSection(path, out _));

        var text = File.ReadAllText(path);
        Assert.Contains("\"megaIds\": [ 1000000 ]", text);
        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.True(cfg.IsMegaId(1_000_000));
        Assert.False(cfg.Admin.AnyoneCanStart);
    }

    [Fact]
    public void CrlfAndBom_ArePreserved()
    {
        var path = Path.Combine(_dir, "config.json");
        var source = "{\r\n  \"admin\": {\r\n    \"ids\": [ 500000 ]\r\n  }\r\n}\r\n";
        File.WriteAllText(path, source, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.True(ConfigMigration.EnsureMegaSection(path, out _));

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF);
        var text = File.ReadAllText(path);
        Assert.Contains("\r\n", text);
        Assert.Equal(
            text.Count(ch => ch == '\n'),
            text.Split("\r\n").Length - 1);
        Assert.Contains("\"megaIds\": [ 1000000 ]", text);
        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.True(cfg.IsMegaId(1_000_000));
    }

    [Fact]
    public void TestSection_IsAddedAtTheBottom_WithBothFlagsOff()
    {
        const string source = """
        {
          // шапка
          "web": { "port": 8080 },
          "admin": {
            "ids": [ 500000 ],
            "megaIds": [ 1000000 ]
          }
        }
        """;
        var path = Write(source);

        Assert.True(ConfigMigration.EnsureTestSection(path, out var message));
        Assert.Contains("test", message);

        var text = File.ReadAllText(path);
        Assert.Contains("\"test\": {", text);
        Assert.Contains("\"noAdminPassword\": false", text);
        Assert.Contains("\"autoMegaAdmin\": false", text);
        Assert.Contains("ЗАПРЕЩЕНО ВКЛЮЧАТЬ", text);
        Assert.Contains("// шапка", text);
        Assert.Contains("\"port\": 8080", text);
        Assert.Contains("\"ids\": [ 500000 ]", text);
        Assert.True(text.TrimEnd().EndsWith('}'));

        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.False(cfg.Test.NoAdminPassword);
        Assert.False(cfg.Test.AutoMegaAdmin);
        Assert.True(cfg.IsMegaId(1_000_000));
        Assert.True(cfg.IsAdmin(500000));
    }

    [Fact]
    public void ExplicitTestSection_IsNeverTouched_AndFlagsAreRead()
    {
        const string source = """
        {
          "test": {
            "noAdminPassword": true,
            "autoMegaAdmin": true
          },
          "admin": { "ids": [ 500000 ], "megaIds": [ 1000000 ] }
        }
        """;
        var path = Write(source);

        Assert.False(ConfigMigration.EnsureTestSection(path, out _));
        Assert.Equal(source, File.ReadAllText(path));

        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.True(cfg.Test.NoAdminPassword);
        Assert.True(cfg.Test.AutoMegaAdmin);
    }

    [Fact]
    public void TestSectionMigration_IsIdempotent_AndLeavesBrokenJsonAlone()
    {
        var path = Write("""
        {
          "admin": { "ids": [ 500000 ], "megaIds": [ 1000000 ] }
        }
        """);

        Assert.True(ConfigMigration.EnsureTestSection(path, out _));
        var once = File.ReadAllText(path);
        Assert.False(ConfigMigration.EnsureTestSection(path, out _));
        Assert.Equal(once, File.ReadAllText(path));

        const string broken = """{ "test": { "noAdminPassword": true """;
        File.WriteAllText(path, broken);
        Assert.False(ConfigMigration.EnsureTestSection(path, out _));
        Assert.Equal(broken, File.ReadAllText(path));
    }

    // ── внешний доступ (playit/туннели) ─────────────────────────────────────

    [Fact]
    public void PublicAccessSection_IsAddedAndLegacyAdvertiseIpMovesIntoIt()
    {
        const string source = """
        {
          // шапка
          "network": {
            // адрес матча
            "advertiseIp": "203.0.113.5"
          },
          "admin": { "ids": [ 500000 ], "megaIds": [ 1000000 ] }
        }
        """;
        var path = Write(source);

        Assert.True(ConfigMigration.EnsurePublicAccessSection(path, out var message));
        Assert.Contains("publicAccess", message);

        var text = File.ReadAllText(path);
        Assert.Contains("\"publicAccess\": {", text);
        Assert.Contains("\"address\": \"203.0.113.5\"", text);
        Assert.Contains("\"lobbyPort\": 0", text);
        Assert.Contains("\"matchPort\": 0", text);
        Assert.Contains("\"advertiseIp\": null", text);
        Assert.Contains("// шапка", text);
        Assert.Contains("// адрес матча", text);

        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.Equal("203.0.113.5", cfg.PublicAccess.Address);
        Assert.Null(cfg.Network.AdvertiseIp);

        // Повторный запуск ничего не меняет.
        var once = File.ReadAllText(path);
        Assert.False(ConfigMigration.EnsurePublicAccessSection(path, out _));
        Assert.Equal(once, File.ReadAllText(path));
    }

    [Fact]
    public void PublicAccessSection_WithoutLegacy_IsAddedDisabled()
    {
        var path = Write("""
        {
          "admin": { "ids": [ 500000 ], "megaIds": [ 1000000 ] }
        }
        """);

        Assert.True(ConfigMigration.EnsurePublicAccessSection(path, out _));

        var text = File.ReadAllText(path);
        Assert.Contains("\"address\": null", text);
        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.Null(cfg.PublicAccess.Address);
    }

    [Fact]
    public void ExistingPublicAccess_FillsEmptyAddressFromLegacy_AndKeepsPorts()
    {
        var path = Write("""
        {
          "network": { "advertiseIp": "203.0.113.5" },
          "publicAccess": {
            "address": null,
            "lobbyPort": 6606,
            "matchPort": 6605
          }
        }
        """);

        Assert.True(ConfigMigration.EnsurePublicAccessSection(path, out _));

        var text = File.ReadAllText(path);
        Assert.Contains("\"address\": \"203.0.113.5\"", text);
        Assert.Contains("\"advertiseIp\": null", text);
        Assert.Contains("\"lobbyPort\": 6606", text);
        Assert.Contains("\"matchPort\": 6605", text);

        Assert.True(ServerConfig.TryLoad(path, out var cfg, out var error), error);
        Assert.Equal("203.0.113.5", cfg.PublicAccess.Address);
        Assert.Equal(6606, cfg.PublicAccess.LobbyPort);
        Assert.Equal(6605, cfg.PublicAccess.MatchPort);
    }

    [Fact]
    public void ExistingPublicAccessWithAddress_KeepsIt_AndOnlyNullsLegacy()
    {
        var path = Write("""
        {
          "network": { "advertiseIp": "203.0.113.5" },
          "publicAccess": { "address": "198.51.100.7" }
        }
        """);

        Assert.True(ConfigMigration.EnsurePublicAccessSection(path, out _));

        var text = File.ReadAllText(path);
        Assert.Contains("\"address\": \"198.51.100.7\"", text);
        Assert.Contains("\"advertiseIp\": null", text);
        Assert.DoesNotContain("203.0.113.5", text);
    }

    [Fact]
    public void PublicAccessSection_LeavesBrokenJsonAlone()
    {
        const string broken = """{ "publicAccess": { "address": 1 """;
        var path = Write(broken);

        Assert.False(ConfigMigration.EnsurePublicAccessSection(path, out _));
        Assert.Equal(broken, File.ReadAllText(path));
    }
}
