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
/// Схема v4 — разовая перенумерация аккаунтов: у всех уже выданных банов снимаются
/// теги <c>matchIds</c>. IP, устройства, ники и алиасы остаются, поэтому читеры
/// продолжают ловиться по железу и адресу, а невинный, которому после перенумерации
/// достанется старый номер, не наследует чужой бан. Баны, выданные после v4, снова
/// получают ID аккаунта — миграция уже произошла, номер принадлежит тому же человеку.
/// </para>
/// <para>
/// Схема v5 добавляет каждой записи стабильный номер <c>BanId</c> и приводит все IP
/// к каноническому виду (<c>::ffff:1.2.3.4</c> → <c>1.2.3.4</c>): без этого один и
/// тот же адрес в разных формах считался разными людьми.
/// </para>
/// <para>
/// Запись атомарная: сначала <c>banlist.json.tmp</c>, потом замена. Битый файл не
/// роняет сервер — логируется и стартует пустым, а не падает на первом входе.
/// </para>
/// </remarks>
public sealed class BanStore
{
    /// <summary>
    /// Текущая схема файла. v3 сбросила баны v1/v2 и почистила изученные IP/устройства;
    /// v4 снимает старые ID аккаунтов с уже выданных банов, сохраняя IP/устройства/ники;
    /// v5 выдаёт стабильные номера банов и канонизирует адреса.
    /// </summary>
    public const int CurrentVersion = 5;

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
    /// v1/v2 копируются в <c>banlist.json.vN.bak</c>, баны сбрасываются, справочник
    /// переписывается без IP/устройств. v3 → v4: копия и снятие старых ID с банов,
    /// IP/устройства/ники остаются. v4 → v5: копия, выдача номеров банов и канон адресов.
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
                Migrate(version, bans, players);
            }
            else if (!IsReadOnly)
            {
                // Файл уже текущей схемы, но мог быть записан старой сборкой: без
                // канона адресов и номеров банов. Доводим в памяти и переписываем.
                var normalized = NormalizeRecords(bans);
                if (normalized.IpsChanged > 0 || normalized.BanIds > 0)
                {
                    Save(bans, players);
                    Console.WriteLine(
                        $"[ban] v5: номеров выдано {normalized.BanIds}, " +
                        $"адресов приведено к канону у {normalized.IpsChanged} записей");
                }
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
    /// Довести файл до текущей схемы: v1/v2 → v3 (сброс старых банов и очистка
    /// справочника), v3 → v4 (снять старые ID с банов, остальное сохранить),
    /// v4 → v5 (выдать номера банов и канонизировать адреса).
    /// </summary>
    /// <remarks>
    /// Бэкап — best-effort: если скопировать не удалось, активный файл всё равно
    /// переписывается. Важно не запускать v3-сброс на файле v3: он бы уничтожил
    /// уже нормальные баны. Поэтому шаги идут по отдельности, а не одним
    /// <c>version &lt; CurrentVersion</c>.
    /// </remarks>
    private void Migrate(int fromVersion, List<BanRecord> bans, List<SeenPlayer> players)
    {
        Backup(fromVersion);

        if (fromVersion < 3)
        {
            var oldBans = bans.Count;
            bans.Clear();

            var sanitized = SanitizePlayers(players, out var cleaned, out var merged);
            players.Clear();
            players.AddRange(sanitized);

            Console.WriteLine(
                $"[ban] миграция v{fromVersion}→v3: старых банов сброшено {oldBans}, " +
                $"личностей {players.Count}, IP/Devices очищено у {cleaned}, " +
                $"дублей ников склеено {merged}");
        }

        if (fromVersion < 4)
        {
            var (records, ids, withoutKeys) = ClearMatchIds(bans);
            Console.WriteLine(
                $"[ban] миграция v3→v4: старые ID сняты у {records} банов (тегов {ids}); " +
                $"без IP/устройства осталось {withoutKeys} — это бан только по нику");
        }

        if (fromVersion < 5)
        {
            var normalized = NormalizeRecords(bans);
            Console.WriteLine(
                $"[ban] миграция v4→v5: номеров банов выдано {normalized.BanIds}, " +
                $"адресов приведено к канону у {normalized.IpsChanged} записей");
        }

        if (Save(bans, players))
        {
            LastLoadedVersion = CurrentVersion;
            Console.WriteLine(
                $"[ban] миграция v{fromVersion}→v{CurrentVersion}: активный файл v{CurrentVersion}");
        }
    }

    /// <summary>Копия файла до миграции. Имя — по исходной версии, чтобы не затереть старую копию.</summary>
    private void Backup(int version)
    {
        var backup = Path + $".v{version}.bak";
        if (File.Exists(backup))
            backup = Path + $".v{version}.{DateTime.Now:yyyyMMdd-HHmmss}.bak";

        try
        {
            File.Copy(Path, backup);
            Console.WriteLine($"[ban] миграция v{version}: бэкап {backup}");
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[ban] миграция v{version}: бэкап {backup} не удался ({ex.Message}) — продолжаю");
        }
    }

    /// <summary>
    /// Довести записи до v5: выдать номера банов и канонизировать IP. Возвращает,
    /// сколько номеров выдано и у скольких записей адреса реально изменились.
    /// </summary>
    private static (int Records, int IpsChanged, int BanIds) NormalizeRecords(List<BanRecord> bans)
    {
        var records = 0;
        var ipsChanged = 0;
        var banIds = 0;

        foreach (var record in bans)
        {
            if (record is null)
                continue;

            records++;
            record.Aliases ??= new List<string>();
            record.Devices ??= new List<string>();
            record.MatchIds ??= new List<int>();
            record.Ips ??= new List<string>();

            var normalizedIps = record.Ips
                .Select(BanRecord.NormalizeIp)
                .Where(ip => ip.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (!record.Ips.SequenceEqual(normalizedIps, StringComparer.Ordinal))
            {
                record.Ips = normalizedIps;
                ipsChanged++;
            }

            if (string.IsNullOrWhiteSpace(record.BanId))
            {
                record.EnsureBanId();
                banIds++;
            }
        }

        return (records, ipsChanged, banIds);
    }

    /// <summary>
    /// Снять старые ID аккаунтов со всех банов. Запись, у которой нет ни IP, ни
    /// устройства, остаётся баном по нику — ключи не выдумываем.
    /// </summary>
    private static (int Records, int Ids, int WithoutKeys) ClearMatchIds(List<BanRecord> bans)
    {
        var records = 0;
        var ids = 0;
        var withoutKeys = 0;

        foreach (var record in bans)
        {
            if (record is null)
                continue;

            record.MatchIds ??= new List<int>();
            if (record.MatchIds.Count > 0)
            {
                records++;
                ids += record.MatchIds.Count;
                record.MatchIds.Clear();
            }

            if (record.Ips is not { Count: > 0 } && record.Devices is not { Count: > 0 })
                withoutKeys++;
        }

        return (records, ids, withoutKeys);
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

        // Точный ник — личность: «Skipp» и «skipp» остаются разными записями.
        var byNick = new Dictionary<string, SeenPlayer>(StringComparer.Ordinal);
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
