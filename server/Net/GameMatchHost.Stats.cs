using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Stats;

namespace StandChillow.LanServer.Net;

/// <summary>Комната для семплирования статистики: ключ, фаза боя и люди в ней.</summary>
/// <param name="Key">Устойчивый ключ комнаты (номер панели или её RoomId).</param>
/// <param name="Playing">Бой идёт — секунды и киллы/смерти засчитываются.</param>
/// <param name="Ended">Забег кончился (катка сыграна или комната снова ждёт).</param>
/// <param name="Players">Аккаунты людей в комнате — хост в них не входит.</param>
public sealed record StatsRoomSample(
    string Key, bool Playing, bool Ended, IReadOnlyList<(int Id, string Nick)> Players);

/// <summary>
/// Статистика игроков из матча: киллы, смерти, время и уникальные матчи.
/// </summary>
/// <remarks>
/// <para>
/// Хук стоит в одном месте на каждое событие: килл — в точках, где киллерский
/// <c>kills</c>++ подтвердился с разрешённой жертвой (по одной ветке на устранение,
/// повторные <c>kills</c>++ уже отфильтрованы дедупом авторства), смерть — в точках
/// коммита авторской смерти и в <c>NoteActorDeath</c> бомбовых режимов (дедуп через
/// <c>DeadActors</c>). Двойного счёта нет, лёгкое расхождение в экзотических гонках
/// допустимо.
/// </para>
/// <para>
/// Ничего на провод не меняется и игрового пути не тормозим: вызовы хука кладут
/// пару чисел в память, диск пишет фоновый семплер панели.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>Хранилище статистики. <c>null</c> — статистика не ведётся (панель выключена).</summary>
    public PlayerStatsStore? Stats { get; set; }

    /// <summary>
    /// Снимок всех комнат для семплера статистики. Только чтение под <c>_roomGate</c>:
    /// фаза, играется ли прямо сейчас и ники людей.
    /// </summary>
    public IReadOnlyList<StatsRoomSample> SnapshotRoomsForStats()
    {
        var list = new List<StatsRoomSample>();
        lock (_roomGate)
        {
            foreach (var room in _roomsByPassword.Values)
            {
                var players = new List<(int Id, string Nick)>();
                foreach (var (nr, name, _) in room.Actors)
                {
                    if (nr == MatchHostActor.ActorNr || string.IsNullOrWhiteSpace(name))
                        continue;

                    var id = AccountIdOfActorLocked(room, nr);
                    if (id == 0 && Accounts is null)
                        id = LegacyNickHash.ForNick(name); // тесты/проба без аккаунтов
                    if (id > 0)
                        players.Add((id, name));
                }

                var phase = room.Flow.Phase;
                var key = room.DisplayNumber > 0 ? "n" + room.DisplayNumber : "r" + room.RoomId;
                list.Add(new StatsRoomSample(
                    key,
                    Playing: StatsPhaseCountable(phase),
                    Ended: StatsPhaseEnded(phase),
                    Players: players));
            }
        }
        return list;
    }

    /// <summary>Фазы, в которых бой действительно идёт — только они дают секунды и киллы.</summary>
    internal static bool StatsPhaseCountable(MatchFlowPhase phase) => phase switch
    {
        MatchFlowPhase.PurchasePhase or MatchFlowPhase.RoundLive or MatchFlowPhase.BombPlanted
        or MatchFlowPhase.RoundEndPause or MatchFlowPhase.DeathMatchLive or MatchFlowPhase.DeathMatchWarmup
        or MatchFlowPhase.HalfTimeIntro or MatchFlowPhase.HalfTimeSwap or MatchFlowPhase.HalfTimeTransition
        or MatchFlowPhase.AlliesPreWarmup => true,
        _ => false,
    };

    /// <summary>Забег кончился: катка сыграна или комната снова просто ждёт людей.</summary>
    internal static bool StatsPhaseEnded(MatchFlowPhase phase) => phase switch
    {
        MatchFlowPhase.WaitingPlayers or MatchFlowPhase.MatchOver
        or MatchFlowPhase.DeathMatchEnded or MatchFlowPhase.DeathMatchFinalHud => true,
        _ => false,
    };

    /// <summary>Засчитать килл киллеру. Зовут из боевых путей, когда жертва разрешена.</summary>
    private void NoteStatsKill(MatchRoom room, byte killerActorNr)
    {
        var stats = Stats;
        if (stats is null || killerActorNr == 0 || killerActorNr == MatchHostActor.ActorNr)
            return;

        int id;
        string nick;
        lock (_roomGate)
        {
            if (!StatsPhaseCountable(room.Flow.Phase))
                return;
            if (!TryActorName(room, killerActorNr, out nick))
                return;
            id = AccountIdOfActorLocked(room, killerActorNr);
            if (id == 0 && Accounts is null)
                id = LegacyNickHash.ForNick(nick);
        }

        stats.NoteKill(id, nick);
    }

    /// <summary>Засчитать смерть жертве. Зовут из точек коммита смерти.</summary>
    private void NoteStatsDeath(MatchRoom room, byte victimActorNr)
    {
        // История матча считает смерти в тех же точках и не зависит от того, включена ли
        // общая статистика: запись должна собраться и при выключенном stats-сторе.
        NoteHistoryDeath(room, victimActorNr);

        var stats = Stats;
        if (stats is null || victimActorNr == 0 || victimActorNr == MatchHostActor.ActorNr)
            return;

        int id;
        string nick;
        lock (_roomGate)
        {
            if (!StatsPhaseCountable(room.Flow.Phase))
                return;
            if (!TryActorName(room, victimActorNr, out nick))
                return;
            id = AccountIdOfActorLocked(room, victimActorNr);
            if (id == 0 && Accounts is null)
                id = LegacyNickHash.ForNick(nick);
        }

        stats.NoteDeath(id, nick);
    }

    private static bool TryActorName(MatchRoom room, byte actorNr, out string nick)
    {
        foreach (var (nr, name, _) in room.Actors)
        {
            if (nr != actorNr)
                continue;
            nick = name;
            return !string.IsNullOrWhiteSpace(nick);
        }
        nick = "";
        return false;
    }
}
