using System.Buffers.Binary;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// GrenadeManager wire shapes — issue #53 («гранаты не работают»).
/// <para>
/// Evidence: <c>captures/grenades.zip</c> (session <c>20261115_0821..0824</c>, one round,
/// 4 throws: flash/HE/smoke/molotov, announced in chat right before each throw) plus the
/// gold set <c>captures/captures.zip</c> (<c>20261112_0351..0404</c>, 20 more throws). A
/// grenade never becomes a world object: the thrower's client sends a
/// <see cref="MatchOpcode.WorldObjectRpc"/> on <b>GrenadeManager</b> (scene id=5) with
/// <c>field=1 gaa=AllViaServer(4)</c>; every client then simulates the flight locally
/// (that is why the flight is already visible on the dedicated host — the relay of
/// <c>field=1</c> is enough). The <b>explosion</b> is a second Rpc, <c>field=2
/// gaa=AllCached(2)</c>, that only the phone host ever put on the wire — clients never send
/// it — so on a dedicated host the grenade landed and nothing happened. This module lets the
/// dedicated host author that second frame.
/// </para>
/// <para>
/// Throw payload (45 B, all 24 observed samples byte-aligned):
/// <c>[0]=grenadeId [1]=00 [2]=00 [3]=throwerActor [4]=itemId [5..11]=00×7 [12..23]=origin
/// XYZ [24..35]=direction XYZ [36]=flag? [37..39]=00×3 [40..43]=float? [44]=throwerActor</c>.
/// <c>[0]</c> is the <b>client-assigned grenade id</b> (low byte): the client keys the
/// grenade by <c>(throwerActor &lt;&lt; 24) | [0]</c> (decompiled <c>GrenadeManager</c>; the
/// composite is what the explode varint echoes back in all 24 pairs), and it is <b>not</b> a
/// throw counter — gold has back-to-back throws both using id=1 (client-side reset, likely
/// per life) — so the host must reuse the client's id, never mint its own. <c>[3]</c> and
/// <c>[44]</c> are the <b>thrower's actor nr</b>: captures show 01 (the thrower was always
/// actor 1 there), the live dedicated-host sessions show 02/03 while actors 2/3 threw
/// (latest-2/latest-3.log). Bytes <c>[36]</c> and <c>[40..43]</c> vary per throw without a
/// recovered meaning — they are <b>not</b> interpreted here, and the throw itself is
/// relayed verbatim.
/// </para>
/// <para>
/// Explode payload (24 B, identical shape in all 24 samples) — the serialized
/// <c>GrenadeDetonateData</c> of the decompiled client for one grenade:
/// <c>01 (HasInitiator) 0100 (Ids.Count u16) &lt;varint (throwerActor&lt;&lt;24)|grenadeId&gt;
/// 0100 (Positions.Count u16) &lt;pos XYZ&gt; 0100 (AlternativeZones.Count u16) 00 (false)</c>.
/// The initiator itself travels in the frame's <b>rpc author byte</b>, not in the payload.
/// The frame's <c>timeValue</c> stays on the <b>thrower's clock</b>: the phone host's explode
/// tv equals the throw tv + fuse to the millisecond (same device clock); a dedicated host
/// must reproduce that relation (<see cref="BuildGrenadeExplodeRpc"/>) instead of minting
/// tv from its own uptime — the client clock and <c>Environment.TickCount</c> are unrelated
/// bases, and a live test showed the client silently ignore such a frame. The position is
/// the master client's own physics result; the dedicated host cannot replicate the map
/// collision, so <see cref="MatchGrenadeBallistics"/> approximates it.
/// </para>
/// </summary>
public static class MatchGrenadeWire
{
    /// <summary>GrenadeManager scene object id — our bootstrap catalog and phone gold both use id=5.</summary>
    public const short GrenadeManagerObjectId = 5;

    /// <summary>WorldObjectRpc <c>field</c> of the client-authored throw (gaa=4 AllViaServer).</summary>
    public const short ThrowField = 1;

    /// <summary>WorldObjectRpc <c>field</c> of the host-authored explosion (gaa=2 AllCached).</summary>
    public const short ExplodeField = 2;

    /// <summary>gaa of the explode Rpc — AllCached(2) in all 24 samples.</summary>
    public const byte ExplodeGaaTarget = 2;

    /// <summary>HE grenade item id (<c>0x5B</c>=91; Duel OnlyGrenades loadout uses it too).</summary>
    public const byte ItemHe = 0x5B;

    /// <summary>Smoke grenade item id (<c>0x5C</c>=92).</summary>
    public const byte ItemSmoke = 0x5C;

    /// <summary>Flash grenade item id (<c>0x5D</c>=93).</summary>
    public const byte ItemFlash = 0x5D;

    /// <summary>Molotov item id (<c>0x5E</c>=94).</summary>
    public const byte ItemMolotov = 0x5E;

    /// <summary>
    /// Full network id of a grenade: <c>(throwerActorNr &lt;&lt; 24) | grenadeId</c>. The client
    /// (decompiled <c>GrenadeManager</c>) keys its <c>_droppedGrenades</c> by this composite —
    /// the low byte comes from the throw payload <c>[0]</c> and resets client-side, so the
    /// actor prefix is what keeps ids unique — and the detonation handler looks the grenade
    /// up by exactly this int. A frame carrying any other id is dropped <b>silently</b>
    /// (<c>ContainsKey</c> fails, the id is merely removed from the active list — three live
    /// tests failed this way without a single client error). All 24 captured pairs show
    /// <c>0x01000000 | id</c> because the thrower was always actor 1 there.
    /// </summary>
    public static int GrenadeNetworkId(byte throwerActorNr, byte grenadeId)
        => (throwerActorNr << 24) | grenadeId;

    /// <summary>Observed throw payload length — every one of the 24 samples is exactly 45 B.</summary>
    public const int ThrowPayloadLength = 45;

    /// <summary>
    /// Fixed fuse for flash / HE — the timed grenades. 16 matched throw→explode pairs give
    /// 1.708–1.712 s (grenades.zip: 1.709/1.712; gold: 1.708–1.710), i.e. a flat ~1.71 s
    /// timer that expires wherever the grenade happens to be.
    /// </summary>
    public const double TimedFuseSeconds = 1.71;

    /// <summary>
    /// Smoke fuse. Only two matched samples exist (2.009 s / 3.071 s); the midpoint is used as
    /// a flat timer — двух пар мало, чтобы выдумывать модель «ждать приземления».
    /// </summary>
    public const double SmokeFuseSeconds = 2.5;

    /// <summary>
    /// Safety cap for impact-detonated grenades (molotov). Observed molotov fuses span
    /// 0.28–1.64 s (first ground contact); the cap only guards a throw that never reaches
    /// the modelled floor (e.g. thrown straight up).
    /// </summary>
    public const double ImpactFuseCapSeconds = 2.0;

    /// <summary>
    /// Parse the 45-byte throw payload. Returns false for any other length — the layout is
    /// only known from the 45-byte samples, so an unknown shape is left unimplemented
    /// (logged by the caller), never guessed.
    /// </summary>
    public static bool TryParseThrowPayload(
        ReadOnlySpan<byte> payload,
        out byte grenadeId,
        out byte itemId,
        out float posX, out float posY, out float posZ,
        out float dirX, out float dirY, out float dirZ)
    {
        grenadeId = 0;
        itemId = 0;
        posX = posY = posZ = 0f;
        dirX = dirY = dirZ = 0f;
        if (payload.Length != ThrowPayloadLength)
            return false;

        grenadeId = payload[0];
        itemId = payload[4];
        posX = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(12, 4));
        posY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(16, 4));
        posZ = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(20, 4));
        dirX = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(24, 4));
        dirY = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(28, 4));
        dirZ = BinaryPrimitives.ReadSingleLittleEndian(payload.Slice(32, 4));
        return true;
    }

    /// <summary>
    /// Build the 24-byte explode payload — the serialized <c>GrenadeDetonateData</c> from the
    /// decompiled client (<c>Ids</c>, <c>Positions</c>, <c>AlternativeZones</c>,
    /// <c>HasInitiator</c>), byte-identical to the phone-host frames for one grenade:
    /// <c>01</c> HasInitiator=true, <c>0100</c> Ids.Count (u16 LE), varint
    /// <see cref="GrenadeNetworkId">full id</see>, <c>0100</c> Positions.Count, position XYZ,
    /// <c>0100</c> AlternativeZones.Count, <c>00</c> InAlternativeZone=false. The master
    /// client groups same-tick grenades of one thrower into a single frame — counts &gt; 1
    /// never appear in the captures and are not built here.
    /// </summary>
    public static byte[] BuildExplodePayload(
        byte grenadeId, byte throwerActorNr, float posX, float posY, float posZ)
    {
        var w = new LobbyWriter();
        w.WriteBool(true);                      // HasInitiator — the initiator is known
        w.WriteInt16(1);                        // Ids.Count
        w.WriteVarInt(GrenadeNetworkId(throwerActorNr, grenadeId));
        w.WriteInt16(1);                        // Positions.Count
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteInt16(1);                        // AlternativeZones.Count
        w.WriteBool(false);                     // InAlternativeZone
        return w.ToArray();
    }

    /// <summary>
    /// Host-authored GrenadeManager explode frame: <c>WorldObjectRpc id=5 gaa=2 field=2</c>
    /// with the 24-byte payload. On the phone the <b>master client</b> authored this frame
    /// (<c>GrenadeManager.SendDetonationRPCs</c> — the fuse runs client-side and only
    /// <c>IsMasterClient</c> sends); a dedicated host has no Unity client, so the server
    /// stands in for it. Two fields come from the throw, never from the host's own state:
    /// <list type="bullet">
    /// <item>the <b>rpc author byte</b> is the thrower's actor nr — the client passes the
    /// initiator as a separate Rpc argument (<c>CallDetonateViaServer(initiator, …)</c>),
    /// which lands in the frame's author byte. Captures show 1 only because actor 1 threw
    /// there (same author-subject rule as the phone host's WeaponDropManager frames
    /// rpc=2/3/4);</item>
    /// <item>the payload's varint id is the <see cref="GrenadeNetworkId">full composite
    /// id</see> — the client's lookup key, silently dropped on mismatch.</item>
    /// </list>
    /// <paramref name="timeValue"/> must stay on the thrower's clock — pass
    /// <c>throw.TimeValue + fuse</c> (phone host: 24/24 explode tv = throw tv + fuse
    /// within 2 ms; on the phone they shared one device clock, a dedicated host does
    /// not). The envelope's <paramref name="serverTime"/> stays ours, exactly like every
    /// other host frame the clients already accept (relay echoes carry our stime with the
    /// client's original tv — same combination, and the throw echo works).
    /// </summary>
    public static byte[] BuildGrenadeExplodeRpc(
        int serverTime,
        byte grenadeId,
        byte throwerActorNr,
        double timeValue,
        float posX, float posY, float posZ) =>
        MatchCodec.BuildWorldObjectRpc(
            serverTime,
            objectId: GrenadeManagerObjectId,
            rpcId: throwerActorNr,
            gaaTarget: ExplodeGaaTarget,
            field: ExplodeField,
            timeValue: timeValue,
            payload: BuildExplodePayload(grenadeId, throwerActorNr, posX, posY, posZ));

    /// <summary>
    /// Fuse policy per item id. Timed items (flash, HE, unknown) detonate on the flat timer;
    /// the molotov detonates on the first modelled ground contact (capped). Smoke keeps its
    /// own flat timer (see <see cref="SmokeFuseSeconds"/>).
    /// </summary>
    public static bool DetonatesOnImpact(byte itemId) => itemId == ItemMolotov;

    /// <summary>Fuse seconds for the given item. Unknown ids get the timed default.</summary>
    public static double FuseSecondsFor(byte itemId) => itemId switch
    {
        ItemSmoke => SmokeFuseSeconds,
        _ => TimedFuseSeconds,
    };
}

/// <summary>
/// Approximate grenade trajectory for the host-authored explode position. The phone host
/// computed this with the real game physics (gravity + map collision); a dedicated host has
/// no map, so this is a fitted ballistic model: fixed throw speed, Unity gravity, one ground
/// plane estimated from the throw origin, bounces with restitution. Fitted against all 24
/// matched throw→explode pairs from <c>captures/grenades.zip</c> + <c>captures/captures.zip</c>
/// (grid search over speed/gravity/restitution/friction; mean position error ~8.7 m — the
/// residue is wall/terrain bounces the model cannot see). The wire <b>format</b> is exact;
/// only this position value is an approximation, and the client renders its own flight
/// either way.
/// </summary>
public static class MatchGrenadeBallistics
{
    /// <summary>Throw speed along the payload direction (m/s) — fitted (best grid point 11).</summary>
    public const float Speed = 11f;

    /// <summary>Gravity (m/s²) — Unity default also fit best.</summary>
    public const float Gravity = 9.81f;

    /// <summary>Vertical restitution on the modelled floor — fitted.</summary>
    public const float FloorRestitution = 0.55f;

    /// <summary>Horizontal friction applied on each floor bounce — fitted.</summary>
    public const float FloorFriction = 0.55f;

    /// <summary>
    /// Ground offset below the throw origin: phone-host throw origins sit ~1.5 m above the
    /// local floor (gold flat-area throws are all Y=1.50 with explosions at Y≈0.06–0.22).
    /// </summary>
    public const float FloorDropMeters = 1.5f;

    /// <summary>Fixed simulation step — deterministic, so tests can assert exact positions.</summary>
    public const double StepSeconds = 1.0 / 120.0;

    /// <summary>
    /// Simulate the throw and resolve where/when it detonates.
    /// Timed grenades run to <paramref name="fuseSeconds"/>; impact grenades
    /// (<paramref name="impactDetonation"/>) stop at the first floor contact (capped by the
    /// caller at <see cref="MatchGrenadeWire.ImpactFuseCapSeconds"/>).
    /// </summary>
    public static void ResolveDetonation(
        float posX, float posY, float posZ,
        float dirX, float dirY, float dirZ,
        bool impactDetonation,
        double fuseSeconds,
        out double detonateAfterSeconds,
        out float outX, out float outY, out float outZ)
    {
        var x = posX;
        var y = posY;
        var z = posZ;
        var vx = dirX * Speed;
        var vy = dirY * Speed;
        var vz = dirZ * Speed;
        var floorY = posY - FloorDropMeters;

        var t = 0.0;
        while (t < fuseSeconds)
        {
            var step = Math.Min(StepSeconds, fuseSeconds - t);
            vy -= Gravity * (float)step;
            x += vx * (float)step;
            y += vy * (float)step;
            z += vz * (float)step;
            t += step;

            if (y > floorY)
                continue;

            y = floorY;
            if (impactDetonation && t > MinImpactSeconds)
                break;
            vy = -vy * FloorRestitution;
            vx *= FloorFriction;
            vz *= FloorFriction;
        }

        detonateAfterSeconds = t;
        outX = x;
        outY = y;
        outZ = z;
    }

    /// <summary>Guard so a spawn already under the modelled floor still flies a moment.</summary>
    public const double MinImpactSeconds = 0.05;
}
