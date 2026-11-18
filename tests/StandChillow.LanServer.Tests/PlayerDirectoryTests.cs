using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Справочник «ник ↔ id ↔ IP ↔ устройство»: нужен, чтобы <c>/ban</c> работал для ушедшего
/// игрока и после рестарта, и чтобы 200 банов не путали личности между собой.
/// </summary>
public sealed class PlayerDirectoryTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private const string Device = "LB143C7D41FAE30EB14F49B1678E3933C9";

    [Fact]
    public void RememberStoresDeterministicIdAndIps()
    {
        var directory = new PlayerDirectory();
        var player = directory.Remember("Ktulxy", "203.0.113.9", Now);

        Assert.NotNull(player);
        Assert.Equal(MatchPublicId.ForNick("Ktulxy"), player!.Id);
        Assert.Contains("203.0.113.9", player.Ips);

        directory.Remember("Ktulxy", "10.8.0.2", Now.AddMinutes(5));
        Assert.Equal(2, directory.Resolve(player.Id)!.Ips.Count);
    }

    [Fact]
    public void ResolveFindsPlayerWhoAlreadyLeft()
    {
        var directory = new PlayerDirectory();
        var id = MatchPublicId.ForNick("Ktulxy");
        directory.Remember("Ktulxy", "203.0.113.9", Now);

        var found = directory.Resolve(id);

        Assert.NotNull(found);
        Assert.Equal("Ktulxy", found!.Nick);
        Assert.Contains("203.0.113.9", found.Ips);
    }

    [Fact]
    public void DifferentPeopleAreSeparateEntries()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Ktulxy", "203.0.113.9", Now);
        directory.Remember("Другой", "203.0.113.10", Now);

        Assert.Equal(2, directory.Count);
        Assert.Equal("Ktulxy", directory.Resolve(MatchPublicId.ForNick("Ktulxy"))!.Nick);
        Assert.Equal("Другой", directory.Resolve(MatchPublicId.ForNick("Другой"))!.Nick);
    }

    [Fact]
    public void TwoHundredPeopleStayDistinct()
    {
        var directory = new PlayerDirectory();
        for (var i = 0; i < 200; i++)
            directory.Remember($"игрок-{i}", $"10.0.0.{i % 250}", Now);

        Assert.Equal(200, directory.Count);

        for (var i = 0; i < 200; i++)
        {
            var found = directory.Resolve(MatchPublicId.ForNick($"игрок-{i}"));
            // При редкой коллизии хеша Resolve вернёт одного из них — сверяем только
            // тех, у кого id уникален.
            if (directory.CountById(found!.Id) == 1)
                Assert.Equal($"игрок-{i}", found.Nick);
        }
    }

    [Fact]
    public void LoadRecomputesIdFromNickSoOldFilesStayConsistent()
    {
        var id = MatchPublicId.ForNick("Ktulxy");
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            // Битый/старый id в файле: при чтении он пересчитывается из ника.
            new SeenPlayer { Id = 1, Nick = "Ktulxy", Ips = new List<string> { "203.0.113.9" } },
        });

        Assert.Equal(id, directory.Resolve(id)!.Id);
        Assert.Equal(1, directory.Count);
    }

    [Fact]
    public void EmptyNickIsNotStored()
    {
        var directory = new PlayerDirectory();
        Assert.Null(directory.Remember("   ", "10.0.0.1", Now));
        Assert.Equal(0, directory.Count);
    }

    [Fact]
    public void RememberStoresDeviceAndNormalizesIt()
    {
        var directory = new PlayerDirectory();
        var player = directory.Remember("Ktulxy", "203.0.113.9", Now, Device.ToLowerInvariant());

        Assert.NotNull(player);
        Assert.Contains(Device, player!.Devices);
        Assert.Contains(Device, directory.DevicesForNick("ktulxy"));
    }

    [Fact]
    public void InvalidDeviceIsIgnored()
    {
        var directory = new PlayerDirectory();
        var player = directory.Remember("Ktulxy", "203.0.113.9", Now, "not-a-device");

        Assert.NotNull(player);
        Assert.Empty(player!.Devices);
        Assert.Empty(directory.DevicesForNick("Ktulxy"));
    }

    [Fact]
    public void LoadKeepsDevicesAndDropsJunk()
    {
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            new SeenPlayer
            {
                Nick = "Ktulxy",
                Ips = new List<string> { "203.0.113.9" },
                Devices = new List<string> { Device.ToLowerInvariant(), "legacy-user-id" },
            },
        });

        var found = directory.Resolve(MatchPublicId.ForNick("Ktulxy"));
        Assert.NotNull(found);
        Assert.Single(found!.Devices);
        Assert.Contains(Device, found.Devices);
        Assert.Contains(Device, directory.DevicesForNick("Ktulxy"));
    }
}
