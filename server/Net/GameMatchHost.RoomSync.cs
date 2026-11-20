using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Net;

public sealed partial class GameMatchHost
{
    /// <summary>
    /// Перенести состояние комнат в реестр панели: сколько людей, какая фаза, счёт,
    /// номер раунда и живой состав для публичной страницы комнаты.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Панель не читает <c>_roomsByPassword</c> напрямую: это горячая структура игрового
    /// цикла, и HTTP-поток не должен её блокировать. Реестр — отдельная маленькая копия,
    /// которую обновляет сам хост, раз в секунду.
    /// </para>
    /// <para>
    /// Перевод фаз: до <see cref="MatchFlowPhase.Warmup"/> включительно комната ещё
    /// «ждёт» — играть в неё можно, игроков добирают. С <see cref="MatchFlowPhase.PurchasePhase"/>
    /// идёт катка. <see cref="MatchFlowPhase.MatchOver"/> и финальные стадии DeathMatch —
    /// «закончилась», такие комнаты удаляются по TTL.
    /// </para>
    /// <para>
    /// В живой состав попадают только текущие актёры комнаты — без денег: страница
    /// <c>#/room/&lt;n&gt;</c> публичная. Смерти берутся из того же забега истории, что
    /// попадёт в итоговую таблицу, поэтому числа совпадают.
    /// </para>
    /// </remarks>
    internal void SyncRoomRegistry()
    {
        var registry = Rooms;
        if (registry is null)
            return;

        List<(int Number, RoomState State, int Players, int ScoreCt, int ScoreTr,
            int Round, string Key, List<(byte Nr, RoomLivePlayer Player)> Live)> snapshot = new();
        lock (_roomGate)
        {
            foreach (var room in _roomsByPassword.Values)
            {
                if (!room.IsPanelRoom || room.DisplayNumber <= 0)
                    continue;

                var humans = 0;
                var live = new List<(byte Nr, RoomLivePlayer Player)>();
                foreach (var (nr, name, _) in room.Actors)
                {
                    if (nr == MatchHostActor.ActorNr)
                        continue;
                    humans++;

                    if (string.IsNullOrWhiteSpace(name))
                        continue;
                    var id = AccountIdOfActorLocked(room, nr);
                    if (id == 0 && Accounts is null)
                        id = LegacyNickHash.ForNick(name); // тесты/проба без аккаунтов
                    if (id <= 0)
                        continue;

                    var team = MatchFlowRules.GetActorTeam(room.ActorProps, nr);
                    var kills = ReadActorIntProp(room, nr, MatchRoomPropKeys.Kills);
                    if (kills < 0)
                        kills = ReadActorIntProp(room, nr, MatchRoomPropKeys.FairKills);

                    live.Add((nr, new RoomLivePlayer(
                        Id: id,
                        Nick: name,
                        Team: TeamKey(team),
                        Kills: Math.Max(0, kills),
                        Assists: Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Assists)),
                        Deaths: 0,
                        Score: Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Score2)),
                        Ping: ReadActorIntProp(room, nr, MatchRoomPropKeys.Ping))));
                }

                // Только чтение счёта: в игровой цикл ничего не пишем и провод не трогаем.
                snapshot.Add((
                    room.DisplayNumber,
                    MapPhase(room.Flow.Phase, humans),
                    humans,
                    room.Flow.ScoreCt,
                    room.Flow.ScoreTr,
                    room.Flow.RoundIndex,
                    room.PasswordKey,
                    live));
            }
        }

        // Смерти — отдельный замок истории. Не вкладываем его в _roomGate: порядок
        // захвата замков остаётся таким же, как у остального кода.
        foreach (var (number, state, players, scoreCt, scoreTr, round, key, live) in snapshot)
        {
            Dictionary<byte, int>? deaths = null;
            lock (_historyGate)
            {
                if (_historyRuns.TryGetValue(key, out var run) && run.Deaths.Count > 0)
                    deaths = new Dictionary<byte, int>(run.Deaths);
            }

            var roster = new List<RoomLivePlayer>(live.Count);
            foreach (var (nr, player) in live)
            {
                var dead = deaths is not null && deaths.TryGetValue(nr, out var count) ? count : 0;
                roster.Add(player with { Deaths = dead });
            }

            registry.TryUpdate(number, state, players, scoreCt, scoreTr, roster, round);
        }
    }

    /// <summary>
    /// Фаза матча → состояние комнаты для панели.
    /// </summary>
    /// <remarks>
    /// Ни одна ветка не выдумывает данных: «идёт» — только те фазы, в которых бой
    /// действительно идёт, «закончилась» — только явный конец. Всё остальное «ждёт»,
    /// даже если в комнате никого нет: удалит её TTL, а не выдуманный статус.
    /// </remarks>
    internal static RoomState MapPhase(MatchFlowPhase phase, int humans)
    {
        switch (phase)
        {
            case MatchFlowPhase.PurchasePhase:
            case MatchFlowPhase.RoundLive:
            case MatchFlowPhase.BombPlanted:
            case MatchFlowPhase.RoundEndPause:
            case MatchFlowPhase.DeathMatchLive:
            case MatchFlowPhase.DeathMatchWarmup:
            case MatchFlowPhase.HalfTimeIntro:
            case MatchFlowPhase.HalfTimeSwap:
            case MatchFlowPhase.HalfTimeTransition:
                return RoomState.Live;

            case MatchFlowPhase.MatchOver:
            case MatchFlowPhase.DeathMatchEnded:
            case MatchFlowPhase.DeathMatchFinalHud:
                return RoomState.Finished;

            default:
                // WaitingPlayers / Warmup / WarmupWillFinish / AlliesPreWarmup.
                // Пустая комната «ждёт» столько, сколько ей даёт TTL.
                _ = humans;
                return RoomState.Waiting;
        }
    }
}
