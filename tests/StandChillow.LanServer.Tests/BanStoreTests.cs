using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// <c>banlist.json</c>: миграция v1 → v2, бэкап, новые поля и защита от файла будущей версии.
/// </summary>
public sealed class BanStoreTests : IDisposable
{
    private const string Device = "LB143C7D41FAE30EB14F49B1678E3933C9";

    private readonly string _dir;
    private readonly string _path;

    public BanStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-banstore-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "banlist.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    [Fact]
    public void V1IsBackedUpAndRewrittenAsV2()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddDays(1));
        var v1 = $$"""
        {
          "version": 1,
          "bans": [
            {
              "nick": "Ktulxy",
              "aliases": [],
              "ips": ["203.0.113.9"],
              "matchIds": [671488],
              "reason": "софт",
              "bannedBy": "влал",
              "createdUtcMs": 0,
              "expiresUtcMs": {{expires}},
              "durationSeconds": 3600,
              "utcOffsetMinutes": 0,
              "createdLocalIso": ""
            }
          ],
          "players": [
            {
              "id": 0,
              "nick": "Ktulxy",
              "ips": ["203.0.113.9"],
              "lastSeenUtcMs": 0
            }
          ]
        }
        """;
        File.WriteAllText(_path, v1);

        var store = new BanStore(_path);
        var (bans, players) = store.Load();

        Assert.Equal(BanStore.CurrentVersion, store.LastLoadedVersion);
        Assert.True(File.Exists(_path + ".v1.bak"));
        Assert.Equal(v1, File.ReadAllText(_path + ".v1.bak"));

        var saved = File.ReadAllText(_path);
        Assert.Contains($"\"version\": {BanStore.CurrentVersion}", saved);
        Assert.Single(bans);
        Assert.Single(players);
        Assert.Equal("Ktulxy", bans[0].Nick);
        Assert.Equal("203.0.113.9", Assert.Single(bans[0].Ips));
        Assert.Empty(bans[0].Devices);
        Assert.Empty(players[0].Devices);
    }

    [Fact]
    public void V2RoundTripsDevices()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddHours(1));
        var store = new BanStore(_path);

        Assert.True(store.Save(
            new[]
            {
                new BanRecord
                {
                    Nick = "A",
                    Ips = new List<string> { "203.0.113.9" },
                    Devices = new List<string> { Device },
                    ExpiresUtcMs = expires,
                },
            },
            new[]
            {
                new SeenPlayer
                {
                    Nick = "A",
                    Ips = new List<string> { "203.0.113.9" },
                    Devices = new List<string> { Device },
                },
            }));

        var loaded = new BanStore(_path);
        var (bans, players) = loaded.Load();

        Assert.Equal(BanStore.CurrentVersion, loaded.LastLoadedVersion);
        Assert.False(loaded.IsReadOnly);
        Assert.Contains(Device, bans[0].Devices);
        Assert.Contains(Device, players[0].Devices);
        Assert.False(File.Exists(_path + ".v1.bak"));
    }

    [Fact]
    public void MissingVersionIsTreatedAsV1()
    {
        File.WriteAllText(_path, "{\"bans\":[],\"players\":[]}");

        var store = new BanStore(_path);
        store.Load();

        Assert.Equal(BanStore.CurrentVersion, store.LastLoadedVersion);
        Assert.True(File.Exists(_path + ".v1.bak"));
        Assert.Contains($"\"version\": {BanStore.CurrentVersion}", File.ReadAllText(_path));
    }

    [Fact]
    public void FailedBackupBlocksEverySaveAndKeepsV1()
    {
        const string v1 = "{\"version\":1,\"bans\":[],\"players\":[]}";
        File.WriteAllText(_path, v1);
        // Каталог на месте бэкапа: File.Exists его не видит, а File.Copy падает.
        Directory.CreateDirectory(_path + ".v1.bak");

        var store = new BanStore(_path);
        store.Load();

        Assert.Equal(1, store.LastLoadedVersion);
        Assert.False(store.IsReadOnly);
        Assert.Equal(v1, File.ReadAllText(_path));

        // Любой последующий Save (бан, алиас, справочник) не должен перезаписать исходник.
        Assert.False(store.Save(Array.Empty<BanRecord>(), Array.Empty<SeenPlayer>()));
        Assert.Equal(v1, File.ReadAllText(_path));
    }

    [Fact]
    public void NewerVersionIsReadOnlyAndIsNotOverwritten()
    {
        const string v3 = "{\"version\":3,\"bans\":[],\"players\":[]}";
        File.WriteAllText(_path, v3);

        var store = new BanStore(_path);
        store.Load();

        Assert.True(store.IsReadOnly);
        Assert.False(store.Save(Array.Empty<BanRecord>(), Array.Empty<SeenPlayer>()));
        Assert.Equal(v3, File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".v1.bak"));
    }
}
