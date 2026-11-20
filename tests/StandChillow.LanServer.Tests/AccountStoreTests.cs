using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Реестр аккаунтов: номера выдаются на входе по порядку, ник — подпись, устройство —
/// доказательство личности. Тёзка не должен получить чужой номер или чужой бан.
/// </summary>
public sealed class AccountStoreTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc);
    private const string Device = "LB143C7D41FAE30EB14F49B1678E3933C9";
    private const string OtherDevice = "LB4B7BA14444774A9D4FBBEC23F4519998";

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "standchillow-account-tests", Guid.NewGuid() + ".json");

    private static AccountStore NewStore() => new(TempPath());

    [Fact]
    public void FirstAccountGetsFirstIdAndNumbersGrowByOne()
    {
        var store = NewStore();

        var first = store.GetOrCreate("Ktulxy", Now);
        var second = store.GetOrCreate("Другой", Now);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(AccountStore.FirstId, first!.Id);
        Assert.Equal(AccountStore.FirstId + 1, second!.Id);
        Assert.Equal(AccountStore.FirstId + 2, store.NextId);
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void GetOrCreateReusesAccountByNickIgnoringCase()
    {
        var store = NewStore();
        var first = store.GetOrCreate("Влал", Now);
        var second = store.GetOrCreate("  влАЛ  ", Now.AddMinutes(1));

        Assert.Same(first, second);
        Assert.Equal(1, store.Count);
        Assert.Equal("влАЛ", first!.Nick);
    }

    [Fact]
    public void EnsureForJoinBindsDeviceToLobbyAccount()
    {
        var store = NewStore();
        var lobby = store.GetOrCreate("Ktulxy", Now);

        var bound = store.EnsureForJoin("Ktulxy", Device, Now.AddMinutes(1));

        Assert.Same(lobby, bound);
        Assert.True(bound!.HasDevice(Device));
        Assert.Equal(lobby!.Id, store.ProbeId(Device));
    }

    [Fact]
    public void NewDeviceUnderTakenNickGetsNewAccount()
    {
        var store = NewStore();
        var owner = store.EnsureForJoin("Влал", Device, Now);
        var namesake = store.EnsureForJoin("Влал", OtherDevice, Now.AddMinutes(1));

        Assert.NotNull(owner);
        Assert.NotNull(namesake);
        Assert.NotEqual(owner!.Id, namesake!.Id);
        Assert.Equal(2, store.Count);
        Assert.Equal(owner.Id, store.ProbeId(Device));
        Assert.Equal(namesake.Id, store.ProbeId(OtherDevice));
    }

    [Fact]
    public void LegacyJoinWithoutDeviceReusesNick()
    {
        var store = NewStore();
        var first = store.EnsureForJoin("Ktulxy", null, Now);
        var second = store.EnsureForJoin("Ktulxy", null, Now.AddMinutes(1));

        Assert.Same(first, second);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void SameDeviceUnderNewNickKeepsTheAccount()
    {
        var store = NewStore();
        var first = store.EnsureForJoin("A", Device, Now);
        var renamed = store.EnsureForJoin("B", Device, Now.AddMinutes(1));

        Assert.Same(first, renamed);
        Assert.Equal(1, store.Count);
        Assert.Equal("B", renamed!.Nick);
        Assert.Contains("A", renamed.Aliases);
        Assert.NotNull(store.FindByNick("A"));
    }

    [Fact]
    public void ProbeIdOnlyKnowsBoundDevices()
    {
        var store = NewStore();
        store.GetOrCreate("Ktulxy", Now);

        Assert.Equal(0, store.ProbeId(null));
        Assert.Equal(0, store.ProbeId(""));
        Assert.Equal(0, store.ProbeId(Device));
        Assert.Equal(0, store.ProbeId("not-a-device"));
    }

    [Fact]
    public void NewestAccountWithTheSameNickWinsFindByNick()
    {
        var store = NewStore();
        var older = store.EnsureForJoin("Влал", Device, Now);
        var newer = store.EnsureForJoin("Влал", OtherDevice, Now.AddMinutes(1));

        Assert.Equal(newer!.Id, store.FindByNick("влал")!.Id);
        Assert.NotEqual(older!.Id, newer.Id);
        Assert.Equal(2, store.FindAccountsByNick("Влал").Count);
    }

    [Fact]
    public void FindByIdRejectsValuesOutsideTheAccountRange()
    {
        var store = NewStore();
        store.GetOrCreate("Ktulxy", Now);

        Assert.Null(store.FindById(0));
        Assert.Null(store.FindById(1));
        Assert.Null(store.FindById(AccountStore.MaxId + 1));
        Assert.NotNull(store.FindById(AccountStore.FirstId));
    }

    [Fact]
    public void SeedFromPlayersOrdersByLastSeenAndBindsDevices()
    {
        var store = NewStore();
        var seeded = store.SeedFromPlayers(new[]
        {
            new SeenPlayer { Nick = "поздний", LastSeenUtcMs = 2000 },
            new SeenPlayer
            {
                Nick = "ранний",
                LastSeenUtcMs = 1000,
                Devices = new List<string> { Device },
            },
        });

        Assert.Equal(2, seeded);
        Assert.Equal(AccountStore.FirstId, store.FindByNick("ранний")!.Id);
        Assert.Equal(AccountStore.FirstId + 1, store.FindByNick("поздний")!.Id);
        Assert.Equal(AccountStore.FirstId, store.ProbeId(Device));
    }

    [Fact]
    public void SeedDoesNotDuplicateExistingNick()
    {
        var store = NewStore();
        var existing = store.GetOrCreate("Ktulxy", Now);

        var seeded = store.SeedFromPlayers(new[]
        {
            new SeenPlayer { Nick = "Ktulxy", LastSeenUtcMs = 1000 },
        });

        Assert.Equal(0, seeded);
        Assert.Equal(1, store.Count);
        Assert.Equal(existing!.Id, store.FindByNick("Ktulxy")!.Id);
    }

    [Fact]
    public void MapLegacyIdPrefersNickThenRealId()
    {
        var store = NewStore();
        var account = store.GetOrCreate("Ktulxy", Now);

        Assert.Equal(account!.Id, store.MapLegacyId(123, "ktulxy"));
        Assert.Equal(account.Id, store.MapLegacyId(account.Id, null));
        Assert.Equal(0, store.MapLegacyId(123, "неизвестный"));
    }

    [Fact]
    public void MapLegacyIdFindsAccountByLegacyHashScan()
    {
        var store = NewStore();
        var account = store.GetOrCreate("Ktulxy", Now);
        var legacy = LegacyNickHash.ForNick("Ktulxy");

        // Ника нет, настоящего номера тоже — остаётся скан старых хешей по никам.
        Assert.Equal(account!.Id, store.MapLegacyId(legacy, null));
    }

    [Fact]
    public void SaveAndLoadKeepsAccountsDevicesAndNextId()
    {
        var path = TempPath();
        var first = new AccountStore(path);
        var account = first.EnsureForJoin("Ktulxy", Device, Now);
        first.Save();

        var second = new AccountStore(path);

        Assert.Equal(1, second.Count);
        Assert.Equal(account!.Id, second.FindByNick("Ktulxy")!.Id);
        Assert.Equal(account.Id, second.ProbeId(Device));
        Assert.Equal(first.NextId, second.NextId);
    }

    [Fact]
    public void NextIdSurvivesReload()
    {
        var path = TempPath();
        var store = new AccountStore(path);
        store.GetOrCreate("A", Now);
        store.GetOrCreate("B", Now);
        store.Save();

        var reloaded = new AccountStore(path);
        var third = reloaded.GetOrCreate("C", Now);

        Assert.Equal(AccountStore.FirstId + 2, third!.Id);
    }

    [Fact]
    public void EmptyNickDoesNotCreateAnAccount()
    {
        var store = NewStore();

        Assert.Null(store.GetOrCreate("   ", Now));
        Assert.Null(store.EnsureForJoin("", Device, Now));
        Assert.Null(store.FindByNick(null));
        Assert.Equal(0, store.Count);
    }
}
