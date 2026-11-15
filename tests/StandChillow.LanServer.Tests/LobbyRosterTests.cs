using StandChillow.LanServer.Net.Lobby;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Роспись лобби на проводе: клиент видит только себя и сервер.
/// </summary>
/// <remarks>
/// Отчёт: в лобби больше ~10 игроков, и клиент ломается на полной росписи. Поэтому
/// op4/op5 не рассылаются, а <c>JoinResponse</c> несёт одну строку — <c>Server</c>.
/// На проводе «я» — <c>selfMemberId = 1</c>: раз чужих строк нет, глобальный номер
/// участника не нужен и мог бы выйти за предел клиента. Полный состав остаётся в
/// состоянии сессии (дашборд, сайт, <c>/ids</c>), эти тесты его не касаются.
/// </remarks>
public sealed class LobbyRosterTests
{
    [Fact]
    public void RosterOnTheWire_IsOnlyTheServerLine()
    {
        var roster = LobbySession.BuildHiddenLobbyRoster();

        var only = Assert.Single(roster);
        Assert.Equal(LobbySession.ServerMemberId, only.Id);
        Assert.Equal(LobbySession.ServerPlayerName, only.Name);
        Assert.Null(only.Avatar);
    }

    [Fact]
    public void SelfMemberId_IsOne_AndServerStaysZero()
    {
        // Клиент рисует себя по selfMemberId, сервер — по member id в росписи.
        Assert.Equal(0, LobbySession.ServerMemberId);
        Assert.Equal(1, LobbySession.WireSelfMemberId);
        Assert.NotEqual(LobbySession.ServerMemberId, LobbySession.WireSelfMemberId);

        var roster = LobbySession.BuildHiddenLobbyRoster();
        Assert.DoesNotContain(roster, m => m.Id == LobbySession.WireSelfMemberId);
    }

    /// <summary>Свежая роспись на каждый вход: клиент не должен получить общий список.</summary>
    [Fact]
    public void RosterIsAFreshList_PerJoin()
    {
        var first = LobbySession.BuildHiddenLobbyRoster();
        var second = LobbySession.BuildHiddenLobbyRoster();

        Assert.NotSame(first, second);
        Assert.NotSame(first[0], second[0]);
    }

    /// <summary>
    /// Чужой чат подписывается автором Server, поэтому имя говорящего несёт сам текст:
    /// иначе в приватной росписи чужое сообщение было бы безымянным.
    /// </summary>
    [Fact]
    public void RelayedChatCarriesTheSpeakerName()
    {
        Assert.Equal("влал: привет", LobbySession.FormatRelayedChat("влал", "привет"));
        Assert.Equal("Server: тест", LobbySession.FormatRelayedChat("Server", "тест"));
    }
}
