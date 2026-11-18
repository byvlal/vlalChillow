using System.Text.Json;
using System.Text.Json.Serialization;

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
/// Запись атомарная: сначала <c>banlist.json.tmp</c>, потом замена. Битый файл не
/// роняет сервер — логируется и стартует пустым, а не падает на первом входе.
/// </para>
/// </remarks>
public sealed class BanStore
{
    /// <summary>Текущая схема файла. v2 добавила <c>Devices</c> к банам и игрокам.</summary>
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Бэкап v1 не удался: до перезапуска файл нельзя перезаписывать, иначе исходник
    /// пропадёт без копии.
    /// </summary>
    private bool _migrationBackupFailed;

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
    /// Старый v1 автоматически копируется в <c>banlist.json.v1.bak</c> и переписывается в v2.
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
                MigrateV1ToV2(version, bans, players);
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
    /// v1 → v2: сначала бэкап, потом атомарная перезапись. Если бэкап не удался —
    /// файл не трогаем до перезапуска: v1 читается и работает, миграция повторится
    /// на следующем старте. Флаг обязателен: иначе первый же бан/алиас вызвал бы
    /// <see cref="Save"/> и перезаписал v1 без копии.
    /// </summary>
    private void MigrateV1ToV2(int fromVersion, List<BanRecord> bans, List<SeenPlayer> players)
    {
        var backup = Path + ".v1.bak";
        try
        {
            if (!File.Exists(backup))
                File.Copy(Path, backup);
        }
        catch (Exception ex)
        {
            _migrationBackupFailed = true;
            Console.WriteLine(
                $"[ban] миграция v{fromVersion}→v{CurrentVersion}: бэкап {backup} не удался " +
                $"({ex.Message}) — файл не перезаписываю до перезапуска");
            return;
        }

        if (Save(bans, players))
        {
            LastLoadedVersion = CurrentVersion;
            Console.WriteLine(
                $"[ban] миграция v{fromVersion}→v{CurrentVersion}: банов {bans.Count}, " +
                $"личностей {players.Count}, бэкап {backup}");
        }
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

        if (_migrationBackupFailed)
        {
            Console.WriteLine(
                $"[ban] {Path}: миграция v1→v2 не завершена (бэкап не удался) — " +
                "сохранение пропущено, исходный файл не перезаписываю");
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
