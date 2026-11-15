using StandChillow.LanServer.Bans;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Бан по серверному ID игрока (#51): сайт знает про человека только ID, а справочник
/// знает по нему ник и все адреса. Сокетов нет — проверяется только решение.
/// </summary>
public sealed class PlayerBanLookupTests
{
    private static readonly DateTime Now = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    private static (PlayerBanLookup Lookup, PlayerDirectory Directory, BanRegistry Bans, int Id) NewWorld(
        string nick = "влал",
        string ip = "192.0.2.10",
        Func<string?, bool>? isExempt = null)
    {
        var directory = new PlayerDirectory();
        var bans = new BanRegistry();
        var seen = directory.Remember(nick, ip, Now);
        Assert.NotNull(seen);

        return (new PlayerBanLookup(directory, bans, isExempt), directory, bans, seen!.Id);
    }

    private static void Ban(BanRegistry bans, string? nick, string? ip) =>
        bans.Add(new BanProbe(nick, ip), TimeSpan.FromHours(1), "тест", "админ", Now);

    [Fact]
    public void UnknownIdIsNotBanned()
    {
        var (lookup, _, _, _) = NewWorld();

        // Реестр молчит про человека — молчание баном не считается.
        Assert.False(lookup.IsBanned(4242, Now));
    }

    [Fact]
    public void NonPositiveIdIsNotBanned()
    {
        var (lookup, _, _, _) = NewWorld();
        Assert.False(lookup.IsBanned(0, Now));
        Assert.False(lookup.IsBanned(-1, Now));
    }

    [Fact]
    public void BanByNickIsVisibleById()
    {
        var (lookup, _, bans, id) = NewWorld();
        Ban(bans, "влал", null);

        Assert.True(lookup.IsBanned(id, Now));
    }

    [Fact]
    public void BanByKnownIpIsVisibleById()
    {
        var (lookup, _, bans, id) = NewWorld();

        // Бан по адресу, с которого человек заходил: ник он мог сменить, а ID вместе
        // с ним — поэтому смотрят и по адресам.
        Ban(bans, null, "192.0.2.10");

        Assert.True(lookup.IsBanned(id, Now));
    }

    [Fact]
    public void ExpiredBanLetsPlayerThrough()
    {
        var (lookup, _, bans, id) = NewWorld();
        Ban(bans, "влал", null);

        Assert.True(lookup.IsBanned(id, Now));
        Assert.False(lookup.IsBanned(id, Now.AddHours(2)));
    }

    [Fact]
    public void AdminIsExemptEvenWhenBanned()
    {
        var (lookup, _, bans, id) = NewWorld(isExempt: nick => nick == "влал");
        Ban(bans, "влал", null);

        Assert.False(lookup.IsBanned(id, Now));
    }

    [Fact]
    public void OtherPlayersBanDoesNotCatchThisOne()
    {
        var (lookup, _, bans, id) = NewWorld();
        var other = new PlayerDirectory();
        other.Remember("другой", "192.0.2.77", Now);
        Ban(bans, "другой", "192.0.2.77");

        Assert.False(lookup.IsBanned(id, Now));
    }
}
