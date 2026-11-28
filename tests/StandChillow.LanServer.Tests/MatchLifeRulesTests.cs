using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Single source of truth for «is this actor alive in the current round?».
/// <para>
/// These are the real scenarios from latest.log round 9→10 and from a high-ping spawn:
/// a dead player reconnecting in PreStart must not make the round run with nobody on his
/// side, a live player without a body must get his 5 s grace instead of a false death, and
/// a carried death must end the round at C2=31 without waiting for the 109 s timeout.
/// </para>
/// </summary>
public sealed class MatchLifeRulesTests
{
    private static readonly DateTime T0 = new(2026, 11, 28, 19, 0, 0, DateTimeKind.Utc);

    private static Dictionary<(byte Actor, string Key), LobbyVariant> Props(
        params (byte Actor, MatchTeam Team)[] fighters)
    {
        var props = new Dictionary<(byte, string), LobbyVariant>();
        foreach (var (actor, team) in fighters)
            props[(actor, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)team);
        return props;
    }

    private static HashSet<byte> Pawns(params byte[] owners) => owners.ToHashSet();

    private static MatchRoom NewRoom() => new()
    {
        PasswordKey = "Dedik",
        RoomId = "Sandstone",
    };

    /// <summary>
    /// Exact bug from latest.log: влал died in round 9 (wipe Ct → T), reconnected in the
    /// PreStart of round 10, his stats were restored, but the server froze the roster with
    /// him «alive» and no pawn — the round ran with a dead player. Now the death travels
    /// with the reconnect memory and C2=31 resolves the wipe immediately.
    /// </summary>
    [Fact]
    public void DeadPlayerReconnectsInPreStart_IsWipedAtCombatStart()
    {
        var room = NewRoom();
        room.ActorProps[(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Ct);
        room.ActorProps[(3, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        room.ActorProps[(2, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(2);

        // Round 9 combat: влал (Ct) is killed, the wipe already ended the round.
        room.Flow.Phase = MatchFlowPhase.PurchasePhase;
        room.Flow.RoundRoster[2] = MatchTeam.Ct;
        room.Flow.RoundRoster[3] = MatchTeam.Tr;
        room.Flow.DeadActors.Add(2);

        // C2=101: the body snapshot carries his death into the pause.
        MatchFlowRules.NoteRoundEndBodies(room.Flow, livingPawnOwners: new byte[] { 3 });
        Assert.Contains((byte)2, room.Flow.CarriedDeadActors);

        // Socket drop: the room deletes the actor and every death mark with it.
        var memory = MatchReconnectMemory.Capture(room, 2, MatchTeam.Ct);
        Assert.True(memory.WasDead);
        room.Flow.DeadActors.Remove(2);
        room.Flow.CarriedDeadActors.Remove(2);
        MatchFlowRules.RemoveFromRoundRoster(room.Flow, 2);
        room.ActorProps.Remove((2, MatchRoomPropKeys.Team));

        // C2=22 round 10: new round, the returning player gets a fresh actor number.
        room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
        MatchFlowRules.BeginRound(room.Flow);
        var restored = memory.RestoreInto(room, actorNr: 5);
        room.ActorProps[(5, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Ct);

        Assert.Contains(restored, p => p.Key == MatchRoomPropKeys.Death);
        Assert.Equal(2, room.ActorProps[(5, MatchRoomPropKeys.Death)].Int);
        Assert.Contains((byte)5, room.Flow.DeadActors);
        Assert.Contains((byte)5, room.Flow.CarriedDeadActors);

        // C2=31: freeze, seed, arm — only the T actually spawned.
        room.Flow.Phase = MatchFlowPhase.PurchasePhase;
        MatchFlowRules.FreezeRoundRoster(room.Flow, room.ActorProps);
        var pawns = Pawns(3);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(room.Flow, pawns);
        MatchLifeRules.ArmSpawnDeadlines(room.Flow, pawns, T0);

        Assert.Equal(MatchActorLifeState.Dead, MatchLifeRules.GetActorLife(room.Flow, pawns, 5));
        // A carried death is already decided: it gets no 5 s spawn grace.
        Assert.False(room.Flow.SpawnDeadlines.ContainsKey(5));

        var resolved = MatchFlowRules.TryResolveWipe(
            room.Flow, room.ActorProps, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Tr, winner);
        Assert.Equal("wipe-ct", reason);
    }

    /// <summary>
    /// A player who was alive at the end of the last round but whose CWO is delayed by ping
    /// must not be declared dead at C2=31. He has exactly <see cref="MatchLifeRules.SpawnGrace"/>
    /// to prove the body; before that he is <see cref="MatchActorLifeState.AwaitingSpawn"/>
    /// and counts alive for the wipe.
    /// </summary>
    [Fact]
    public void LivePlayerWithoutPawn_GetsFiveSeconds_ThenIsDeadForTheWipe()
    {
        var props = Props((2, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(3);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, pawns);
        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0);

        Assert.Equal(MatchActorLifeState.AwaitingSpawn, MatchLifeRules.GetActorLife(flow, pawns, 2));
        Assert.True(MatchLifeRules.IsAlive(MatchActorLifeState.AwaitingSpawn));
        Assert.Contains((byte)2, flow.SpawnDeadlines);

        // 4.9 s — still inside the grace, still alive, no wipe.
        Assert.Empty(MatchLifeRules.ProcessSpawnDeadlines(
            flow, pawns, T0 + TimeSpan.FromSeconds(4.9)));
        Assert.False(MatchFlowRules.IsActorDead(flow, props, 2));
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));

        // 5 s — the grace is over: dead, and the wipe resolves in the same tick.
        var expired = MatchLifeRules.ProcessSpawnDeadlines(
            flow, pawns, T0 + MatchLifeRules.SpawnGrace);
        Assert.Equal(new byte[] { 2 }, expired);
        Assert.Equal(MatchActorLifeState.Dead, MatchLifeRules.GetActorLife(flow, pawns, 2));

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);
        Assert.True(resolved);
        Assert.Equal(MatchTeam.Tr, winner);
        Assert.Equal("wipe-ct", reason);
    }

    /// <summary>A CWO arriving inside the grace is the spawn proof: the clock is gone.</summary>
    [Fact]
    public void BodyArrivingInsideTheGrace_KeepsThePlayerAlive()
    {
        var props = Props((2, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(3);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, pawns);
        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0);

        MatchFlowRules.NoteSpawn(flow, 2);
        pawns.Add(2);

        Assert.False(flow.SpawnDeadlines.ContainsKey(2));
        Assert.Equal(MatchActorLifeState.Alive, MatchLifeRules.GetActorLife(flow, pawns, 2));
        Assert.Empty(MatchLifeRules.ProcessSpawnDeadlines(flow, pawns, T0 + MatchLifeRules.SpawnGrace));
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));
    }

    /// <summary>
    /// Even if the CWO-side cleanup was missed, the tick sees the body and drops the clock.
    /// The body is the proof of life; the deadline exists only for «no body at all».
    /// </summary>
    [Fact]
    public void TickClearsTheClockWhenTheBodyExists()
    {
        var props = Props((2, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(3);
        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0);

        pawns.Add(2); // body registered, NoteSpawn not called
        Assert.Empty(MatchLifeRules.ProcessSpawnDeadlines(flow, pawns, T0 + MatchLifeRules.SpawnGrace));
        Assert.False(flow.SpawnDeadlines.ContainsKey(2));
        Assert.Equal(MatchActorLifeState.Alive, MatchLifeRules.GetActorLife(flow, pawns, 2));
    }

    /// <summary>
    /// A fighter who was dead at the end of the previous round and did not spawn is dead at
    /// once at C2=31 — no 5 s grace, because his state was already decided by the last round.
    /// </summary>
    [Fact]
    public void CarriedDeadWithoutPawn_IsDeadAtOnce_AndNeverGetsAGrace()
    {
        var props = Props((2, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.WarmupWillFinish };
        flow.DeadActors.Add(2);

        MatchFlowRules.BeginRound(flow);
        flow.Phase = MatchFlowPhase.PurchasePhase;
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(3);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, pawns);
        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0);

        Assert.Equal(MatchActorLifeState.Dead, MatchLifeRules.GetActorLife(flow, pawns, 2));
        Assert.False(flow.SpawnDeadlines.ContainsKey(2));

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);
        Assert.True(resolved);
        Assert.Equal(MatchTeam.Tr, winner);
        Assert.Equal("wipe-ct", reason);
    }

    /// <summary>One dead fighter does not wipe a side that still has a living teammate.</summary>
    [Fact]
    public void OneLivingFighter_KeepsTheSideInTheRound()
    {
        var props = Props((2, MatchTeam.Ct), (4, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.WarmupWillFinish };
        flow.DeadActors.Add(2);

        MatchFlowRules.BeginRound(flow);
        flow.Phase = MatchFlowPhase.PurchasePhase;
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(3, 4);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, pawns);
        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0);

        Assert.Equal(MatchActorLifeState.Dead, MatchLifeRules.GetActorLife(flow, pawns, 2));
        Assert.Equal(MatchActorLifeState.Alive, MatchLifeRules.GetActorLife(flow, pawns, 4));
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));
    }

    /// <summary>
    /// Late joiners, the host and actor 0 are outside the frozen round: their body (or lack of
    /// one) must not influence the wipe either way.
    /// </summary>
    [Fact]
    public void ActorOutsideTheFrozenRoster_IsOutOfRound()
    {
        var props = Props((2, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(2, 3, 9);

        Assert.Equal(MatchActorLifeState.OutOfRound, MatchLifeRules.GetActorLife(flow, pawns, 9));
        Assert.Equal(MatchActorLifeState.OutOfRound, MatchLifeRules.GetActorLife(flow, pawns, 0));
        Assert.Equal(
            MatchActorLifeState.OutOfRound,
            MatchLifeRules.GetActorLife(flow, pawns, MatchHostActor.ActorNr));
        Assert.False(MatchLifeRules.IsDead(MatchLifeRules.GetActorLife(flow, pawns, 9)));
    }

    /// <summary>A proven death outranks a lingering body: the host already authored the kill.</summary>
    [Fact]
    public void ProvenDeath_OutranksALingeringBody()
    {
        var flow = new MatchFlowState();
        flow.DeadActors.Add(2);
        Assert.Equal(MatchActorLifeState.Dead, MatchLifeRules.GetActorLife(flow, Pawns(2), 2));
    }

    /// <summary>
    /// A carried death is cancelled by a real body even if the CWO-side cleanup was missed —
    /// per-player state is reversible, never a one-way «dead forever».
    /// </summary>
    [Fact]
    public void CarriedDeath_IsCancelledByARealBody()
    {
        var flow = new MatchFlowState();
        flow.CarriedDeadActors.Add(2);
        Assert.Equal(MatchActorLifeState.Alive, MatchLifeRules.GetActorLife(flow, Pawns(2), 2));
    }

    /// <summary>
    /// A repeated combat entry must not push the deadline further: otherwise a second
    /// <c>ArmSpawnDeadlines</c> call would silently extend the grace forever.
    /// </summary>
    [Fact]
    public void ArmingTwice_DoesNotPushTheDeadlineLater()
    {
        var props = Props((2, MatchTeam.Ct), (3, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        var pawns = Pawns(3);

        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0);
        MatchLifeRules.ArmSpawnDeadlines(flow, pawns, T0 + TimeSpan.FromSeconds(4));

        Assert.Equal(T0 + MatchLifeRules.SpawnGrace, flow.SpawnDeadlines[2]);
        Assert.Equal(
            new byte[] { 2 },
            MatchLifeRules.ProcessSpawnDeadlines(flow, pawns, T0 + MatchLifeRules.SpawnGrace));
    }

    /// <summary>Round transitions forget the spawn clocks together with the deaths.</summary>
    [Fact]
    public void RoundTransitions_ClearSpawnDeadlines()
    {
        var flow = new MatchFlowState();
        flow.SpawnDeadlines[2] = T0 + MatchLifeRules.SpawnGrace;
        MatchFlowRules.BeginRound(flow);
        Assert.Empty(flow.SpawnDeadlines);

        flow.SpawnDeadlines[3] = T0 + MatchLifeRules.SpawnGrace;
        MatchFlowRules.NoteRoundEndBodies(flow, Array.Empty<byte>());
        Assert.Empty(flow.SpawnDeadlines);

        flow.SpawnDeadlines[4] = T0 + MatchLifeRules.SpawnGrace;
        MatchFlowRules.ResetRoundDeaths(flow);
        Assert.Empty(flow.SpawnDeadlines);
    }

    /// <summary>
    /// Takeover mid-round: the old session's actor is dropped from the frozen roster exactly
    /// like a disconnect, so the remaining side can resolve the wipe immediately.
    /// </summary>
    [Fact]
    public void TakeoverMidRound_DropsTheOldSessionFromTheRound()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_first"));
        room.ActorProps[(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Ct);
        room.ActorProps[(3, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        room.Flow.Phase = MatchFlowPhase.PurchasePhase;
        room.Flow.RoundRoster[2] = MatchTeam.Ct;
        room.Flow.RoundRoster[3] = MatchTeam.Tr;
        room.Flow.DeadActors.Add(2);

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

        Assert.False(room.Flow.RoundRoster.ContainsKey(2));
        var resolved = MatchFlowRules.TryResolveWipe(
            room.Flow, room.ActorProps, bombPlanted: false, out var winner, out var reason);
        Assert.True(resolved);
        Assert.Equal(MatchTeam.Tr, winner);
        Assert.Equal("wipe-ct", reason);
    }

    /// <summary>
    /// Takeover during buy time: no round is frozen yet, so the old session's death is
    /// cleared and the returning player may spawn before the round starts.
    /// </summary>
    [Fact]
    public void TakeoverDuringBuy_ClearsTheRoundDeathForAFreshSpawn()
    {
        var room = NewRoom();
        room.Actors.Add((2, "влал", "LBHASH_first"));
        room.ActorProps[(2, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Ct);
        room.Flow.Phase = MatchFlowPhase.WarmupWillFinish;
        room.Flow.DeadActors.Add(2);
        room.Flow.CarriedDeadActors.Add(2);
        room.Flow.SpawnDeadlines[2] = T0 + MatchLifeRules.SpawnGrace;

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

        var adoption = MatchReconnect.Adopt(room, newState, oldState, actorNr: 2);

        Assert.True(adoption.ClearedRoundDeath);
        Assert.DoesNotContain((byte)2, room.Flow.DeadActors);
        Assert.DoesNotContain((byte)2, room.Flow.CarriedDeadActors);
        Assert.False(room.Flow.SpawnDeadlines.ContainsKey(2));
        Assert.Equal(MatchActorLifeState.AwaitingSpawn, MatchLifeRules.GetActorLife(room.Flow, Pawns(), 2));
    }
}
