using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Реестр банов: связка ник ↔ IP ↔ токен, выученные алиасы, админ-исключение, срок.
/// </summary>
/// <remarks>
/// Никаких сокетов: реестр — чистая логика входа. Именно эти тесты держат обещания
/// «сменил ник, но IP тот же — тоже бан» и «админа не банят».
/// </remarks>
public sealed class BanRegistryTests
{
    private static readonly DateTime Now = new(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc);

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
    public void BanByNickAndIp_BlocksBothKeys()
    {
        var registry = NewRegistry();
        Ban(registry, "Ktulxy", "192.168.1.37");

        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now).Banned);
        Assert.True(registry.Inspect(new BanProbe("Ktulxy", "192.168.1.37"), Now).Banned);
        Assert.True(registry.Inspect(new BanProbe("Другой", "192.168.1.37"), Now).Banned);

        // Уже наученный ник («Другой» попал в запись по IP выше) банится и с нового адреса —
        // это и есть «тот же IP, сменил ник». А вот совсем новый ник с новым IP — свободен.
        Assert.True(registry.Inspect(new BanProbe("Другой", "192.168.1.99"), Now).Banned);
        // Адрес, который ни разу не встречался, свободен: «новый ник + новый IP» не банится.
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
    public void NewIpUnderSameNick_IsLearnedAndBlocked()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now);

        Assert.True(decision.Banned);
        Assert.Equal("ник", decision.MatchedBy);
        Assert.True(decision.LearnedAlias);
        Assert.Contains("10.8.0.2", record.Ips);
        Assert.True(registry.IsIpBanned("10.8.0.2", Now));
    }

    [Fact]
    public void NewNickOnBannedIp_IsLearnedAndBlocked()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(new BanProbe("Ktulxy2", "203.0.113.9"), Now);

        Assert.True(decision.Banned);
        Assert.Equal("IP", decision.MatchedBy);
        Assert.True(decision.LearnedAlias);
        Assert.Contains("Ktulxy2", record.Aliases);

        // И следующий заход уже под ником-алиасом с нового адреса тоже закрыт.
        Assert.True(registry.Inspect(new BanProbe("Ktulxy2", "198.51.100.7"), Now).Banned);
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
        Assert.DoesNotContain("влал", record.Aliases);
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
    public void LearnAliasesCanBeSwitchedOff()
    {
        var registry = new BanRegistry { LearnAliases = false };
        var record = Ban(registry, "Ktulxy", "203.0.113.9");

        var decision = registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now);

        Assert.True(decision.Banned);
        Assert.False(decision.LearnedAlias);
        Assert.DoesNotContain("10.8.0.2", record.Ips);
        Assert.False(registry.IsIpBanned("10.8.0.2", Now));
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
    public void ChangedFiresWhenTheSetMutates()
    {
        var registry = NewRegistry();
        var saves = 0;
        registry.Changed = () => saves++;

        Ban(registry, "Ktulxy", "203.0.113.9");
        Assert.True(saves > 0);

        var before = saves;
        registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now); // выучил новый IP
        Assert.True(saves > before);

        before = saves;
        registry.Inspect(new BanProbe("Ktulxy", "10.8.0.2"), Now); // уже знает
        Assert.Equal(before, saves);
    }

    private const string Device = "LB143C7D41FAE30EB14F49B1678E3933C9";
    private const string OtherDevice = "LB4B7BA14444774A9D4FBBEC23F4519998";

    [Fact]
    public void DeviceBanBlocksNewNickAndVpnIp()
    {
        var registry = NewRegistry();
        var record = registry.Add(
            new BanProbe("A", "203.0.113.9", Device),
            TimeSpan.FromMinutes(30),
            "софт",
            "влал",
            Now);

        // Новый ник + новый IP (VPN), но то же устройство.
        var decision = registry.Inspect(
            new BanProbe("B", "198.51.100.7", Device.ToLowerInvariant()), Now);

        Assert.True(decision.Banned);
        Assert.Equal("устройство", decision.MatchedBy);
        Assert.True(decision.LearnedAlias);
        Assert.True(record.HasNick("B"));
        Assert.True(record.HasIp("198.51.100.7"));
    }

    [Fact]
    public void DeviceBanDoesNotCatchAnotherPhoneOnTheSameIp()
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
    public void IpOnlyMatchDoesNotLearnDevice()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "A", "203.0.113.9");

        // Совпало только по IP: новый ник выучивается (как раньше), устройство — нет.
        var byIp = registry.Inspect(new BanProbe("B", "203.0.113.9", Device), Now);
        Assert.True(byIp.Banned);
        Assert.Equal("IP", byIp.MatchedBy);
        Assert.False(record.HasDevice(Device));

        // Другой ник с того же устройства и с нового IP не банится: устройство не выучено.
        var later = registry.Inspect(new BanProbe("C", "198.51.100.7", Device), Now);
        Assert.False(later.Banned);
    }

    [Fact]
    public void NickMatchMakesAnOldIpBanDeviceAware()
    {
        var registry = NewRegistry();
        var record = Ban(registry, "A", "203.0.113.9");

        // Старый v1-бан: тот же ник с нового IP — совпало по нику, устройство записалось.
        var byNick = registry.Inspect(new BanProbe("A", "198.51.100.7", Device), Now);
        Assert.True(byNick.Banned);
        Assert.Equal("ник", byNick.MatchedBy);
        Assert.True(byNick.LearnedAlias);
        Assert.True(record.HasDevice(Device));

        // Теперь смена ника и IP уже не помогает.
        var byDevice = registry.Inspect(new BanProbe("B", "192.0.2.55", Device), Now);
        Assert.True(byDevice.Banned);
        Assert.Equal("устройство", byDevice.MatchedBy);
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
        Assert.Equal("устройство", decision.MatchedBy);
    }
}
