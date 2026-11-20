using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Bans;

/// <summary>
/// <c>banlist.json</c> рядом с конфигом: баны и справочник «ник ↔ id ↔ IP».
/// </summary>
/// <remarks>
/// <para>
/// Две секции в одном файле: <c>bans</c> — кто забанен, <c>players</c> — кого сервер
/// вообще видел (нужно, чтобы <c>/ban</c> работал для уже вышедшего игрока, в том
/// числе после рестарта).
/// </para>
/// <para>
/// Схема v3 сбрасывает все баны старых версий: в v1/v2 реестр учил ник/IP/устройство
/// слишком агрессивно, и в записях могли остаться чужие адреса и телефоны. Справочник
/// при миграции сохраняется, но без старых <c>ips</c>/<c>devices</c> — их заново
/// соберут только реальные входы под новой логикой.
/// </para>
/// <para>
/// Запись атомарная: сначала <c>banlist.json.tmp</c>, потом замена. Битый файл не
/// роняет сервер — логируется и стартует пустым, а не падает на первом входе.
/// </para>
/// </remarks>
public sealed class BanStore
{
    /// <summary>
    /// Текущая схема файла. v3 сбрасывает старые баны и чистит изученные IP/устройства
    /// в справочнике: до неё сервер мог приклеить чужой адрес к чужому бану.
    /// </summary>
    public const int CurrentVersion = 3;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public BanStore(string path) => Path = path;

    /// <summary>Полный путь к файлу.</summary>
    public string Path { get; }

    /// <summary>Версия, которую реально прочитали (0 — файла не было).</summary>
    public int LastLoadedVersion { get; private set; }

    /// <summary>
    /// Файл новее этой сборки: читаем, но не сохраняем, чтобы не потерять незнакомые поля.
    /// </summary>
    public bool IsReadOnly { get; private set; }

    /// <summary>
    /// Читает файл; отсутствие/битый JSON — пустые списки и строка в лог.
    /// Старые v1/v2 копируются в <c>banlist.json.vN.bak</c>, баны сбрасываются,
    /// справочник переписывается без IP/устройств.
    /// </summary>
    public (List<BanRecord> Bans, List<SeenPlayer> Players) Load()
    {
        try
        {
            if (!File.Exists(Path))
                return (new List<BanRecord>(), new List<SeenPlayer>());

            var json = File.ReadAllText(Path);
            if (string.IsNullOrWhiteSpace(json))
                return (new List<BanRecord>(), new List<SeenPlayer>());

            var doc = JsonSerializer.Deserialize<FileDto>(json, JsonOptions);
            // Нет поля version — это старый формат без номера, то есть v1 (а не «v0»).
            var version = doc?.Version ?? 0;
            if (version <= 0)
                version = 1;
            var bans = doc?.Bans ?? new List<BanRecord>();
            var players = doc?.Players ?? new List<SeenPlayer>();
            LastLoadedVersion = version;

            if (version > CurrentVersion)
            {
                IsReadOnly = true;
                Console.WriteLine(
                    $"[ban] {Path}: версия {version} новее поддерживаемой {CurrentVersion} — " +
                    "читаю, но сохранять не буду");
            }
            else if (version < CurrentVersion)
            {
                MigrateToV3(version, bans, players);
            }

            Console.WriteLine(
                $"[ban] прочитано из {Path}: версия {version}, банов {bans.Count}, " +
                $"личностей в справочнике {players.Count}");
            return (bans, players);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] не смог прочитать {Path}: {ex.Message} — начинаю с пустого списка");
            return (new List<BanRecord>(), new List<SeenPlayer>());
        }
    }

    /// <summary>
    /// v1/v2 → v3: бэкап, обнуление банов, очистка IP/устройств в справочнике.
    /// </summary>
    /// <remarks>
    /// Бэкап — best-effort: если скопировать не удалось, активный файл всё равно
    /// переписывается. Старые баны по условию задачи не мигрируются — их место
    /// занимает новая логика, а не подозрительные записи прошлой.
    /// </remarks>
    private void MigrateToV3(int fromVersion, List<BanRecord> bans, List<SeenPlayer> players)
    {
        var backup = Path + $".v{fromVersion}.bak";
        if (File.Exists(backup))
            backup = Path + $".v{fromVersion}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";

        try
        {
            File.Copy(Path, backup);
            Console.WriteLine($"[ban] миграция v{fromVersion}→v{CurrentVersion}: бэкап {backup}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ban] миграция v{fromVersion}→v{CurrentVersion}: бэкап {backup} не удался " +
                $"({ex.Message}) — старые баны всё равно сбрасываю");
        }

        var oldBans = bans.Count;
        bans.Clear();

        var sanitized = SanitizePlayers(players, out var cleaned, out var merged);
        players.Clear();
        players.AddRange(sanitized);

        if (Save(bans, players))
        {
            LastLoadedVersion = CurrentVersion;
            Console.WriteLine(
                $"[ban] миграция v{fromVersion}→v{CurrentVersion}: старых банов сброшено {oldBans}, " +
                $"личностей {players.Count}, IP/Devices очищено у {cleaned}, " +
                $"дублей ников склеено {merged}, активный файл v{CurrentVersion}");
        }
    }

    /// <summary>
    /// Оставить в справочнике только ник, id и время последнего входа.
    /// IP и устройства старых версий не заслуживают доверия: они могли попасть туда
    /// при входе под забаненным ником и потом приклеиться к чужому бану.
    /// </summary>
    private static List<SeenPlayer> SanitizePlayers(
        List<SeenPlayer> players,
        out int cleaned,
        out int merged)
    {
        cleaned = 0;
        merged = 0;

        var byNick = new Dictionary<string, SeenPlayer>(StringComparer.OrdinalIgnoreCase);
        foreach (var player in players)
        {
            if (player is null || string.IsNullOrWhiteSpace(player.Nick))
                continue;

            var nick = ServerConfig.NormalizeNick(player.Nick);
            if (nick.Length == 0)
                continue;

            if (player.Ips is { Count: > 0 } || player.Devices is { Count: > 0 })
                cleaned++;

            if (byNick.TryGetValue(nick, out var existing))
            {
                if (player.LastSeenUtcMs > existing.LastSeenUtcMs)
                {
                    existing.LastSeenUtcMs = player.LastSeenUtcMs;
                    existing.Id = player.Id;
                }

                merged++;
                continue;
            }

            byNick[nick] = new SeenPlayer
            {
                Id = player.Id,
                Nick = nick,
                LastSeenUtcMs = player.LastSeenUtcMs,
            };
        }

        return byNick.Values.ToList();
    }

    /// <summary>Пишет файл атомарно. Никогда не бросает — вызывающему нужен только факт.</summary>
    public bool Save(IReadOnlyList<BanRecord> bans, IReadOnlyList<SeenPlayer> players)
    {
        if (IsReadOnly)
        {
            Console.WriteLine(
                $"[ban] {Path}: файл новее поддерживаемой схемы — сохранение пропущено");
            return false;
        }

        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);

            var dto = new FileDto
            {
                Version = CurrentVersion,
                Bans = bans.ToList(),
                Players = players.ToList(),
            };
            var json = JsonSerializer.Serialize(dto, JsonOptions);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ban] не смог сохранить {Path}: {ex.Message}");
            return false;
        }
    }

    private sealed class FileDto
    {
        [JsonPropertyName("version")]
        public int Version { get; set; } = 1;

        [JsonPropertyName("bans")]
        public List<BanRecord>? Bans { get; set; } = new();

        [JsonPropertyName("players")]
        public List<SeenPlayer>? Players { get; set; } = new();
    }
}
