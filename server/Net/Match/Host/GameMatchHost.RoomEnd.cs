using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Досрочное завершение комнаты с сайта: кнопка «Завершить игру» у «Моей комнаты» (#51).
/// </summary>
/// <remarks>
/// <para>
/// Путь ровно тот, которым катка кончается сама, — золотой, подтверждённый захватом:
/// <c>C2=201</c> FinalHud (<c>FinalPlayers</c> + <c>FinalWinTeam</c>) → выдержка 5 с →
/// <c>C2=255</c> teardown → закрытие комнаты. Никакого нового опкода, поля или тайминга:
/// клиент не отличит «завершили с сайта» от «катка доигралась».
/// </para>
/// <para>
/// Итог показывается даже в ожидании игроков: владелец нажал «завершить» — люди должны
/// увидеть WIN/LOSE по текущему счёту (пусть и 0:0), а не просто обрыв связи.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>
    /// Завершить катку в комнате досрочно, с итогами.
    /// </summary>
    /// <param name="passwordKey">Ключ комнаты (<c>room#N</c>).</param>
    /// <param name="reason">Что написать в лог.</param>
    /// <returns><c>false</c>, если комнаты нет или она уже завершается.</returns>
    public bool TryFinishRoomEarly(string passwordKey, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(passwordKey))
            return false;

        MatchRoom room;
        int round;
        int scoreTr;
        int scoreCt;

        lock (_roomGate)
        {
            if (!_roomsByPassword.TryGetValue(passwordKey, out var found) || found is null)
                return false;

            // Катка уже в процессе завершения: второй FinalHud только сломал бы клиенту
            // экран итогов, поэтому «уже завершается» — это не ошибка, а «не надо».
            if (found.Flow.Phase == MatchFlowPhase.MatchOver)
                return false;

            room = found;
            round = found.Flow.RoundIndex;
            scoreTr = found.Flow.ScoreTr;
            scoreCt = found.Flow.ScoreCt;
        }

        Console.WriteLine(
            $"[match-host] комната '{passwordKey}' завершена досрочно: " +
            $"счёт Tr={scoreTr} Ct={scoreCt} раунд={round} ({reason ?? "без причины"})");

        // Дальше — обычный конец катки: те же props, та же выдержка, то же закрытие.
        EnterMatchResultsAndArmLobbyReturn(room, scoreTr, scoreCt, "web-end", round,
            extra: reason ?? "досрочно с сайта");

        return true;
    }
}
