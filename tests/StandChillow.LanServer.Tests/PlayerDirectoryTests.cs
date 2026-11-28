using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Справочник «аккаунт ↔ ник ↔ IP ↔ устройство»: нужен, чтобы <c>/ban</c> работал для
/// ушедшего игрока и после рестарта, а тёзки оставались разными людьми.
/// </summary>
public sealed class PlayerDirectoryTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);
    private const string Device = "LB143C7D41FAE30EB14F49B1678E3933C9";
    private const int AccountA = AccountStore.FirstId;
    private const int AccountB = AccountStore.FirstId + 1;

    [Fact]
    public void RememberStoresAccountIdAndIps()
    {
        var directory = new PlayerDirectory();
        var player = directory.Remember("Ktulxy", "203.0.113.9", Now, accountId: AccountA);

        Assert.NotNull(player);
        Assert.Equal(AccountA, player!.Id);
        Assert.Contains("203.0.113.9", player.Ips);

        directory.Remember("Ktulxy", "10.8.0.2", Now.AddMinutes(5), accountId: AccountA);
        Assert.Equal(2, directory.Resolve(AccountA)!.Ips.Count);
    }

    [Fact]
    public void RememberWithoutAccountIsNotStored()
    {
        var directory = new PlayerDirectory();
        Assert.Null(directory.Remember("Ktulxy", "203.0.113.9", Now));
        Assert.Null(directory.Remember("Ktulxy", "203.0.113.9", Now, accountId: 1));
        Assert.Equal(0, directory.Count);
    }

    [Fact]
    public void ResolveFindsPlayerWhoAlreadyLeft()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Ktulxy", "203.0.113.9", Now, accountId: AccountA);

        var found = directory.Resolve(AccountA);

        Assert.NotNull(found);
        Assert.Equal("Ktulxy", found!.Nick);
        Assert.Contains("203.0.113.9", found.Ips);
    }

    [Fact]
    public void DifferentPeopleAreSeparateEntries()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Ktulxy", "203.0.113.9", Now, accountId: AccountA);
        directory.Remember("Другой", "203.0.113.10", Now, accountId: AccountB);

        Assert.Equal(2, directory.Count);
        Assert.Equal("Ktulxy", directory.Resolve(AccountA)!.Nick);
        Assert.Equal("Другой", directory.Resolve(AccountB)!.Nick);
    }

    [Fact]
    public void SameExactNickOnTwoAccountsStaysTwoPeople()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Влал", "203.0.113.9", Now, accountId: AccountA);
        directory.Remember("Влал", "203.0.113.10", Now.AddMinutes(1), accountId: AccountB);

        // Ник — не личность: два старых аккаунта с одной строкой остаются двумя записями.
        Assert.Equal(2, directory.Count);
        Assert.Equal(AccountA, directory.Resolve(AccountA)!.Id);
        Assert.Equal(AccountB, directory.Resolve(AccountB)!.Id);

        // Ссылка по нику ведёт к тому, кто заходил позже.
        Assert.Equal(AccountB, directory.FindByNick("Влал")!.Id);

        directory.Remember("Влал", "203.0.113.9", Now.AddMinutes(2), accountId: AccountA);
        Assert.Equal(AccountA, directory.FindByNick("Влал")!.Id);
        Assert.Equal(2, directory.Count);
    }

    [Fact]
    public void NickCaseIsIdentity()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Влал", "203.0.113.9", Now, accountId: AccountA);
        directory.Remember("влал", "203.0.113.10", Now.AddMinutes(1), accountId: AccountB);

        Assert.Equal(AccountA, directory.FindByNick("Влал")!.Id);
        Assert.Equal(AccountB, directory.FindByNick("влал")!.Id);
        Assert.Null(directory.FindByNick("ВЛАЛ"));
    }

    [Fact]
    public void SameAccountKeepsOneEntryWhenNickChanges()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Влал", "203.0.113.9", Now, accountId: AccountA);
        directory.Remember("Влал2", "10.8.0.2", Now.AddMinutes(1), accountId: AccountA);

        Assert.Equal(1, directory.Count);
        var found = directory.Resolve(AccountA);
        Assert.NotNull(found);
        Assert.Equal("Влал2", found!.Nick);
        Assert.Equal(2, found.Ips.Count);
        Assert.Null(directory.FindByNick("Влал"));
        Assert.Equal(AccountA, directory.FindByNick("Влал2")!.Id);
        Assert.Null(directory.FindByNick("влал2"));
    }

    [Fact]
    public void TwoHundredPeopleStayDistinct()
    {
        var directory = new PlayerDirectory();
        for (var i = 0; i < 200; i++)
        {
            directory.Remember(
                $"игрок-{i}", $"10.0.0.{i % 250}", Now, accountId: AccountStore.FirstId + i);
        }

        Assert.Equal(200, directory.Count);

        for (var i = 0; i < 200; i++)
        {
            var found = directory.Resolve(AccountStore.FirstId + i);
            Assert.NotNull(found);
            Assert.Equal($"игрок-{i}", found!.Nick);
        }
    }

    [Fact]
    public void LoadKeepsLegacyIdsUntilRemap()
    {
        var legacyId = 4242;
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            new SeenPlayer { Id = legacyId, Nick = "Ktulxy", Ips = new List<string> { "203.0.113.9" } },
        });

        Assert.Equal(legacyId, directory.Resolve(legacyId)!.Id);
        Assert.Equal(1, directory.Count);

        var remapped = directory.RemapAccountIds(
            (oldId, nick) => oldId == legacyId && nick == "Ktulxy" ? AccountA : 0);

        Assert.Equal(1, remapped);
        Assert.Null(directory.Resolve(legacyId));
        Assert.Equal("Ktulxy", directory.Resolve(AccountA)!.Nick);
    }

    [Fact]
    public void RemapMergesLegacyRecordsOfOneAccount()
    {
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            new SeenPlayer
            {
                Id = 111,
                Nick = "Влал",
                Ips = new List<string> { "203.0.113.9" },
                Devices = new List<string> { Device },
                LastSeenUtcMs = 100,
            },
            new SeenPlayer
            {
                Id = 222,
                Nick = "влал",
                Ips = new List<string> { "10.8.0.2" },
                Devices = new List<string> { Device.ToLowerInvariant() },
                LastSeenUtcMs = 200,
            },
        });

        // Пока id старые, это две записи: диапазон номера ничего не доказывает.
        Assert.Equal(2, directory.Count);

        directory.RemapAccountIds((oldId, nick) => AccountA);

        Assert.Equal(1, directory.Count);
        var found = directory.Resolve(AccountA);
        Assert.NotNull(found);
        Assert.Equal(200, found!.LastSeenUtcMs);
        Assert.Contains("203.0.113.9", found.Ips);
        Assert.Contains("10.8.0.2", found.Ips);
        Assert.Single(found.Devices);
        Assert.Contains(Device, found.Devices);
    }

    [Fact]
    public void RemapDropsUnknownPeopleAndCallsMapForAccountIds()
    {
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            new SeenPlayer { Id = 100, Nick = "умерший" },
            new SeenPlayer { Id = AccountB, Nick = "живой" },
        });

        var seen = new List<int>();
        directory.RemapAccountIds((oldId, nick) =>
        {
            seen.Add(oldId);
            return nick == "живой" ? AccountB : 0;
        });

        Assert.Contains(100, seen);
        Assert.Contains(AccountB, seen);
        Assert.Equal(1, directory.Count);
        Assert.Null(directory.Resolve(100));
        Assert.Equal("живой", directory.Resolve(AccountB)!.Nick);
    }

    [Fact]
    public void LoadSkipsEmptyNick()
    {
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            new SeenPlayer { Id = AccountA, Nick = "   " },
        });

        Assert.Equal(0, directory.Count);
    }

    [Fact]
    public void EmptyNickIsNotStored()
    {
        var directory = new PlayerDirectory();
        Assert.Null(directory.Remember("   ", "10.0.0.1", Now, accountId: AccountA));
        Assert.Equal(0, directory.Count);
    }

    [Fact]
    public void RememberStoresDeviceAndNormalizesIt()
    {
        var directory = new PlayerDirectory();
        var player = directory.Remember(
            "Ktulxy", "203.0.113.9", Now, Device.ToLowerInvariant(), AccountA);

        Assert.NotNull(player);
        Assert.Contains(Device, player!.Devices);
        Assert.Contains(Device, directory.DevicesForAccount(AccountA));
    }

    [Fact]
    public void InvalidDeviceIsIgnored()
    {
        var directory = new PlayerDirectory();
        var player = directory.Remember(
            "Ktulxy", "203.0.113.9", Now, "not-a-device", AccountA);

        Assert.NotNull(player);
        Assert.Empty(player!.Devices);
        Assert.Empty(directory.DevicesForAccount(AccountA));
    }

    [Fact]
    public void DevicesForAccountIsPerAccountNotPerNick()
    {
        var directory = new PlayerDirectory();
        directory.Remember("Влал", "203.0.113.9", Now, Device, AccountA);
        directory.Remember("Влал", "203.0.113.10", Now, accountId: AccountB);

        Assert.Single(directory.DevicesForAccount(AccountA));
        Assert.Empty(directory.DevicesForAccount(AccountB));
    }

    [Fact]
    public void LoadKeepsDevicesAndDropsJunk()
    {
        var directory = new PlayerDirectory();
        directory.Load(new[]
        {
            new SeenPlayer
            {
                Id = AccountA,
                Nick = "Ktulxy",
                Ips = new List<string> { "203.0.113.9" },
                Devices = new List<string> { Device.ToLowerInvariant(), "legacy-user-id" },
            },
        });

        var found = directory.Resolve(AccountA);
        Assert.NotNull(found);
        Assert.Single(found!.Devices);
        Assert.Contains(Device, found.Devices);
        Assert.Contains(Device, directory.DevicesForAccount(AccountA));
    }

    [Fact]
    public void RememberUpdatesNickForSameAccount()
    {
        var directory = new PlayerDirectory();
        var first = directory.Remember("Влал", "203.0.113.9", Now, accountId: AccountA);
        var second = directory.Remember("влал", "10.8.0.2", Now.AddMinutes(1), accountId: AccountA);

        // Это один аккаунт, который сменил написание: запись одна, ник — последний.
        Assert.Same(first, second);
        Assert.Equal(1, directory.Count);
        Assert.Equal(2, first!.Ips.Count);
        Assert.Equal("влал", first.Nick);
        Assert.Null(directory.FindByNick("Влал"));
        Assert.Equal(AccountA, directory.FindByNick("влал")!.Id);
    }
}
