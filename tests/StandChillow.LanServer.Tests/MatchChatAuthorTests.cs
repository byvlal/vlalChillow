using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Кем подписан серверный кадр чата катки.
/// </summary>
/// <remarks>
/// Наблюдение, а не догадка: прогон <c>/chattest</c> 2026-11-14 (latest-3.log, 3 пира,
/// Ranked2v2 / Sandstone) отправил один текст четырьмя кадрами, и игрок увидел только
/// букву <b>C</b> — автор 4 (FinalSapper78, живой игрок), <c>field=1</c>. A и B (автор 1,
/// «Server» в Spectator без пешки) и D (<c>field=2</c>) не нарисовались ни у кого. Отличия
/// A от C — только байт автора, C от D — только <c>field</c>, значит кадр от актора сервера
/// клиент не рисует вовсе, и серверный чат обязан занимать автора среди живых.
/// </remarks>
public sealed class MatchChatAuthorTests
{
    private static readonly byte[] Live = { 2, 3, 4 };

    [Fact]
    public void ServerVoice_IsALiveActor_NeverTheHostActor()
    {
        var author = MatchChatAuthor.PickServerVoice(3, Live, MatchHostActor.ActorNr);

        Assert.Equal((byte)2, author);
        Assert.NotEqual(MatchHostActor.ActorNr, author);
        Assert.Contains(author, Live);
    }

    /// <summary>
    /// Не сам получатель: своё сообщение клиент может не нарисовать, и тогда именно он
    /// остался бы без серверного чата.
    /// </summary>
    [Fact]
    public void ServerVoice_IsNotTheRecipient_WhileSomebodyElseIsLive()
    {
        Assert.Equal((byte)3, MatchChatAuthor.PickServerVoice(2, Live, MatchHostActor.ActorNr));
        Assert.Equal((byte)2, MatchChatAuthor.PickServerVoice(4, Live, MatchHostActor.ActorNr));
        Assert.Equal((byte)2, MatchChatAuthor.PickServerVoice(0, Live, MatchHostActor.ActorNr));
    }

    [Fact]
    public void OnlyRecipientIsLive_TakesHim_ButStillNotTheHostActor()
    {
        // Живой один, и это сам получатель: свой актор всё же рисуется, актор сервера — нет.
        Assert.Equal((byte)4, MatchChatAuthor.PickServerVoice(4, new byte[] { 4 }, MatchHostActor.ActorNr));
    }

    [Fact]
    public void NobodyIsLive_FallsBackToTheHostActor()
    {
        // До матча тел нет: прежний, заведомо невидимый автор — но и рисовать некому.
        Assert.Equal(
            MatchHostActor.ActorNr,
            MatchChatAuthor.PickServerVoice(0, Array.Empty<byte>(), MatchHostActor.ActorNr));
    }

    /// <summary>
    /// Комната из одного игрока: пешек ещё нет, но пир 7777 уже прошёл bootstrap. Без этого
    /// источника аудитория пуста, голос сервера — актор 1, и ответ на команду не рисуется
    /// никому (latest.log, 2026-11-15: один пир actor=2, все ответы «отложенно, peers=1»).
    /// </summary>
    [Fact]
    public void LiveAudience_FallsBackToReadyPeers_SoAOnePeerRoomGetsAVoice()
    {
        var audience = MatchChatAuthor.LiveChatAudience(
            spawnedPawns: Array.Empty<byte>(),
            livingOwners: Array.Empty<byte>(),
            readyPeers: new byte[] { 2, 0, 2 });

        Assert.Equal(new byte[] { 2 }, audience.ToArray());
        // И этот голос — сам единственный получатель: свой актор лучше невидимого серверного.
        Assert.Equal((byte)2, MatchChatAuthor.PickServerVoice(2, audience, MatchHostActor.ActorNr));
    }

    /// <summary>Пешки комнаты важнее: пока они есть, порядок источников не меняется.</summary>
    [Fact]
    public void LiveAudience_PrefersPawns_ThenLivingOwners()
    {
        Assert.Equal(
            new byte[] { 5 },
            MatchChatAuthor.LiveChatAudience(
                spawnedPawns: new byte[] { 5 },
                livingOwners: new byte[] { 6 },
                readyPeers: new byte[] { 7 }).ToArray());

        Assert.Equal(
            new byte[] { 6 },
            MatchChatAuthor.LiveChatAudience(
                spawnedPawns: Array.Empty<byte>(),
                livingOwners: new byte[] { 6 },
                readyPeers: new byte[] { 7 }).ToArray());

        // Никто не жив — пусто, и вызывающий честно падает на актора сервера.
        Assert.Empty(MatchChatAuthor.LiveChatAudience(
            Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>()));
    }

    /// <summary>
    /// Кадр подписан чужим актором, поэтому текст помечен: без пометки серверное
    /// «матч armed» выглядело бы словами того игрока, чей актор заняли.
    /// </summary>
    [Fact]
    public void ServerTextIsMarked_ExactlyOnce()
    {
        Assert.Equal("[Server] матч armed", MatchChatAuthor.Sign("матч armed"));
        Assert.Equal("[Server] матч armed", MatchChatAuthor.Sign("[Server] матч armed"));
    }
}
