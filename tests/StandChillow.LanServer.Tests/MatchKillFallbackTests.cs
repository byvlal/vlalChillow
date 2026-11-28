using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// «kills++ без атрибуции урона» must never invent a victim when the killer himself is dead
/// or has no living pawn. latest.log round 2: влал threw a grenade at his feet, his pawn was
/// destroyed, the client still sent <c>kills++</c>, and the old single-enemy fallback authored
/// the only enemy's death — wiping CT and awarding the round to the suicider. These tests pin
/// the corrected rule: the fallback needs a living killer.
/// </summary>
public sealed class MatchKillFallbackTests
{
    private static Dictionary<(byte Actor, string Key), LobbyVariant> Props(
        params (byte Actor, MatchTeam Team)[] fighters)
    {
        var props = new Dictionary<(byte, string), LobbyVariant>();
        foreach (var (actor, team) in fighters)
            props[(actor, MatchRoomPropKeys.Team)] = LobbyVariant.FromByte((byte)team);
        return props;
    }

    private static HashSet<byte> Pawns(params byte[] owners) => owners.ToHashSet();

    private static Func<byte, MatchTeam> Teams(
        Dictionary<(byte Actor, string Key), LobbyVariant> props) =>
        actor => MatchFlowRules.GetActorTeam(props, actor);

    /// <summary>
    /// Exact round-2 scenario: the killer's pawn was destroyed by his own grenade (suicide),
    /// the only enemy is still alive. No victim may be resolved; the killer's own death then
    /// ends the round for the enemy.
    /// </summary>
    [Fact]
    public void SuicideKillsPlusPlus_DoesNotAuthorTheOnlyEnemy()
    {
        var props = Props((2, MatchTeam.Tr), (3, MatchTeam.Ct));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        // Actor 2 killed himself: his pawn is gone, the enemy pawn is still alive.
        var pawns = Pawns(3);
        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, pawns, killerActorNr: 2, Teams(props), out var why);

        Assert.Equal(0, victim);
        Assert.Contains("no living pawn", why);
        // No invented death → no wipe of the enemy side.
        Assert.False(MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out _, out _));

        // The killer's own death prop is the real round result: Tr wiped, Ct wins.
        flow.DeadActors.Add(2);
        var resolved = MatchFlowRules.TryResolveWipe(
            flow, props, bombPlanted: false, out var winner, out var reason);
        Assert.True(resolved);
        Assert.Equal(MatchTeam.Ct, winner);
        Assert.Equal("wipe-tr", reason);
    }

    /// <summary>Control: a living killer with one living enemy still resolves that enemy.</summary>
    [Fact]
    public void LivingKiller_StillResolvesTheOnlyEnemy()
    {
        var props = Props((2, MatchTeam.Tr), (3, MatchTeam.Ct));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(2, 3), killerActorNr: 2, Teams(props), out var why);

        Assert.Equal(3, victim);
        Assert.Equal("single living enemy", why);
    }

    /// <summary>A proven death blocks the fallback even if a stale pawn is still tracked.</summary>
    [Fact]
    public void DeadKiller_EvenWithPawn_DoesNotResolve()
    {
        var props = Props((2, MatchTeam.Tr), (3, MatchTeam.Ct));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);
        flow.DeadActors.Add(2);

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(2, 3), killerActorNr: 2, Teams(props), out var why);

        Assert.Equal(0, victim);
        Assert.Contains("killer is dead", why);
    }

    /// <summary>Two living enemies are ambiguous — the fallback must not pick one at random.</summary>
    [Fact]
    public void TwoLivingEnemies_AreAmbiguous()
    {
        var props = Props((2, MatchTeam.Tr), (3, MatchTeam.Ct), (4, MatchTeam.Ct));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(2, 3, 4), killerActorNr: 2, Teams(props), out var why);

        Assert.Equal(0, victim);
        Assert.Contains("more than one living enemy", why);
    }

    /// <summary>A living teammate on the killer's side is not a victim candidate.</summary>
    [Fact]
    public void TeammateIsNotAnEnemy()
    {
        var props = Props(
            (2, MatchTeam.Tr),
            (3, MatchTeam.Ct),
            (4, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(2, 3, 4), killerActorNr: 2, Teams(props), out _);

        Assert.Equal(3, victim);
    }

    /// <summary>The host actor and actor 0 own scene objects but are never fighters.</summary>
    [Fact]
    public void HostAndZero_AreNeverVictims()
    {
        var props = Props((2, MatchTeam.Tr), (3, MatchTeam.Ct));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };
        MatchFlowRules.FreezeRoundRoster(flow, props);

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow,
            Pawns(2, 3, 0, MatchHostActor.ActorNr),
            killerActorNr: 2,
            Teams(props),
            out _);

        Assert.Equal(3, victim);
    }

    /// <summary>A killer with no fighting team (spectator / stale prop) resolves nothing.</summary>
    [Fact]
    public void UnknownKillerTeam_DoesNotResolve()
    {
        var props = Props((3, MatchTeam.Ct));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(2, 3), killerActorNr: 2, Teams(props), out var why);

        Assert.Equal(0, victim);
        Assert.Contains("team unknown", why);
    }

    /// <summary>No living enemy at all (already dead / left) resolves nothing.</summary>
    [Fact]
    public void NoLivingEnemy_DoesNotResolve()
    {
        var props = Props((2, MatchTeam.Tr));
        var flow = new MatchFlowState { Phase = MatchFlowPhase.PurchasePhase };

        var victim = MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(2), killerActorNr: 2, Teams(props), out var why);

        Assert.Equal(0, victim);
        Assert.Contains("no living enemy", why);
    }

    /// <summary>The host actor and actor 0 are never killers either.</summary>
    [Fact]
    public void HostAndZero_AreNeverKillers()
    {
        var props = Props((2, MatchTeam.Tr), (3, MatchTeam.Ct));
        var flow = new MatchFlowState();

        Assert.Equal(0, MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(0, 2, 3), killerActorNr: 0, Teams(props), out _));
        Assert.Equal(0, MatchFlowRules.ResolveSingleEnemyFallback(
            flow, Pawns(MatchHostActor.ActorNr, 2, 3),
            killerActorNr: MatchHostActor.ActorNr, Teams(props), out _));
    }
}
