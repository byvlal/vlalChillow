using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Ключ Telegram-бота: генерация, загрузка, права файла и ротация. Ключ — секрет,
/// поэтому проверяется и то, что он не появляется в тексте ошибок и не остаётся
/// в файле после сброса.
/// </summary>
public sealed class BotKeyStoreTests : IDisposable
{
    private readonly string _dir;

    public BotKeyStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-botkey-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    private BotKeyStore NewStore() =>
        new(Path.Combine(_dir, "auth", BotKeyStore.DirectoryName));

    [Fact]
    public void LoadOrCreate_CreatesNestedDirectoryAndHex64File()
    {
        var store = NewStore();

        Assert.True(store.LoadOrCreate());
        Assert.True(File.Exists(store.FilePath));
        Assert.True(BotKeyStore.IsValid(store.Current));
        Assert.Equal(64, store.Current!.Length);
        Assert.Equal(store.Current, File.ReadAllText(store.FilePath).Trim());
    }

    [Fact]
    public void LoadOrCreate_KeepsExistingKey()
    {
        var first = NewStore();
        Assert.True(first.LoadOrCreate());
        var key = first.Current;

        var second = NewStore();
        Assert.True(second.LoadOrCreate());

        Assert.Equal(key, second.Current);
    }

    [Fact]
    public void LoadOrCreate_NormalizesUppercaseHex()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.DirectoryPath);
        var upper = new string('A', 64);
        File.WriteAllText(store.FilePath, upper + "\n");

        Assert.True(store.LoadOrCreate());
        Assert.Equal(new string('a', 64), store.Current);
    }

    [Fact]
    public void LoadOrCreate_ReplacesBrokenFileAndKeepsItAsBad()
    {
        var store = NewStore();
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(store.FilePath, "это не ключ");

        Assert.True(store.LoadOrCreate());
        Assert.True(BotKeyStore.IsValid(store.Current));
        Assert.True(File.Exists(store.FilePath + ".bad"));
        Assert.Contains("это не ключ", File.ReadAllText(store.FilePath + ".bad"));
    }

    [Fact]
    public void Matches_RequiresExactCurrentKey()
    {
        var store = NewStore();
        Assert.True(store.LoadOrCreate());
        var key = store.Current!;

        Assert.True(store.Matches(key));
        Assert.True(store.Matches("  " + key + "\n"));
        Assert.False(store.Matches(key[..^1]));
        Assert.False(store.Matches(key + "0"));
        Assert.False(store.Matches(key.ToUpperInvariant()));
        Assert.False(store.Matches(""));
        Assert.False(store.Matches(null));
    }

    [Fact]
    public void Rotate_ChangesKeyImmediately()
    {
        var store = NewStore();
        Assert.True(store.LoadOrCreate());
        var oldKey = store.Current!;

        var rotation = store.Rotate();

        Assert.True(rotation.Ok);
        Assert.True(BotKeyStore.IsValid(rotation.Key));
        Assert.NotEqual(oldKey, rotation.Key);
        Assert.Equal(rotation.Key, store.Current);
        Assert.Equal(rotation.Key, File.ReadAllText(store.FilePath).Trim());
        Assert.False(store.Matches(oldKey));
        Assert.True(store.Matches(rotation.Key!));
    }

    [Fact]
    public void KeyFileAndDirectory_AreOwnerOnlyOnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;

        var store = NewStore();
        Assert.True(store.LoadOrCreate());

        var file = File.GetUnixFileMode(store.FilePath);
        Assert.Equal(
            UnixFileMode.UserRead | UnixFileMode.UserWrite,
            file & (UnixFileMode.UserRead | UnixFileMode.UserWrite |
                    UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
                    UnixFileMode.OtherRead | UnixFileMode.OtherWrite));

        var directory = File.GetUnixFileMode(store.DirectoryPath);
        Assert.False(directory.HasFlag(UnixFileMode.GroupRead));
        Assert.False(directory.HasFlag(UnixFileMode.OtherRead));
        Assert.False(directory.HasFlag(UnixFileMode.GroupExecute));
        Assert.False(directory.HasFlag(UnixFileMode.OtherExecute));
    }
}
