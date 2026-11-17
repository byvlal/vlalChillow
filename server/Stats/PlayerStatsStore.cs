using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Net;

namespace StandChillow.LanServer.Stats;

/// <summary>Снимок статистики одного игрока — то, что отдаёт API.</summary>
/// <param name="Id">Серверный id (<see cref="MatchPublicId.ForNick"/>).</param>
/// <param name="Nick">Последний замеченный ник.</param>
/// <param name="Kills">Киллы за всё время.</param>
/// <param name="Deaths">Смерти за всё время.</param>
/// <param name="Seconds">Секунды в живых матчах.</param>
/// <param name="Matches">Уникальные матчи: один забег комнаты считается один раз.</param>
public sealed record PlayerStatsSnapshot(
    int Id, string Nick, long Kills, long Deaths, long Seconds, long Matches);

/// <summary>
/// Статистика игроков: киллы, смерти, время в матчах, уникальные матчи — на диске.
/// </summary>
/// <remarks>
/// <para>
/// Файл <c>stats.json</c> рядом с исполняемым файлом (тот же каталог, где живёт
/// <c>banlist.json</c>). Память — только рабочий кэш: источник правды диск, и после
/// рестарта цифры продолжаются с того же места.
/// </para>
/// <para>
/// Запись атомарная (tmp → замена), отложенная: сохранение не чаще раза в десять
/// секунд по dirty-флагу, плюс принудительный <see cref="Flush"/> на остановке.
/// Битый файл не роняет сервер — статистика стартует пустой.
/// </para>
/// <para>
/// «Сыграно матчей» считает не заходы, а забеги: одна комната от старта катки до
/// конца — один матч, сколько бы раз игрок в неё ни возвращался. Забег кончается,
/// когда комната ушла, закончилась или вернулась в ожидание.
/// </para>
/// </remarks>
public sealed class PlayerStatsStore
{
    private const int SaveIntervalSec = 10;

    private sealed class StatsEntry
    {
        public string Nick { get; set; } = "";
        public long Kills { get; set; }
        public long Deaths { get; set; }
        public long Seconds { get; set; }
        public long Matches { get; set; }
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class Root
    {
        public Dictionary<string, StatsEntry> Players { get; set; } = new();
    }

    /// <summary>Состояние одного забега комнаты: кто уже засчитан в этот матч.</summary>
    private sealed class RunState
    {
        public readonly HashSet<int> Seen = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTime> _clock;
    private readonly Dictionary<int, StatsEntry> _players = new();
    private readonly Dictionary<string, RunState> _runs = new(StringComparer.Ordinal);
    private bool _dirty;
    private DateTime _lastSaveUtc = DateTime.MinValue;

    public PlayerStatsStore(string path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>Где лежит файл — для стартового лога.</summary>
    public string FilePath => _path;

    /// <summary>Сколько игроков со статистикой сейчас в памяти.</summary>
    public int Count { get { lock (_gate) return _players.Count; } }

    public void NoteKill(int id, string nick) => Bump(id, nick, kills: 1);

    public void NoteDeath(int id, string nick) => Bump(id, nick, deaths: 1);

    private void Bump(int id, string nick, long kills = 0, long deaths = 0)
    {
        if (id <= 0)
            return;

        lock (_gate)
        {
            var e = EntryOf(id);
            e.Kills += kills;
            e.Deaths += deaths;
            if (!string.IsNullOrWhiteSpace(nick))
                e.Nick = nick;
            e.UpdatedUtc = _clock();
            _dirty = true;
        }
    }

    /// <summary>
    /// Семплировать комнаты: насчитать секунды и уникальные матчи по живым забегам.
    /// </summary>
    /// <remarks>
    /// Вызывается раз в ~15 секунд из фонового тика панели. Комната сообщает сама:
    /// идёт ли бой (<paramref name="rooms"/>.<c>Playing</c>), кончился ли забег
    /// (<c>Ended</c>) и кто из людей в ней сейчас. Секунды идут только по Playing;
    /// матч засчитывается один раз на забег — перезашёл в ту же комнату, второй раз
    /// не считается.
    /// </remarks>
    public void SampleRooms(IReadOnlyList<StatsRoomSample> rooms, int stepSeconds)
    {
        if (rooms.Count == 0)
            return;

        lock (_gate)
        {
            var now = _clock();
            var present = new HashSet<string>(rooms.Select(r => r.Key), StringComparer.Ordinal);

            // Комната исчезла из матча — её забег закончен, состояние выбрасываем.
            foreach (var gone in _runs.Keys.Where(k => !present.Contains(k)).ToList())
                _runs.Remove(gone);

            var changed = false;
            foreach (var room in rooms)
            {
                if (room.Ended)
                {
                    // Катка закончилась (или комната вернулась в ожидание): следующий
                    // Live в этой комнате — новый матч, а не продолжение старого.
                    _runs.Remove(room.Key);
                    continue;
                }

                if (!room.Playing)
                    continue;

                if (!_runs.TryGetValue(room.Key, out var run))
                {
                    run = new RunState();
                    _runs[room.Key] = run;
                }

                foreach (var nick in room.Nicks)
                {
                    var id = MatchPublicId.ForNick(nick);
                    if (id <= 0)
                        continue;

                    var e = EntryOf(id);
                    e.Seconds += stepSeconds;
                    if (!string.IsNullOrWhiteSpace(nick))
                        e.Nick = nick;
                    e.UpdatedUtc = now;
                    if (run.Seen.Add(id))
                        e.Matches++;
                    changed = true;
                }
            }

            if (changed)
                _dirty = true;
        }

        MaybeSave();
    }

    /// <summary>Статистика игрока или <c>null</c>, если сервер о нём ничего не знает.</summary>
    public PlayerStatsSnapshot? Get(int id)
    {
        lock (_gate)
        {
            if (!_players.TryGetValue(id, out var e))
                return null;
            return new PlayerStatsSnapshot(id, e.Nick, e.Kills, e.Deaths, e.Seconds, e.Matches);
        }
    }

    /// <summary>Сохранить немедленно, если есть что сохранять. Зовут на остановке.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty)
                return;
            SaveLocked();
        }
    }

    private void MaybeSave()
    {
        lock (_gate)
        {
            if (!_dirty || (_clock() - _lastSaveUtc).TotalSeconds < SaveIntervalSec)
                return;
            SaveLocked();
        }
    }

    private void SaveLocked()
    {
        try
        {
            var root = new Root();
            foreach (var (id, e) in _players)
                root.Players[id.ToString(CultureInfo.InvariantCulture)] = e;

            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(root, JsonOptions));
            if (File.Exists(_path))
                File.Replace(tmp, _path, null);
            else
                File.Move(tmp, _path);

            _dirty = false;
            _lastSaveUtc = _clock();
        }
        catch (Exception ex)
        {
            // Не сохранили — не беда: попробуем в следующий тик. Падать нельзя.
            Console.WriteLine($"[stats] не сохранился {_path}: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var root = JsonSerializer.Deserialize<Root>(File.ReadAllText(_path), JsonOptions);
            if (root?.Players is null)
                return;

            foreach (var (key, e) in root.Players)
            {
                if (!int.TryParse(key, CultureInfo.InvariantCulture, out var id) || id <= 0 || e is null)
                    continue;
                _players[id] = e;
            }

            Console.WriteLine(
                $"[stats] загрузил {_players.Count} игроков из {_path}");
        }
        catch (Exception ex)
        {
            // Битый файл — статистика стартует пустой, сервер не падает.
            Console.WriteLine($"[stats] файл {_path} не читается ({ex.Message}) — начинаю с нуля");
            _players.Clear();
        }
    }

    private StatsEntry EntryOf(int id)
    {
        if (!_players.TryGetValue(id, out var e))
        {
            e = new StatsEntry();
            _players[id] = e;
        }
        return e;
    }
}
