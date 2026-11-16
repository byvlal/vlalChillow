namespace StandChillow.LanServer.Net.Match;

using StandChillow.LanServer.Net.Lobby;

/// <summary>
/// Catalog mode id → wire mode id. The two are <b>not</b> always the same.
/// </summary>
/// <remarks>
/// <para>
/// Our catalog (and the web panel) calls the duel mode <c>Duel</c>, but the 2.07 client
/// knows it as <c>RankedDuel</c> — in <b>both</b> channels:
/// </para>
/// <list type="bullet">
/// <item>lobby: <c>op7 CustomProperties</c> → <c>GameModeId='RankedDuel'</c>
///   (phone gold, <c>captures/duel.zip</c> → <c>20261116_033101_003_client_op7_len56.bin</c>);</item>
/// <item>match: <c>JoinRoomResponse</c> gap → <c>C0='RankedDuel'</c>, <c>C1='Block'</c>
///   (same capture → <c>20261116_033108_004_match_rx002_JoinRoomResponse_len330.bin</c>).</item>
/// </list>
/// <para>
/// The bare id <c>Duel</c> appears in <b>zero</b> frames of that capture set — the wire only
/// ever carries <c>RankedDuel</c>. The other modes need no translation: <c>Ranked2v2</c>,
/// <c>Ranked2v2Alt</c> and <c>RankedDefuse</c> are already identical on both sides, which is
/// exactly why those modes connect fine and the duel room did not.
/// </para>
/// <para>
/// An unknown mode id makes the client dereference a null mode/scene config while it builds
/// the match: the 2.07 client logged a <c>NullReferenceException</c> and died right after
/// <c>JoinRoomResponse Found</c>, before the host sent a single scene manager (issue #56).
/// </para>
/// </remarks>
public static class MatchModeIds
{
    /// <summary>Catalog id of the duel mode (lobby / panel side, user alias <c>duel</c>).</summary>
    public const string Duel = "Duel";

    /// <summary>Wire id of the duel mode (client side).</summary>
    public const string DuelWire = "RankedDuel";

    /// <summary>Wire id for a catalog mode id — what the client must see.</summary>
    public static string ToWire(string modeId) =>
        string.Equals(modeId, Duel, StringComparison.Ordinal) ? DuelWire : modeId;

    /// <summary>Catalog id for a wire mode id — what our own UI/logs show.</summary>
    public static string ToCatalog(string modeId) =>
        string.Equals(modeId, DuelWire, StringComparison.Ordinal) ? Duel : modeId;

    /// <summary>
    /// Is this the duel mode? Accepts both spellings: a room prop may still hold the
    /// catalog id (e.g. a room created before the wire mapping, or a hand-written one).
    /// </summary>
    public static bool IsDuel(string? modeId) =>
        string.Equals(modeId, Duel, StringComparison.Ordinal)
        || string.Equals(modeId, DuelWire, StringComparison.Ordinal);

    /// <summary>
    /// Room identity props (actor 0) of a match room: <c>C0</c> = wire mode id, <c>C1</c> = map.
    /// </summary>
    /// <remarks>
    /// Both the gap (first thing the client reads) and the late-join snapshot publish these
    /// props, so they are written in one place — otherwise the two could disagree and the
    /// client would see a different mode after a reconnect.
    /// </remarks>
    public static void ApplyRoomIdentity(
        IDictionary<(byte Actor, string Key), LobbyVariant> actorProps,
        string modeId,
        string selectedLevel)
    {
        actorProps[(0, MatchRoomPropKeys.C0)] =
            LobbyVariant.FromString(ToWire(modeId));
        actorProps[(0, MatchRoomPropKeys.C1)] =
            LobbyVariant.FromString(selectedLevel);
    }
}
