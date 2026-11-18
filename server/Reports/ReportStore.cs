using System.Text.Json;
using System.Text.Json.Serialization;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Reports;

/// <summary>
/// Жалобы игроков: <c>reports.json</c> рядом с исполняемым файлом.
/// </summary>
/// <remarks>
/// <para>
/// Файл — единственный источник правды: рестарт ничего не теряет. Запись атомарная
/// (tmp → замена), битый файл не роняет сервер — он откладывается в сторону, хранилище
/// стартует пустым. Формат версионирован: файл новее сервера читается, но не
/// перезаписывается.
/// </para>
/// <para>
/// Жалоба — не бан и никогда не банит автоматически. Счётчик в профиле считает
/// открытые и подтверждённые жалобы за последние <see cref="BadgeWindowDays"/> дней;
/// отклонённые админом не считаются вовсе.
/// </para>
/// <para>
/// Чтобы смена ника не обнуляла историю, жалоба хранит и числовой ID, и ключи
/// устройств цели (когда сервер их знает). При следующем входе устройства
/// дописываются в старые жалобы через <see cref="LearnDevices"/>.
/// </para>
/// </remarks>
public sealed class ReportStore
{
    public const int CurrentVersion = 1;
    public const int MaxReports = 10_000;
    public const int MaxReasonLength = 500;
    public const int MaxAdminNoteLength = 200;
    public const int BadgeWindowDays = 30;
    public const int DailyLimit = 5;
    public const int CooldownSeconds = 20;
    public const int DuplicateWindowHours = 24;
    public const int MaxDevicesPerReport = 8;

    private sealed class Root
    {
        public int Version { get; set; } = CurrentVersion;
        public List<ReportRecord> Reports { get; set; } = new();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    private readonly string _path;
    private readonly Func<DateTime> _clock;
    private readonly List<ReportRecord> _reports = new();
    private bool _readOnly;
    private bool _dirty;

    public ReportStore(string path, Func<DateTime>? clock = null)
    {
        _path = path;
        _clock = clock ?? (() => DateTime.UtcNow);
        Load();
    }

    /// <summary>Где лежит файл — для стартового лога.</summary>
    public string FilePath => _path;

    /// <summary>Сколько жалоб сейчас в памяти.</summary>
    public int Count { get { lock (_gate) return _reports.Count; } }

    /// <summary>Версия файла, которую прочитали при старте (0 — файла не было).</summary>
    public int LastLoadedVersion { get; private set; }

    /// <summary>Файл новее поддерживаемого: читаем, но не перезаписываем.</summary>
    public bool IsReadOnly { get { lock (_gate) return _readOnly; } }

    /// <summary>
    /// Добавить жалобу. Ник и устройства цели приходят от сервера, а не из тела
    /// запроса: подставить чужой ник или ID нельзя.
    /// </summary>
    public ReportSubmitResult Add(
        int targetId,
        string targetNick,
        bool targetAdmin,
        IEnumerable<string>? targetDevices,
        int reporterId,
        string reporterNick,
        string category,
        string reason,
        DateTime nowUtc)
    {
        if (targetId <= 0 || reporterId <= 0)
            return new(ReportSubmitError.Storage, null, 0);
        if (targetId == reporterId)
            return new(ReportSubmitError.Self, null, 0);

        var devices = NormalizeDevices(targetDevices);
        var reasonText = NormalizeReason(reason);

        lock (_gate)
        {
            if (_readOnly)
                return new(ReportSubmitError.Storage, null, 0);

            var duplicateSince = nowUtc.AddHours(-DuplicateWindowHours);
            var recent = _reports
                .Where(r => r.ReporterId == reporterId
                            && FromMs(r.CreatedUtcMs) >= duplicateSince)
                .OrderByDescending(r => r.CreatedUtcMs)
                .ToList();

            var duplicate = recent.FirstOrDefault(r => SameTarget(r, targetId, devices));
            if (duplicate is not null)
            {
                var left = (int)Math.Ceiling(
                    (FromMs(duplicate.CreatedUtcMs).AddHours(DuplicateWindowHours) - nowUtc)
                    .TotalSeconds);
                return new(ReportSubmitError.Duplicate, null, Math.Max(60, left));
            }

            if (recent.Count >= DailyLimit)
            {
                var oldest = recent[^1];
                var left = (int)Math.Ceiling(
                    (FromMs(oldest.CreatedUtcMs).AddHours(24) - nowUtc).TotalSeconds);
                return new(ReportSubmitError.DailyLimit, null, Math.Max(60, left));
            }

            var last = _reports
                .Where(r => r.ReporterId == reporterId)
                .OrderByDescending(r => r.CreatedUtcMs)
                .FirstOrDefault();
            if (last is not null)
            {
                var elapsed = (nowUtc - FromMs(last.CreatedUtcMs)).TotalSeconds;
                if (elapsed < CooldownSeconds)
                {
                    var left = (int)Math.Ceiling(CooldownSeconds - elapsed);
                    return new(ReportSubmitError.Cooldown, null, Math.Max(1, left));
                }
            }

            var record = new ReportRecord
            {
                Id = NewId(),
                TargetId = targetId,
                TargetNick = ServerConfig.NormalizeNick(targetNick),
                TargetAdmin = targetAdmin,
                TargetDevices = devices,
                ReporterId = reporterId,
                ReporterNick = ServerConfig.NormalizeNick(reporterNick),
                Category = ReportCategories.IsValid(category) ? category : ReportCategories.Other,
                Reason = reasonText,
                CreatedUtcMs = ToMs(nowUtc),
                Status = ReportStatuses.Open,
            };

            _reports.Add(record);
            TrimLocked();
            _dirty = true;

            if (!SaveLocked())
            {
                // Не притворяемся, что жалоба принята: без файла она исчезнет
                // после рестарта, а человек будет думать, что она у админа.
                _reports.Remove(record);
                _dirty = false;
                return new(ReportSubmitError.Storage, null, 0);
            }

            _dirty = false;
            return new(ReportSubmitError.None, record, 0);
        }
    }

    /// <summary>
    /// Сколько жалоб у цели: по числовому ID и по известным устройствам. Окно —
    /// <see cref="BadgeWindowDays"/> дней; отклонённые не считаются.
    /// </summary>
    public ReportCounts CountsFor(
        int targetId,
        IEnumerable<string>? devices,
        DateTime nowUtc)
    {
        var cutoff = ToMs(nowUtc.AddDays(-BadgeWindowDays));
        var keys = new HashSet<string>(
            NormalizeDevices(devices), StringComparer.OrdinalIgnoreCase);

        lock (_gate)
        {
            var total = 0;
            var open = 0;
            var accepted = 0;

            foreach (var report in _reports)
            {
                if (report.CreatedUtcMs < cutoff)
                    continue;
                if (report.Status == ReportStatuses.Rejected)
                    continue;
                if (!Matches(report, targetId, keys))
                    continue;

                total++;
                if (report.Status == ReportStatuses.Accepted)
                    accepted++;
                else
                    open++;
            }

            return new ReportCounts(total, open, accepted);
        }
    }

    /// <summary>Страница жалоб для админской модерации. <paramref name="status"/> пустой — все.</summary>
    public ReportPage List(string? status, int page, int pageSize)
    {
        lock (_gate)
        {
            IEnumerable<ReportRecord> filtered = _reports;
            if (ReportStatuses.IsValid(status))
                filtered = filtered.Where(r => r.Status == status);

            return PageOf(filtered, page, pageSize);
        }
    }

    /// <summary>Страница «моих жалоб»: только этот автор, новые сверху.</summary>
    public ReportPage ListByReporter(int reporterId, int page, int pageSize)
    {
        lock (_gate)
            return PageOf(_reports.Where(r => r.ReporterId == reporterId), page, pageSize);
    }

    /// <summary>Счётчики для шапки админской вкладки.</summary>
    public ReportSummary Summary()
    {
        lock (_gate)
        {
            return new ReportSummary(
                _reports.Count(r => r.Status == ReportStatuses.Open),
                _reports.Count(r => r.Status == ReportStatuses.Accepted),
                _reports.Count(r => r.Status == ReportStatuses.Rejected));
        }
    }

    /// <summary>
    /// Разобрать жалобу. <paramref name="status"/> — accepted/rejected/open;
    /// <paramref name="note"/> короткая, в файл и в панель.
    /// </summary>
    public (string? Error, ReportRecord? Record) SetStatus(
        string reportId,
        string status,
        string reviewedBy,
        string? note,
        DateTime nowUtc)
    {
        if (!ReportStatuses.IsValid(status))
            return ("статус — accepted, rejected или open", null);
        if (string.IsNullOrWhiteSpace(reviewedBy))
            return ("неизвестно, кто разбирал", null);

        lock (_gate)
        {
            if (_readOnly)
                return ("файл жалоб новее сервера — запись запрещена", null);

            var record = _reports.FirstOrDefault(r =>
                string.Equals(r.Id, reportId, StringComparison.Ordinal));
            if (record is null)
                return ("жалоба не найдена", null);

            record.Status = status;
            record.ReviewedBy = ServerConfig.NormalizeNick(reviewedBy);
            record.ReviewedUtcMs = ToMs(nowUtc);
            record.AdminNote = NormalizeNote(note);
            _dirty = true;

            if (!SaveLocked())
            {
                _dirty = false;
                return ("не удалось сохранить reports.json", null);
            }

            _dirty = false;
            return (null, record);
        }
    }

    /// <summary>
    /// Дописать устройства к жалобам цели: человек зашёл с тем же телефоном под новым
    /// ником — старые жалобы должны остаться на устройстве, а не только на старом ID.
    /// </summary>
    /// <returns>Сколько записей изменилось.</returns>
    public int LearnDevices(int targetId, IEnumerable<string>? devices, DateTime nowUtc)
    {
        _ = nowUtc;
        if (targetId <= 0)
            return 0;

        var fresh = NormalizeDevices(devices);
        if (fresh.Count == 0)
            return 0;

        lock (_gate)
        {
            if (_readOnly)
                return 0;

            var changed = 0;
            foreach (var report in _reports)
            {
                if (report.TargetId != targetId)
                    continue;
                if (report.TargetDevices.Count >= MaxDevicesPerReport)
                    continue;

                var added = false;
                foreach (var device in fresh)
                {
                    if (report.TargetDevices.Count >= MaxDevicesPerReport)
                        break;
                    if (report.TargetDevices.Any(d => string.Equals(
                            d, device, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    report.TargetDevices.Add(device);
                    added = true;
                }

                if (added)
                    changed++;
            }

            if (changed > 0)
            {
                _dirty = true;
                if (SaveLocked())
                    _dirty = false;
            }

            return changed;
        }
    }

    /// <summary>Сохранить немедленно, если есть что сохранять. Зовут на остановке.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty || _readOnly)
                return;
            if (SaveLocked())
                _dirty = false;
        }
    }

    /// <summary>Копия записей для тестов и диагностики.</summary>
    public IReadOnlyList<ReportRecord> Export()
    {
        lock (_gate)
            return _reports.ToList();
    }

    // ── загрузка / сохранение ────────────────────────────────────────────────

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            Root? root;
            using (var stream = File.OpenRead(_path))
                root = JsonSerializer.Deserialize<Root>(stream, JsonOptions);

            var version = root?.Version ?? 0;
            LastLoadedVersion = version;
            if (version > CurrentVersion)
            {
                _readOnly = true;
                Console.WriteLine(
                    $"[report] {_path}: версия {version} новее поддерживаемой " +
                    $"{CurrentVersion} — читаю, но не перезаписываю");
            }

            if (root?.Reports is null)
                return;

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var report in root.Reports)
            {
                if (report is null || report.TargetId <= 0 || report.ReporterId <= 0)
                    continue;

                var id = (report.Id ?? "").Trim();
                if (id.Length == 0 || !seenIds.Add(id))
                {
                    id = NewId();
                    if (!seenIds.Add(id))
                        continue;
                }

                report.Id = id;
                report.TargetNick = ServerConfig.NormalizeNick(report.TargetNick);
                report.ReporterNick = ServerConfig.NormalizeNick(report.ReporterNick);
                report.TargetDevices = NormalizeDevices(report.TargetDevices);
                report.Category = ReportCategories.IsValid(report.Category)
                    ? report.Category
                    : ReportCategories.Other;
                report.Reason = NormalizeReason(report.Reason);
                report.Status = ReportStatuses.IsValid(report.Status)
                    ? report.Status
                    : ReportStatuses.Open;
                if (report.CreatedUtcMs <= 0)
                    continue;
                report.ReviewedBy = string.IsNullOrWhiteSpace(report.ReviewedBy)
                    ? null
                    : ServerConfig.NormalizeNick(report.ReviewedBy);
                report.AdminNote = NormalizeNote(report.AdminNote);

                _reports.Add(report);
            }

            _reports.Sort((a, b) => a.CreatedUtcMs.CompareTo(b.CreatedUtcMs));
            TrimLocked();
        }
        catch (Exception ex)
        {
            Console.WriteLine(
                $"[report] WARN не прочитал {_path}: {ex.Message} — стартую пустым");
            PreserveCorruptFile();
            _reports.Clear();
        }
    }

    /// <summary>Не удалять битый файл молча: он может понадобиться для разбора.</summary>
    private void PreserveCorruptFile()
    {
        try
        {
            if (!File.Exists(_path))
                return;
            var backup = _path + ".corrupt.bak";
            File.Copy(_path, backup, overwrite: true);
            Console.WriteLine($"[report] битый файл сохранён как {backup}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[report] WARN не смог сохранить копию битого файла: {ex.Message}");
        }
    }

    private bool SaveLocked()
    {
        if (_readOnly)
            return false;

        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var root = new Root
            {
                Version = CurrentVersion,
                Reports = _reports.ToList(),
            };

            var tmp = _path + ".tmp";
            using (var stream = File.Create(tmp))
                JsonSerializer.Serialize(stream, root, JsonOptions);
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[report] WARN не записал {_path}: {ex.Message}");
            return false;
        }
    }

    // ── мелочи ───────────────────────────────────────────────────────────────

    private void TrimLocked()
    {
        if (_reports.Count <= MaxReports)
            return;
        _reports.Sort((a, b) => a.CreatedUtcMs.CompareTo(b.CreatedUtcMs));
        _reports.RemoveRange(0, _reports.Count - MaxReports);
    }

    private static ReportPage PageOf(
        IEnumerable<ReportRecord> source, int page, int pageSize)
    {
        var safePage = Math.Max(1, page);
        var safeSize = Math.Clamp(pageSize, 1, 50);
        var list = source
            .OrderByDescending(r => r.CreatedUtcMs)
            .ThenByDescending(r => r.Id, StringComparer.Ordinal)
            .ToList();
        var items = list
            .Skip((safePage - 1) * safeSize)
            .Take(safeSize)
            .ToList();
        return new ReportPage(items, list.Count, safePage, safeSize);
    }

    private static bool SameTarget(
        ReportRecord report, int targetId, IReadOnlyCollection<string> devices)
    {
        if (report.TargetId == targetId)
            return true;
        if (devices.Count == 0 || report.TargetDevices.Count == 0)
            return false;
        return report.TargetDevices.Any(d => devices.Any(x => string.Equals(
            x, d, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool Matches(
        ReportRecord report, int targetId, IReadOnlyCollection<string> devices)
    {
        if (report.TargetId == targetId)
            return true;
        if (devices.Count == 0 || report.TargetDevices.Count == 0)
            return false;
        return report.TargetDevices.Any(d => devices.Any(x => string.Equals(
            x, d, StringComparison.OrdinalIgnoreCase)));
    }

    private static List<string> NormalizeDevices(IEnumerable<string>? devices)
    {
        if (devices is null)
            return new List<string>();

        var result = new List<string>();
        foreach (var device in devices)
        {
            var normalized = BanRecord.NormalizeDevice(device);
            if (normalized.Length == 0)
                continue;
            if (result.Any(d => string.Equals(
                    d, normalized, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(normalized);
            if (result.Count >= MaxDevicesPerReport)
                break;
        }

        return result;
    }

    private static string NormalizeReason(string? reason)
    {
        var text = (reason ?? "").Trim();
        return text.Length <= MaxReasonLength ? text : text[..MaxReasonLength].Trim();
    }

    private static string? NormalizeNote(string? note)
    {
        var text = (note ?? "").Trim();
        if (text.Length == 0)
            return null;
        return text.Length <= MaxAdminNoteLength
            ? text
            : text[..MaxAdminNoteLength].Trim();
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static long ToMs(DateTime utc)
    {
        var value = utc.Kind == DateTimeKind.Utc
            ? utc
            : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        return new DateTimeOffset(value).ToUnixTimeMilliseconds();
    }

    private static DateTime FromMs(long ms) =>
        DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
}
