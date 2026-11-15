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
/// <c>[0]=slot? [1]=00 [2]=00 [3]=01 [4]=itemId [5..11]=00×7 [12..23]=origin XYZ
/// [24..35]=direction XYZ [36]=flag? [37..39]=00×3 [40..43]=float? [44]=01</c>.
/// Bytes <c>[0]</c>, <c>[36]</c> and <c>[40..43]</c> vary per throw without a recovered
/// meaning (slot / charge?) — they are <b>not</b> interpreted here, and the throw itself is
/// relayed verbatim. Only <c>itemId</c> + pose are read.
/// </para>
/// <para>
/// Explode payload (24 B, identical shape in all 24 samples):
/// <c>01 01 00 &lt;varint 0x01000000|ordinal&gt; 01 00 &lt;pos XYZ&gt; 01 00 00</c>.
/// The varint (<c>81 80 80 08</c> for ordinal 1) restarts at 1 across rounds in the gold
/// set — treated as a per-round explode ordinal. The position is the phone host's own
/// physics result; the dedicated host cannot replicate the map collision, so
/// <see cref="MatchGrenadeBallistics"/> approximates it (see the fitting notes there).
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

    /// <summary>Author byte of the explode Rpc — 1 in all 24 samples (host actor).</summary>
    public const byte ExplodeRpcId = 1;

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
    /// Explode varint base — every observed counter is <c>0x01000000 | ordinal</c>
    /// (<c>81 80 80 08</c> = 0x01000001 for the first grenade of a round).
    /// </summary>
    public const int ExplodeCounterBase = 0x0100_0000;

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
        out byte itemId,
        out float posX, out float posY, out float posZ,
        out float dirX, out float dirY, out float dirZ)
    {
        itemId = 0;
        posX = posY = posZ = 0f;
        dirX = dirY = dirZ = 0f;
        if (payload.Length != ThrowPayloadLength)
            return false;

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
    /// Build the 24-byte explode payload — byte-identical to the phone-host frames
    /// (<c>grenades.zip</c> flash: <c>010100818080080100…010000</c> and 23 more samples).
    /// </summary>
    public static byte[] BuildExplodePayload(int ordinal, float posX, float posY, float posZ)
    {
        var w = new LobbyWriter();
        w.WriteByte(1);
        w.WriteByte(1);
        w.WriteByte(0);
        w.WriteVarInt(ExplodeCounterBase | ordinal);
        w.WriteByte(1);
        w.WriteByte(0);
        w.WriteFloat(posX);
        w.WriteFloat(posY);
        w.WriteFloat(posZ);
        w.WriteByte(1);
        w.WriteByte(0);
        w.WriteByte(0);
        return w.ToArray();
    }

    /// <summary>
    /// Host-authored GrenadeManager explode frame: <c>WorldObjectRpc id=5 rpc=1 gaa=2 field=2</c>
    /// with the 24-byte payload. Same envelope as every host operation
    /// (<see cref="MatchCodec.BuildWorldObjectRpc"/>), so the phone-host shape is reproduced
    /// through the regular codec path — not by replaying a captured blob.
    /// </summary>
    public static byte[] BuildGrenadeExplodeRpc(
        int serverTime,
        int ordinal,
        float posX, float posY, float posZ) =>
        MatchCodec.BuildWorldObjectRpc(
            serverTime,
            objectId: GrenadeManagerObjectId,
            rpcId: ExplodeRpcId,
            gaaTarget: ExplodeGaaTarget,
            field: ExplodeField,
            timeValue: serverTime / 1000.0,
            payload: BuildExplodePayload(ordinal, posX, posY, posZ));

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
