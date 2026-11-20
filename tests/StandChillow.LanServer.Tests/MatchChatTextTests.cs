using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Чат катки: сервер чистит текст, но пересобирает только payload — автор, gaa и field
/// остаются из клиентского кадра, иначе клиент нарисует чужое сообщение от другого актора.
/// </summary>
public sealed class MatchChatTextTests
{
    [Fact]
    public void Payload_RoundTripsThroughTheClientFormat()
    {
        var payload = MatchCodec.BuildChatManagerTextPayload("привет");

        Assert.True(MatchCodec.TryParseChatManagerText(payload, out var text));
        Assert.Equal("привет", text);
    }

    [Fact]
    public void RebuiltPayload_KeepsTheFrameAuthorAndDecodesCleanText()
    {
        var frame = MatchCodec.BuildChatManagerText(
            serverTime: 1524,
            text: NickPolicy.CleanChat("да <color=red>красный</color>"),
            authorActorNr: 7,
            gaaTarget: 3,
            field: 1);

        Assert.True(MatchCodec.TryOpenBody(frame, out _, out var opcode, out _, out var body));
        Assert.Equal(MatchOpcode.WorldObjectRpc, opcode);

        var parsed = MatchCodec.ParseWorldObjectRpcBody(new LobbyReader(body));
        Assert.Equal(MatchSceneManagers.ChatManagerObjectId, parsed.ObjectId);
        Assert.Equal((byte)7, parsed.RpcId);
        Assert.Equal((byte)3, parsed.GaaTarget);
        Assert.Equal((short)1, parsed.Field);

        Assert.True(MatchCodec.TryParseChatManagerText(parsed.Payload, out var text));
        Assert.Equal("да красный", text);
    }

    /// <summary>Пустой пузырь после чистки — не повод слать кадр: релеить нечего.</summary>
    [Fact]
    public void CleanChat_OfPureMarkup_IsEmpty()
    {
        Assert.Equal("", NickPolicy.CleanChat("<color=red></color>"));
    }
}
