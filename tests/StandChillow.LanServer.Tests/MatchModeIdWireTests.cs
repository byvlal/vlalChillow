using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Issue #56 — the 2.07 client died right after <c>JoinRoomResponse Found</c> when it joined a
/// duel room: <c>NullReferenceException</c> while it built the match scene, before the host
/// sent a single scene manager.
/// </summary>
/// <remarks>
/// Root cause: the mode id on the wire. Our catalog calls the mode <c>Duel</c>, but the client
/// knows it as <c>RankedDuel</c> — in the lobby (<c>op7</c> → <c>GameModeId='RankedDuel'</c>)
/// and in the match gap (<c>C0='RankedDuel'</c>). Phone gold:
/// <c>captures/duel.zip</c> → <c>20261116_033101_003_client_op7_len56.bin</c> and
/// <c>20261116_033108_004_match_rx002_JoinRoomResponse_len330.bin</c>. The bare id <c>Duel</c>
/// appears in <b>zero</b> frames of that capture set, so an unknown id is what killed the
/// client. The other modes need no translation — <c>Ranked2v2</c>, <c>Ranked2v2Alt</c> and
/// <c>RankedDefuse</c> are identical on both sides, which is why they connected fine.
/// </remarks>
public sealed class MatchModeIdWireTests
{
    [Fact]
    public void Duel_is_published_as_RankedDuel()
    {
        Assert.Equal("RankedDuel", MatchModeIds.ToWire("Duel"));
        Assert.Equal("Duel", MatchModeIds.ToCatalog("RankedDuel"));
    }

    [Theory]
    [InlineData("Ranked2v2")]
    [InlineData("Ranked2v2Alt")]
    [InlineData("RankedDefuse")]
    [InlineData("DeathMatch")]
    [InlineData("Escalation")]
    public void Other_modes_keep_their_id(string modeId)
    {
        // Those modes connect fine today — their catalog id already is the wire id.
        Assert.Equal(modeId, MatchModeIds.ToWire(modeId));
        Assert.Equal(modeId, MatchModeIds.ToCatalog(modeId));
    }

    [Fact]
    public void Duel_is_recognised_by_both_spellings()
    {
        Assert.True(MatchModeIds.IsDuel("Duel"));
        Assert.True(MatchModeIds.IsDuel("RankedDuel"));
        Assert.False(MatchModeIds.IsDuel("Ranked2v2"));
        Assert.False(MatchModeIds.IsDuel(null));
    }

    [Fact]
    public void Mapping_is_idempotent()
    {
        // The gap builder and the room-prop writer may both translate the same value.
        Assert.Equal("RankedDuel", MatchModeIds.ToWire(MatchModeIds.ToWire("Duel")));
        Assert.Equal("Duel", MatchModeIds.ToCatalog(MatchModeIds.ToCatalog("RankedDuel")));
    }

    /// <summary>
    /// The match gap is what the client reads first: <c>C0</c> must be the wire id, otherwise
    /// the client cannot resolve the mode and dies before the bootstrap.
    /// </summary>
    [Fact]
    public void Match_gap_carries_the_wire_mode_id()
    {
        var props = MatchGapDefaults.BuildRoomProps(
            MatchModeIds.ToWire("Duel"), "Block", c2: 0);

        var c0 = Assert.Single(props, p => p.Key == MatchRoomPropKeys.C0);
        Assert.Equal(LobbyVariantKind.String, c0.Value.Kind);
        Assert.Equal("RankedDuel", c0.Value.String);

        var c1 = Assert.Single(props, p => p.Key == MatchRoomPropKeys.C1);
        Assert.Equal("Block", c1.Value.String);
    }

    /// <summary>
    /// A panel room stores the same wire id, so the gap and the late-join snapshot can never
    /// disagree about the mode.
    /// </summary>
    [Fact]
    public void Panel_room_identity_uses_the_wire_mode_id()
    {
        var actorProps = new Dictionary<(byte Actor, string Key), LobbyVariant>();
        MatchModeIds.ApplyRoomIdentity(actorProps, "Duel", "Block");

        Assert.Equal("RankedDuel", actorProps[(0, MatchRoomPropKeys.C0)].String);
        Assert.Equal("Block", actorProps[(0, MatchRoomPropKeys.C1)].String);
    }

    /// <summary>The lobby bag must speak the same id as the match channel.</summary>
    [Fact]
    public void Lobby_props_carry_the_wire_mode_id()
    {
        var session = new LobbySession("lobby-id");
        session.GameModeId = "Duel";
        session.SelectedLevels = new List<string> { "Block" };

        var modeId = Assert.Single(
            session.BuildModeLevelProps(), p => p.Key == LobbyPropKeys.GameModeId);
        Assert.Equal("RankedDuel", modeId.Value.String);
    }
}
