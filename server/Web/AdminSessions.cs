using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using StandChillow.LanServer.Auth;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Админ-сессии веб-панели: «кто ввёл свой личный пароль».
/// </summary>
/// <remarks>
/// <para>
/// Сессии переживают рестарт сервера и закрытие браузера: в <c>auth/sessions.json</c>
/// лежит только SHA-256 токена, срок — 7 дней. Смена пароля поднимает
/// <c>authVersion</c>, и все сессии с прежней версией перестают находиться.
/// </para>
/// <para>
/// Отдельно от <see cref="SiteSessions"/>: сессия сайта — «кто ты», админ-сессия —
/// «ты знаешь пароль». Вход в админку не требует сессии сайта.
/// </para>
/// </remarks>
public sealed class AdminSessions
{
    public const int MaxSessions = 100;
    public const int TtlDays = 7;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, AdminSession> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _byPlayer = new();
    private readonly string? _filePath;
    private readonly AdminStore? _store;
    private readonly TimeSpan _ttl;

    public AdminSessions(
        string? filePath = null,
        AdminStore? store = null,
        TimeSpan? ttl = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        _store = store;
        _ttl = ttl ?? TimeSpan.FromDays(TtlDays);
        if (_filePath is not null)
            Load();
    }

    public int Count
    {
        get { lock (_gate) return _byHash.Count; }
    }

    /// <summary>Открыть админ-сессию после проверки пароля.</summary>
    public AdminSession? Create(int playerId, string nick, int authVersion, DateTime nowUtc)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
        {
            PruneExpiredLocked(nowUtc);
            if (_byHash.Count >= MaxSessions)
                return null;

            var token = NewToken();
            var hash = HashToken(token);
            var session = new AdminSession(
                token, playerId, nick ?? "", nowUtc, nowUtc.Add(_ttl), authVersion, hash);
            _byHash[hash] = session;

            if (!_byPlayer.TryGetValue(playerId, out var hashes))
                _byPlayer[playerId] = hashes = new HashSet<string>(StringComparer.Ordinal);
            hashes.Add(hash);
            SaveLocked();
            return session;
        }
    }

    /// <summary>Живая ли админ-сессия по токену из куки.</summary>
    public AdminSession? Find(string? token, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var hash = HashToken(token);
        lock (_gate)
        {
            if (!_byHash.TryGetValue(hash, out var session))
                return null;

            if (session.ExpiresUtc <= nowUtc || !VersionMatchesLocked(session))
            {
                RemoveLocked(hash, session.PlayerId);
                SaveLocked();
                return null;
            }

            return session;
        }
    }

    /// <summary>Выйти из админ-панели (одна конкретная кука).</summary>
    public bool Drop(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var hash = HashToken(token);
        lock (_gate)
        {
            if (!_byHash.TryGetValue(hash, out var session))
                return false;
            RemoveLocked(hash, session.PlayerId);
            SaveLocked();
            return true;
        }
    }

    /// <summary>Снять все админ-сессии игрока (смена пароля, удаление из админов).</summary>
    public int DropByPlayer(int playerId)
    {
        lock (_gate)
        {
            if (!_byPlayer.Remove(playerId, out var hashes))
                return 0;

            var dropped = 0;
            foreach (var hash in hashes)
            {
                if (_byHash.Remove(hash))
                    dropped++;
            }

            if (dropped > 0)
                SaveLocked();
            return dropped;
        }
    }

    /// <summary>Снять всё (полный сброс доступа).</summary>
    public int DropAll()
    {
        lock (_gate)
        {
            var n = _byHash.Count;
            _byHash.Clear();
            _byPlayer.Clear();
            if (n > 0)
                SaveLocked();
            return n;
        }
    }

    private bool VersionMatchesLocked(AdminSession session)
    {
        if (_store is null)
            return true;
        var record = _store.Find(session.PlayerId);
        return record is not null && record.AuthVersion == session.AuthVersion;
    }

    private void PruneExpiredLocked(DateTime nowUtc)
    {
        var dead = _byHash
            .Where(pair => pair.Value.ExpiresUtc <= nowUtc || !VersionMatchesLocked(pair.Value))
            .Select(pair => pair.Key)
            .ToList();

        foreach (var hash in dead)
        {
            if (_byHash.TryGetValue(hash, out var session))
                RemoveLocked(hash, session.PlayerId);
        }
    }

    private void RemoveLocked(string hash, int playerId)
    {
        _byHash.Remove(hash);
        if (!_byPlayer.TryGetValue(playerId, out var hashes))
            return;
        hashes.Remove(hash);
        if (hashes.Count == 0)
            _byPlayer.Remove(playerId);
    }

    private void Load()
    {
        if (_filePath is null || !File.Exists(_filePath))
            return;

        try
        {
            var json = File.ReadAllText(_filePath, Encoding.UTF8);
            var saved = JsonSerializer.Deserialize<List<SavedSession>>(json, JsonOptions);
            var now = DateTime.UtcNow;
            foreach (var item in saved ?? new List<SavedSession>())
            {
                if (item.PlayerId <= 0 || string.IsNullOrEmpty(item.TokenHash)
                    || item.ExpiresUtc <= now)
                    continue;

                var session = new AdminSession(
                    "", item.PlayerId, item.Nick ?? "", item.IssuedUtc,
                    item.ExpiresUtc, item.AuthVersion, item.TokenHash);
                _byHash[item.TokenHash] = session;
                if (!_byPlayer.TryGetValue(item.PlayerId, out var hashes))
                    _byPlayer[item.PlayerId] = hashes = new HashSet<string>(StringComparer.Ordinal);
                hashes.Add(item.TokenHash);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auth] {_filePath}: сессии не прочитаны ({ex.Message})");
        }
    }

    private void SaveLocked()
    {
        if (_filePath is null)
            return;

        try
        {
            var directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(directory, (UnixFileMode)0b111_000_000);
            }

            var saved = _byHash.Values
                .OrderBy(session => session.PlayerId)
                .Select(session => new SavedSession
                {
                    TokenHash = session.TokenHash,
                    PlayerId = session.PlayerId,
                    Nick = session.Nick,
                    IssuedUtc = session.IssuedUtc,
                    ExpiresUtc = session.ExpiresUtc,
                    AuthVersion = session.AuthVersion,
                })
                .ToList();

            var json = JsonSerializer.Serialize(saved, JsonOptions);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temp, (UnixFileMode)0b110_000_000);
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auth] {_filePath}: сессии не сохранены ({ex.Message})");
        }
    }

    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private sealed class SavedSession
    {
        public string TokenHash { get; set; } = "";
        public int PlayerId { get; set; }
        public string Nick { get; set; } = "";
        public DateTime IssuedUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
        public int AuthVersion { get; set; }
    }
}

/// <summary>Одна админ-сессия: кто и когда вошёл.</summary>
/// <param name="Token">Значение куки при создании; у загруженной сессии пустое.</param>
/// <param name="PlayerId">Числовой ID админа.</param>
/// <param name="Nick">Ник на момент входа.</param>
/// <param name="IssuedUtc">Момент входа.</param>
/// <param name="ExpiresUtc">Срок (7 дней).</param>
/// <param name="AuthVersion">Версия пароля на момент входа.</param>
/// <param name="TokenHash">SHA-256 токена — то, что лежит на диске.</param>
public sealed record AdminSession(
    string Token,
    int PlayerId,
    string Nick,
    DateTime IssuedUtc,
    DateTime ExpiresUtc,
    int AuthVersion,
    string TokenHash);
