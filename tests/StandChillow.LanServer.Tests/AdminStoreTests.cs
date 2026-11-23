using System.Net;
using System.Text.Json;
using StandChillow.LanServer.Auth;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Хранилище админов: персональные пароли, права, мега-IP и журнал. Файлы — во
/// временном каталоге, поэтому проверяется и то, что реально ложится на диск.
/// </summary>
public sealed class AdminStoreTests : IDisposable
{
    private const int AdminId = 1000;
    private const int SecondId = 1001;
    private static readonly DateTime Now = new(2026, 11, 24, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly AdminStore _store;

    public AdminStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-adminstore-" + Guid.NewGuid().ToString("N"));
        _store = new AdminStore(Path.Combine(_dir, "auth"));
        _store.Initialize(new[] { AdminId, SecondId }, Now);
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
        Assert.Empty(record.MegaIps);
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

        _store.Sync(new[] { AdminId, SecondId, 1002 }, Now.AddMinutes(1));

        var added = _store.Find(1002);
        Assert.NotNull(added);
        Assert.Equal(new[] { "dashboard", "journal" }, added!.Permissions);
        Assert.True(File.Exists(_store.PasswordPathOf(1002)));

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
        Assert.True(_store.HasPermission(AdminId, null, "bans"));
        Assert.True(_store.HasPermission(AdminId, null, "journal"));
        Assert.False(_store.HasPermission(AdminId, null, "reports"));
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
    public void MegaIp_GrantsAllPermissionsOnlyFromThatAddress()
    {
        var megaIp = IPAddress.Parse("192.168.1.50");
        Assert.True(_store.AddMegaIp(null, AdminId, megaIp.ToString(), Now, out _));

        Assert.True(_store.IsMega(AdminId, megaIp));
        Assert.False(_store.IsMega(AdminId, IPAddress.Parse("192.168.1.51")));
        Assert.Equal(AdminPermissions.All, _store.PermissionsFor(AdminId, megaIp));
        Assert.True(_store.HasPermission(AdminId, megaIp, "rollback"));
        Assert.False(_store.HasPermission(AdminId, IPAddress.Parse("192.168.1.51"), "rollback"));
    }

    [Fact]
    public void MegaIp_NormalizesMappedIPv4AndRejectsDuplicates()
    {
        Assert.True(_store.AddMegaIp(null, AdminId, "::ffff:192.168.1.50", Now, out _));

        Assert.True(_store.IsMega(AdminId, IPAddress.Parse("192.168.1.50")));
        Assert.False(_store.AddMegaIp(null, AdminId, "192.168.1.50", Now, out var duplicate));
        Assert.NotNull(duplicate);
        Assert.Single(_store.Find(AdminId)!.MegaIps);
    }

    [Fact]
    public void RemoveMegaIp_StopsMegaRights()
    {
        Assert.True(_store.AddMegaIp(null, AdminId, "192.168.1.50", Now, out _));
        Assert.True(_store.RemoveMegaIp(null, AdminId, "192.168.1.50", Now, out _));

        Assert.False(_store.IsMega(AdminId, IPAddress.Parse("192.168.1.50")));
        Assert.Equal("admin", _store.Find(AdminId)!.Role);
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

        var result = _store.RegenerateAllPasswords(null, Now.AddMinutes(1));

        Assert.Equal(2, result.Count);
        Assert.NotEqual(firstOld, PasswordOf(AdminId));
        Assert.NotEqual(secondOld, PasswordOf(SecondId));
        Assert.All(result, pair => Assert.True(File.Exists(pair.Path)));
    }

    [Fact]
    public void Initialize_RetiresLegacyAuthTxt()
    {
        var legacy = Path.Combine(_store.Directory, "..", "auth.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(legacy)!);
        File.WriteAllText(legacy, "OLDCODE");

        var store = new AdminStore(Path.Combine(_dir, "auth"));
        store.Initialize(new[] { AdminId }, Now);

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
