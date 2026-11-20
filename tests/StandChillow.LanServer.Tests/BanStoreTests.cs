using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// <c>banlist.json</c>: сброс старых банов при переходе на v3, бэкап, очистка
/// изученных адресов/устройств, roundtrip v3 и защита от файла будущей версии.
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
    public void V1IsBackedUpAndResetToV3()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddDays(1));
        var v1 = $$"""
        {
          "version": 1,
          "bans": [
            {
              "nick": "Ktulxy",
              "aliases": [ "Ktulxy2" ],
              "ips": [ "203.0.113.9" ],
              "matchIds": [ 671488 ],
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
              "id": 671488,
              "nick": "Ktulxy",
              "ips": [ "203.0.113.9" ],
              "lastSeenUtcMs": 1763035200000
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
        Assert.Contains("\"bans\": []", saved);
        Assert.DoesNotContain("203.0.113.9", saved);
        Assert.DoesNotContain("Ktulxy2", saved);

        // Старые баны не мигрируют: их место занимает новая логика.
        Assert.Empty(bans);

        // Справочник остаётся, но только ник, id и время: старым IP/устройствам нет доверия.
        var player = Assert.Single(players);
        Assert.Equal("Ktulxy", player.Nick);
        Assert.Equal(671488, player.Id);
        Assert.Equal(1763035200000, player.LastSeenUtcMs);
        Assert.Empty(player.Ips);
        Assert.Empty(player.Devices);
    }

    [Fact]
    public void V2IsBackedUpAndOldBansAreDropped()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddDays(1));
        var v2 = $$"""
        {
          "version": 2,
          "bans": [
            {
              "nick": "Ktulxy",
              "aliases": [ "Ktulxy2" ],
              "ips": [ "203.0.113.9" ],
              "devices": [ "{{Device}}" ],
              "matchIds": [ 671488 ],
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
              "id": 671488,
              "nick": "Ktulxy",
              "ips": [ "203.0.113.9" ],
              "devices": [ "{{Device}}" ],
              "lastSeenUtcMs": 1763035200000
            }
          ]
        }
        """;
        File.WriteAllText(_path, v2);

        var store = new BanStore(_path);
        var (bans, players) = store.Load();

        Assert.Equal(BanStore.CurrentVersion, store.LastLoadedVersion);
        Assert.True(File.Exists(_path + ".v2.bak"));
        Assert.Equal(v2, File.ReadAllText(_path + ".v2.bak"));

        Assert.Empty(bans);
        var player = Assert.Single(players);
        Assert.Equal("Ktulxy", player.Nick);
        Assert.Empty(player.Ips);
        Assert.Empty(player.Devices);

        var saved = File.ReadAllText(_path);
        Assert.Contains($"\"version\": {BanStore.CurrentVersion}", saved);
        Assert.DoesNotContain(Device, saved);
        Assert.DoesNotContain("203.0.113.9", saved);
    }

    [Fact]
    public void MigrationMergesCaseVariantsAndKeepsTheNewestId()
    {
        const string v2 = """
        {
          "version": 2,
          "bans": [],
          "players": [
            { "id": 111, "nick": "Влал", "lastSeenUtcMs": 100 },
            { "id": 222, "nick": "влал", "lastSeenUtcMs": 200 },
            { "id": 333, "nick": "Другой", "lastSeenUtcMs": 50 }
          ]
        }
        """;
        File.WriteAllText(_path, v2);

        var store = new BanStore(_path);
        var (_, players) = store.Load();

        // «Влал» и «влал» — один ник: иначе /ban по id отказывал бы «id делят два игрока».
        Assert.Equal(2, players.Count);
        var merged = Assert.Single(players, p => string.Equals(p.Nick, "Влал", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(222, merged.Id);
        Assert.Equal(200, merged.LastSeenUtcMs);
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
    public void FailedBackupStillResetsBansAndWritesV3()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddDays(1));
        var v1 = $$"""
        {
          "version": 1,
          "bans": [
            {
              "nick": "Ktulxy",
              "ips": [ "203.0.113.9" ],
              "expiresUtcMs": {{expires}}
            }
          ],
          "players": []
        }
        """;
        File.WriteAllText(_path, v1);
        // Каталог на месте бэкапа: File.Exists его не видит, а File.Copy падает.
        Directory.CreateDirectory(_path + ".v1.bak");

        var store = new BanStore(_path);
        var (bans, _) = store.Load();

        // Бэкап — best-effort: активный файл всё равно переписывается, старые баны сброшены.
        Assert.Equal(BanStore.CurrentVersion, store.LastLoadedVersion);
        Assert.False(store.IsReadOnly);
        Assert.Empty(bans);

        var saved = File.ReadAllText(_path);
        Assert.Contains($"\"version\": {BanStore.CurrentVersion}", saved);
        Assert.Contains("\"bans\": []", saved);
        Assert.DoesNotContain("203.0.113.9", saved);
    }

    [Fact]
    public void V3RoundTripsBansAndPlayers()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddHours(1));
        var store = new BanStore(_path);

        Assert.True(store.Save(
            new[]
            {
                new BanRecord
                {
                    Nick = "A",
                    Aliases = new List<string> { "B" },
                    Ips = new List<string> { "203.0.113.9" },
                    Devices = new List<string> { Device },
                    MatchIds = new List<int> { 671488 },
                    Reason = "софт",
                    BannedBy = "влал",
                    CreatedUtcMs = BanRecord.ToUnixMs(DateTime.UtcNow),
                    ExpiresUtcMs = expires,
                    DurationSeconds = 3600,
                },
            },
            new[]
            {
                new SeenPlayer
                {
                    Id = 671488,
                    Nick = "A",
                    Ips = new List<string> { "203.0.113.9" },
                    Devices = new List<string> { Device },
                    LastSeenUtcMs = 1763035200000,
                },
            }));

        var loaded = new BanStore(_path);
        var (bans, players) = loaded.Load();

        Assert.Equal(BanStore.CurrentVersion, loaded.LastLoadedVersion);
        Assert.False(loaded.IsReadOnly);
        Assert.False(File.Exists(_path + ".v1.bak"));
        Assert.False(File.Exists(_path + ".v2.bak"));

        var ban = Assert.Single(bans);
        Assert.Equal("A", ban.Nick);
        Assert.Contains("B", ban.Aliases);
        Assert.Contains("203.0.113.9", ban.Ips);
        Assert.Contains(Device, ban.Devices);
        Assert.Contains(671488, ban.MatchIds);
        Assert.Equal(expires, ban.ExpiresUtcMs);

        var player = Assert.Single(players);
        Assert.Equal(671488, player.Id);
        Assert.Equal("A", player.Nick);
        Assert.Contains(Device, player.Devices);
        Assert.Equal(1763035200000, player.LastSeenUtcMs);
    }

    [Fact]
    public void CurrentV3FileKeepsItsBans()
    {
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddDays(1));
        var v3 = $$"""
        {
          "version": 3,
          "bans": [
            {
              "nick": "Ktulxy",
              "ips": [ "203.0.113.9" ],
              "expiresUtcMs": {{expires}}
            }
          ],
          "players": []
        }
        """;
        File.WriteAllText(_path, v3);

        var store = new BanStore(_path);
        var (bans, _) = store.Load();

        Assert.Equal(BanStore.CurrentVersion, store.LastLoadedVersion);
        Assert.False(store.IsReadOnly);
        Assert.Single(bans);
        Assert.Equal("Ktulxy", bans[0].Nick);
        Assert.False(File.Exists(_path + ".v1.bak"));
        Assert.False(File.Exists(_path + ".v2.bak"));
    }

    [Fact]
    public void NewerVersionIsReadOnlyAndIsNotOverwritten()
    {
        const string v4 = "{\"version\":4,\"bans\":[],\"players\":[]}";
        File.WriteAllText(_path, v4);

        var store = new BanStore(_path);
        store.Load();

        Assert.True(store.IsReadOnly);
        Assert.False(store.Save(Array.Empty<BanRecord>(), Array.Empty<SeenPlayer>()));
        Assert.Equal(v4, File.ReadAllText(_path));
        Assert.False(File.Exists(_path + ".v3.bak"));
    }
}
