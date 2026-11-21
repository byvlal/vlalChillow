using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Web;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Перенумерация аватаров: номера могут меняться местами (1 000 000 ↔ 1 000 001),
/// поэтому файлы должны пережить такой обмен без потерь и без архива.
/// </summary>
public sealed class AvatarCacheRemapTests : IDisposable
{
    private readonly string _dir;

    public AvatarCacheRemapTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "stch-avatars-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* тестовый мусор не должен ронять прогон */ }
    }

    [Fact]
    public void RemapSwapsTwoIdsWithoutArchivingEitherFile()
    {
        var first = Path.Combine(_dir, "1000000.jpg");
        var second = Path.Combine(_dir, "1000001.png");
        File.WriteAllBytes(first, new byte[] { 1, 1, 1 });
        File.WriteAllBytes(second, new byte[] { 2, 2, 2 });

        var cache = new AvatarCache(_dir);
        var moved = cache.RemapIds(oldId => oldId switch
        {
            1000000 => 1000001,
            1000001 => 1000000,
            _ => oldId,
        });

        Assert.Equal(2, moved);
        Assert.Equal(new byte[] { 1, 1, 1 }, File.ReadAllBytes(Path.Combine(_dir, "1000001.jpg")));
        Assert.Equal(new byte[] { 2, 2, 2 }, File.ReadAllBytes(Path.Combine(_dir, "1000000.png")));
        Assert.False(File.Exists(first));
        Assert.False(File.Exists(second));
        Assert.False(Directory.Exists(Path.Combine(_dir, "legacy")));
    }

    [Fact]
    public void RemapArchivesUnknownIdInsteadOfOverwritingTheCurrentAvatar()
    {
        File.WriteAllBytes(Path.Combine(_dir, "1000000.jpg"), new byte[] { 1 });
        // Старый хеш ника, который теперь указывает на занятый номер 1 000 000.
        File.WriteAllBytes(Path.Combine(_dir, (LegacyNickHash.Max - 1) + ".jpg"), new byte[] { 9 });

        var cache = new AvatarCache(_dir);
        var moved = cache.RemapIds(oldId => oldId == LegacyNickHash.Max - 1 ? 1000000 : oldId);

        Assert.Equal(1, moved);
        Assert.Equal(new byte[] { 1 }, File.ReadAllBytes(Path.Combine(_dir, "1000000.jpg")));
        Assert.Equal(
            new byte[] { 9 },
            File.ReadAllBytes(Path.Combine(_dir, "legacy", (LegacyNickHash.Max - 1) + ".jpg")));
    }
}
