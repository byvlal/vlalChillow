using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Issue #53 — host-authored grenade detonation. On a phone host the game server itself
/// runs the grenade fuse and broadcasts the <c>GrenadeManager field=2</c> Rpc; phone clients
/// never send it. The dedicated host used to relay only the client's <c>field=1</c> throw
/// (hence «все видят куда и как летит») and never authored the explosion — so every grenade
/// landed and silently did nothing. This partial schedules the detonation when a throw is
/// observed and fans the explode frame out when the fuse expires.
/// </summary>
public sealed partial class GameMatchHost
{
    /// <summary>
    /// Observe a client-authored GrenadeManager <c>field=1</c> throw: parse the payload,
    /// resolve the detonation, park it on the room. The throw itself keeps flowing through
    /// the regular relay path — this only arms the host-authored answer.
    /// </summary>
    private void ScheduleGrenadeExplode(
        MatchRoom room, MatchCodec.ParsedWorldObjectRpc parsed, byte throwerActorNr)
    {
        if (!MatchGrenadeWire.TryParseThrowPayload(
                parsed.Payload,
                out var itemId,
                out var posX, out var posY, out var posZ,
                out var dirX, out var dirY, out var dirZ))
        {
            // Unknown shape — repo rules: log it, do not guess a layout.
            Console.WriteLine(
                $"[grenade] throw from actor={throwerActorNr} payloadLen={parsed.Payload.Length} " +
                "— не 45 байт, раскладка неизвестна, взрыв не запланирован (релей броска не тронут)");
            return;
        }

        var impact = MatchGrenadeWire.DetonatesOnImpact(itemId);
        var fuse = impact
            ? MatchGrenadeWire.ImpactFuseCapSeconds
            : MatchGrenadeWire.FuseSecondsFor(itemId);
        MatchGrenadeBallistics.ResolveDetonation(
            posX, posY, posZ,
            dirX, dirY, dirZ,
            impactDetonation: impact,
            fuseSeconds: fuse,
            out var afterSeconds,
            out var ex, out var ey, out var ez);

        int ordinal;
        lock (_roomGate)
        {
            // Explode ordinal restarts each round (gold counters 0x01000001+ reset across
            // round boundaries). Allocated at throw time so it matches the phone-host
            // order (k-th grenade of the round → k).
            if (room.GrenadeCounterRoundIndex != room.Flow.RoundIndex)
            {
                room.GrenadeCounterRoundIndex = room.Flow.RoundIndex;
                room.GrenadeExplodeCounter = 0;
            }
            ordinal = ++room.GrenadeExplodeCounter;
            room.PendingGrenades.Add(new PendingGrenade(
                itemId, throwerActorNr, ordinal,
                DateTime.UtcNow + TimeSpan.FromSeconds(afterSeconds),
                ex, ey, ez));
        }

        Console.WriteLine(
            $"[grenade] throw item=0x{itemId:X} actor={throwerActorNr} " +
            $"pos=({posX:0.##},{posY:0.##},{posZ:0.##}) dir=({dirX:0.##},{dirY:0.##},{dirZ:0.##}) " +
            $"→ взрыв через {afterSeconds:0.###}с в ({ex:0.##},{ey:0.##},{ez:0.##}) ordinal={ordinal} " +
            "(host-authored field=2, как на телефон-хосте)");
    }

    /// <summary>
    /// Sweep one room for expired fuses and fan the explode frames out. Called from
    /// <c>TickMatchFlow</c> every host tick (see <c>GameMatchHost.Ranked2v2Phases.cs</c>);
    /// one pass over the pending list, no per-packet work while nothing is due.
    /// </summary>
    private void TickGrenadeFusesRoom(MatchRoom room)
    {
        List<PendingGrenade> due;
        lock (_roomGate)
        {
            if (room.PendingGrenades.Count == 0)
                return;
            var now = DateTime.UtcNow;
            due = room.PendingGrenades.Where(g => g.DetonateAtUtc <= now).ToList();
            if (due.Count == 0)
                return;
            foreach (var g in due)
                room.PendingGrenades.Remove(g);
        }

        foreach (var g in due)
        {
            var stime = NextServerTime();
            var pkt = MatchGrenadeWire.BuildGrenadeExplodeRpc(
                stime, g.Ordinal, g.ExplodeX, g.ExplodeY, g.ExplodeZ);
            // Critical path: an explode dropped to backpressure is a grenade that never pops
            // on that peer — the exact #53 failure class, and the same reason plant/defuse
            // frames ride the critical path (#33).
            var n = BroadcastInitReadyCritical(room, pkt, tag: "match_tx_WorldObjectRpc");
            Console.WriteLine(
                $"[grenade] explode item=0x{g.ItemId:X} actor={g.ThrowerActorNr} " +
                $"pos=({g.ExplodeX:0.##},{g.ExplodeY:0.##},{g.ExplodeZ:0.##}) ordinal={g.Ordinal} " +
                $"→ peers={n} (GrenadeManager field=2, host-authored)");
        }
    }
}
