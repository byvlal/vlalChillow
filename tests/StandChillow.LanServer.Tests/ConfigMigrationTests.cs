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
}
