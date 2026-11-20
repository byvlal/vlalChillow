using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Bans;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Кастомный отображаемый ID админа: то, что видят игроки вместо числового.
/// </summary>
/// <remarks>
/// <para>
/// Оригинальный номер аккаунта нигде не теряется: баны, <c>admin.ids</c> и статистика
/// продолжают работать по нему — подменяется только то, что показывают клиентам.
/// </para>
/// <para>
/// Файл <c>customids.json</c> рядом с исполняемым файлом (как <c>stats.json</c> и
/// <c>banlist.json</c>): рестарт ничего не теряет. Запись атомарная (tmp → замена),
/// сразу при изменении — меняют её редко, отложенный сброс не нужен.
/// Битый файл не роняет сервер — хранилище стартует пустым.
/// </para>
/// <para>
/// Правила значения: только цифры и английские буквы, 1..24 символа, хотя бы одна
/// буква — чисто цифровой ID сливался бы с номером аккаунта (спека владельца).
/// Уникальность — без учёта регистра: два админа не должны выглядеть одинаково.
/// </para>
/// </remarks>
public sealed class CustomIdStore
{
    /// <summary>Сколько символов допускаем: короткий читаемый игровой ID.</summary>
    public const int MaxLength = 24;

    /// <summary>Шаблон валидного значения: A-Z, a-z, 0-9, 1..24, хотя бы одна буква.</summary>
    public const string Pattern = "^(?=.*[A-Za-z])[A-Za-z0-9]{1,24}$";

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Dictionary<int, Entry> _byId = new();

    private sealed class Entry
    {
        public int Id { get; set; }
        public string Nick { get; set; } = "";
        public string CustomId { get; set; } = "";
        public DateTime UpdatedUtc { get; set; }
    }

    private sealed class Root
    {
        public List<Entry> Customs { get; set; } = new();
    }

    public CustomIdStore(string path)
    {
        _path = path;
        Load();
        Console.WriteLine($"[customid] хранилище: {_path} (в памяти {_byId.Count} кастомных)");
    }

    /// <summary>Сколько админов поставили себе кастомный ID.</summary>
    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>Снимок записей для миграции аккаунтов: id, ник и само значение.</summary>
    public IReadOnlyList<(int Id, string Nick, string CustomId)> Export()
    {
        lock (_gate)
            return _byId.Values
                .OrderBy(e => e.Id)
                .Select(e => (e.Id, e.Nick, e.CustomId))
                .ToList();
    }

    /// <summary>Путь файла — для логов.</summary>
    public string FilePath => _path;

    /// <summary>Кастомный ID игрока или <c>null</c> — показываем обычный числовой.</summary>
    public string? Get(int playerId)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
            return _byId.TryGetValue(playerId, out var entry) ? entry.CustomId : null;
    }

    /// <summary>
    /// Владелец кастомного ID по его значению (без учёта регистра). Нужно поиску
    /// игрока на сайте: игроки видят в игре именно кастомный ID, а не число.
    /// </summary>
    public int? FindPlayerByCustomId(string? customId)
    {
        if (string.IsNullOrWhiteSpace(customId))
            return null;

        var value = customId.Trim();
        lock (_gate)
        {
            foreach (var (id, entry) in _byId)
            {
                if (string.Equals(entry.CustomId, value, StringComparison.OrdinalIgnoreCase))
                    return id;
            }
        }

        return null;
    }

    /// <summary>
    /// Поставить кастомный ID. <paramref name="customId"/> пустой/_null — сброс к числовому.
    /// Возвращает текст ошибки или <c>null</c> при успехе.
    /// </summary>
    public string? Set(int playerId, string? nick, string? customId)
    {
        if (playerId <= 0)
            return "неизвестный игрок";

        if (string.IsNullOrWhiteSpace(customId))
            return Clear(playerId);

        var value = customId.Trim();
        if (value.Length > MaxLength)
            return $"максимум {MaxLength} символов (сейчас {value.Length})";
        if (!System.Text.RegularExpressions.Regex.IsMatch(value, Pattern))
            return "только цифры и английские буквы, хотя бы одна буква";

        lock (_gate)
        {
            // Уникальность без учёта регистра: Vlal и vlal — одно и то же лицо для игроков.
            foreach (var other in _byId.Values)
            {
                if (other.Id != playerId
                    && string.Equals(other.CustomId, value, StringComparison.OrdinalIgnoreCase))
                    return $"«{value}» уже занят другим админом";
            }

            _byId[playerId] = new Entry
            {
                Id = playerId,
                Nick = nick ?? "",
                CustomId = value,
                UpdatedUtc = DateTime.UtcNow,
            };
        }

        Save();
        return null;
    }

    /// <summary>Сбросить кастомный ID — снова виден числовой. Возвращает ошибку или <c>null</c>.</summary>
    public string? Clear(int playerId)
    {
        lock (_gate)
        {
            if (!_byId.Remove(playerId))
                return null; // сбрасывать нечего — это не ошибка
        }

        Save();
        return null;
    }

    /// <summary>
    /// Снять кастомные ID у всех, кто больше не админ. Возвращает, сколько снято.
    /// </summary>
    /// <remarks>
    /// Права живут в <c>admin.ids</c> и меняются правкой config.json на лету. Кастомный
    /// ID — привилегия админа: перестал быть админом — значение снимается с диска, а не
    /// просто прячется, иначе вернувшиеся права вернули бы старую строку.
    /// </remarks>
    public int PruneNonAdmins(Func<int, bool> isAdmin)
    {
        List<Entry> removed;
        lock (_gate)
        {
            removed = _byId.Values.Where(e => !isAdmin(e.Id)).ToList();
            foreach (var entry in removed)
                _byId.Remove(entry.Id);
        }

        if (removed.Count == 0)
            return 0;

        Save();
        return removed.Count;
    }

    /// <summary>
    /// Перевести кастомные ID со старых хешей ника на аккаунты. Записи, попавшие на
    /// один аккаунт, склеиваются (побеждает более свежая); неизвестные — выбрасываются.
    /// Возвращает число изменённых записей.
    /// </summary>
    /// <remarks>
    /// <paramref name="map"/> вызывается для каждой записи: старый хеш ника может быть
    /// больше <see cref="AccountStore.FirstId"/> и выглядеть как номер аккаунта.
    /// Настоящий номер функция возвращает без изменений.
    /// </remarks>
    public int RemapAccounts(Func<int, string, int> map)
    {
        List<Entry> snapshot;
        lock (_gate)
            snapshot = _byId.Values.OrderBy(e => e.UpdatedUtc).ToList();

        var changed = 0;
        var next = new Dictionary<int, Entry>();
        var usedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in snapshot)
        {
            var mapped = entry.Id > 0 ? map(entry.Id, entry.Nick) : 0;
            if (mapped is < AccountStore.FirstId or > AccountStore.MaxId)
            {
                changed++;
                continue;
            }

            if (next.ContainsKey(mapped))
            {
                changed++;
                // Более свежая запись уже стоит последней: старую просто не добавляем.
                continue;
            }

            if (!usedValues.Add(entry.CustomId))
            {
                changed++;
                continue;
            }

            if (mapped != entry.Id)
            {
                entry.Id = mapped;
                changed++;
            }

            next[mapped] = entry;
        }

        if (changed == 0)
            return 0;

        lock (_gate)
        {
            _byId.Clear();
            foreach (var (id, entry) in next)
                _byId[id] = entry;
        }

        Save();
        return changed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            using var stream = File.OpenRead(_path);
            var root = JsonSerializer.Deserialize<Root>(stream, JsonOpts);
            if (root?.Customs is null)
                return;

            lock (_gate)
            {
                _byId.Clear();
                foreach (var entry in root.Customs)
                {
                    if (entry is null || entry.Id <= 0)
                        continue;
                    if (string.IsNullOrEmpty(entry.CustomId))
                        continue;
                    // Значение из файла перепроверяем тем же правилом: руками файл править
                    // можно, но мусор туда попадать не должен.
                    if (!System.Text.RegularExpressions.Regex.IsMatch(entry.CustomId, Pattern))
                        continue;
                    _byId[entry.Id] = entry;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[customid] WARN не прочитал {_path}: {ex.Message} — стартую пустым");
        }
    }

    private void Save()
    {
        List<Entry> snapshot;
        lock (_gate)
            snapshot = _byId.Values.OrderBy(e => e.Id).ToList();

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
            {
                JsonSerializer.Serialize(stream, new Root { Customs = snapshot }, JsonOpts);
            }
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[customid] WARN не записал {_path}: {ex.Message}");
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
