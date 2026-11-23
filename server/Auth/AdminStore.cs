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
/// <c>config.json</c> — только <c>admin.ids</c>: кто может быть админом. Роли, права и
/// мега-IP живут здесь и меняются только мега-админом (в панели) или консолью
/// владельца. Обычный админ не может выдать права даже себе.
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
public sealed class AdminStore
{
    public const string DirectoryName = "auth";
    public const string RecordsFileName = "admins.json";
    public const string JournalFileName = "journal.jsonl";
    public const string ReadmeFileName = "README.md";
    public const string PasswordFileName = "password.txt";

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
    /// Загрузить хранилище и синхронизировать его с <c>admin.ids</c>: новым id —
    /// запись и пароль, у кого пароля нет — сгенерировать. Повторный вызов безопасен.
    /// </summary>
    public void Initialize(IEnumerable<int> adminIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            EnsureDirectory(_directory, DirectoryMode);
            LoadLocked();
            SyncLocked(adminIds, nowUtc);
            WriteReadmeLocked();
            RetireLegacyAuthFileLocked();
        }
    }

    /// <summary>
    /// Досоздать записи после перечитывания <c>admin.ids</c>. Существующие права и
    /// пароли не трогаются; удалённые из конфига записи остаются для истории.
    /// </summary>
    public void Sync(IEnumerable<int> adminIds, DateTime nowUtc)
    {
        lock (_gate)
        {
            EnsureDirectory(_directory, DirectoryMode);
            LoadLocked();
            SyncLocked(adminIds, nowUtc);
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

    /// <summary>Мега-права: IP должен совпасть с <c>megaIps</c> записи.</summary>
    public bool IsMega(int id, IPAddress? ip)
    {
        if (ip is null)
            return false;

        var text = NormalizeIp(ip);
        if (text.Length == 0)
            return false;

        lock (_gate)
        {
            return _byId.TryGetValue(id, out var record)
                   && record.MegaIps.Any(candidate =>
                       string.Equals(candidate, text, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>Права с учётом мега-обхода. Неотнимаемые есть всегда.</summary>
    public IReadOnlyList<string> PermissionsFor(int id, IPAddress? ip)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var record))
                return Array.Empty<string>();

            if (IsMegaLocked(record, ip))
                return AdminPermissions.All.ToList();

            return AdminPermissions.Normalize(record.Permissions);
        }
    }

    /// <summary>Есть ли право. Мега-админ получает любое.</summary>
    public bool HasPermission(int id, IPAddress? ip, string permission)
    {
        if (!AdminPermissions.IsKnown(permission))
            return false;

        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var record))
                return false;
            if (IsMegaLocked(record, ip))
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
        out string? error)
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
            }, nowUtc);
            return true;
        }
    }

    /// <summary>Добавить мега-IP. Первого мега создаёт только консоль владельца.</summary>
    public bool AddMegaIp(
        int? actorId,
        int targetId,
        string? ipText,
        DateTime nowUtc,
        out string? error)
    {
        error = null;
        var canonical = NormalizeIpText(ipText);
        if (canonical.Length == 0)
        {
            error = "IP не распознан";
            return false;
        }

        lock (_gate)
        {
            if (!_byId.TryGetValue(targetId, out var record))
            {
                error = $"id #{targetId} нет среди админов";
                return false;
            }

            if (record.MegaIps.Contains(canonical, StringComparer.OrdinalIgnoreCase))
            {
                error = $"IP {canonical} уже в megaIps";
                return false;
            }

            record.MegaIps.Add(canonical);
            record.Role = "mega";
            record.UpdatedUtc = nowUtc;
            SaveLocked();
            JournalLocked(new
            {
                category = "permission",
                action = "mega_add",
                actorId,
                targetId,
                after = canonical,
            }, nowUtc);
            return true;
        }
    }

    /// <summary>Убрать мега-IP.</summary>
    public bool RemoveMegaIp(
        int? actorId,
        int targetId,
        string? ipText,
        DateTime nowUtc,
        out string? error)
    {
        error = null;
        var canonical = NormalizeIpText(ipText);
        if (canonical.Length == 0)
        {
            error = "IP не распознан";
            return false;
        }

        lock (_gate)
        {
            if (!_byId.TryGetValue(targetId, out var record))
            {
                error = $"id #{targetId} нет среди админов";
                return false;
            }

            var removed = record.MegaIps.RemoveAll(candidate =>
                string.Equals(candidate, canonical, StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
            {
                error = $"IP {canonical} нет в megaIps";
                return false;
            }

            if (record.MegaIps.Count == 0)
                record.Role = "admin";
            record.UpdatedUtc = nowUtc;
            SaveLocked();
            JournalLocked(new
            {
                category = "permission",
                action = "mega_remove",
                actorId,
                targetId,
                before = canonical,
            }, nowUtc);
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
        out string? error)
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
            }, nowUtc);
            return true;
        }
    }

    /// <summary>Новый пароль всем записям. Возвращает пары id → путь к файлу.</summary>
    public IReadOnlyList<(int Id, string Path)> RegenerateAllPasswords(
        int? actorId, DateTime nowUtc)
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
            }, nowUtc);
            return result;
        }
    }

    /// <summary>Путь к файлу пароля (для консоли и подсказки в панели).</summary>
    public string PasswordPathOf(int id) =>
        Path.Combine(_directory, id.ToString(CultureInfo.InvariantCulture), PasswordFileName);

    /// <summary>Дописать запись в журнал (JSONL, по одной строке).</summary>
    public void Journal(string category, string action, int? actorId, int? targetId,
        object? before = null, object? after = null)
    {
        lock (_gate)
            JournalLocked(new { category, action, actorId, targetId, before, after }, DateTime.UtcNow);
    }

    // ── внутренности (вызываются под lock) ───────────────────────────────────

    private static bool IsMegaLocked(AdminRecord record, IPAddress? ip)
    {
        if (ip is null || record.MegaIps.Count == 0)
            return false;
        var text = NormalizeIp(ip);
        return text.Length > 0 && record.MegaIps.Contains(text, StringComparer.OrdinalIgnoreCase);
    }

    private void SyncLocked(IEnumerable<int> adminIds, DateTime nowUtc)
    {
        var changed = false;
        foreach (var raw in (adminIds ?? Array.Empty<int>()).Distinct().OrderBy(id => id))
        {
            if (raw < 1)
                continue;

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

            var normalizedIps = record.MegaIps
                .Select(NormalizeIpText)
                .Where(ip => ip.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (!normalizedIps.SequenceEqual(record.MegaIps, StringComparer.OrdinalIgnoreCase))
            {
                record.MegaIps = normalizedIps;
                changed = true;
            }

            var role = record.MegaIps.Count > 0 ? "mega" : "admin";
            if (record.Role != role)
            {
                record.Role = role;
                changed = true;
            }

            if (EnsurePasswordLocked(record, nowUtc))
                changed = true;
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
                record.MegaIps = (record.MegaIps ?? new List<string>())
                    .Select(NormalizeIpText)
                    .Where(ip => ip.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
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

    private void JournalLocked(object entry, DateTime nowUtc)
    {
        try
        {
            EnsureDirectory(_directory, DirectoryMode);
            var payload = new Dictionary<string, object?>
            {
                ["id"] = "J-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant(),
                ["ts"] = nowUtc.ToString("O"),
            };

            // JsonElement из анонимного объекта: дописываем поля в тот же объект.
            // Значения оставляем узлами: списки прав должны лежать массивами, а не
            // строкой — по журналу потом строится откат.
            var node = JsonSerializer.SerializeToNode(entry, JournalJsonOptions)?.AsObject();
            if (node is not null)
            {
                foreach (var property in node)
                    payload[property.Key] = property.Value;
            }

            var line = JsonSerializer.Serialize(payload, JournalJsonOptions) + "\n";
            var path = Path.Combine(_directory, JournalFileName);
            File.AppendAllText(path, line, new UTF8Encoding(false));
            TrySetMode(path, SecretFileMode);
        }
        catch (Exception ex)
        {
            Log($"[auth] журнал не записан: {ex.Message}");
        }
    }

    private void WriteReadmeLocked()
    {
        var content =
            "# auth — админы, пароли и журнал\n\n" +
            "Этот каталог сервер ведёт сам. Руками править можно, но осторожно:\n" +
            "пароль читается из `password.txt` при старте, а права — только из `admins.json`.\n\n" +
            "## Файлы\n\n" +
            "- `admins.json` — id, ник, роль, права, `authVersion`, `megaIps`, SHA-256 пароля.\n" +
            "- `<id>/password.txt` — личный пароль вида `<id>-<hex64>` (права 600).\n" +
            "  Показывать его никому нельзя: кто ввёл пароль, тот и админ.\n" +
            "- `sessions.json` — веб-сессии админки (хранится только SHA-256 токена).\n" +
            "- `site-sessions.json` — сессии сайта (тоже только хеши токенов).\n" +
            "- `journal.jsonl` — журнал действий: вход, права, пароли, баны.\n\n" +
            "## Права\n\n" +
            "`admin.ids` в `config.json` — только список id, кто может быть админом.\n" +
            "Роли и права в конфиге не задаются. Новый админ получает `dashboard` и\n" +
            "`journal`; остальное (`bans`, `reports`, `customization`, `rooms`, `players`,\n" +
            "`config`, `admins`, `rollback`) выдаёт только мега-админ.\n\n" +
            "`dashboard` и `journal` не отнимаются ни у кого. Мега-админ обходит матрицу\n" +
            "прав целиком, но только с IP из своего `megaIps`.\n\n" +
            "## Консоль\n\n" +
            "- `admin list` — кто есть и какие права.\n" +
            "- `admin passwd regen <id>|all` — новый пароль; старые сессии умирают.\n" +
            "- `admin mega add <id> <ip>` — первый мега-админ только отсюда.\n" +
            "- `admin mega remove <id> <ip>`.\n" +
            "- `admin perm grant|revoke|set <id> <права...>`.\n\n" +
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
