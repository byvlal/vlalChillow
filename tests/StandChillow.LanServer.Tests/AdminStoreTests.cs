using System.Text.Json;
using StandChillow.LanServer.Auth;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Хранилище админов: персональные пароли, права, конфиговые мега-админы и журнал.
/// Файлы — во временном каталоге, поэтому проверяется и то, что реально ложится на диск.
/// </summary>
public sealed class AdminStoreTests : IDisposable
{
    private const int AdminId = 1000;
    private const int SecondId = 1001;
    private const int MegaId = 1002;
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly AdminStore _store;

    public AdminStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-adminstore-" + Guid.NewGuid().ToString("N"));
        _store = new AdminStore(Path.Combine(_dir, "auth"));
        _store.Initialize(new[] { AdminId, SecondId }, new[] { MegaId }, Now);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    private string PasswordOf(int id) =>
        File.ReadAllLines(_store.PasswordPathOf(id)).First();

    [Fact]
    public void Initialize_CreatesRecordPasswordFileAndDefaults()
    {
        var record = _store.Find(AdminId);

        Assert.NotNull(record);
        Assert.Equal(AdminId, record!.Id);
        Assert.Equal(1, record.AuthVersion);
        Assert.Equal("admin", record.Role);
        Assert.Equal(new[] { "dashboard", "journal" }, record.Permissions);
        Assert.False(_store.IsMega(AdminId));
        Assert.True(File.Exists(_store.PasswordPathOf(AdminId)));

        var password = PasswordOf(AdminId);
        Assert.StartsWith($"{AdminId}-", password);
        Assert.True(AdminPassword.TryParse(AdminId, password, out _));
        Assert.True(AdminPassword.Matches(AdminId, password, record.PasswordHash));
    }

    [Fact]
    public void PasswordFile_IsOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var mode = File.GetUnixFileMode(_store.PasswordPathOf(AdminId));

        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            mode & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite));
        Assert.False(mode.HasFlag(UnixFileMode.GroupRead));
        Assert.False(mode.HasFlag(UnixFileMode.OtherRead));
    }

    [Fact]
    public void AdminsJson_ContainsOnlyHashNeverThePassword()
    {
        var password = PasswordOf(AdminId);
        var secret = password[(password.IndexOf('-') + 1)..];
        var json = File.ReadAllText(_store.RecordsPath);

        Assert.DoesNotContain(secret, json);
        Assert.Contains(_store.Find(AdminId)!.PasswordHash, json);
    }

    [Fact]
    public void Verify_AcceptsRightPasswordRejectsEveryoneElse()
    {
        var password = PasswordOf(AdminId);

        Assert.NotNull(_store.Verify(AdminId, password, Now));
        Assert.Null(_store.Verify(AdminId, password + "0", Now));
        Assert.Null(_store.Verify(SecondId, password, Now));
        Assert.Null(_store.Verify(9999, password, Now));
    }

    [Fact]
    public void Sync_AddsNewAdminAndKeepsExistingPermissions()
    {
        _store.SetPermissions(null, AdminId, new[] { "bans" }, Now, out _);

        _store.Sync(new[] { AdminId, SecondId, 1003 }, Array.Empty<int>(), Now.AddMinutes(1));

        var added = _store.Find(1003);
        Assert.NotNull(added);
        Assert.Equal(new[] { "dashboard", "journal" }, added!.Permissions);
        Assert.True(File.Exists(_store.PasswordPathOf(1003)));

        var existing = _store.Find(AdminId)!;
        Assert.Equal(new[] { "dashboard", "journal", "bans" }, existing.Permissions);
    }

    [Fact]
    public void SetPermissions_AlwaysKeepsDashboardAndJournal()
    {
        Assert.True(_store.SetPermissions(
            null, AdminId, new[] { "bans", "unknown", "bans" }, Now, out _));

        var record = _store.Find(AdminId)!;
        Assert.Equal(new[] { "dashboard", "journal", "bans" }, record.Permissions);
        Assert.True(_store.HasPermission(AdminId, "bans"));
        Assert.True(_store.HasPermission(AdminId, "journal"));
        Assert.False(_store.HasPermission(AdminId, "reports"));
    }

    [Fact]
    public void SetPermissions_RefusesUnknownAdmin()
    {
        Assert.False(_store.SetPermissions(
            null, 9999, new[] { "bans" }, Now, out var error));

        Assert.NotNull(error);
        Assert.Contains("9999", error);
    }

    [Fact]
    public void MegaFromConfig_GrantsAllPermissionsWithoutAnyIp()
    {
        Assert.True(_store.IsMega(MegaId));
        Assert.False(_store.IsMega(AdminId));
        Assert.Equal(AdminPermissions.All, _store.PermissionsFor(MegaId));
        Assert.True(_store.HasPermission(MegaId, "rollback"));
        Assert.False(_store.HasPermission(AdminId, "rollback"));

        var record = _store.Find(MegaId)!;
        Assert.Equal("mega", record.Role);
        Assert.Equal(AdminPermissions.Normalize(null), record.Permissions);
    }

    [Fact]
    public void IdInBothLists_IsOneRecordAndMega()
    {
        _store.Sync(new[] { AdminId, SecondId }, new[] { AdminId }, Now.AddMinutes(1));

        Assert.True(_store.IsMega(AdminId));
        Assert.Equal("mega", _store.Find(AdminId)!.Role);
        Assert.Single(_store.Records().Where(record => record.Id == AdminId));
        Assert.Equal(AdminPermissions.All, _store.PermissionsFor(AdminId));
    }

    [Fact]
    public void RemovedFromMegaIds_DropsMegaButKeepsAdminRecord()
    {
        _store.Sync(new[] { AdminId, MegaId }, Array.Empty<int>(), Now.AddMinutes(1));

        Assert.False(_store.IsMega(MegaId));
        Assert.Equal("admin", _store.Find(MegaId)!.Role);
        Assert.False(_store.HasPermission(MegaId, "rollback"));
        Assert.True(File.Exists(_store.PasswordPathOf(MegaId)));
    }

    [Fact]
    public void RuntimeMega_IsInMemoryOnly_AndStartupPrunesTheRecord()
    {
        const int PlayerId = 2000;

        Assert.True(_store.GrantRuntimeMega(PlayerId, "тест", Now));
        Assert.True(_store.IsRuntimeMega(PlayerId));
        Assert.True(_store.IsMega(PlayerId));
        Assert.True(_store.HasPermission(PlayerId, "rollback"));
        Assert.Equal("mega", _store.Find(PlayerId)!.Role);

        var journal = _store.ReadJournal(new AdminJournalQuery { Category = "security" });
        Assert.Contains(journal.Entries, entry => entry.Action == "test_auto_mega");

        var restarted = new AdminStore(_store.Directory);
        restarted.Initialize(new[] { AdminId, SecondId }, new[] { MegaId }, Now.AddMinutes(1));

        Assert.False(restarted.IsRuntimeMega(PlayerId));
        Assert.False(restarted.IsMega(PlayerId));
        Assert.Null(restarted.Find(PlayerId));
    }

    [Fact]
    public void OldAdminsJsonWithMegaIps_LoadsAndRoleComesFromConfig()
    {
        var path = _store.RecordsPath;
        var json = File.ReadAllText(path);
        json = json.Replace(
            $"\"id\": {MegaId},",
            $"\"id\": {MegaId},\n    \"megaIps\": [ \"192.168.1.50\" ],");
        File.WriteAllText(path, json);

        var store = new AdminStore(Path.Combine(_dir, "auth"));
        store.Initialize(new[] { AdminId, MegaId }, new[] { MegaId }, Now);

        Assert.True(store.Find(MegaId) is not null);
        Assert.True(store.IsMega(MegaId));

        // Любая следующая запись пересобирает JSON без старого поля.
        store.SetPermissions(null, AdminId, new[] { "bans" }, Now, out _);
        Assert.DoesNotContain("megaIps", File.ReadAllText(path));
    }

    [Fact]
    public void RegeneratePassword_BumpsVersionAndKillsOldPassword()
    {
        var oldPassword = PasswordOf(AdminId);
        var oldVersion = _store.Find(AdminId)!.AuthVersion;

        Assert.True(_store.RegeneratePassword(
            null, AdminId, Now.AddMinutes(1), out var path, out _));

        var newPassword = File.ReadAllLines(path).First();
        Assert.NotEqual(oldPassword, newPassword);
        Assert.Equal(oldVersion + 1, _store.Find(AdminId)!.AuthVersion);
        Assert.Null(_store.Verify(AdminId, oldPassword, Now.AddMinutes(2)));
        Assert.NotNull(_store.Verify(AdminId, newPassword, Now.AddMinutes(2)));
    }

    [Fact]
    public void RegenerateAllPasswords_RewritesEveryAdmin()
    {
        var firstOld = PasswordOf(AdminId);
        var secondOld = PasswordOf(SecondId);
        var megaOld = PasswordOf(MegaId);

        var result = _store.RegenerateAllPasswords(null, Now.AddMinutes(1));

        // Мега-админ тоже админ: пароль есть и у него.
        Assert.Equal(3, result.Count);
        Assert.NotEqual(firstOld, PasswordOf(AdminId));
        Assert.NotEqual(secondOld, PasswordOf(SecondId));
        Assert.NotEqual(megaOld, PasswordOf(MegaId));
        Assert.All(result, pair => Assert.True(File.Exists(pair.Path)));
    }

    [Fact]
    public void Initialize_RetiresLegacyAuthTxt()
    {
        var legacy = Path.Combine(_store.Directory, "..", "auth.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "OLDCODE");

        var store = new AdminStore(Path.Combine(_dir, "auth"));
        store.Initialize(new[] { AdminId }, Array.Empty<int>(), Now);

        Assert.False(File.Exists(legacy));
    }

    [Fact]
    public void Journal_AppendsJsonLines()
    {
        var path = Path.Combine(_store.Directory, AdminStore.JournalFileName);
        File.Delete(path);

        _store.Journal("auth", "login", AdminId, AdminId);
        _store.Journal(
            "permission", "set", AdminId, SecondId,
            before: new[] { "dashboard" },
            after: new[] { "dashboard", "journal", "bans" });

        var lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.StartsWith("{", line));

        using var second = JsonDocument.Parse(lines[1]);
        var after = second.RootElement.GetProperty("after");
        Assert.Equal(JsonValueKind.Array, after.ValueKind);
        Assert.Contains("bans", after.EnumerateArray().Select(item => item.GetString()));
        Assert.All(lines, line =>
        {
            using var doc = JsonDocument.Parse(line);
            Assert.False(string.IsNullOrWhiteSpace(
                doc.RootElement.GetProperty("id").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(
                doc.RootElement.GetProperty("ts").GetString()));
        });
    }

    [Fact]
    public void ResolveDirectory_IsAuthNextToConfigJson()
    {
        var configPath = Path.Combine(_dir, "config.json");

        Assert.Equal(
            Path.Combine(_dir, AdminStore.DirectoryName),
            AdminStore.ResolveDirectory(configPath));
    }
}
