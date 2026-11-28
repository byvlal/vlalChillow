using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// A disconnect deletes the actor together with its props. A reconnect inside the same match
/// must carry the scoreboard counters over instead of starting from zero (влал: «перезашёл —
/// стата снова по нулям»). The team keeps its old fallback role: it is restored only when the
/// client does not pick a team itself.
/// </summary>
public sealed class MatchReconnectStatsTests
{
    [Fact]
    public void CaptureReadsTheCountersBeforeTheActorIsDeleted()
    {
        var room = NewRoom();
        room.ActorProps[(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        room.ActorProps[(2, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(4);
        room.ActorProps[(2, MatchRoomPropKeys.FairKills)] = LobbyVariant.FromInt(4);
        room.ActorProps[(2, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(2);
        room.ActorProps[(2, MatchRoomPropKeys.Score2)] = LobbyVariant.FromInt(14);
        room.ActorProps[(2, MatchRoomPropKeys.Assists)] = LobbyVariant.FromInt(1);
        room.ActorProps[(2, MatchRoomPropKeys.RoundKills)] = LobbyVariant.FromInt(1);
        room.ActorProps[(2, MatchRoomPropKeys.Money)] = LobbyVariant.FromInt(3400);

        var memory = MatchReconnectMemory.Capture(room, 2, MatchTeam.Tr);

        Assert.Same(room, memory.Room);
        Assert.Equal(MatchTeam.Tr, memory.Team);
        Assert.True(memory.HasFightingTeam);
        Assert.Equal(4, memory.Kills);
        Assert.Equal(4, memory.FairKills);
        Assert.Equal(2, memory.Death);
        Assert.Equal(14, memory.Score2);
        Assert.Equal(1, memory.Assists);
        Assert.Equal(1, memory.RoundKills);
        Assert.Equal(3400, memory.Money);
    }

    [Fact]
    public void RestoreWritesTheCarriedCountersForTheNewActor()
    {
        var room = NewRoom();
        var memory = Memory(room, kills: 4, death: 2, score: 14, money: 3400);

        var props = memory.RestoreInto(room, actorNr: 7);

        Assert.Equal(4, Int(room, 7, MatchRoomPropKeys.Kills));
        Assert.Equal(2, Int(room, 7, MatchRoomPropKeys.Death));
        Assert.Equal(14, Int(room, 7, MatchRoomPropKeys.Score2));
        Assert.Equal(3400, Int(room, 7, MatchRoomPropKeys.Money));
        Assert.Contains(props, p => p.Key == MatchRoomPropKeys.Kills);
        Assert.Contains(props, p => p.Key == MatchRoomPropKeys.Money);
    }

    [Fact]
    public void MissingPropsAreNotInventedAsZero()
    {
        var room = NewRoom();
        room.ActorProps[(2, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(3);
        room.ActorProps[(2, MatchRoomPropKeys.Money)] = LobbyVariant.FromInt(0);

        var memory = MatchReconnectMemory.Capture(room, 2, MatchTeam.Ct);
        memory.RestoreInto(room, actorNr: 5);

        Assert.Equal(3, Int(room, 5, MatchRoomPropKeys.Kills));
        Assert.Equal(0, Int(room, 5, MatchRoomPropKeys.Money));
        // Never seen in the room — still absent, not a fabricated 0.
        Assert.False(room.ActorProps.ContainsKey((5, MatchRoomPropKeys.Death)));
        Assert.False(room.ActorProps.ContainsKey((5, MatchRoomPropKeys.Score2)));
        Assert.False(room.ActorProps.ContainsKey((5, MatchRoomPropKeys.Assists)));
        Assert.False(room.ActorProps.ContainsKey((5, MatchRoomPropKeys.RoundKills)));
        Assert.False(room.ActorProps.ContainsKey((5, MatchRoomPropKeys.FairKills)));
    }

    /// <summary>
    /// The fresh client initialises its counters at zero and may author that zero right after
    /// the reconnect. Once the counters were carried over, such a decrease is stale and must
    /// not wipe the restored scoreboard.
    /// </summary>
    [Theory]
    [InlineData(MatchRoomPropKeys.Kills, 4, 0)]
    [InlineData(MatchRoomPropKeys.FairKills, 4, 0)]
    [InlineData(MatchRoomPropKeys.Death, 2, 0)]
    [InlineData(MatchRoomPropKeys.Score2, 14, 0)]
    [InlineData(MatchRoomPropKeys.Assists, 1, 0)]
    [InlineData(MatchRoomPropKeys.RoundKills, 1, 0)]
    [InlineData(MatchRoomPropKeys.Kills, 4, 3)]
    public void CarriedCombatCounterRejectsAStaleDecrease(string key, int previous, int incoming)
    {
        Assert.True(MatchReconnectMemory.RejectsStaleCounter(
            statsCarried: true,
            senderActorNr: 2,
            propActorNr: 2,
            key,
            hadPrevious: true,
            previous,
            incoming));
    }

    [Theory]
    [InlineData(MatchRoomPropKeys.Kills, 0, 1)]   // normal kill: increase is accepted
    [InlineData(MatchRoomPropKeys.Kills, 4, 4)]   // same value: harmless echo
    [InlineData(MatchRoomPropKeys.Money, 3400, 100)] // buy/spend is client-owned
    [InlineData(MatchRoomPropKeys.Team, 1, 0)]    // team is not a combat counter
    public void NonStaleWritesAreAccepted(string key, int previous, int incoming)
    {
        Assert.False(MatchReconnectMemory.RejectsStaleCounter(
            statsCarried: true,
            senderActorNr: 2,
            propActorNr: 2,
            key,
            hadPrevious: true,
            previous,
            incoming));
    }

    [Fact]
    public void HostActorMayStillAuthorAnotherPlayersCounter()
    {
        Assert.False(MatchReconnectMemory.RejectsStaleCounter(
            statsCarried: true,
            senderActorNr: MatchHostActor.ActorNr,
            propActorNr: 2,
            MatchRoomPropKeys.Death,
            hadPrevious: true,
            previous: 2,
            incoming: 1));
    }

    [Fact]
    public void TakeoverMarksTheActorCountersAsCarried()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_first"));
        room.ActorProps[(2, MatchRoomPropKeys.Kills)] = LobbyVariant.FromInt(4);
        var oldState = new MatchPeerState
        {
            Room = room,
            ActorNr = 2,
            UserId = "LBHASH_first",
            IdentityKey = "LBHASH",
            RosterName = "влал",
        };
        var newState = new MatchPeerState
        {
            UserId = "LBHASH_second",
            IdentityKey = "LBHASH",
        };

        MatchReconnect.Adopt(room, newState, oldState, actorNr: 2);

        Assert.True(newState.MatchStatsCarried);
        Assert.Equal(4, Int(room, 2, MatchRoomPropKeys.Kills));
    }

    private static MatchReconnectMemory Memory(
        MatchRoom room, int kills, int death, int score, int money) => new()
    {
        Room = room,
        Team = MatchTeam.Tr,
        Kills = kills,
        FairKills = kills,
        Death = death,
        Score2 = score,
        Assists = 0,
        RoundKills = 0,
        Money = money,
    };

    private static int Int(MatchRoom room, byte actorNr, string key) =>
        room.ActorProps.TryGetValue((actorNr, key), out var v) && v.Kind == LobbyVariantKind.Int
            ? v.Int
            : int.MinValue;

    private static MatchRoom NewRoom() => new()
    {
        PasswordKey = "Dedik",
        RoomId = "Sandstone",
    };
}
