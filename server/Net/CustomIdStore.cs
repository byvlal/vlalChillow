using System.Text.Json;
using System.Text.Json.Serialization;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Кастомный отображаемый ID админа: то, что видят игроки вместо числового.
/// </summary>
/// <remarks>
/// <para>
/// Оригинальный числовой ID нигде не теряется: он детерминирован от ника
/// (<see cref="Bans.MatchPublicId.ForNick"/>), поэтому баны, <c>admin.ids</c> и статистика
/// продолжают работать по-прежнему — подменяется только то, что показывают клиентам.
/// </para>
/// <para>
/// Файл <c>customids.json</c> рядом с исполняемым файлом (как <c>stats.json</c> и
/// <c>banlist.json</c>): рестарт ничего не теряет. Запись атомарная (tmp → замена),
/// сразу при изменении — меняют её редко, отложенный сброс не нужен.
/// Битый файл не роняет сервер — хранилище стартует пустым.
/// </para>
/// <para>
/// Правила значения: только цифры и английские буквы, 1..24 символа (спека владельца).
/// Уникальность — без учёта регистра: два админа не должны выглядеть одинаково.
/// </para>
/// </remarks>
public sealed class CustomIdStore
{
    /// <summary>Сколько символов допускаем: короткий читаемый игровой ID.</summary>
    public const int MaxLength = 24;

    /// <summary>Шаблон валидного значения: только A-Z, a-z, 0-9, непусто.</summary>
    public const string Pattern = "^[A-Za-z0-9]{1,24}$";

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
            return "только цифры и английские буквы";

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
