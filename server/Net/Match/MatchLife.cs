using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Single answer to «is this actor alive right now?» for the current round.
/// </summary>
/// <remarks>
/// <para>
/// Before this type the answer was spread over <c>DeadActors</c>, <c>CarriedDeadActors</c>,
/// <c>RoundRoster</c>, <c>LivingPawns</c> and the cumulative <c>death</c> prop, and every
/// caller combined them differently. A reconnect that lost one of those sets (the actor and
/// its props are deleted on disconnect) therefore produced a player who was dead on his own
/// client but «alive» for the wipe — the round ran on with nobody on his side.
/// </para>
/// <para>
/// The rules are deliberately conservative: a proven death wins over a lingering body, a
/// confirmed body wins over a carried death (his own CWO is the spawn proof), and a fighter
/// in the frozen roster without a body yet is <see cref="MatchActorLifeState.AwaitingSpawn"/>
/// — counted alive until the 5 s spawn grace expires, so a slow phone is never mistaken for
/// a death. Only then does he become <see cref="MatchActorLifeState.Dead"/>.
/// </para>
/// </remarks>
public enum MatchActorLifeState : byte
{
    /// <summary>Not in the current round: no roster slot, spectator, host, or left the room.</summary>
    OutOfRound = 0,
    /// <summary>In the frozen roster, not dead, but no <c>CreateWorldObject</c> yet.</summary>
    AwaitingSpawn = 1,
    /// <summary>Body confirmed and no proven death this round.</summary>
    Alive = 2,
    /// <summary>Proven death, carried death without a body, or the spawn grace expired.</summary>
    Dead = 3,
}

/// <summary>
/// The one place that decides <see cref="MatchActorLifeState"/> and advances the round-local
/// death/spawn bookkeeping. Callers must not re-implement these combinations.
/// </summary>
public static class MatchLifeRules
{
    /// <summary>
    /// How long a live fighter may stay without a body after C2=31 before the host treats
    /// him as dead for the wipe. Buy already gets an extra 5 s when a pawn is missing, so a
    /// normal spawn never reaches this; the grace exists only so a high-ping client is not
    /// punished by a single late CWO.
    /// </summary>
    public static readonly TimeSpan SpawnGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Resolve the actor's life state from the frozen roster, proven deaths, and the bodies
    /// that currently exist. <paramref name="pawnOwners"/> is the set of owners of living
    /// pawns, not the pawn ids.
    /// </summary>
    public static MatchActorLifeState GetActorLife(
        MatchFlowState flow,
        IReadOnlySet<byte> pawnOwners,
        byte actorNr)
    {
        if (actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return MatchActorLifeState.OutOfRound;
        if (flow.RoundRoster.Count > 0 && !flow.RoundRoster.ContainsKey(actorNr))
            return MatchActorLifeState.OutOfRound;
        // A proven death this round outranks a lingering body: the client may still have the
        // corpse registered while the host already authored the elimination.
        if (flow.DeadActors.Contains(actorNr))
            return MatchActorLifeState.Dead;
        // A real body is the spawn proof; it also cancels a carried death even if the
        // CWO-side cleanup was missed for any reason.
        if (pawnOwners.Contains(actorNr))
            return MatchActorLifeState.Alive;
        if (flow.CarriedDeadActors.Contains(actorNr))
            return MatchActorLifeState.Dead;
        return MatchActorLifeState.AwaitingSpawn;
    }

    /// <summary>Alive for wipe purposes: a confirmed body or a spawn still inside the grace.</summary>
    public static bool IsAlive(MatchActorLifeState state) =>
        state is MatchActorLifeState.Alive or MatchActorLifeState.AwaitingSpawn;

    /// <summary>Dead for wipe purposes. <see cref="MatchActorLifeState.OutOfRound"/> is not dead.</summary>
    public static bool IsDead(MatchActorLifeState state) =>
        state == MatchActorLifeState.Dead;

    /// <summary>Proven/carried death by the round-local sets (no body lookup needed).</summary>
    public static bool IsDead(MatchFlowState flow, byte actorNr) =>
        flow.DeadActors.Contains(actorNr) || flow.CarriedDeadActors.Contains(actorNr);

    /// <summary>
    /// C2=31: start the 5 s spawn clock for every live roster fighter that still has no body.
    /// Carried-dead fighters are already dead and are handled by
    /// <see cref="MatchFlowRules.SeedCarriedDeathsWithoutPawn"/>; a fighter who already has a
    /// body needs no clock.
    /// </summary>
    public static void ArmSpawnDeadlines(
        MatchFlowState flow,
        IReadOnlySet<byte> pawnOwners,
        DateTime nowUtc)
    {
        foreach (var actor in flow.RoundRoster.Keys)
        {
            if (flow.DeadActors.Contains(actor) || flow.CarriedDeadActors.Contains(actor))
                continue;
            if (pawnOwners.Contains(actor))
                continue;
            flow.SpawnDeadlines.TryAdd(actor, nowUtc + SpawnGrace);
        }
    }

    /// <summary>
    /// Tick: a live fighter who still has no body when his 5 s grace expires is not in this
    /// round. Mark him dead so the wipe counts the side correctly. A body that arrived in the
    /// meantime simply clears the clock.
    /// </summary>
    /// <returns>Actors that just became dead (empty when nothing expired).</returns>
    public static List<byte> ProcessSpawnDeadlines(
        MatchFlowState flow,
        IReadOnlySet<byte> pawnOwners,
        DateTime nowUtc)
    {
        var expired = new List<byte>();
        if (flow.SpawnDeadlines.Count == 0)
            return expired;

        foreach (var (actor, deadline) in flow.SpawnDeadlines.ToList())
        {
            if (pawnOwners.Contains(actor))
            {
                flow.SpawnDeadlines.Remove(actor);
                continue;
            }
            if (nowUtc < deadline)
                continue;
            flow.SpawnDeadlines.Remove(actor);
            // A live fighter who never produced a body by the deadline is a man down, not a
            // kill: DeadActors drives the wipe, the cumulative death counter stays untouched.
            flow.DeadActors.Add(actor);
            expired.Add(actor);
        }
        return expired;
    }

    /// <summary>Body confirmed or actor removed: no spawn clock may outlive either event.</summary>
    public static void ClearSpawnDeadline(MatchFlowState flow, byte actorNr) =>
        flow.SpawnDeadlines.Remove(actorNr);
}
