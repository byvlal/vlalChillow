using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match;
using StandChillow.LanServer.Net.Match.Host;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Stats;

namespace StandChillow.LanServer.Net;

/// <summary>
/// История матчей: кто был в катке и с какими цифрами.
/// </summary>
/// <remarks>
/// <para>
/// Сбор идёт без работы в горячем пути пакетов: раз в тик хоста (≈30 Гц) смотрим, появился
/// ли в комнате замороженный состав раунда (<see cref="MatchFlowState.RoundRoster"/>), и
/// запоминаем снимок. Храним два последних раунда — этого хватает, чтобы поймать игрока,
/// который вышел за секунду до конца катки.
/// </para>
/// <para>
/// Смерти считает сервер в тех же точках, где и общую статистику (<c>NoteStatsDeath</c>).
/// Киллы/помощи/счёт/деньги/пинг читаются из actor props — как в таблице дашборда, ничего
/// не выдумываем.
/// </para>
/// <para>
/// Запись создаётся один раз на забег: на финале (<c>MatchOver</c>), на «Завершить игру»
/// с сайта и при очистке комнаты. Катка без единого раунда записи не получает.
/// </para>
/// </remarks>
public sealed partial class GameMatchHost
{
    /// <summary>Хранилище истории. <c>null</c> — история не ведётся (панель выключена).</summary>
    public MatchHistoryStore? History { get; set; }

    private readonly object _historyGate = new();
    private readonly Dictionary<string, HistoryRun> _historyRuns = new(StringComparer.Ordinal);

    /// <summary>Состояние одного забега комнаты.</summary>
    private sealed class HistoryRun
    {
        /// <summary>Снимки раундов, старые раньше новых; не больше двух.</summary>
        public readonly List<Dictionary<byte, MatchHistoryPlayer>> Rounds = new(2);
        public readonly Dictionary<byte, int> Deaths = new();
        public DateTime StartedUtc { get; set; }
        public int CapturedRound { get; set; } = -1;
        /// <summary>Когда последний раз снимали состав TDM: у него нет раундов.</summary>
        public DateTime LastTdmSnapshotUtc { get; set; }
        public bool Saved { get; set; }

        public void Reset()
        {
            Rounds.Clear();
            Deaths.Clear();
            StartedUtc = default;
            CapturedRound = -1;
            LastTdmSnapshotUtc = default;
            Saved = false;
        }
    }

    private HistoryRun HistoryRunLocked(string key)
    {
        if (!_historyRuns.TryGetValue(key, out var run))
        {
            run = new HistoryRun();
            _historyRuns[key] = run;
        }
        return run;
    }

    /// <summary>
    /// Тик истории: запомнить состав раунда, когда он заморозился.
    /// </summary>
    /// <remarks>
    /// Вызывается из <c>PollLoop</c> после <c>TickMatchFlow</c>. Замороженный состав
    /// (<c>RoundRoster</c>) живёт весь раунд, поэтому снимок успевает сняться до очистки
    /// в <c>ClearBombAuthority</c>.
    /// </remarks>
    internal void TickMatchHistory()
    {
        if (History is null)
            return;

        List<MatchRoom> rooms;
        lock (_roomGate)
            rooms = _roomsByPassword.Values.ToList();

        foreach (var room in rooms)
        {
            MatchFlowPhase phase;
            int round;
            string? wireMode = null;
            Dictionary<byte, MatchTeam>? roster = null;

            lock (_roomGate)
            {
                phase = room.Flow.Phase;
                round = room.Flow.RoundIndex;
                if (room.Flow.RoundRoster.Count > 0)
                    roster = new Dictionary<byte, MatchTeam>(room.Flow.RoundRoster);
                if (room.ActorProps.TryGetValue(
                        (0, MatchRoomPropKeys.C0), out var c0)
                    && c0.Kind == LobbyVariantKind.String
                    && c0.String is not null)
                {
                    wireMode = c0.String;
                }
            }

            if (phase == MatchFlowPhase.WaitingPlayers)
            {
                lock (_historyGate)
                {
                    HistoryRunLocked(room.PasswordKey).Reset();
                    _historyRuns.Remove(room.PasswordKey);
                }
                continue;
            }

            // TDM: раундов нет, состав снимаем раз в 5 секунд и держим два последних
            // снимка — этого хватает, чтобы поймать того, кто вышел под конец катки.
            if (phase == MatchFlowPhase.DeathMatchLive)
            {
                DateTime now = DateTime.UtcNow;
                bool needTdm;
                lock (_historyGate)
                {
                    var run = HistoryRunLocked(room.PasswordKey);
                    if (run.StartedUtc == default)
                        run.StartedUtc = now;
                    needTdm = (now - run.LastTdmSnapshotUtc).TotalSeconds >= 5;
                }

                if (!needTdm)
                    continue;

                Dictionary<byte, MatchHistoryPlayer> tdmSnapshot;
                lock (_roomGate)
                    tdmSnapshot = SnapshotHistoryPlayersLocked(room, null);

                if (tdmSnapshot.Count == 0)
                    continue;

                lock (_historyGate)
                {
                    var run = HistoryRunLocked(room.PasswordKey);
                    if (run.StartedUtc == default)
                        run.StartedUtc = now;
                    run.Rounds.Add(tdmSnapshot);
                    if (run.Rounds.Count > 2)
                        run.Rounds.RemoveAt(0);
                    run.LastTdmSnapshotUtc = now;
                }
                continue;
            }

            if (round <= 0)
                continue;

            // Duel и Escalation не замораживают RoundRoster: их бой идёт в PurchasePhase,
            // поэтому снимаем состав по актёрам один раз за раунд. Бомбовые режимы в этой
            // же фазе ещё только готовятся — у них ждём RoundRoster, чтобы не поймать
            // зрителей и опоздавших.
            var useRoster = roster is { Count: > 0 };
            if (!useRoster
                && (phase != MatchFlowPhase.PurchasePhase
                    || MatchPlantPolicy.IsBombFamilyMode(wireMode)))
                continue;

            bool needCapture;
            lock (_historyGate)
            {
                var run = HistoryRunLocked(room.PasswordKey);
                needCapture = run.CapturedRound != round;
            }

            if (!needCapture)
                continue;

            Dictionary<byte, MatchHistoryPlayer> snapshot;
            lock (_roomGate)
                snapshot = SnapshotHistoryPlayersLocked(room, useRoster ? roster : null);

            if (snapshot.Count == 0)
                continue;

            lock (_historyGate)
            {
                var run = HistoryRunLocked(room.PasswordKey);
                if (run.CapturedRound == round)
                    continue;

                if (run.StartedUtc == default)
                    run.StartedUtc = DateTime.UtcNow;

                run.Rounds.Add(snapshot);
                if (run.Rounds.Count > 2)
                    run.Rounds.RemoveAt(0);
                run.CapturedRound = round;
            }
        }
    }

    /// <summary>
    /// Засчитать смерть игроку в текущем забеге. Зовут из <c>NoteStatsDeath</c>.
    /// </summary>
    internal void NoteHistoryDeath(MatchRoom room, byte actorNr)
    {
        if (History is null || actorNr == 0 || actorNr == MatchHostActor.ActorNr)
            return;

        // Смерти в неигровых фазах (варм-ап, ожидание) в катку не идут.
        lock (_roomGate)
        {
            if (!StatsPhaseCountable(room.Flow.Phase))
                return;
        }

        lock (_historyGate)
        {
            var run = HistoryRunLocked(room.PasswordKey);
            run.Deaths[actorNr] = run.Deaths.TryGetValue(actorNr, out var count) ? count + 1 : 1;
        }
    }

    /// <summary>
    /// Записать матч в историю. Идемпотентно: второй вызов для того же забега — no-op.
    /// </summary>
    /// <param name="room">Комната, чей забег закончился.</param>
    /// <param name="reason">Откуда пришли (лог).</param>
    internal void RecordMatchHistory(MatchRoom room, string reason)
    {
        var history = History;
        if (history is null)
            return;

        Dictionary<byte, MatchHistoryPlayer> final;
        MatchFlowPhase phase;
        int round, scoreTr, scoreCt, number;
        string modeId, map, roomName;

        lock (_roomGate)
        {
            final = SnapshotHistoryPlayersLocked(room, null);
            phase = room.Flow.Phase;
            round = room.Flow.RoundIndex;
            scoreTr = room.Flow.ScoreTr;
            scoreCt = room.Flow.ScoreCt;
            number = room.DisplayNumber;
            roomName = room.DisplayName;

            var wireMode = room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C0), out var c0)
                           && c0.Kind == LobbyVariantKind.String && c0.String is not null
                ? c0.String
                : _matchGameModeId;
            modeId = MatchModeIds.ToCatalog(wireMode);

            map = room.ActorProps.TryGetValue((0, MatchRoomPropKeys.C1), out var c1)
                  && c1.Kind == LobbyVariantKind.String && c1.String is not null
                ? c1.String
                : _matchSelectedLevel;
        }

        MatchHistoryRecord? record = null;
        lock (_historyGate)
        {
            var run = HistoryRunLocked(room.PasswordKey);
            // Катка без единого раунда записи не получает. Для TDM раундов нет:
            // признак старта — снимок, снятый в Live, или сам StartedUtc.
            if (run.Saved)
                return;
            if (run.Rounds.Count == 0 && run.StartedUtc == default)
                return;

            var players = MatchHistoryMerge.Build(run.Rounds, final, run.Deaths);
            if (players.Count == 0)
                return;

            var ended = DateTime.UtcNow;
            record = new MatchHistoryRecord
            {
                StartedUtc = run.StartedUtc == default ? ended : run.StartedUtc,
                EndedUtc = ended,
                ModeId = modeId,
                Map = map,
                ScoreCt = scoreCt,
                ScoreTr = scoreTr,
                Winner = scoreCt > scoreTr ? "CT" : scoreTr > scoreCt ? "T" : "DRAW",
                Rounds = Math.Max(0, round),
                RoomNumber = number,
                RoomName = roomName ?? "",
                Players = players,
            };

            run.Saved = true;
            run.Rounds.Clear();
            run.Deaths.Clear();
        }

        if (record is null)
            return;

        try
        {
            var saved = history.Add(record);
            Console.WriteLine(
                $"[history] матч #{saved.Id}: {saved.ModeId} «{saved.Map}» " +
                $"{saved.ScoreCt}:{saved.ScoreTr} ({saved.Winner}) раундов {saved.Rounds}, " +
                $"игроков {saved.Players.Count} — {reason}, фаза {phase}");
        }
        catch (Exception ex)
        {
            // История не должна ронять матч: не сохранилась — скажем в лог и жим дальше.
            Console.WriteLine($"[history] матч не записан: {ex.Message}");
        }
    }

    /// <summary>
    /// Снимок игроков комнаты. Вызывать под <c>_roomGate</c>.
    /// </summary>
    /// <param name="room">Комната.</param>
    /// <param name="roster">
    /// Замороженный состав раунда; <c>null</c> — берём всех, кто есть в комнате сейчас.
    /// </param>
    private Dictionary<byte, MatchHistoryPlayer> SnapshotHistoryPlayersLocked(
        MatchRoom room, IReadOnlyDictionary<byte, MatchTeam>? roster)
    {
        var result = new Dictionary<byte, MatchHistoryPlayer>();
        var names = new Dictionary<byte, string>();
        foreach (var (nr, name, _) in room.Actors)
        {
            if (!string.IsNullOrWhiteSpace(name))
                names[nr] = name;
        }

        IEnumerable<KeyValuePair<byte, MatchTeam>> source;
        if (roster is { Count: > 0 })
        {
            source = roster.Select(kv => new KeyValuePair<byte, MatchTeam>(kv.Key, kv.Value));
        }
        else
        {
            source = room.Actors
                .Where(a => a.Nr != MatchHostActor.ActorNr)
                .Select(a => new KeyValuePair<byte, MatchTeam>(
                    a.Nr, MatchFlowRules.GetActorTeam(room.ActorProps, a.Nr)));
        }

        foreach (var (nr, team) in source)
        {
            if (nr == MatchHostActor.ActorNr)
                continue;
            if (!names.TryGetValue(nr, out var nick) || string.IsNullOrWhiteSpace(nick))
                continue;

            var id = AccountIdOfActorLocked(room, nr);
            if (id == 0 && Accounts is null)
                id = LegacyNickHash.ForNick(nick); // тесты/проба без аккаунтов
            if (id <= 0)
                continue;

            var kills = ReadActorIntProp(room, nr, MatchRoomPropKeys.Kills);
            if (kills < 0)
                kills = ReadActorIntProp(room, nr, MatchRoomPropKeys.FairKills);

            result[nr] = new MatchHistoryPlayer
            {
                Id = id,
                Nick = nick,
                Team = TeamKey(team),
                Kills = Math.Max(0, kills),
                Assists = Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Assists)),
                Score = Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Score2)),
                Money = Math.Max(0, ReadActorIntProp(room, nr, MatchRoomPropKeys.Money)),
                Ping = ReadActorIntProp(room, nr, MatchRoomPropKeys.Ping),
                ActorNr = nr,
            };
        }

        return result;
    }

    private static string TeamKey(MatchTeam team) => team switch
    {
        MatchTeam.Ct => "CT",
        MatchTeam.Tr => "T",
        MatchTeam.Spectator => "SPEC",
        _ => "NONE",
    };
}
