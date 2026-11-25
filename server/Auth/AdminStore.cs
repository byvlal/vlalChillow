using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace StandChillow.LanServer.Auth;

/// <summary>
/// Постоянное хранилище админов и журнала: <c>auth/admins.json</c>,
/// <c>auth/&lt;id&gt;/password.txt</c>, <c>auth/journal.jsonl</c>.
/// </summary>
/// <remarks>
/// <para>
/// <c>config.json</c> — <c>admin.ids</c> и <c>admin.megaIds</c>: кто может быть админом
/// и кто из них мега. Права обычных админов живут здесь и меняются только мега-админом
/// (в панели) или консолью владельца. Мега-статус приходит только из конфига, поэтому
/// смена IP или VPN его не отнимает. Обычный админ не может выдать права даже себе.
/// </para>
/// <para>
/// Пароль на диске — только в <c>password.txt</c> (600) и только в виде
/// <c>&lt;id&gt;-&lt;hex64&gt;</c>. В <c>admins.json</c> лежит SHA-256, не сам пароль.
/// </para>
/// <para>
/// Все записи защищены одним lock: панель, игра и консоль могут обращаться
/// одновременно, а файл переписывается атомарно (временный + rename).
/// </para>
/// </remarks>
public sealed partial class AdminStore
{
    public const string DirectoryName = "auth";
    public const string RecordsFileName = "admins.json";
    public const string JournalFileName = "journal.jsonl";
    public const string ReadmeFileName = "README.md";
    public const string PasswordFileName = "password.txt";

    /// <summary>Отметка записи, созданной тестовой автовыдачей мега-прав.</summary>
    private const string RuntimeMegaMarker = "test-auto";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>
    /// Журнал — JSON Lines: одна запись ровно в одну строку, иначе его не прочитать
    /// построчно и не отфильтровать grep'ом.
    /// </summary>
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    /// <summary>Каталог <c>auth</c> и подкаталоги: 700 — чужие не читают пароли.</summary>
    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Файлы с паролями и хешами: 600.</summary>
    private const UnixFileMode SecretFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Action<string>? _log;
    private readonly Dictionary<int, AdminRecord> _byId = new();
    private readonly HashSet<int> _megaIds = new();

    /// <summary>
    /// Мега-права, выданные в рантайме тестовым режимом. В <c>config.json</c> и
    /// <c>admins.json</c> они не попадают; после перезапуска выдаются заново.
    /// </summary>
    private readonly HashSet<int> _runtimeMega = new();

    public AdminStore(string directory, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("каталог auth не может быть пустым", nameof(directory));

        _directory = Path.GetFullPath(directory);
        _log = log;
    }

    /// <summary>Каталог <c>auth</c> рядом с <c>config.json</c>.</summary>
    public static string ResolveDirectory(string? configPath)
    {
        string directory;
        try
        {
            directory = string.IsNullOrWhiteSpace(configPath)
                ? string.Empty
                : (Path.GetDirectoryName(Path.GetFullPath(configPath)) ?? string.Empty);
        }
        catch (Exception)
        {
            directory = string.Empty;
        }

        if (directory.Length == 0)
            directory = AppContext.BaseDirectory;

        return Path.Combine(directory, DirectoryName);
    }

    public string Directory => _directory;

    public string RecordsPath => Path.Combine(_directory, RecordsFileName);

    public int Count
    {
        get { lock (_gate) return _byId.Count; }
    }

    /// <summary>
    /// Загрузить хранилище и синхронизировать его с <c>admin.ids</c> +
    /// <c>admin.megaIds</c>: новым id — запись и пароль, у кого пароля нет —
    /// сгенерировать. Повторный вызов безопасен.
    /// </summary>
    public void Initialize(IEnumerable<int> adminIds, IEnumerable<int> megaIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            EnsureDirectory(_directory, DirectoryMode);
            LoadLocked();

            // Записи, созданные тестовой автовыдачей, не должны переживать
            // перезапуск: сервер снова выдаст мега-права при входе, а мусорные
            // «админы» из прошлого запуска останутся только в журнале.
            var stale = _byId.Values
                .Where(record => record.AddedBy == RuntimeMegaMarker)
                .Select(record => record.Id)
                .ToList();
            foreach (var id in stale)
                _byId.Remove(id);
            if (stale.Count > 0)
                SaveLocked();

            SyncLocked(adminIds, megaIds, nowUtc);
            WriteReadmeLocked();
            RetireLegacyAuthFileLocked();
        }
    }

    /// <summary>
    /// Досоздать записи после перечитывания <c>admin.ids</c>/<c>admin.megaIds</c>.
    /// Существующие права и пароли не трогаются; удалённые из конфига записи остаются
    /// для истории. Роль пересчитывается: мега — только id из <c>megaIds</c>.
    /// </summary>
    public void Sync(IEnumerable<int> adminIds, IEnumerable<int> megaIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            EnsureDirectory(_directory, DirectoryMode);
            LoadLocked();
            SyncLocked(adminIds, megaIds, nowUtc);
            WriteReadmeLocked();
        }
    }

    /// <summary>Запись по id (копия) или <c>null</c>.</summary>
    public AdminRecord? Find(int id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var record) ? record.Clone() : null;
    }

    /// <summary>Все записи, отсортированные по id (копии).</summary>
    public IReadOnlyList<AdminRecord> Records()
    {
        lock (_gate)
            return _byId.Values.OrderBy(r => r.Id).Select(r => r.Clone()).ToList();
    }

    /// <summary>
    /// Проверить пароль. Возвращает запись только если пароль подходит; для неизвестного
    /// id и неверного пароля результат один и тот же — <c>null</c>.
    /// </summary>
    public AdminRecord? Verify(int id, string? password, DateTime nowUtc)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var record))
                return null;

            if (!AdminPassword.Matches(id, password, record.PasswordHash))
                return null;

            record.UpdatedUtc = nowUtc;
            return record.Clone();
        }
    }

    /// <summary>Запомнить ник последнего входа.</summary>
    public void SetNick(int id, string? nick, DateTime nowUtc)
    {
        var value = (nick ?? "").Trim();
        if (value.Length == 0)
            return;

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var record) || record.Nick == value)
                return;
            record.Nick = value;
            record.UpdatedUtc = nowUtc;
            SaveLocked();
        }
    }

    /// <summary>Версия пароля: старые сессии с другой версией недействительны.</summary>
    public int AuthVersionOf(int id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var record) ? record.AuthVersion : 0;
    }

    /// <summary>
    /// Мега-права: id входит в <c>admin.megaIds</c> и для него создана запись либо
    /// права выданы тестовым режимом в этом процессе. IP и адрес запроса не участвуют.
    /// </summary>
    public bool IsMega(int id)
    {
        lock (_gate)
            return (_byId.ContainsKey(id) && _megaIds.Contains(id))
                   || _runtimeMega.Contains(id);
    }

    /// <summary>Мега-права, выданные тестовым режимом (не из конфига).</summary>
    public bool IsRuntimeMega(int id)
    {
        lock (_gate)
            return _runtimeMega.Contains(id);
    }

    /// <summary>
    /// Выдать мега-права в рантайме (тестовый режим). Создаёт запись админа без
    /// пароля, чтобы затворы нашли её; в <c>config.json</c> ничего не пишется.
    /// Повторная выдача идемпотентна.
    /// </summary>
    public bool GrantRuntimeMega(
        int id, string nick, DateTime nowUtc, AdminJournalContext? context = null)
    {
        if (id <= 0)
            return false;

        lock (_gate)
        {
            var changed = _runtimeMega.Add(id);
            if (!_byId.TryGetValue(id, out var record))
            {
                record = new AdminRecord
                {
                    Id = id,
                    Nick = nick ?? "",
                    Role = "mega",
                    Permissions = AdminPermissions.All.ToList(),
                    AddedBy = RuntimeMegaMarker,
                    AuthVersion = 1,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                };
                _byId[id] = record;
                changed = true;
            }
            else
            {
                if (record.Role != "mega")
                {
                    record.Role = "mega";
                    changed = true;
                }
                if (record.UpdatedUtc < nowUtc)
                {
                    record.UpdatedUtc = nowUtc;
                    changed = true;
                }
            }

            if (!changed)
                return true;

            SaveLocked();
            JournalLocked(new
            {
                category = "security",
                action = "test_auto_mega",
                actorId = id,
                targetId = id,
                nick,
            }, nowUtc, context ?? new AdminJournalContext(
                id, nick ?? "", TargetType: "admin", TargetId: id, TargetNick: nick ?? "",
                Note: "тестовый режим: автовыдача мега-админки"));
            return true;
        }
    }

    /// <summary>Права с учётом мега-обхода. Неотнимаемые есть всегда.</summary>
    public IReadOnlyList<string> PermissionsFor(int id)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var record))
                return Array.Empty<string>();

            if (_megaIds.Contains(id) || _runtimeMega.Contains(id))
                return AdminPermissions.All.ToList();

            return AdminPermissions.Normalize(record.Permissions);
        }
    }

    /// <summary>Есть ли право. Мега-админ получает любое.</summary>
    public bool HasPermission(int id, string permission)
    {
        if (!AdminPermissions.IsKnown(permission))
            return false;

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var record))
                return false;
            if (_megaIds.Contains(id) || _runtimeMega.Contains(id))
                return true;
            return AdminPermissions.Normalize(record.Permissions)
                .Contains(permission, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Выдать/отнять операционные права. <c>dashboard</c> и <c>journal</c> остаются
    /// всегда. Проверку «актор — мега» делает вызывающий: хранилище не знает HTTP.
    /// </summary>
    public bool SetPermissions(
        int? actorId,
        int targetId,
        IEnumerable<string>? permissions,
        DateTime nowUtc,
        out string? error,
        AdminJournalContext? context = null)
    {
        error = null;
        lock (_gate)
        {
            if (!_byId.TryGetValue(targetId, out var record))
            {
                error = $"id #{targetId} нет среди админов";
                return false;
            }

            var before = AdminPermissions.Normalize(record.Permissions);
            var after = AdminPermissions.Normalize(permissions);
            record.Permissions = after;
            record.UpdatedUtc = nowUtc;
            SaveLocked();

            JournalLocked(new
            {
                category = "permission",
                action = "set",
                actorId,
                targetId,
                before,
                after,
            }, nowUtc, context ?? new AdminJournalContext(
                actorId, TargetType: "admin", TargetId: targetId, Reversible: true));
            return true;
        }
    }

    /// <summary>
    /// Новый пароль одному админу. Старый умирает сразу: <c>authVersion</c> растёт, и
    /// веб-сессии с прежней версией перестают находиться.
    /// </summary>
    public bool RegeneratePassword(
        int? actorId,
        int targetId,
        DateTime nowUtc,
        out string passwordPath,
        out string? error,
        AdminJournalContext? context = null)
    {
        passwordPath = "";
        error = null;
        lock (_gate)
        {
            if (!_byId.TryGetValue(targetId, out var record))
            {
                error = $"id #{targetId} нет среди админов";
                return false;
            }

            passwordPath = WriteNewPasswordLocked(record, nowUtc);
            SaveLocked();
            JournalLocked(new
            {
                category = "auth",
                action = "password_regen",
                actorId,
                targetId,
                authVersion = record.AuthVersion,
            }, nowUtc, context ?? new AdminJournalContext(
                actorId, TargetType: "admin", TargetId: targetId));
            return true;
        }
    }

    /// <summary>Новый пароль всем записям. Возвращает пары id → путь к файлу.</summary>
    public IReadOnlyList<(int Id, string Path)> RegenerateAllPasswords(
        int? actorId, DateTime nowUtc, AdminJournalContext? context = null)
    {
        lock (_gate)
        {
            var result = new List<(int, string)>();
            foreach (var record in _byId.Values.OrderBy(r => r.Id))
                result.Add((record.Id, WriteNewPasswordLocked(record, nowUtc)));

            SaveLocked();
            JournalLocked(new
            {
                category = "auth",
                action = "password_regen_all",
                actorId,
                count = result.Count,
            }, nowUtc, context ?? new AdminJournalContext(
                actorId, TargetType: "admin", Note: $"всего {result.Count}"));
            return result;
        }
    }

    /// <summary>Путь к файлу пароля (для консоли и подсказки в панели).</summary>
    public string PasswordPathOf(int id) =>
        Path.Combine(_directory, id.ToString(CultureInfo.InvariantCulture), PasswordFileName);

    /// <summary>Дописать запись в журнал (JSONL, по одной строке).</summary>
    /// <returns>Id записи или <c>null</c>, если журнал недоступен.</returns>
    public string? Journal(
        string category, string action, int? actorId, int? targetId,
        object? before = null, object? after = null,
        AdminJournalContext? context = null)
    {
        lock (_gate)
            return JournalLocked(
                new { category, action, actorId, targetId, before, after },
                DateTime.UtcNow,
                context ?? new AdminJournalContext(
                    actorId, TargetType: "player", TargetId: targetId));
    }

    // ── внутренности (вызываются под lock) ───────────────────────────────────

    private void SyncLocked(
        IEnumerable<int> adminIds,
        IEnumerable<int> megaIds,
        DateTime nowUtc)
    {
        var changed = false;
        var mega = new HashSet<int>((megaIds ?? Array.Empty<int>()).Where(id => id >= 1));
        _megaIds.Clear();
        foreach (var id in mega)
            _megaIds.Add(id);

        // Мега-админ обязан быть админом, даже если в ids его забыли вписать.
        var allIds = (adminIds ?? Array.Empty<int>())
            .Concat(mega)
            .Where(id => id >= 1)
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        foreach (var raw in allIds)
        {
            if (!_byId.TryGetValue(raw, out var record))
            {
                record = new AdminRecord
                {
                    Id = raw,
                    Nick = "",
                    Role = "admin",
                    Permissions = AdminPermissions.Normalize(null),
                    AddedBy = "config",
                    AuthVersion = 1,
                    CreatedUtc = nowUtc,
                    UpdatedUtc = nowUtc,
                };
                _byId[raw] = record;
                changed = true;
            }

            var normalizedPermissions = AdminPermissions.Normalize(record.Permissions);
            if (!normalizedPermissions.SequenceEqual(record.Permissions))
            {
                record.Permissions = normalizedPermissions;
                changed = true;
            }

            if (EnsurePasswordLocked(record, nowUtc))
                changed = true;
        }

        // Роль — информационная, но список и журнал должны показывать правду.
        foreach (var record in _byId.Values)
        {
            var role = mega.Contains(record.Id) || _runtimeMega.Contains(record.Id)
                ? "mega"
                : "admin";
            if (record.Role != role)
            {
                record.Role = role;
                changed = true;
            }
        }

        if (changed)
            SaveLocked();
    }

    /// <summary>Проверить/создать пароль. <c>true</c> — запись изменилась.</summary>
    private bool EnsurePasswordLocked(AdminRecord record, DateTime nowUtc)
    {
        var path = PasswordPathOf(record.Id);
        string? fromFile = null;
        try
        {
            if (File.Exists(path))
            {
                var first = File.ReadLines(path).FirstOrDefault();
                if (AdminPassword.TryParse(record.Id, first, out var normalized))
                    fromFile = normalized;
                else
                    Log($"[auth] {path}: не пароль вида <id>-<hex64> — файл перезапишу");
            }
        }
        catch (Exception ex)
        {
            Log($"[auth] не смог прочитать {path}: {ex.Message}");
        }

        if (fromFile is not null)
        {
            var hash = AdminPassword.Hash(fromFile);
            var changed = false;
            if (!string.Equals(hash, record.PasswordHash, StringComparison.Ordinal))
            {
                // Файл — источник правды: поправили руками — значит сменили пароль.
                if (!string.IsNullOrEmpty(record.PasswordHash))
                    record.AuthVersion++;
                record.PasswordHash = hash;
                record.UpdatedUtc = nowUtc;
                changed = true;
                Log($"[auth] #{record.Id}: пароль взят из {path} (версия {record.AuthVersion})");
            }

            WritePasswordFile(path, fromFile);
            return changed;
        }

        // Файла нет или он битый: старый хеш восстановить нельзя, поэтому новый пароль.
        var secret = AdminPassword.GenerateSecret();
        var full = AdminPassword.Format(record.Id, secret);
        if (!string.IsNullOrEmpty(record.PasswordHash))
            record.AuthVersion++;
        record.PasswordHash = AdminPassword.Hash(full);
        record.UpdatedUtc = nowUtc;
        WritePasswordFile(path, full);
        Log($"[auth] #{record.Id}: новый пароль — {path}");
        return true;
    }

    private string WriteNewPasswordLocked(AdminRecord record, DateTime nowUtc)
    {
        var full = AdminPassword.Format(record.Id, AdminPassword.GenerateSecret());
        record.PasswordHash = AdminPassword.Hash(full);
        record.AuthVersion++;
        record.UpdatedUtc = nowUtc;
        var path = PasswordPathOf(record.Id);
        WritePasswordFile(path, full);
        return path;
    }

    private void WritePasswordFile(string path, string password)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            EnsureDirectory(directory, DirectoryMode);

        var content = password + Environment.NewLine;
        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(false));
        TrySetMode(temp, SecretFileMode); // 600
        File.Move(temp, path, overwrite: true);
        TrySetMode(path, SecretFileMode);
    }

    private void LoadLocked()
    {
        _byId.Clear();
        var path = RecordsPath;
        if (!File.Exists(path))
            return;

        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            var records = JsonSerializer.Deserialize<List<AdminRecord>>(json, JsonOptions);
            foreach (var record in records ?? new List<AdminRecord>())
            {
                if (record.Id < 1)
                    continue;
                record.Nick ??= "";
                record.Permissions = AdminPermissions.Normalize(record.Permissions);
                record.AuthVersion = Math.Max(1, record.AuthVersion);
                record.CreatedUtc = record.CreatedUtc == default ? DateTime.UtcNow : record.CreatedUtc;
                record.UpdatedUtc = record.UpdatedUtc == default ? record.CreatedUtc : record.UpdatedUtc;
                _byId[record.Id] = record;
            }
        }
        catch (Exception ex)
        {
            // Битый файл не должен убивать сервер: откладываем его в сторону и начинаем
            // с чистого листа. Пароли при этом будут пересозданы.
            Log($"[auth] {path}: не читается ({ex.Message}) — переименую в .bad");
            try
            {
                File.Move(path, path + ".bad", overwrite: true);
            }
            catch (Exception moveError)
            {
                Log($"[auth] не смог переименовать битый {path}: {moveError.Message}");
            }
        }
    }

    private void SaveLocked()
    {
        EnsureDirectory(_directory, DirectoryMode);
        var records = _byId.Values.OrderBy(r => r.Id).ToList();
        var json = JsonSerializer.Serialize(records, JsonOptions);
        var temp = RecordsPath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        TrySetMode(temp, SecretFileMode);
        File.Move(temp, RecordsPath, overwrite: true);
        TrySetMode(RecordsPath, SecretFileMode);
    }

    private string? JournalLocked(
        object entry, DateTime nowUtc, AdminJournalContext? context = null) =>
        AppendJournal(entry, nowUtc, context);

    private void WriteReadmeLocked()
    {
        var content =
            "# auth — админы, пароли и журнал\n\n" +
            "Этот каталог сервер ведёт сам. Руками править можно, но осторожно:\n" +
            "пароль читается из `password.txt` при старте, а права — только из `admins.json`.\n\n" +
            "## Файлы\n\n" +
            "- `admins.json` — id, ник, роль, права, `authVersion`, SHA-256 пароля.\n" +
            "- `<id>/password.txt` — личный пароль вида `<id>-<hex64>` (права 600).\n" +
            "  Показывать его никому нельзя: кто ввёл пароль, тот и админ.\n" +
            "- `sessions.json` — веб-сессии админки (хранится только SHA-256 токена).\n" +
            "- `site-sessions.json` — сессии сайта (тоже только хеши токенов).\n" +
            "- `journal.jsonl` — журнал действий: вход, права, пароли, баны.\n\n" +
            "## Права\n\n" +
            "`admin.ids` в `config.json` — обычные админы, `admin.megaIds` — мега-админы.\n" +
            "Мега-статус даёт только конфиг и не зависит от IP. Если id есть в обоих\n" +
            "списках, он мега. Новый админ получает `dashboard` и `journal`; остальное\n" +
            "(`bans`, `reports`, `customization`, `rooms`, `players`, `config`, `admins`,\n" +
            "`rollback`) выдаёт только мега-админ.\n\n" +
            "`dashboard` и `journal` не отнимаются ни у кого. Мега-админ обходит матрицу\n" +
            "прав целиком с любого адреса.\n\n" +
            "## Консоль\n\n" +
            "- `admin list` — кто есть и какие права.\n" +
            "- `admin passwd regen <id>|all` — новый пароль; старые сессии умирают.\n" +
            "- `admin perm grant|revoke|set <id> <права...>`.\n" +
            "- Мега-админы задаются в `config.json` → `admin.megaIds`; из консоли список\n" +
            "  не меняется.\n\n" +
            "`auth.txt` больше не используется: у каждого свой пароль в этом каталоге.\n";

        try
        {
            EnsureDirectory(_directory, DirectoryMode);
            var path = Path.Combine(_directory, ReadmeFileName);
            if (File.Exists(path) && File.ReadAllText(path, Encoding.UTF8) == content)
                return;
            File.WriteAllText(path, content, new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            Log($"[auth] README не записан: {ex.Message}");
        }
    }

    private void RetireLegacyAuthFileLocked()
    {
        try
        {
            var parent = Path.GetDirectoryName(_directory);
            if (string.IsNullOrEmpty(parent))
                return;
            var legacy = Path.Combine(parent, "auth.txt");
            if (!File.Exists(legacy))
                return;
            File.Delete(legacy);
            Log($"[auth] старый auth.txt удалён — у каждого админа свой пароль в {_directory}");
        }
        catch (Exception ex)
        {
            Log($"[auth] не смог удалить старый auth.txt: {ex.Message}");
        }
    }

    private void Log(string text) => _log?.Invoke(text);

    private static void EnsureDirectory(string path, UnixFileMode mode)
    {
        if (!System.IO.Directory.Exists(path))
            System.IO.Directory.CreateDirectory(path);
        TrySetMode(path, mode);
    }

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // FAT/exFAT и некоторые Android-разделы не умеют chmod — это не повод падать.
        }
    }

    /// <summary>Канон IP: срезать zone, свернуть IPv4-mapped, привести к строке.</summary>
    public static string NormalizeIp(IPAddress? ip)
    {
        if (ip is null)
            return string.Empty;
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        return ip.ToString();
    }

    public static string NormalizeIpText(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return string.Empty;

        var percent = value.IndexOf('%');
        if (percent > 0)
            value = value[..percent];

        return IPAddress.TryParse(value, out var parsed)
            && parsed.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
            ? NormalizeIp(parsed)
            : string.Empty;
    }
}
