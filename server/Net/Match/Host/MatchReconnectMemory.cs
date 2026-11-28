using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Net.Match.Host;

/// <summary>
/// Match state kept for a player whose socket dropped: the team to fall back to and the
/// combat/economy counters he had at that moment.
/// </summary>
/// <remarks>
/// <para>
/// The room deletes an actor together with all of its <c>ActorProps</c> when the socket
/// closes, so a reconnect that gets a fresh actor number would otherwise start the
/// scoreboard from zero. The memory survives the disconnect inside the same match and is
/// cleared on match teardown, exactly like <c>_reconnectMemory</c>'s team half did before.
/// </para>
/// <para>
/// Only counters are restored here. The team is a fallback for a silent client: if the
/// returning player picks a team himself, the host never overwrites that choice.
/// </para>
/// </remarks>
internal sealed class MatchReconnectMemory
{
    public required MatchRoom Room { get; init; }

    /// <summary>Last fighting team, or <see cref="MatchTeam.None"/> for a spectator.</summary>
    public MatchTeam Team { get; init; } = MatchTeam.None;

    /// <summary>
    /// The player was dead (or carried-dead) when the socket dropped. The room deletes
    /// <c>DeadActors</c>/<c>CarriedDeadActors</c> with the actor, so without this flag a
    /// returning player would be «alive» for the wipe even though his own client shows the
    /// corpse and will not send a CWO until the next round.
    /// </summary>
    public bool WasDead { get; init; }

    // -1 means "the room never had this prop" — do not invent a zero on restore.
    public int Kills { get; init; } = -1;
    public int FairKills { get; init; } = -1;
    public int Death { get; init; } = -1;
    public int Score2 { get; init; } = -1;
    public int Assists { get; init; } = -1;
    public int RoundKills { get; init; } = -1;
    public int Money { get; init; } = -1;

    public bool HasFightingTeam => Team is MatchTeam.Tr or MatchTeam.Ct;

    /// <summary>Read the counters that still live in the room, before the actor is deleted.</summary>
    public static MatchReconnectMemory Capture(MatchRoom room, byte actorNr, MatchTeam team) => new()
    {
        Room = room,
        Team = team,
        WasDead = MatchLifeRules.IsDead(room.Flow, actorNr),
        Kills = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.Kills),
        FairKills = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.FairKills),
        Death = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.Death),
        Score2 = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.Score2),
        Assists = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.Assists),
        RoundKills = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.RoundKills),
        Money = GameMatchHost.ReadActorIntProp(room, actorNr, MatchRoomPropKeys.Money),
    };

    /// <summary>
    /// Write the remembered counters onto <paramref name="actorNr"/> and return the same bag
    /// so peers can be told immediately (the scoreboard is built from actor props).
    /// Caller must hold the room gate.
    /// </summary>
    public IReadOnlyList<(string Key, LobbyVariant Value)> RestoreInto(MatchRoom room, byte actorNr)
    {
        var props = new List<(string Key, LobbyVariant Value)>(7);
        Put(props, room, actorNr, MatchRoomPropKeys.Kills, Kills);
        Put(props, room, actorNr, MatchRoomPropKeys.FairKills, FairKills);
        Put(props, room, actorNr, MatchRoomPropKeys.Death, Death);
        Put(props, room, actorNr, MatchRoomPropKeys.Score2, Score2);
        Put(props, room, actorNr, MatchRoomPropKeys.Assists, Assists);
        Put(props, room, actorNr, MatchRoomPropKeys.RoundKills, RoundKills);
        Put(props, room, actorNr, MatchRoomPropKeys.Money, Money);
        if (WasDead)
        {
            // Carry the death through the actor deletion. CWO (NoteSpawn) clears both sets,
            // so a returning player who really spawns is alive again; a client that stays on
            // the corpse remains dead for the wipe.
            room.Flow.DeadActors.Add(actorNr);
            room.Flow.CarriedDeadActors.Add(actorNr);
        }
        return props;
    }

    private static void Put(
        List<(string Key, LobbyVariant Value)> props,
        MatchRoom room,
        byte actorNr,
        string key,
        int value)
    {
        if (value < 0)
            return;
        var variant = LobbyVariant.FromInt(value);
        room.ActorProps[(actorNr, key)] = variant;
        props.Add((key, variant));
    }

    /// <summary>
    /// A fresh client starts its counters at zero and may author that zero right after the
    /// reconnect. Once the match state was carried over, a decrease of a cumulative combat
    /// counter is stale by definition — the host keeps the remembered value.
    /// </summary>
    /// <remarks>
    /// Only the actor's own writes are filtered; the synthetic host actor still authors
    /// deaths for everyone. Money is deliberately not in this set: spending during the buy
    /// phase is a legitimate decrease and must stay client-owned.
    /// </remarks>
    public static bool RejectsStaleCounter(
        bool statsCarried,
        byte senderActorNr,
        byte propActorNr,
        string key,
        bool hadPrevious,
        int previous,
        int incoming)
    {
        if (!statsCarried || !hadPrevious)
            return false;
        if (senderActorNr != propActorNr)
            return false;
        if (!MatchPropAuthority.IsCombatOutcomeKey(key))
            return false;
        return incoming < previous;
    }
}
