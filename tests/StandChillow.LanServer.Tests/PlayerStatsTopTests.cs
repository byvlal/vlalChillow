using StandChillow.LanServer.Net;
using StandChillow.LanServer.Stats;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Таблица лидеров #/top: что попадает в зачёт и в каком порядке.
/// </summary>
/// <remarks>
/// Проверяем ровно контракт страницы: пустые записи не занимают места, ничьи не
/// прыгают между запросами, а K/D считается тем же правилом, что и в профиле.
/// </remarks>
public sealed class PlayerStatsTopTests
{
    private static PlayerStatsStore NewStore() =>
        new(Path.Combine(Path.GetTempPath(), "vlal-top-" + Guid.NewGuid().ToString("N") + ".json"));

    [Fact]
    public void TopSortsByKillsAndSkipsRowsWithoutAnyPlay()
    {
        var store = NewStore();
        store.NoteKill(1, "alice");
        store.NoteKill(1, "alice");
        store.NoteKill(1, "alice");
        store.NoteKill(2, "bob");
        // Смерть без киллов и без матчей — это не игрок рейтинга, а след в лобби.
        store.NoteDeath(3, "carol");

        var rows = store.Top(LeaderboardSort.Kills, 10);

        Assert.Equal(new[] { "alice", "bob" }, rows.Select(r => r.Nick).ToArray());
        Assert.Equal(3, rows[0].Kills);
    }

    [Fact]
    public void TopByKdCountsKillsWithoutDeathsAsKills()
    {
        var store = NewStore();
        for (var i = 0; i < 5; i++)
            store.NoteKill(1, "alice");
        store.NoteDeath(1, "alice");           // 5 / 1 = 5.00
        for (var i = 0; i < 3; i++)
            store.NoteKill(2, "bob");          // смертей нет — считаем по киллам, 3.00
        store.NoteKill(3, "carol");            // 1.00

        var rows = store.Top(LeaderboardSort.Kd, 10);

        Assert.Equal(new[] { "alice", "bob", "carol" }, rows.Select(r => r.Nick).ToArray());
        Assert.Equal(5.0, PlayerStatsStore.Kd(rows[0]));
        Assert.Equal(3.0, PlayerStatsStore.Kd(rows[1]));
    }

    [Fact]
    public void TopRespectsLimit()
    {
        var store = NewStore();
        for (var id = 1; id <= 5; id++)
            for (var i = 0; i < id; i++)
                store.NoteKill(id, "p" + id);

        var rows = store.Top(LeaderboardSort.Kills, 3);

        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "p5", "p4", "p3" }, rows.Select(r => r.Nick).ToArray());
        Assert.Equal(5, store.Top(LeaderboardSort.Kills, 1000).Count);
    }

    [Fact]
    public void TopBreaksTiesByKillsThenNick()
    {
        var store = NewStore();
        store.NoteKill(1, "bob");
        store.NoteKill(1, "bob");
        store.NoteKill(2, "alice");
        store.NoteKill(2, "alice");
        store.NoteKill(3, "zoe");
        store.NoteKill(3, "zoe");
        store.NoteKill(3, "zoe");

        var rows = store.Top(LeaderboardSort.Kills, 10);

        // Сначала больше киллов, потом равные — по нику: порядок не должен зависеть
        // от порядка в словаре.
        Assert.Equal(new[] { "zoe", "alice", "bob" }, rows.Select(r => r.Nick).ToArray());
    }

    [Fact]
    public void TopCountsSecondsAndMatchesFromSamples()
    {
        var store = NewStore();
        var players = new List<(int Id, string Nick)> { (1, "alice"), (2, "bob") };
        var playing = new[] { new StatsRoomSample("room-1", Playing: true, Ended: false, Players: players) };

        store.SampleRooms(playing, 60);
        store.SampleRooms(playing, 60);

        var bySeconds = store.Top(LeaderboardSort.Seconds, 10);
        Assert.Equal(2, bySeconds.Count);
        Assert.Equal(120, bySeconds[0].Seconds);
        Assert.Equal(1, bySeconds[0].Matches);   // один забег, сколько бы раз ни семплировали

        var byMatches = store.Top(LeaderboardSort.Matches, 10);
        Assert.Equal(new[] { "alice", "bob" }, byMatches.Select(r => r.Nick).ToArray());
    }

    [Fact]
    public void TopReturnsEmptyListWhenNobodyPlayed()
    {
        var store = NewStore();
        Assert.Empty(store.Top(LeaderboardSort.Kills, 10));
    }
}
