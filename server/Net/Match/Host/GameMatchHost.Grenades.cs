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
/// <para>
/// Live-test follow-ups: build 1 (host-minted counter, <c>tv = stime/1000</c>), build 2
/// (client id + thrower-clock tv, but rpc byte and payload actor still 1) and build 3
/// (thrower-signed rpc byte, but the varint id still based on actor 1 and payload[0] set
/// to the actor) were all silently ignored by the live client. The decompiled
/// <c>GrenadeManager</c> explained the silence: the detonation handler looks the grenade
/// up by its full network id <c>(throwerActor &lt;&lt; 24) | clientLowId</c> and an unknown
/// id is just dropped from the active list — no error. Everything the frame carries
/// relative to the throw is now taken from the throw (24/24 capture pairs + live logs +
/// the client code): the varint is the client's <b>composite id</b>, the rpc author byte
/// is the <b>thrower's actor</b> (the client passes the initiator as a separate Rpc
/// argument), the payload is the serialized <c>GrenadeDetonateData</c> with
/// <c>HasInitiator=true</c> and count-1 arrays, and the timeValue stays on the
/// <b>thrower's</b> clock (<c>throw.tv + fuse</c>).
/// </para>
/// <para>
/// Fuse rules are the game dev's own («осколочная и флешка — через 1,7 с после создания
/// гранаты в мире; дымовая — при остановке или через 10 с; молотов и зажигательная — при
/// первом контакте с полом или через 3 с»), cross-checked against the 24 captured pairs;
/// see <see cref="MatchGrenadeWire.DetonationModeFor"/>. The detonation <b>position</b>
/// remains a map-less approximation (median ~7.5 m vs captures): every client simulates the
/// flight itself and only uses our point for sound/VFX, so the residue moves an effect,
/// it cannot break a grenade.
/// </para>
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
                out var grenadeId,
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

        MatchGrenadeBallistics.ResolveDetonation(
            posX, posY, posZ,
            dirX, dirY, dirZ,
            MatchGrenadeWire.DetonationModeFor(itemId),
            MatchGrenadeWire.DetonationCapSeconds(itemId),
            out var afterSeconds,
            out var ex, out var ey, out var ez);

        lock (_roomGate)
        {
            room.PendingGrenades.Add(new PendingGrenade(
                itemId, throwerActorNr, grenadeId,
                DateTime.UtcNow + TimeSpan.FromSeconds(afterSeconds),
                // Phone host: explode tv = throw tv + fuse (same device clock, 24/24 pairs
                // within 2 ms). Our stime is machine-boot ms — a different base entirely, and
                // the live test showed the client ignores frames stamped on it.
                parsed.TimeValue + afterSeconds,
                ex, ey, ez));
        }

        Console.WriteLine(
            $"[grenade] throw item=0x{itemId:X} actor={throwerActorNr} grenadeId={grenadeId} " +
            $"pos=({posX:0.##},{posY:0.##},{posZ:0.##}) dir=({dirX:0.##},{dirY:0.##},{dirZ:0.##}) " +
            $"throwTv={parsed.TimeValue:0.###} " +
            $"→ взрыв через {afterSeconds:0.###}с в ({ex:0.##},{ey:0.##},{ez:0.##}) " +
            "(host-authored field=2, как на телефон-хосте)");
        // Полный hex payload броска: [0] = id гранаты от клиента, остальное — на разбор
        // следующего live-лога, если взрыв всё ещё будет игнорироваться.
        Console.WriteLine($"[grenade] throw payload hex={Convert.ToHexString(parsed.Payload)}");
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
                stime, g.GrenadeId, g.ThrowerActorNr, g.DetonateTimeValue,
                g.ExplodeX, g.ExplodeY, g.ExplodeZ);
            // Critical path: an explode dropped to backpressure is a grenade that never pops
            // on that peer — the exact #53 failure class, and the same reason plant/defuse
            // frames ride the critical path (#33).
            var n = BroadcastInitReadyCritical(room, pkt, tag: "match_tx_WorldObjectRpc");
            Console.WriteLine(
                $"[grenade] explode item=0x{g.ItemId:X} actor={g.ThrowerActorNr} " +
                $"grenadeId={g.GrenadeId} pos=({g.ExplodeX:0.##},{g.ExplodeY:0.##},{g.ExplodeZ:0.##}) " +
                $"tv={g.DetonateTimeValue:0.###} → peers={n} (GrenadeManager field=2, host-authored)");
            // Полный hex отправленного кадра: если клиент снова молча проигнорирует взрыв,
            // следующий лог сразу даст дамп для побайтового разбора (captures обычно off).
            Console.WriteLine($"[grenade] explode frame hex={Convert.ToHexString(pkt)}");
        }
    }
}
