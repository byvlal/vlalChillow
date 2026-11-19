using StandChillow.LanServer.Stats;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// История матчей: сшивание участников и хранилище на диске.
/// </summary>
/// <remarks>
/// Проверяем ровно те места, где ошибка портит итоги: игрок, вышедший за секунду до
/// конца, обязан остаться в таблице; зритель не должен затирать боевую команду;
/// смерти считаются по всем актёрам игрока; хранилище не растёт бесконечно и
/// переживает рестарт.
/// </remarks>
public sealed class MatchHistoryTests
{
    private static MatchHistoryPlayer P(
        int id, string nick, string team, byte actor, int kills = 0, int deaths = 0)
        => new()
        {
            Id = id,
            Nick = nick,
            Team = team,
            ActorNr = actor,
            Kills = kills,
            Deaths = deaths,
        };

    [Fact]
    public void Build_KeepsPlayerWhoLeftBeforeTheEnd()
    {
        // Предпоследний раунд: 1 и 2 играли. Последний: 2 уже вышел, зашёл 3.
        var round1 = new Dictionary<byte, MatchHistoryPlayer>
        {
            [2] = P(1, "byvlal", "CT", 2, kills: 10),
            [3] = P(2, "chillow", "T", 3, kills: 8),
        };
        var round2 = new Dictionary<byte, MatchHistoryPlayer>
        {
            [2] = P(1, "byvlal", "CT", 2, kills: 14),
            [4] = P(3, "shadow", "T", 4, kills: 12),
        };
        var final = new Dictionary<byte, MatchHistoryPlayer>
        {
            [2] = P(1, "byvlal", "CT", 2, kills: 16),
            [4] = P(3, "shadow", "T", 4, kills: 13),
        };
        var deaths = new Dictionary<byte, int> { [2] = 1, [3] = 5, [4] = 2 };

        var players = MatchHistoryMerge.Build(
            new[] { round1, round2 }, final, deaths);

        Assert.Equal(3, players.Count);
        var leaver = Assert.Single(players, p => p.Nick == "chillow");
        Assert.Equal("T", leaver.Team);
        Assert.True(leaver.Left);
        Assert.Equal(5, leaver.Deaths);

        var stayed = Assert.Single(players, p => p.Nick == "byvlal");
        Assert.False(stayed.Left);
        Assert.Equal(16, stayed.Kills);
        Assert.Equal(1, stayed.Deaths);
    }

    [Fact]
    public void Build_SpectatorAtEndDoesNotOverwriteBattleTeam()
    {
        var round = new Dictionary<byte, MatchHistoryPlayer>
        {
            [2] = P(1, "byvlal", "CT", 2, kills: 7),
        };
        var final = new Dictionary<byte, MatchHistoryPlayer>
        {
            // Игрок после смерти переключился в наблюдатели — это не уход.
            [2] = P(1, "byvlal", "SPEC", 2, kills: 7),
            // Чистый зритель, который не играл ни раунда.
            [9] = P(9, "spectator", "SPEC", 9),
        };

        var players = MatchHistoryMerge.Build(
            new[] { round }, final, new Dictionary<byte, int>());

        var only = Assert.Single(players);
        Assert.Equal("byvlal", only.Nick);
        Assert.Equal("CT", only.Team);
        Assert.False(only.Left);
    }

    [Fact]
    public void Build_SumsDeathsAcrossActorChange()
    {
        // Игрок переподключился — актёр сменился, смерти должны сложиться.
        var round1 = new Dictionary<byte, MatchHistoryPlayer>
        {
            [2] = P(1, "byvlal", "CT", 2),
        };
        var round2 = new Dictionary<byte, MatchHistoryPlayer>
        {
            [5] = P(1, "byvlal", "CT", 5),
        };
        var final = new Dictionary<byte, MatchHistoryPlayer>
        {
            [5] = P(1, "byvlal", "CT", 5),
        };
        var deaths = new Dictionary<byte, int> { [2] = 3, [5] = 4 };

        var players = MatchHistoryMerge.Build(
            new[] { round1, round2 }, final, deaths);

        var only = Assert.Single(players);
        Assert.Equal(7, only.Deaths);
        Assert.False(only.Left);
    }

    [Fact]
    public void Store_AssignsIdsAndKeepsOnlyLatest()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vlal-history-{Guid.NewGuid():N}.json");
        try
        {
            var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
            var store = new MatchHistoryStore(path, () => now);

            for (var i = 0; i < MatchHistoryStore.MaxMatches + 5; i++)
            {
                now = now.AddSeconds(1);
                store.Add(new MatchHistoryRecord
                {
                    ModeId = "Ranked2v2",
                    Map = "de_dust2",
                    Winner = "CT",
                    Players = { P(1, "byvlal", "CT", 2) },
                });
            }

            Assert.Equal(MatchHistoryStore.MaxMatches, store.Count);
            Assert.NotNull(store.Get(MatchHistoryStore.MaxMatches + 5));
            Assert.Null(store.Get(5));
            Assert.NotNull(store.Get(6));
        }
        finally
        {
            TryDelete(path);
        }
    }

    [Fact]
    public void Store_ReloadsSavedMatch()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vlal-history-{Guid.NewGuid():N}.json");
        try
        {
            var now = new DateTime(2026, 11, 19, 12, 0, 0, DateTimeKind.Utc);
            var store = new MatchHistoryStore(path, () => now);
            var added = store.Add(new MatchHistoryRecord
            {
                StartedUtc = now.AddMinutes(-20),
                EndedUtc = now,
                ModeId = "Duel",
                Map = "de_mirage",
                ScoreCt = 9,
                ScoreTr = 6,
                Winner = "CT",
                Rounds = 15,
                Players =
                {
                    P(1, "byvlal", "CT", 2, kills: 18),
                    P(2, "chillow", "T", 3, kills: 20),
                },
            });
            store.Flush();

            var reopened = new MatchHistoryStore(path, () => now);
            var match = Assert.Single(reopened.Latest(10));

            Assert.Equal(added.Id, match.Id);
            Assert.Equal("Duel", match.ModeId);
            Assert.Equal(9, match.ScoreCt);
            Assert.Equal(2, match.Players.Count);
            Assert.Equal(18, match.Player(1)?.Kills);
            Assert.Equal(20, match.Player(2)?.Kills);
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* тестовый мусор не должен ронять прогон */ }
        try { File.Delete(path + ".tmp"); } catch { /* см. выше */ }
    }
}
