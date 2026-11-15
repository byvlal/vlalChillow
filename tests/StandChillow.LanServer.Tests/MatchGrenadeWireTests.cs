using System.Buffers.Binary;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #53 («гранаты не работают»): на выделенном хосте гранаты летели (релей броска),
/// но не взрывались — <c>field=2</c> на проводе авторит только телефон-хост, клиенты его
/// никогда не шлют. Здесь зафиксирована провода гранат по живым капчурам
/// <c>captures/grenades.zip</c> (сессия 2026-11-15, один раунд, 4 броска — флеш/ХЕ/смок/
/// молотов, каждый анонсирован в чате прямо перед броском) и поведенческие инварианты
/// хост-авторского взрыва.
/// </summary>
/// <remarks>
/// Общий вид кадров (тождественен обеим сессиям, 24 броска + 24 взрыва):
/// <c>04 cb &lt;i32 stime&gt; 00000000 0500 &lt;rpc=01&gt; &lt;gaa&gt; &lt;field&gt; &lt;f64 timeValue&gt; &lt;payload&gt;</c>.
/// Бросок: <c>gaa=4 field=1</c>, payload 45 байт. Взрыв: <c>gaa=2 field=2</c>, payload 24 байта
/// <c>010100 &lt;varint 0x01000000|grenadeId&gt; 0100 &lt;pos XYZ&gt; 010000</c>.
/// <para>
/// Два правила, доказанные всеми 24 парами и live-тестом (первая сборка их нарушала —
/// клиент молча игнорировал кадр):
/// <list type="bullet">
/// <item><c>grenadeId</c> — это байт <c>[0]</c> payload броска, id гранаты, назначенный
/// КЛИЕНТОМ (в золоте бывают два броска подряд с id=1 — сброс на стороне клиента), а не
/// счётчик хоста;</item>
/// <item><c>timeValue</c> взрыва живёт на часах бросавшего: <c>tv броска + фит</c> (на
/// телефон-хосте это одни часы; наш <c>NextServerTime</c> — мс с загрузки машины, другое
/// основание).</item>
/// </list>
/// Позиция взрыва у телефон-хоста — результат его собственной физики с картой;
/// выделенный хост карты не имеет, поэтому позиция — аппроксимация
/// (<see cref="MatchGrenadeBallistics"/>), а формат и тайминги — точные.
/// </para>
/// </remarks>
public sealed class MatchGrenadeWireTests
{
    private const int HostHeaderLen = 10; // flags + opcode + i32 serverTime + i32 reserved
    private const int RpcBodyHeaderLen = 14; // i16 id + u8 rpc + u8 gaa + i16 field + f64 time

    // Полные кадры взрыва из grenades.zip (телефон-хост → проба), как есть в файлах.
    private const string ExplodeFrameFlash =
        "04CBF5BB40000000000005000102020079E926316892B040010100818080080100BE4BC541D105CCC126FD4FC2010000";
    private const string ExplodeFrameHe =
        "04CB4EDD400000000000050001020200CBA145B6F39AB0400101008280800801005D14B4419B35E2C1C2F6E5C1010000";
    private const string ExplodeFrameSmoke =
        "04CB82FF40000000000005000102020091ED7C3FB5A3B040010100838080080100B15A184240D2E9C11EC81BC2010000";
    private const string ExplodeFrameMolotov =
        "04CB93184100000000000500010202001D5A643B1FAAB040010100848080080100A5511F4209A7F0C1B6006AC2010000";
    // Золотой молотов (второй бросок золотой сессии, grenadesId сброшен клиентом в 1):
    // взрыв ссылается на id=1, хотя это не первый бросок сессии.
    private const string ExplodeFrameGoldMolotov =
        "04CB3C45A300000000000500010202006F1283C00AE6C440010100818080080100FE8F1D41A88C743D87DD12C0010000";

    // Payload броска (45 байт) тех же бросков.
    private const string ThrowPayloadFlash =
        "010000015D000000000000001852BA41A4B8D6C129AEF9C192D69D3DE7F4DC3E6C8267BF010000000000000001";
    private const string ThrowPayloadHe =
        "020000015B00000000000000FBC1C641A5F0D9C1A93D09C2E861F4BDA1A0F5BE6BFF5F3F010000000000000001";
    private const string ThrowPayloadSmoke =
        "030000015C00000000000000F3CDF941EC57CFC1EA7622C2105E6B3F3A6AF23E3E0C9A3E00000000BBF29C4001";
    private const string ThrowPayloadMolotov =
        "040000015E000000000000009A12FA41FA6CDBC180231EC29DCBC33EDE74063E61896BBF010000003D0A824001";
    // Золотой молотов с id=1 (следом за золотой флешкой тоже с id=1) — id назначает клиент
    // и сбрасывает его сам, счётчиком бросков он не является.
    private const string ThrowPayloadGoldMolotov =
        "010000015E00000000000000906138411467BF3FFBC4BB41DDA079BD189FB73EA5D46FBF01000000BB52CC3F01";

    /// <summary>
    /// 24-байтный payload взрыва собирается побайтово как в капче, включая varint
    /// <c>0x01000000|grenadeId</c> (<c>8n 80 80 08</c>).
    /// </summary>
    [Theory]
    [InlineData(ExplodeFrameFlash, 1, 1)]
    [InlineData(ExplodeFrameHe, 2, 1)]
    [InlineData(ExplodeFrameSmoke, 3, 1)]
    [InlineData(ExplodeFrameMolotov, 4, 1)]
    [InlineData(ExplodeFrameGoldMolotov, 1, 1)]
    public void ExplodePayload_IsByteIdenticalToTheCapturedOne(
        string frameHex, byte grenadeId, byte throwerActorNr)
    {
        var golden = Convert.FromHexString(frameHex);
        var goldenPayload = golden.AsSpan(HostHeaderLen + RpcBodyHeaderLen).ToArray();

        // Позиция берётся из самой капчи — тест сверяет форму, а не скопированные числа.
        var posX = BinaryPrimitives.ReadSingleLittleEndian(goldenPayload.AsSpan(9, 4));
        var posY = BinaryPrimitives.ReadSingleLittleEndian(goldenPayload.AsSpan(13, 4));
        var posZ = BinaryPrimitives.ReadSingleLittleEndian(goldenPayload.AsSpan(17, 4));

        var built = MatchGrenadeWire.BuildExplodePayload(grenadeId, throwerActorNr, posX, posY, posZ);

        Assert.Equal(24, built.Length);
        Assert.Equal(goldenPayload, built);
    }

    /// <summary>
    /// Полный кадр взрыва, собранный хостом, побайтово равен капче (stime, tv и актор
    /// бросавшего поданы из самой капчи — в капчурах бросал всегда актор 1): заголовок,
    /// id=5, rpc=актор бросавшего, gaa=2, field=2, double и payload.
    /// </summary>
    [Theory]
    [InlineData(ExplodeFrameFlash, 1, 1)]
    [InlineData(ExplodeFrameHe, 2, 1)]
    [InlineData(ExplodeFrameSmoke, 3, 1)]
    [InlineData(ExplodeFrameMolotov, 4, 1)]
    [InlineData(ExplodeFrameGoldMolotov, 1, 1)]
    public void BuiltExplodeFrame_IsByteIdenticalToTheLiveCapture(
        string frameHex, byte grenadeId, byte throwerActorNr)
    {
        var golden = Convert.FromHexString(frameHex);
        var goldenPayload = golden.AsSpan(HostHeaderLen + RpcBodyHeaderLen).ToArray();
        var stime = BinaryPrimitives.ReadInt32LittleEndian(golden.AsSpan(2, 4));
        var timeValue = BinaryPrimitives.ReadDoubleLittleEndian(golden.AsSpan(HostHeaderLen + 6, 8));
        var posX = BinaryPrimitives.ReadSingleLittleEndian(goldenPayload.AsSpan(9, 4));
        var posY = BinaryPrimitives.ReadSingleLittleEndian(goldenPayload.AsSpan(13, 4));
        var posZ = BinaryPrimitives.ReadSingleLittleEndian(goldenPayload.AsSpan(17, 4));

        var rebuilt = MatchGrenadeWire.BuildGrenadeExplodeRpc(
            stime, grenadeId, throwerActorNr, timeValue, posX, posY, posZ);

        // Идентичность объекта, автора и цели рассылки — суть исправления #53:
        // id=5 (GrenadeManager), rpc=throwerActor, gaa=2 (AllCached — всем), field=2 (explode).
        Assert.Equal(0x04, rebuilt[0]);
        Assert.Equal((byte)MatchOpcode.WorldObjectRpc, rebuilt[1]);
        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(rebuilt.AsSpan(HostHeaderLen, 2)));
        Assert.Equal(throwerActorNr, rebuilt[HostHeaderLen + 2]);
        Assert.Equal(2, rebuilt[HostHeaderLen + 3]);
        Assert.Equal((short)2, BinaryPrimitives.ReadInt16LittleEndian(rebuilt.AsSpan(HostHeaderLen + 4, 2)));

        Assert.Equal(golden, rebuilt);
    }

    /// <summary>
    /// tv взрыва обязан жить на часах бросавшего: <c>tv броска + фит</c>. У телефон-хоста
    /// это одни часы (24/24 пар: разница tv = 1.708–1.712 с для флеш/ХЕ), у выделенного —
    /// нет, поэтому tv берётся от броска, а не от <c>NextServerTime</c> (live-тест: кадр с
    /// tv на наших часах клиент молча проигнорировал). Значения tv — из файлов капчур.
    /// </summary>
    [Theory]
    [InlineData(MatchGrenadeWire.ItemFlash, 4240.695, 4242.407)] // 20261115_082326_6215 → 082328_6376
    [InlineData(MatchGrenadeWire.ItemHe, 4249.243, 4250.952)]    // 20261115_082335_7013 → 082336_7175
    public void ExplodeTimeValue_StaysOnTheThrowersClock(byte itemId, double throwTv, double boomTv)
    {
        // Как в GameMatchHost.Grenades.cs: tv = throwTv + смоделированный фит.
        var builtTv = throwTv + MatchGrenadeWire.FuseSecondsFor(itemId);

        // Капча: flash 1.712 / HE 1.709 против нашего плоского 1.71 — расхождение ≤ 5 мс.
        Assert.InRange(builtTv, boomTv - 0.005, boomTv + 0.005);
    }

    /// <summary>
    /// Полный сетевой id гранаты — <c>(actor&lt;&lt;24)|lowId</c> (ключ _droppedGrenades из
    /// декомпилированного клиента): actor 1 id 1..7 даёт <c>81..87 80 80 08</c> (как во всех
    /// капчах), actor 2 id 1 → <c>81 80 80 10</c>, actor 3 id 2 → <c>82 80 80 18</c>.
    /// </summary>
    [Fact]
    public void ExplodeIdVarint_EncodesTheCompositeNetworkId()
    {
        for (byte n = 1; n <= 7; n++)
        {
            var payload = MatchGrenadeWire.BuildExplodePayload(n, 1, 1f, 2f, 3f);
            Assert.Equal(0x80 | n, payload[3]);
            Assert.Equal(0x80, payload[4]);
            Assert.Equal(0x80, payload[5]);
            Assert.Equal(0x08, payload[6]);
        }

        Assert.Equal(0x0200_0001, MatchGrenadeWire.GrenadeNetworkId(2, 1));
        Assert.Equal(0x0300_0002, MatchGrenadeWire.GrenadeNetworkId(3, 2));

        var actor2 = MatchGrenadeWire.BuildExplodePayload(1, 2, 1f, 2f, 3f);
        Assert.Equal(new byte[] { 0x81, 0x80, 0x80, 0x10 }, actor2[3..7]);

        var actor3 = MatchGrenadeWire.BuildExplodePayload(2, 3, 1f, 2f, 3f);
        Assert.Equal(new byte[] { 0x82, 0x80, 0x80, 0x18 }, actor3[3..7]);
    }

    /// <summary>
    /// Кадр взрыва для бросавшего-не-актора-1: rpc-байт = актор бросавшего (инициатор идёт
    /// отдельным аргументом Rpc в клиенте — <c>CallDetonateViaServer(initiator, …)</c>), а
    /// payload — сериализованный <c>GrenadeDetonateData</c>: <c>HasInitiator=01</c> (НЕ актор —
    /// третья live-сессия ушла в молоко в том числе из-за 02 в этом байте), счётчики массивов
    /// <c>0100</c>, varint = полный композитный id <c>(actor&lt;&lt;24)|lowId</c> — именно по нему
    /// клиент ищет гранату, промах означает тихий дроп без единой ошибки.
    /// </summary>
    [Fact]
    public void ExplodeFrame_CarriesInitiatorRpcAndCompositeId()
    {
        var stime = 0x056FF8E8; // из live-лога latest-2.log
        var pkt = MatchGrenadeWire.BuildGrenadeExplodeRpc(
            stime, grenadeId: 1, throwerActorNr: 2, timeValue: 91224.17,
            posX: 17.89f, posY: 0.74f, posZ: 4.95f);

        // rpc-байт = актор бросавшего (2), не актор хоста (1).
        Assert.Equal(2, pkt[HostHeaderLen + 2]);

        var payload = pkt.AsSpan(HostHeaderLen + RpcBodyHeaderLen).ToArray();
        Assert.Equal(24, payload.Length);
        // GrenadeDetonateData: HasInitiator=true, Ids.Count=1, полный id (actor<<24)|1.
        Assert.Equal(1, payload[0]);
        Assert.Equal(new byte[] { 0x01, 0x00 }, payload[1..3]);
        Assert.Equal(new byte[] { 0x81, 0x80, 0x80, 0x10 }, payload[3..7]);
        Assert.Equal(new byte[] { 0x01, 0x00 }, payload[7..9]);
        Assert.Equal(new byte[] { 0x01, 0x00 }, payload[21..23]);
        Assert.Equal(0, payload[23]); // InAlternativeZone = false
    }

    /// <summary>
    /// Разбор 45-байтного payload броска: id гранаты, предмет, позиция и направление
    /// читаются с тех же смещений, что и в капчах. Предмет подтверждён чатом сессии:
    /// 0x5D=флеш, 0x5B=ХЕ, 0x5C=смок, 0x5E=молотов («флешку/хаешку/смок/молотов» перед
    /// каждым броском). <c>grenadeId</c> — байт [0]: в grenades-сессии он совпал с порядком
    /// бросков (1..4), а в золоте два броска подряд имеют id=1 — он назначается клиентом.
    /// </summary>
    [Theory]
    [InlineData(ThrowPayloadFlash, MatchGrenadeWire.ItemFlash, 1)]
    [InlineData(ThrowPayloadHe, MatchGrenadeWire.ItemHe, 2)]
    [InlineData(ThrowPayloadSmoke, MatchGrenadeWire.ItemSmoke, 3)]
    [InlineData(ThrowPayloadMolotov, MatchGrenadeWire.ItemMolotov, 4)]
    [InlineData(ThrowPayloadGoldMolotov, MatchGrenadeWire.ItemMolotov, 1)]
    public void ThrowPayload_ParsesIdItemAndPoseFromTheCapturedOffsets(
        string payloadHex, byte expectedItem, byte expectedGrenadeId)
    {
        var payload = Convert.FromHexString(payloadHex);

        var ok = MatchGrenadeWire.TryParseThrowPayload(
            payload,
            out var grenadeId,
            out var itemId,
            out var posX, out var posY, out var posZ,
            out var dirX, out var dirY, out var dirZ);

        Assert.True(ok);
        Assert.Equal(expectedGrenadeId, grenadeId);
        Assert.Equal(expectedItem, itemId);
        // Позиция [12..23] и направление [24..35] — как в файле, без перекоса смещений.
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(12, 4)), posX);
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(16, 4)), posY);
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(20, 4)), posZ);
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(24, 4)), dirX);
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(28, 4)), dirY);
        Assert.Equal(BinaryPrimitives.ReadSingleLittleEndian(payload.AsSpan(32, 4)), dirZ);
    }

    /// <summary>
    /// Неизвестная длина payload — не разбираем вовсе (правило репозитория: неизвестная
    /// раскладка = отсутствие интерпретации, а не выдумка).
    /// </summary>
    [Fact]
    public void ThrowPayload_OfUnknownLengthIsRejected()
    {
        var ok = MatchGrenadeWire.TryParseThrowPayload(
            new byte[44],
            out _, out _, out _, out _, out _, out _, out _, out _);
        Assert.False(ok);
    }

    /// <summary>
    /// Политика предохранителей по предметам, из 24 пар капчур: флеш и ХЕ — плоский
    /// таймер ~1.71 с (наблюдено 1.708–1.712, взрыв в воздухе), смок — плоский ~2.5 с
    /// (две пары: 2.009/3.071), молотов — детонация при касании земли (0.28–1.64 с) с
    /// предохранительным потолком.
    /// </summary>
    [Fact]
    public void FusePolicy_MatchesTheObservedPerItemBehavior()
    {
        // 16 пар флеш/ХЕ легли в 1.708–1.712 — константа внутри ±5 мс.
        Assert.InRange(MatchGrenadeWire.TimedFuseSeconds, 1.705, 1.715);
        Assert.False(MatchGrenadeWire.DetonatesOnImpact(MatchGrenadeWire.ItemFlash));
        Assert.False(MatchGrenadeWire.DetonatesOnImpact(MatchGrenadeWire.ItemHe));
        Assert.Equal(MatchGrenadeWire.TimedFuseSeconds, MatchGrenadeWire.FuseSecondsFor(MatchGrenadeWire.ItemFlash));
        Assert.Equal(MatchGrenadeWire.TimedFuseSeconds, MatchGrenadeWire.FuseSecondsFor(MatchGrenadeWire.ItemHe));

        Assert.False(MatchGrenadeWire.DetonatesOnImpact(MatchGrenadeWire.ItemSmoke));
        Assert.InRange(MatchGrenadeWire.SmokeFuseSeconds, 2.0, 3.1);
        Assert.Equal(MatchGrenadeWire.SmokeFuseSeconds, MatchGrenadeWire.FuseSecondsFor(MatchGrenadeWire.ItemSmoke));

        Assert.True(MatchGrenadeWire.DetonatesOnImpact(MatchGrenadeWire.ItemMolotov));
        Assert.InRange(
            MatchGrenadeWire.ImpactFuseCapSeconds,
            1.64, // самый поздний наблюденный молотов уже на земле
            2.5);
    }

    /// <summary>
    /// Релей броска обязан включать отправителя (gaa=4 AllViaServer — клиент не исполняет
    /// rpc локально и ждёт эха хоста), поэтому полёт виден всем, включая бросавшего.
    /// Хост-авторский взрыв уходит отдельным broadcast без exceptSender — всем пирам
    /// комнаты сразу; его gaa=2 (AllCached) уже зафиксирован тестом формы кадра выше.
    /// </summary>
    [Fact]
    public void RelayPolicy_GrenadeThrowEchoIncludesSender()
    {
        Assert.False(WorldObjectRpcRelayPolicy.ExcludesSender(4)); // бросок — эхо всем, вкл. бросавшего
    }

    /// <summary>
    /// Баллистика: флеш детонирует ровно на таймере (1.71 с), в воздухе — как в капчах
    /// (там fallY от −4.8 до +1.3, т.е. приземления не ждут). Позиция — аппроксимация без
    /// карты: расхождение с капчей у этой пары ~6 м (см. замечание в
    /// <see cref="MatchGrenadeBallistics"/>), поэтому допуск щадящий, но он ловит
    /// сломанную физику (например, забытую гравитацию или двойную скорость).
    /// </summary>
    [Fact]
    public void Ballistics_FlashDetonatesOnTheFlatFuseMidAir()
    {
        var payload = Convert.FromHexString(ThrowPayloadFlash);
        Assert.True(MatchGrenadeWire.TryParseThrowPayload(
            payload, out _, out _, out var px, out var py, out var pz, out var dx, out var dy, out var dz));

        MatchGrenadeBallistics.ResolveDetonation(
            px, py, pz, dx, dy, dz,
            impactDetonation: false,
            fuseSeconds: MatchGrenadeWire.FuseSecondsFor(MatchGrenadeWire.ItemFlash),
            out var afterSeconds, out var ex, out var ey, out var ez);

        Assert.Equal(MatchGrenadeWire.TimedFuseSeconds, afterSeconds, 5);
        // В воздухе: выше смоделированного пола (пол = origin.y − 1.5).
        Assert.True(ey > py - MatchGrenadeBallistics.FloorDropMeters - 0.01f,
            $"expected mid-air detonation, got y={ey} vs floor {py - MatchGrenadeBallistics.FloorDropMeters}");
        // Не улетела в космос и не упала на месте: горизонтальный прогресс того же знака, что dir.
        Assert.Equal(Math.Sign(dx), Math.Sign(ex - px));
        // Аппроксимация против капчи этой пары (24.66, −25.50, −52.00): допуск 20 м.
        Assert.InRange(Math.Sqrt(
            (ex - 24.66f) * (ex - 24.66f) +
            (ey - -25.50f) * (ey - -25.50f) +
            (ez - -52.00f) * (ez - -52.00f)), 0, 20);
    }

    /// <summary>
    /// Баллистика: молотов останавливается на первом касании пола (в рамках потолка 2 с),
    /// а не на таймере, — как вели себя все 6 молотовых пар капчур.
    /// </summary>
    [Fact]
    public void Ballistics_MolotovStopsAtTheFirstFloorContact()
    {
        var payload = Convert.FromHexString(ThrowPayloadMolotov);
        Assert.True(MatchGrenadeWire.TryParseThrowPayload(
            payload, out _, out var itemId, out var px, out var py, out var pz, out var dx, out var dy, out var dz));
        Assert.Equal(MatchGrenadeWire.ItemMolotov, itemId);

        MatchGrenadeBallistics.ResolveDetonation(
            px, py, pz, dx, dy, dz,
            impactDetonation: true,
            fuseSeconds: MatchGrenadeWire.ImpactFuseCapSeconds,
            out var afterSeconds, out var ex, out var ey, out var ez);

        Assert.True(afterSeconds > MatchGrenadeBallistics.MinImpactSeconds, "must fly a moment before impact");
        Assert.True(afterSeconds < MatchGrenadeWire.ImpactFuseCapSeconds, "must stop at floor, not at the cap");
        Assert.Equal((double)(py - MatchGrenadeBallistics.FloorDropMeters), ey, 2);
        // Аппроксимация против капчи этой пары (39.83, −30.08, −58.50): допуск 20 м.
        Assert.InRange(Math.Sqrt(
            (ex - 39.83f) * (ex - 39.83f) +
            (ey - -30.08f) * (ey - -30.08f) +
            (ez - -58.50f) * (ez - -58.50f)), 0, 20);
    }

    /// <summary>Симуляция детерминирована — одинаковый вход даёт одинаковый взрыв.</summary>
    [Fact]
    public void Ballistics_IsDeterministic()
    {
        MatchGrenadeBallistics.ResolveDetonation(
            10f, 2f, 30f, 0.3f, 0.4f, -0.866f,
            impactDetonation: false, fuseSeconds: 1.71,
            out var t1, out var x1, out var y1, out var z1);
        MatchGrenadeBallistics.ResolveDetonation(
            10f, 2f, 30f, 0.3f, 0.4f, -0.866f,
            impactDetonation: false, fuseSeconds: 1.71,
            out var t2, out var x2, out var y2, out var z2);
        Assert.Equal(t1, t2);
        Assert.Equal(x1, x2);
        Assert.Equal(y1, y2);
        Assert.Equal(z1, z2);
    }
}
