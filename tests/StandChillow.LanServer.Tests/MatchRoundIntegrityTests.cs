using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #33 — a desync is any place where two clients answer «who is alive / who is in this
/// round» differently. These tests pin the two rules that were asked of the round state:
/// the wipe is judged against the roster frozen for that round, and no peer may author another
/// player's combat counters.
/// </summary>
public sealed class MatchRoundIntegrityTests
{
    private static Dictionary<(byte Actor, string Key), LobbyVariant> Props(
        params (byte Actor, MatchTeam Team, int? Death)[] fighters)
    {
        var props = new Dictionary<(byte, string), LobbyVariant>();
        foreach (var (actor, team, death) in fighters)
        {
            props[(actor, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)team);
            if (death is int d)
                props[(actor, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(d);
        }
        return props;
    }

    /// <summary>
    /// The roster is frozen when combat starts, so a stale team prop (player left, never spawned,
    /// rejoined as spectator) cannot fake a wipe on some peers only.
    /// </summary>
    [Fact]
    public void StaleTeamProp_CannotFakeAWipe()
    {
        var props = Props(
            (2, MatchTeam.Tr, null),
            (3, MatchTeam.Ct, null),
            (4, MatchTeam.Ct, null));

        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        Assert.Equal(3, flow.RoundRoster.Count); // all three fighters are in this round

        // Actor 4 leaves mid-round — its team prop survives, the round roster does not grow.
        props.Remove((4, MatchRoomPropKeys.Team));
        var (aliveTr, aliveCt, totalTr, totalCt) =
            MatchFlowRules.CountAliveFighters(flow, props);

        Assert.Equal(1, totalTr);
        Assert.Equal(2, totalCt);
        Assert.Equal(1, aliveTr);
        Assert.Equal(2, aliveCt);
    }

    /// <summary>A joiner mid-round is not part of this round and must not create a phantom side.</summary>
    [Fact]
    public void MidRoundJoiner_IsNotCountedInThisRound()
    {
        var props = Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        props[(5, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);
        var (_, _, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);

        Assert.Equal(1, totalTr);
        Assert.Equal(1, totalCt);
    }

    /// <summary>Deaths still count against the frozen roster — a wipe is still a wipe.</summary>
    [Fact]
    public void FrozenRoster_StillResolvesAWipe()
    {
        var props = Props((2, MatchTeam.Tr, 1), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        flow.DeadActors.Add(2);
        MatchFlowRules.FreezeRoundRoster(flow, props);

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>Round identity dies with the round: a new round re-freezes it from live props.</summary>
    [Fact]
    public void Roster_IsClearedWithTheRound()
    {
        var flow = new MatchFlowState();
        MatchFlowRules.FreezeRoundRoster(flow, Props((2, MatchTeam.Tr, null)));
        Assert.Single(flow.RoundRoster);

        MatchFlowRules.ClearRoundRoster(flow);
        Assert.Empty(flow.RoundRoster);
    }

    /// <summary>
    /// Only somebody who entered this round may be turned into a death (#33). An unfrozen roster
    /// means «not in combat yet» and accepts everyone, so the guard is safe to call always.
    /// </summary>
    [Fact]
    public void OnlyRoundParticipants_CanBecomeCombatDeaths()
    {
        var flow = new MatchFlowState();
        // Before the freeze the roster is empty and accepts everyone but the host actor.
        Assert.True(MatchFlowRules.IsRoundParticipant(flow, actorNr: 9));

        MatchFlowRules.FreezeRoundRoster(flow, Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null)));
        Assert.True(MatchFlowRules.IsRoundParticipant(flow, actorNr: 2));
        Assert.False(MatchFlowRules.IsRoundParticipant(flow, actorNr: 9));
    }

    [Fact]
    public void CumulativeDeathProp_DoesNotMakeRespawnedActorDead()
    {
        var flow = new MatchFlowState();
        var props = Props((2, MatchTeam.Tr, 4));
        flow.DeadActors.Add(2);

        Assert.True(MatchFlowRules.IsActorDead(flow, props, 2));
        flow.DeadActors.Remove(2);
        Assert.False(MatchFlowRules.IsActorDead(flow, props, 2));
    }

    /// <summary>
    /// Round 2, C2=31: the player died in round 1 (his cumulative <c>death</c> is still 1),
    /// respawned at C2=22 and sent his own CWO. The stale wire counter must not mark him dead
    /// again — otherwise the server wipes his team at combat start and awards a free round.
    /// </summary>
    [Fact]
    public void CumulativeDeathProp_DoesNotKillARespawnedPlayerAtNextCombat()
    {
        var props = Props((2, MatchTeam.Tr, 1), (3, MatchTeam.Ct, 0));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.WarmupWillFinish };
        flow.DeadActors.Add(2); // round 1 death

        MatchFlowRules.BeginRound(flow); // C2=22: round-local deaths reset, carried keeps the fact
        Assert.Empty(flow.DeadActors);
        Assert.Contains((byte)2, flow.CarriedDeadActors);

        // C2=22 respawn: his own CWO is the proof of life.
        MatchFlowRules.NoteSpawn(flow, 2);
        Assert.Empty(flow.CarriedDeadActors);

        // C2=31: freeze + carried seed must not revive the cumulative death counter.
        flow.Phase = MatchFlowPhase.PurchasePhase;
        MatchFlowRules.FreezeRoundRoster(flow, props);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, livingPawnOwners: new byte[] { 2, 3 });

        Assert.False(MatchFlowRules.IsActorDead(flow, props, 2));
        var (aliveTr, aliveCt, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);
        Assert.Equal(1, aliveTr);
        Assert.Equal(1, aliveCt);
        Assert.Equal(1, totalTr);
        Assert.Equal(1, totalCt);
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));
    }

    /// <summary>
    /// Issue #56 made <c>death</c> cumulative. A host that rebuilds <c>DeadActors</c> from a
    /// non-zero death prop marks everyone who ever died as dead at the next C2=31. Pin the
    /// removal of that path so it cannot quietly come back.
    /// </summary>
    [Fact]
    public void NoSourceFile_RebuildsDeadActorsFromCumulativeDeathProps()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(
                     ServerSourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            if (File.ReadAllText(file).Contains("SyncDeadActorsFromDeathProps", StringComparison.Ordinal))
                offenders.Add(Path.GetFileName(file));
        }

        Assert.True(
            offenders.Count == 0,
            "DeadActors is event-driven; a cumulative `death` prop must never rebuild it. Offenders: " +
            string.Join(", ", offenders));
    }

    private static string ServerSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "server");
    }

    /// <summary>
    /// Exact phantom from the live probe: T died in round 1, never sent a new
    /// <c>CreateWorldObject</c>, and the blanket <c>DeadActors.Clear()</c> at C2=22 made him
    /// «alive» for round 2 — the round then ran to the 109s timeout instead of a wipe.
    /// Per-player state keeps him dead until his own CWO, and only then revives him.
    /// </summary>
    [Fact]
    public void CarriedDeath_WithoutRespawn_ResolvesWipeNextRound()
    {
        var props = Props((2, MatchTeam.Ct, null), (3, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        flow.DeadActors.Add(3); // round 1: T eliminated

        // C2=22 round 2: round-local deaths reset for respawn, per-player fact is kept.
        MatchFlowRules.BeginRound(flow);
        Assert.Empty(flow.DeadActors);
        Assert.Contains((byte)3, flow.CarriedDeadActors);

        MatchFlowRules.FreezeRoundRoster(flow, props);
        // Only CT sent his body this round; the T never came back.
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, livingPawnOwners: new byte[] { 2 });

        Assert.True(MatchFlowRules.IsActorDead(flow, props, 3));
        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);
        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>
    /// The same carried death is cancelled by the player's own spawn proof (CWO), in either
    /// order: before the combat freeze or after it. Death state is per player, so it is
    /// reversible — never a one-way «phase says dead».
    /// </summary>
    [Fact]
    public void CarriedDeath_IsClearedByThePlayersOwnSpawn()
    {
        var props = Props((2, MatchTeam.Ct, null), (3, MatchTeam.Tr, null));

        // Spawn during buy, before the freeze.
        var flow = new MatchFlowState { Phase = MatchFlowPhase.WarmupWillFinish };
        flow.DeadActors.Add(3);
        MatchFlowRules.BeginRound(flow);
        MatchFlowRules.NoteSpawn(flow, 3);
        MatchFlowRules.FreezeRoundRoster(flow, props);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, livingPawnOwners: new byte[] { 2, 3 });

        flow.Phase = MatchFlowPhase.PurchasePhase;
        Assert.False(MatchFlowRules.IsActorDead(flow, props, 3));
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));

        // Same player, CWO arrives after the freeze already seeded him: clears it again.
        var late = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        late.DeadActors.Add(3);
        MatchFlowRules.BeginRound(late);
        MatchFlowRules.FreezeRoundRoster(late, props);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(late, livingPawnOwners: new byte[] { 2 });
        Assert.True(MatchFlowRules.IsActorDead(late, props, 3));

        MatchFlowRules.NoteSpawn(late, 3);
        Assert.False(MatchFlowRules.IsActorDead(late, props, 3));
    }

    /// <summary>
    /// A player who was alive at the end of the previous round is never carried dead. If his
    /// CWO is delayed by ping, the freeze/wipe tick has no per-player evidence against him and
    /// must keep him alive — this is the regression the phase-based rule would cause.
    /// </summary>
    [Fact]
    public void LivePlayer_WithPingDelayedSpawn_IsNotSeededDead()
    {
        var props = Props((2, MatchTeam.Ct, null), (3, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };

        // Nobody died last round; the T simply has not spawned yet at the freeze tick.
        MatchFlowRules.BeginRound(flow);
        MatchFlowRules.FreezeRoundRoster(flow, props);
        MatchFlowRules.SeedCarriedDeathsWithoutPawn(flow, livingPawnOwners: new byte[] { 2 });

        Assert.False(MatchFlowRules.IsActorDead(flow, props, 3));
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));
    }

    /// <summary>
    /// Round end is the safe moment to snapshot bodies: clients only Destroy+Create at the next
    /// C2=22, so a live player still owns his pawn. A roster fighter without one was dead or
    /// never spawned and is carried into the next round until his own CWO.
    /// </summary>
    [Fact]
    public void RoundEnd_CarriesOnlyRosterFightersWithoutABody()
    {
        var flow = new MatchFlowState();
        flow.RoundRoster[2] = MatchTeam.Ct;
        flow.RoundRoster[3] = MatchTeam.Tr;
        flow.RoundRoster[4] = MatchTeam.Tr;

        MatchFlowRules.NoteRoundEndBodies(flow, livingPawnOwners: new byte[] { 2, 4 });

        Assert.DoesNotContain((byte)2, flow.CarriedDeadActors);
        Assert.Contains((byte)3, flow.CarriedDeadActors);
        Assert.DoesNotContain((byte)4, flow.CarriedDeadActors);
    }

    [Theory]
    [InlineData(MatchRoomPropKeys.Death)]
    [InlineData(MatchRoomPropKeys.Kills)]
    [InlineData(MatchRoomPropKeys.RoundKills)]
    [InlineData(MatchRoomPropKeys.FairKills)]
    [InlineData(MatchRoomPropKeys.Assists)]
    [InlineData(MatchRoomPropKeys.Score2)]
    public void CombatCounter_FromAnotherActor_IsRejected(string key)
    {
        // sender actor 2 tries to write actor 3's counter
        Assert.True(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 3, key, hostActorNr: 1));
    }

    [Fact]
    public void OwnCombatCounter_AndHostAuthoredDeath_AreAccepted()
    {
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 2, MatchRoomPropKeys.Kills, hostActorNr: 1));
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 1, propActorNr: 3, MatchRoomPropKeys.Death, hostActorNr: 1));
    }

    /// <summary>Non-combat props (team, loadout, ping, identity) keep flowing from their actor.</summary>
    [Fact]
    public void OrdinaryActorProps_AreNeverBlocked()
    {
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 2, MatchRoomPropKeys.Team, hostActorNr: 1));
        Assert.False(MatchPropAuthority.RejectsForeignCombatProp(
            senderActorNr: 2, propActorNr: 2, MatchRoomPropKeys.Ping, hostActorNr: 1));
        Assert.False(MatchPropAuthority.IsCombatOutcomeKey(MatchRoomPropKeys.Team));
    }

    /// <summary>
    /// Fix #3, exact shape of latest.log round 3: T had two fighters when the round froze, one of
    /// them left mid-round, and the last T was then killed. Freezing <b>before</b> the leaver
    /// walked out keeps the side non-empty, so the death of the last T is a real wipe — not an
    /// «empty roster» case, and not «wait for the bomb» either.
    /// </summary>
    [Fact]
    public void LeaverThenLastFighterDies_ResolvesWipeTr()
    {
        var props = Props(
            (2, MatchTeam.Ct, null),
            (3, MatchTeam.Tr, null),
            (5, MatchTeam.Tr, null));

        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        Assert.Equal(3, flow.RoundRoster.Count);

        // Actor 5 leaves mid-round (disconnect removes him from the roster, not from history).
        MatchFlowRules.RemoveFromRoundRoster(flow, actorNr: 5);
        props.Remove((5, MatchRoomPropKeys.Team));

        // The remaining T is eliminated.
        props[(3, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(1);
        flow.DeadActors.Add(3);

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>
    /// A mid-round joiner takes a team prop while the round is already running. He is not part of
    /// it (cold start = dead for this round) and must not keep his side alive after a wipe.
    /// </summary>
    [Fact]
    public void MidRoundJoiner_IsNotInTheFrozenRoster()
    {
        var props = Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // Joiner arrives after the freeze.
        props[(6, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Tr);

        Assert.False(flow.RoundRoster.ContainsKey(6));
        var (_, _, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);
        Assert.Equal(1, totalTr);
        Assert.Equal(1, totalCt);
    }

    /// <summary>
    /// A team change inside a running round is a death, never a transfer: to the other side it is
    /// punished with a death, to Spectator the player «disappears» and can only watch. Before the
    /// round (buy time) it is free — he respawns on the new side.
    /// </summary>
    [Fact]
    public void MidRoundTeamSwitch_IsADeath_ButBuyTimeIsNot()
    {
        var props = Props((2, MatchTeam.Ct, null), (3, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.WarmupWillFinish };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // Buy time: switching sides is free (he respawns on the new side).
        Assert.False(MatchFlowRules.IsMidRoundTeamSwitch(
            flow, actorNr: 3, MatchTeam.Ct, out _));

        // Combat: switching to the other side is a death.
        flow.Phase = MatchFlowPhase.PurchasePhase;
        Assert.True(MatchFlowRules.IsMidRoundTeamSwitch(
            flow, actorNr: 3, MatchTeam.Ct, out var from));
        Assert.Equal(MatchTeam.Tr, from);

        // Same team again / an unknown actor are no-ops.
        Assert.False(MatchFlowRules.IsMidRoundTeamSwitch(
            flow, actorNr: 3, MatchTeam.Tr, out _));
        Assert.False(MatchFlowRules.IsMidRoundTeamSwitch(
            flow, actorNr: 9, MatchTeam.Ct, out _));
    }

    /// <summary>
    /// The frozen entry is kept when a fighter switches team mid-round (he is marked dead).
    /// Deleting it would empty the side, and an empty side stops producing a result — the round
    /// would then wait for the timeout instead of ending on the wipe.
    /// </summary>
    [Fact]
    public void MidRoundSwitchToSpectator_StillEndsTheRound()
    {
        var props = Props((2, MatchTeam.Ct, null), (3, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // The only T switches to Spectator mid-round and «disappears» from the fight.
        flow.DeadActors.Add(3);
        props[(3, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)MatchTeam.Spectator);

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>
    /// The whole side disconnects mid-round (any number of leaves, not just one). This must still
    /// produce a result in the bomb family, whose combat phase is PurchasePhase — the empty-team
    /// rule used to be limited to RoundLive/BombPlanted and returned nothing here, so the round
    /// rolled on to the 109s timeout instead of ending.
    /// </summary>
    [Fact]
    public void WholeSideLeaves_StillEndsTheRound()
    {
        var props = Props((2, MatchTeam.Ct, null), (3, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // Both Ct disconnect — roster entries and team props are gone.
        MatchFlowRules.RemoveFromRoundRoster(flow, actorNr: 2);
        props.Remove((2, MatchRoomPropKeys.Team));

        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);

        Assert.True(resolved);
        Assert.Equal(MatchTeam.Tr, winner);
        Assert.Equal("wipe-ct", reason);
    }

    /// <summary>
    /// Same rule during buy time must invent nothing: no combat has been played, so an empty side
    /// is not a wipe.
    /// </summary>
    [Fact]
    public void EmptySide_DuringBuyTime_IsNotAWipe()
    {
        var props = Props((3, MatchTeam.Tr, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.WarmupWillFinish };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));
    }

    /// <summary>A leaver leaves the frozen roster, so the round can still be decided.</summary>
    [Fact]
    public void Leaver_IsDroppedFromTheRoster()
    {
        var props = Props((2, MatchTeam.Tr, null), (3, MatchTeam.Ct, null));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        MatchFlowRules.RemoveFromRoundRoster(flow, actorNr: 3);
        props[(2, MatchRoomPropKeys.Death)] = LobbyVariant.FromInt(0);

        var (_, aliveCt, totalTr, totalCt) = MatchFlowRules.CountAliveFighters(flow, props);
        Assert.Equal(1, totalTr);
        Assert.Equal(0, totalCt);
        Assert.Equal(0, aliveCt);
    }

    /// <summary>
    /// The host actor and actor 0 are never round participants — they must not be turned into
    /// combat deaths even though they may own scene objects.
    /// </summary>
    [Fact]
    public void HostAndZero_AreNeverRoundParticipants()
    {
        var flow = new MatchFlowState();
        Assert.False(MatchFlowRules.IsRoundParticipant(flow, actorNr: 0));
        Assert.False(MatchFlowRules.IsRoundParticipant(flow, actorNr: MatchHostActor.ActorNr));
        Assert.True(MatchFlowRules.IsRoundParticipant(flow, actorNr: 2));
    }
}
