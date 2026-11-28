using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Empty fighting team during the C2=22 buy: the buy is held instead of opening C2=31.
/// A round with nobody on the other side would be a free win for the team that stayed.
/// </summary>
public sealed class AlliesBuyHoldTests
{
    [Theory]
    [InlineData(false, false, AlliesBuyHoldAction.Hold)]
    [InlineData(false, true, AlliesBuyHoldAction.Hold)]
    [InlineData(true, false, AlliesBuyHoldAction.None)]
    [InlineData(true, true, AlliesBuyHoldAction.Resume)]
    public void MissingTeam_Holds_AndReturn_GivesAFullBuy(
        bool bothTeamsPresent, bool holdActive, AlliesBuyHoldAction expected)
    {
        Assert.Equal(expected, MatchFlowRules.DecideAlliesBuyHold(bothTeamsPresent, holdActive));
    }

    [Fact]
    public void HoldBag_KeepsC2_22_WithoutBomberId()
    {
        const double nowSec = 1234.5;
        var props = GameMatchHost.BuildAlliesBuyHoldRoomProps(nowSec);
        var map = props.ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal(LobbyVariantKind.Byte, map[MatchRoomPropKeys.C2].Kind);
        Assert.Equal(MatchC2States.WarmupWillFinish, map[MatchRoomPropKeys.C2].Byte);
        Assert.Equal(nowSec, map[MatchRoomPropKeys.Time].Double);
        Assert.Equal(nowSec, map[MatchRoomPropKeys.RoundStartTime].Double);
        Assert.Equal(AlliesFlowParams.BuyPhase.TotalSeconds, map[MatchRoomPropKeys.Duration].Double);
        Assert.DoesNotContain(MatchRoomPropKeys.BomberId, map.Keys);
    }

    /// <summary>
    /// Structural guard: the WarmupWillFinish tick must ask the missing-team hold before it
    /// opens C2=31. Moving the hold below <c>EnterAlliesCombat</c> would start the round for a
    /// side that has nobody to fight.
    /// </summary>
    [Fact]
    public void WarmupWillFinishTick_ChecksHoldBeforeCombat()
    {
        var path = Path.Combine(
            ServerSourceRoot(),
            "Net", "Match", "Modes", "Ranked2v2", "GameMatchHost.Allies.cs");
        var text = File.ReadAllText(path);

        var caseAt = text.IndexOf("case MatchFlowPhase.WarmupWillFinish:", StringComparison.Ordinal);
        Assert.True(caseAt >= 0, "WarmupWillFinish case not found");
        var nextCase = text.IndexOf("case MatchFlowPhase.RoundEndPause:", caseAt, StringComparison.Ordinal);
        Assert.True(nextCase > caseAt, "RoundEndPause case not found after WarmupWillFinish");
        var block = text[caseAt..nextCase];

        var holdAt = block.IndexOf("TryHoldAlliesBuyForMissingTeam(room)", StringComparison.Ordinal);
        var combatAt = block.IndexOf("EnterAlliesCombat(room)", StringComparison.Ordinal);

        Assert.True(holdAt >= 0, "WarmupWillFinish must check the missing-team hold");
        Assert.True(combatAt > holdAt, "C2=31 must only be opened after the hold check");
    }

    private static string ServerSourceRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "server")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "server");
    }
}
