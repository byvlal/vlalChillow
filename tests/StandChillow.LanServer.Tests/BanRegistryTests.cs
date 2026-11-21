using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Реестр банов v3: userId, IP и номер аккаунта — жёсткие ключи с обучением, ник —
/// мягкий ключ, который блокирует только эту попытку и ничего не учит.
/// </summary>
/// <remarks>
/// Никаких сокетов: реестр — чистая логика входа. Именно эти тесты держат обещания
/// «сменил ник, но userId/IP тот же — тоже бан», «чужой ник не наследует бан»
/// и «админа не банят».
/// </remarks>
public sealed class BanRegistryTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

    private const string Device = "LB143C7D41FAE30EB14F49B1678E3933C9";
    private const string OtherDevice = "LB4B7BA14444774A9D4FBBEC23F4519998";

    private static BanRegistry NewRegistry() => new() { LearnAliases = true };

    private static BanRecord Ban(
        BanRegistry registry,
        string nick,
        string? ip,
        DateTime? now = null,
        int minutes = 30)
    {
        return registry.Add(
            new BanProbe(nick, ip),
            TimeSpan.FromMinutes(minutes),
            "софт",
            "влал",
            now ?? Now);
    }

    [Fact]
    public void NickAndIpBan_BlocksBothKeys()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "192.168.1.37");

        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now).Banned);
        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "192.168.1.37"), Now).Banned);
        Assert.True(registry.Inspect(new BanProbe("Другой", "192.168.1.37"), Now).Banned);

        // Уже наученный ник («Другой» попал в запись по IP выше) банится и с нового адреса —
        // это и есть «тот же IP, сменил ник». А вот совсем новый ник с новым IP — свободен.
        Assert.True(registry.Inspect(new BanProbe("Другой", "192.168.1.99"), Now).Banned);
        Assert.False(registry.Inspect(new BanProbe("Третий", "172.20.5.5"), Now).Banned);
    }

    [Fact]
    public void AnyAddressKindIsBanned_LanHamachiVpnPublic()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "192.168.1.37");

        // Проверок «только локальный» нет: запись по адресу работает для любого.
        Assert.True(registry.IsIpBanned("192.168.1.37", Now));
        Assert.False(registry.IsIpBanned("203.0.113.9", Now));

        Ban(registry, "HamachiGuy", "25.14.3.9");
        Ban(registry, "VpnGuy", "10.8.0.2");
        Ban(registry, "PublicGuy", "203.0.113.9");

        Assert.True(registry.IsIpBanned("25.14.3.9", Now));
        Assert.True(registry.IsIpBanned("10.8.0.2", Now));
        Assert.True(registry.IsIpBanned("203.0.113.9", Now));
    }

    [Fact]
    public void NickOnlyMatchBlocksTheAttemptAndLearnsNothing()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        // Тот же ник с нового адреса и нового userId: попытку блокируем, но новые ключи
        // не записываем — иначе невинный тёзка навсегда унаследует чужой бан.
        var decision = registry.Inspect(
            new BanProbe("Ktulxy", "10.8.0.2", Device), Now);

        Assert.True(decision.Banned);
        Assert.Equal("ник", decision.MatchedBy);
        Assert.Equal(BanMatchKind.Nick, decision.MatchKind);
        Assert.False(decision.LearnedAlias);
        Assert.False(record.HasIp("10.8.0.2"));
        Assert.False(record.HasDevice(Device));
        Assert.False(registry.IsIpBanned("10.8.0.2", Now));

        // Главное следствие: новый человек с новым ником, адресом и userId свободен.
        Assert.False(registry.Inspect(
            new BanProbe("Новый", "10.8.0.2", Device), Now).Banned);
    }

    [Fact]
    public void NewNickOnBannedIp_LearnsNickAndUserId()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(
            new BanProbe("Ktulxy2", "203.0.113.9", Device), Now);

        Assert.True(decision.Banned);
        Assert.Equal("IP", decision.MatchedBy);
        Assert.Equal(BanMatchKind.Ip, decision.MatchKind);
        Assert.True(decision.LearnedAlias);
        Assert.True(record.HasNick("Ktulxy2"));
        Assert.True(record.HasDevice(Device));

        // Выученный userId сам становится жёстким ключом: ник и IP можно менять.
        var byId = registry.Inspect(new BanProbe("Ktulxy3", "198.51.100.7", Device), Now);
        Assert.True(byId.Banned);
        Assert.Equal("userId", byId.MatchedBy);
        Assert.True(record.HasNick("Ktulxy3"));
        Assert.True(record.HasIp("198.51.100.7"));
    }

    [Fact]
    public void UserIdMatchLearnsNewNickAndIp()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("A", "203.0.113.9", Device),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now);

        // Новый ник + VPN-адрес, но та же установка.
        var decision = registry.Inspect(
            new BanProbe("B", "198.51.100.7", Device.ToLowerInvariant()), Now);

        Assert.True(decision.Banned);
        Assert.Equal("userId", decision.MatchedBy);
        Assert.Equal(BanMatchKind.UserId, decision.MatchKind);
        Assert.True(decision.LearnedAlias);
        Assert.True(record.HasNick("B"));
        Assert.True(record.HasIp("198.51.100.7"));
    }

    [Fact]
    public void UserIdIsCheckedBeforeIpAndNick()
    {
        var registry = NewRegistry();
        Ban(registry, "A", "203.0.113.9");
        var byId = registry.Add(
            new BanProbe("B", "198.51.100.7", Device),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now);

        // Совпали и адрес первой записи, и userId второй: отвечает userId.
        var decision = registry.Inspect(
            new BanProbe("C", "203.0.113.9", Device), Now);

        Assert.True(decision.Banned);
        Assert.Equal("userId", decision.MatchedBy);
        Assert.Same(byId, decision.Record);
    }

    [Fact]
    public void IpIsCheckedBeforeNickSoNewNickIsLearned()
    {
        var registry = NewRegistry();
        var expires = BanRecord.ToUnixMs(DateTime.UtcNow.AddHours(1));
        registry.Load(new[]
        {
            new BanRecord { Nick = "A", ExpiresUtcMs = expires },
            new BanRecord
            {
                Nick = "B",
                Ips = new List<string> { "203.0.113.9" },
                ExpiresUtcMs = expires,
            },
        });

        // Ник «A» забанен отдельной записью, но IP-совпадение сильнее: новый ник
        // должен выучиться в запись адреса, а не пройти как мягкое совпадение.
        var decision = registry.Inspect(new BanProbe("A", "203.0.113.9"), Now);

        Assert.True(decision.Banned);
        Assert.Equal("IP", decision.MatchedBy);
        Assert.Equal("B", decision.Record!.Nick);
        Assert.True(decision.Record.HasNick("A"));
    }

    [Fact]
    public void UserIdBanDoesNotCatchAnotherPhoneOnTheSameIp()
    {
        var registry = NewRegistry();
        registry.Add(
            new BanProbe("A", null, Device),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now);

        // У записи нет IP; второй телефон с тем же адресом и другим устройством свободен.
        var decision = registry.Inspect(new BanProbe("B", "203.0.113.9", OtherDevice), Now);

        Assert.False(decision.Banned);
    }

    [Fact]
    public void LearnAliasesOff_BlocksButDoesNotGrowTheRecord()
    {
        var registry = new BanRegistry { LearnAliases = false };
        var record = Ban(registry, "A", "203.0.113.9");

        var decision = registry.Inspect(
            new BanProbe("B", "203.0.113.9", Device), Now);

        Assert.True(decision.Banned);
        Assert.False(decision.LearnedAlias);
        Assert.False(record.HasNick("B"));
        Assert.False(record.HasDevice(Device));
        Assert.False(record.HasPublicId(LegacyNickHash.ForNick("B")));
    }

    [Fact]
    public void ReadOnlyInspectDoesNotGrowTheRecord()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "A", "203.0.113.9");

        // Так смотрит сайт: найти бан можно, приклеить к нему свои ключи — нельзя.
        var decision = registry.Inspect(
            new BanProbe("B", "203.0.113.9", Device), Now, learn: false);

        Assert.True(decision.Banned);
        Assert.False(decision.LearnedAlias);
        Assert.False(record.HasNick("B"));
        Assert.False(record.HasDevice(Device));
    }

    [Fact]
    public void AdminIsNeverBannedAndNoAliasIsLearned()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(
            new BanProbe("влал", "203.0.113.9"), Now, nick => nick == "влал");

        Assert.False(decision.Banned);
        Assert.True(decision.AdminExempt);
        Assert.False(record.HasNick("влал"));
    }

    [Fact]
    public void ExpiredBanStopsBlockingAndIsPruned()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "203.0.113.9", minutes: 30);

        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), Now).Banned);

        var later = Now.AddMinutes(31);
        Assert.False(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), later).Banned);
        Assert.Equal(0, registry.Count);
    }

    [Fact]
    public void RepeatedBanExtendsTheSameRecordInsteadOfDuplicating()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "203.0.113.9", minutes: 30);
        var record = Ban(registry, "Ktulxy", "203.0.113.9", minutes: 180);

        Assert.Equal(1, registry.Count);
        Assert.Single(registry.Export());
        Assert.Equal(TimeSpan.FromMinutes(180), record.Remaining(Now));
    }

    [Fact]
    public void PublicIdIsAHardKeyForTheAccount()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("A", null, null, 1000001),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now,
            matchId: 1000001);

        Assert.True(record.HasPublicId(1000001));

        // Номер аккаунта выдаёт сервер, клиент его не подделывает: совпадение номера —
        // это тот же человек, даже с новым ником, адресом и устройством.
        Assert.True(registry.Inspect(
            new BanProbe("B", "198.51.100.7", null, 1000001), Now).Banned);
    }

    [Fact]
    public void HardMatchTagsTheRecordWithThePublicId()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "A", "203.0.113.9");

        var decision = registry.Inspect(
            new BanProbe("B", "203.0.113.9", null, 424242), Now);

        Assert.True(decision.LearnedAlias);
        Assert.True(record.HasPublicId(424242));
    }

    [Fact]
    public void MatchIdTagAllowsUnbanById()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("Ktulxy", "203.0.113.9"),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now,
            matchId: 671488);

        Assert.True(record.HasMatchId(671488));

        var removed = registry.RemoveByMatchId(671488);

        Assert.Single(removed);
        Assert.False(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), Now).Banned);
    }

    [Fact]
    public void KnownIpsFromDirectoryAreBannedWithThePerson()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("Ktulxy", null),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now,
            matchId: 1,
            knownIps: new[] { "203.0.113.9", "10.8.0.2" });

        Assert.Equal(2, record.Ips.Count);
        Assert.True(registry.IsIpBanned("203.0.113.9", Now));
        Assert.True(registry.IsIpBanned("10.8.0.2", Now));
    }

    [Fact]
    public void KnownDevicesFromDirectoryAreBannedWithThePerson()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("A", null),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now,
            matchId: 1,
            knownDevices: new[] { Device, OtherDevice });

        Assert.Equal(2, record.Devices.Count);
        Assert.True(registry.Inspect(new BanProbe("B", null, OtherDevice), Now).Banned);
    }

    [Fact]
    public void TimeAndTimezoneAreStoredTimezoneProof()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        // Срок считается от unix ms, а не от локальной строки: смена пояса не влияет.
        Assert.Equal(BanRecord.ToUnixMs(Now.AddMinutes(30)), record.ExpiresUtcMs);
        Assert.Equal(TimeSpan.FromMinutes(30), record.Remaining(Now));
        Assert.False(string.IsNullOrWhiteSpace(record.CreatedLocalIso));
    }

    [Fact]
    public void RemoveByNickAndByIp()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "203.0.113.9");

        Assert.True(registry.Remove("ktulxy", out var byNick));
        Assert.Equal("Ktulxy", byNick!.DisplayNick);
        Assert.False(registry.Inspect(new BanProbe("Ktulxy", "203.0.113.9"), Now).Banned);

        Ban(registry, "Ktulxy", "203.0.113.9");
        Assert.True(registry.Remove("203.0.113.9", out var byIp));
        Assert.NotNull(byIp);
        Assert.False(registry.IsIpBanned("203.0.113.9", Now));
    }

    [Fact]
    public void RemoveByDeviceKey()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("A", null, Device),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now);

        Assert.True(registry.Remove(Device, out var removed));
        Assert.Same(record, removed);
        Assert.False(registry.Inspect(new BanProbe("B", null, Device), Now).Banned);
    }

    [Fact]
    public void DeviceCheckCanBeSwitchedOff()
    {
        var registry = new BanRegistry { LearnAliases = true, ByDevice = false };
        var record = registry.Add(
            new BanProbe("A", null, Device),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now);

        Assert.Empty(record.Devices);
        Assert.False(registry.Inspect(new BanProbe("B", null, Device), Now).Banned);
        Assert.False(registry.Inspect(new BanProbe("B", null, Device), Now, learn: false).Banned);
    }

    [Fact]
    public void ActiveListShowsNearestExpiryFirstAndSkipsExpired()
    {
        var registry = NewRegistry();
        Ban(registry, "Долгий", "203.0.113.1", minutes: 600);
        Ban(registry, "Короткий", "203.0.113.2", minutes: 30);

        var active = registry.Active(Now);

        Assert.Equal(2, active.Count);
        Assert.Equal("Короткий", active[0].DisplayNick);
        Assert.Equal("Долгий", active[1].DisplayNick);

        Assert.Single(registry.Active(Now.AddMinutes(60)));
    }

    [Fact]
    public void ChangedFiresOnlyWhenAHardKeyActuallyLearns()
    {
        var registry = NewRegistry();
        var saves = 0;
        registry.Changed = () => saves++;

        Ban(registry, "Ktulxy", "203.0.113.9");
        Assert.True(saves > 0);

        // Совпадение по нику ничего не пишет — диск не трогаем.
        var before = saves;
        registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now);
        Assert.Equal(before, saves);

        // Совпадение по IP учит новый ник — одна запись на диске.
        registry.Inspect(new BanProbe("Новый", "203.0.113.9"), Now);
        Assert.True(saves > before);

        // Повторный вход уже ничего нового не учит.
        before = saves;
        registry.Inspect(new BanProbe("Новый", "203.0.113.9"), Now);
        Assert.Equal(before, saves);
    }

    [Fact]
    public void MigratedRecordWithoutAccountIdBansByIpAndDeviceOnly()
    {
        var now = DateTime.UtcNow;
        var expires = BanRecord.ToUnixMs(now.AddHours(1));
        var registry = NewRegistry();

        // Так выглядит запись после бановой миграции v4: номер аккаунта снят,
        // IP, устройство и ник остались.
        registry.Load(new[]
        {
            new BanRecord
            {
                Nick = "Ktulxy",
                Ips = new List<string> { "203.0.113.9" },
                Devices = new List<string> { Device },
                MatchIds = new List<int>(),
                ExpiresUtcMs = expires,
            },
        });

        // Тот же старый номер достался другому человеку — его не баним.
        Assert.False(registry.Inspect(
            new BanProbe("Невинный", "198.51.100.7", OtherDevice, 1000005), now).Banned);

        // А читер по-прежнему ловится по устройству и по адресу.
        Assert.True(registry.Inspect(
            new BanProbe("НовыйНик", null, Device), now).Banned);
        Assert.True(registry.Inspect(
            new BanProbe("ЕщёНик", "203.0.113.9", OtherDevice), now).Banned);
    }

    [Fact]
    public void LoadIndexesDevicesFromTheFile()
    {
        var now = DateTime.UtcNow;
        var registry = NewRegistry();
        registry.Load(new[]
        {
            new BanRecord
            {
                Nick = "A",
                Devices = new List<string> { Device },
                ExpiresUtcMs = BanRecord.ToUnixMs(now.AddHours(1)),
            },
        });

        var decision = registry.Inspect(new BanProbe("B", null, Device), now);

        Assert.True(decision.Banned);
        Assert.Equal("userId", decision.MatchedBy);
    }
}
