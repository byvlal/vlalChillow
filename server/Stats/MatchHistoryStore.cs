using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Bans;

namespace StandChillow.LanServer.Stats;

/// <summary>
/// История матчей на диске: последние <see cref="MaxMatches"/> каток.
/// </summary>
/// <remarks>
/// <para>
/// Файл <c>matches.json</c> рядом с исполняемым файлом (там же, где <c>stats.json</c> и
/// <c>banlist.json</c>). Формат отдельный: <c>stats.json</c> — агрегаты за всё время,
/// историю матчей туда не мешаем, чтобы не ломать старые записи.
/// </para>
/// <para>
/// Запись атомарная (tmp → замена) и отложенная: диск трогаем не чаще раза в
/// <see cref="SaveIntervalSec"/> секунд, плюс <see cref="Flush"/> на остановке. Битый файл
/// не роняет сервер — история стартует пустой.
/// </para>
/// <para>
/// Память — рабочий кэш, источник правды диск. Читатели (веб-панель) получают записи
/// под замком; после добавления запись не меняется, так что отдавать её на чтение безопасно.
/// </para>
/// </remarks>
public sealed class MatchHistoryStore
{
    public const int MaxMatches = 300;
    private const int SaveIntervalSec = 5;

    private sealed class Root
    {
        public List<MatchHistoryRecord> Matches { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTime> _clock;
    private readonly List<MatchHistoryRecord> _matches = new();
    private bool _dirty;
    private DateTime _lastSaveUtc = DateTime.MinValue;
    private int _nextId;

    public MatchHistoryStore(string path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>Где лежит файл — для стартового лога.</summary>
    public string FilePath => _path;

    public int Count { get { lock (_gate) return _matches.Count; } }

    /// <summary>
    /// Перевести игроков в истории со старых хешей ника на аккаунты. Если два старых id
    /// попали на один аккаунт, строки склеиваются: накопительные числа берутся
    /// максимумом, смерти складываются. Возвращает число изменённых матчей.
    /// </summary>
    /// <remarks>
    /// <paramref name="map"/> вызывается для каждого игрока: старый хеш ника может быть
    /// больше <see cref="AccountStore.FirstId"/> и выглядеть как номер аккаунта.
    /// Настоящий номер функция возвращает без изменений.
    /// </remarks>
    public int RemapAccountIds(Func<int, string, int> map)
    {
        lock (_gate)
        {
            var changed = 0;
            foreach (var match in _matches)
            {
                if (match?.Players is null || match.Players.Count == 0)
                    continue;

                var merged = new Dictionary<int, MatchHistoryPlayer>();
                var order = new List<int>();
                var matchChanged = false;

                foreach (var player in match.Players)
                {
                    if (player is null)
                        continue;

                    var mapped = player.Id > 0 ? map(player.Id, player.Nick) : 0;

                    if (mapped is < AccountStore.FirstId or > AccountStore.MaxId)
                    {
                        matchChanged = true;
                        continue;
                    }

                    if (mapped != player.Id)
                    {
                        player.Id = mapped;
                        matchChanged = true;
                    }

                    if (!merged.TryGetValue(mapped, out var existing))
                    {
                        merged[mapped] = player;
                        order.Add(mapped);
                        continue;
                    }

                    existing.Kills = Math.Max(existing.Kills, player.Kills);
                    existing.Assists = Math.Max(existing.Assists, player.Assists);
                    existing.Score = Math.Max(existing.Score, player.Score);
                    existing.Deaths += player.Deaths;
                    existing.Money = player.Money;
                    existing.Ping = player.Ping;
                    if (player.Team is "CT" or "T")
                        existing.Team = player.Team;
                    if (!string.IsNullOrWhiteSpace(player.Nick))
                        existing.Nick = player.Nick;
                    matchChanged = true;
                }

                if (!matchChanged)
                    continue;

                match.Players = order.Select(id => merged[id]).ToList();
                changed++;
            }

            if (changed == 0)
                return 0;

            _dirty = true;
            SaveLocked();
            return changed;
        }
    }

    /// <summary>Сколько матчей храним — для лога и тестов.</summary>
    public int Capacity => MaxMatches;

    /// <summary>
    /// Добавить запись. Id выдаёт само хранилище (по возрастанию, не переиспользуется),
    /// старые записи вытесняются, когда список перерос <see cref="MaxMatches"/>.
    /// </summary>
    public MatchHistoryRecord Add(MatchHistoryRecord record)
    {
        if (record is null)
            throw new ArgumentNullException(nameof(record));

        lock (_gate)
        {
            record.Id = _nextId++;
            if (record.EndedUtc == default)
                record.EndedUtc = _clock();
            if (record.StartedUtc == default)
                record.StartedUtc = record.EndedUtc;

            _matches.Insert(0, record);
            if (_matches.Count > MaxMatches)
                _matches.RemoveRange(MaxMatches, _matches.Count - MaxMatches);

            _dirty = true;
            MaybeSaveLocked();
            return record;
        }
    }

    /// <summary>Матч по номеру или <c>null</c>.</summary>
    public MatchHistoryRecord? Get(int id)
    {
        lock (_gate)
        {
            foreach (var m in _matches)
            {
                if (m.Id == id)
                    return m;
            }
            return null;
        }
    }

    /// <summary>
    /// Последняя записанная катка этой комнаты — чтобы завершённая <c>#/room/&lt;n&gt;</c>
    /// вела на полную статистику, а не оставалась тупиком.
    /// </summary>
    /// <remarks>
    /// Номера комнат переиспользуются (наименьший свободный), поэтому «последняя» — это
    /// именно самая свежая запись с таким <see cref="MatchHistoryRecord.RoomNumber"/>,
    /// а не любая: старую катку #2 после её закрытия не показываем.
    /// </remarks>
    public MatchHistoryRecord? LatestForRoom(int roomNumber)
    {
        if (roomNumber <= 0)
            return null;

        lock (_gate)
        {
            MatchHistoryRecord? best = null;
            foreach (var m in _matches)
            {
                if (m.RoomNumber != roomNumber)
                    continue;
                if (best is null || m.EndedUtc > best.EndedUtc)
                    best = m;
            }
            return best;
        }
    }

    /// <summary>Последние матчи игрока, новые сверху.</summary>
    public IReadOnlyList<MatchHistoryRecord> ForPlayer(int playerId, int limit)
    {
        if (playerId <= 0)
            return Array.Empty<MatchHistoryRecord>();
        limit = Math.Clamp(limit, 1, 100);

        lock (_gate)
        {
            var list = new List<MatchHistoryRecord>(limit);
            foreach (var m in _matches)
            {
                if (m.Player(playerId) is null)
                    continue;
                list.Add(m);
                if (list.Count >= limit)
                    break;
            }
            return list;
        }
    }

    /// <summary>Просто последние матчи — пригодится для общей страницы.</summary>
    public IReadOnlyList<MatchHistoryRecord> Latest(int limit)
    {
        limit = Math.Clamp(limit, 1, 100);
        lock (_gate)
        {
            var count = Math.Min(limit, _matches.Count);
            var list = new List<MatchHistoryRecord>(count);
            for (var i = 0; i < count; i++)
                list.Add(_matches[i]);
            return list;
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

    private void MaybeSaveLocked()
    {
        if (!_dirty || (_clock() - _lastSaveUtc).TotalSeconds < SaveIntervalSec)
            return;
        SaveLocked();
    }

    private void SaveLocked()
    {
        try
        {
            var root = new Root { Matches = _matches };
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
            // Не сохранили — не беда: попробуем в следующий раз. Падать нельзя.
            Console.WriteLine($"[history] не сохранился {_path}: {ex.Message}");
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                _nextId = 1;
                return;
            }

            var root = JsonSerializer.Deserialize<Root>(File.ReadAllText(_path), JsonOptions);
            if (root?.Matches is null)
            {
                _nextId = 1;
                return;
            }

            foreach (var m in root.Matches)
            {
                if (m is null || m.Id <= 0)
                    continue;
                if (_matches.Count >= MaxMatches)
                    break;
                _matches.Add(m);
                if (m.Id >= _nextId)
                    _nextId = m.Id + 1;
            }

            Console.WriteLine(
                $"[history] загрузил {_matches.Count} матчей из {_path}");
        }
        catch (Exception ex)
        {
            // Битый файл — история стартует пустой, сервер не падает.
            Console.WriteLine($"[history] файл {_path} не читается ({ex.Message}) — начинаю с нуля");
            _matches.Clear();
            _nextId = 1;
        }
    }
}

/// <summary>
/// Сшивание участников матча: последние два раунда + финальный состав.
/// </summary>
/// <remarks>
/// Игрок, который был в предпоследнем раунде и вышел за секунду до конца, обязан попасть
/// в итоги — поэтому берём объединение, а не только тех, кто остался в комнате. Свежие
/// данные (команда, деньги, пинг) перекрывают старые; киллы/помощи/счёт — накопительные,
/// поэтому берём максимум, чтобы уход из комнаты не «обнулил» статистику.
/// </remarks>
public static class MatchHistoryMerge
{
    /// <summary>
    /// Собрать список игроков записи.
    /// </summary>
    /// <param name="rounds">Снимки последних раундов, старые раньше новых.</param>
    /// <param name="final">Кто был в комнате на момент финала.</param>
    /// <param name="deaths">Смерти по номеру актёра за весь забег.</param>
    public static List<MatchHistoryPlayer> Build(
        IReadOnlyList<IReadOnlyDictionary<byte, MatchHistoryPlayer>> rounds,
        IReadOnlyDictionary<byte, MatchHistoryPlayer> final,
        IReadOnlyDictionary<byte, int> deaths)
    {
        var merged = new Dictionary<int, MatchHistoryPlayer>();
        var order = new List<int>();
        var actorsById = new Dictionary<int, HashSet<byte>>();

        void RememberActor(MatchHistoryPlayer player)
        {
            if (!actorsById.TryGetValue(player.Id, out var actors))
            {
                actors = new HashSet<byte>();
                actorsById[player.Id] = actors;
            }
            actors.Add((byte)player.ActorNr);
        }

        foreach (var round in rounds)
        {
            foreach (var player in round.Values)
            {
                if (player.Id <= 0 || string.IsNullOrWhiteSpace(player.Nick))
                    continue;

                RememberActor(player);
                if (!merged.TryGetValue(player.Id, out var existing))
                {
                    merged[player.Id] = Clone(player);
                    order.Add(player.Id);
                    continue;
                }

                // Свежий раунд перекрывает команду и «живые» числа; накопительные — максимум.
                // Наблюдатель в снимке TDM не должен затирать последнюю боевую сторону.
                if (player.Team is "CT" or "T")
                    existing.Team = player.Team;
                existing.ActorNr = player.ActorNr;
                existing.Kills = Math.Max(existing.Kills, player.Kills);
                existing.Assists = Math.Max(existing.Assists, player.Assists);
                existing.Score = Math.Max(existing.Score, player.Score);
                existing.Money = player.Money;
                existing.Ping = player.Ping;
            }
        }

        foreach (var player in final.Values)
        {
            if (player.Id <= 0 || string.IsNullOrWhiteSpace(player.Nick))
                continue;

            RememberActor(player);
            if (!merged.TryGetValue(player.Id, out var existing))
            {
                merged[player.Id] = Clone(player);
                order.Add(player.Id);
                continue;
            }

            // Наблюдатель в финальном составе не должен затирать боевую команду:
            // уход в спектаторы — не смена стороны и не повод выкинуть из итогов.
            if (player.Team is "CT" or "T")
                existing.Team = player.Team;
            existing.ActorNr = player.ActorNr;
            existing.Kills = Math.Max(existing.Kills, player.Kills);
            existing.Assists = Math.Max(existing.Assists, player.Assists);
            existing.Score = Math.Max(existing.Score, player.Score);
            existing.Money = player.Money;
            existing.Ping = player.Ping;
            existing.Left = false;
        }

        var result = new List<MatchHistoryPlayer>(merged.Count);
        foreach (var id in order)
        {
            var player = merged[id];
            // Зрители, не сыгравшие ни одного раунда, в итоговую таблицу не входят.
            if (player.Team is not ("CT" or "T"))
                continue;
            result.Add(player);
        }

        foreach (var player in result)
        {
            if (actorsById.TryGetValue(player.Id, out var actors))
            {
                var total = 0;
                foreach (var actor in actors)
                {
                    if (deaths.TryGetValue(actor, out var count))
                        total += count;
                }
                player.Deaths = total;
            }

            // В финальном составе его уже нет — значит вышел до конца катки.
            var presentAtEnd = false;
            foreach (var endPlayer in final.Values)
            {
                if (endPlayer.Id == player.Id)
                {
                    presentAtEnd = true;
                    break;
                }
            }
            if (!presentAtEnd)
                player.Left = true;
        }

        // Порядок как на экране итогов: сначала CT, потом T, внутри — по убийствам.
        result.Sort((a, b) =>
        {
            var teamA = TeamRank(a.Team);
            var teamB = TeamRank(b.Team);
            if (teamA != teamB)
                return teamA.CompareTo(teamB);
            if (a.Kills != b.Kills)
                return b.Kills.CompareTo(a.Kills);
            return string.Compare(a.Nick, b.Nick, StringComparison.OrdinalIgnoreCase);
        });

        return result;
    }

    private static int TeamRank(string team) => team switch
    {
        "CT" => 0,
        "T" => 1,
        _ => 2,
    };

    private static MatchHistoryPlayer Clone(MatchHistoryPlayer p) => new()
    {
        Id = p.Id,
        Nick = p.Nick,
        Team = p.Team,
        Kills = p.Kills,
        Assists = p.Assists,
        Deaths = p.Deaths,
        Score = p.Score,
        Money = p.Money,
        Ping = p.Ping,
        Left = p.Left,
        ActorNr = p.ActorNr,
    };
}
