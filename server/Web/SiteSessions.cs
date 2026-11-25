using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Сессия сайта: кто авторизовался по коду из лобби.
/// </summary>
/// <param name="Token">Токен из куки; у загруженной сессии пустой.</param>
/// <param name="PlayerId">Серверный ID игрока — тот же, что в лобби и в катке.</param>
/// <param name="Nick">Ник на момент входа.</param>
/// <param name="IssuedUtc">Когда обменяли код.</param>
/// <param name="LastSeenUtc">Последний запрос с этой сессией.</param>
/// <param name="ExpiresUtc">Срок жизни (30 дней).</param>
/// <param name="TokenHash">SHA-256 токена — то, что лежит в файле.</param>
public sealed record SiteSession(
    string Token,
    int PlayerId,
    string Nick,
    DateTime IssuedUtc,
    DateTime LastSeenUtc,
    DateTime ExpiresUtc = default,
    string TokenHash = "");

/// <summary>
/// Сессии сайта: обменял код → получил токен → работаешь.
/// </summary>
/// <remarks>
/// <para>
/// Сессия живёт 30 дней и переживает рестарт сервера: в <c>auth/site-sessions.json</c>
/// лежит только SHA-256 токена, кука ставится с <c>Max-Age</c>. Ротация кода и
/// перезаход в лобби сессию не убивают; гасит её выход и бан.
/// </para>
/// <para>
/// Одному игроку разрешено несколько сессий (телефон и компьютер), поэтому гасим сразу
/// все его токены: снять бан или выгнать человека иначе нельзя.
/// </para>
/// </remarks>
public sealed class SiteSessions
{
    public const int MaxSessions = 5000;
    public const int TtlDays = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, SiteSession> _byHash = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _byPlayer = new();
    private readonly string? _filePath;
    private readonly TimeSpan _ttl;

    public SiteSessions(string? filePath = null, TimeSpan? ttl = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath) ? null : Path.GetFullPath(filePath);
        _ttl = ttl ?? TimeSpan.FromDays(TtlDays);
        if (_filePath is not null)
            Load();
    }

    public int Count
    {
        get { lock (_gate) return _byHash.Count; }
    }

    /// <summary>Создать сессию. Возвращает <c>null</c>, если место кончилось.</summary>
    public SiteSession? Create(int playerId, string nick, DateTime nowUtc)
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
            var session = new SiteSession(
                token, playerId, nick, nowUtc, nowUtc, nowUtc.Add(_ttl), hash);
            _byHash[hash] = session;

            if (!_byPlayer.TryGetValue(playerId, out var hashes))
                _byPlayer[playerId] = hashes = new HashSet<string>(StringComparer.Ordinal);
            hashes.Add(hash);
            SaveLocked();
            return session;
        }
    }

    /// <summary>
    /// Найти живую сессию по токену из куки. Заодно обновляет «последний запрос».
    /// </summary>
    public SiteSession? Find(string? token, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var hash = HashToken(token);
        lock (_gate)
        {
            if (!_byHash.TryGetValue(hash, out var session))
                return null;

            if (session.ExpiresUtc <= nowUtc)
            {
                RemoveLocked(hash, session.PlayerId);
                SaveLocked();
                return null;
            }

            // LastSeen обновляет Renew при заходе на сайт, а не каждый запрос:
            // иначе страница с десятком GET переписывала бы файл.
            return session;
        }
    }

    /// <summary>
    /// Продлить сессию при заходе на сайт и вернуть обновлённую. На диск пишем не
    /// чаще раза в 6 часов; куку всё равно перевыставляем при каждом <c>/api/me</c>.
    /// </summary>
    public SiteSession? Renew(string? token, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        var hash = HashToken(token);
        lock (_gate)
        {
            if (!_byHash.TryGetValue(hash, out var session))
                return null;
            if (session.ExpiresUtc <= nowUtc)
            {
                RemoveLocked(hash, session.PlayerId);
                SaveLocked();
                return null;
            }

            var extended = session with
            {
                LastSeenUtc = nowUtc,
                ExpiresUtc = nowUtc.Add(_ttl),
            };
            _byHash[hash] = extended;
            if (session.ExpiresUtc - nowUtc < _ttl - TimeSpan.FromHours(6))
                SaveLocked();
            return extended;
        }
    }

    /// <summary>Погасить одну сессию (выход).</summary>
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

    /// <summary>Погасить все сессии игрока — например, он забанен.</summary>
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

    private void PruneExpiredLocked(DateTime nowUtc)
    {
        var dead = _byHash
            .Where(pair => pair.Value.ExpiresUtc <= nowUtc)
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

                var session = new SiteSession(
                    "", item.PlayerId, item.Nick ?? "", item.IssuedUtc,
                    item.LastSeenUtc, item.ExpiresUtc, item.TokenHash);
                _byHash[item.TokenHash] = session;
                if (!_byPlayer.TryGetValue(item.PlayerId, out var hashes))
                    _byPlayer[item.PlayerId] = hashes = new HashSet<string>(StringComparer.Ordinal);
                hashes.Add(item.TokenHash);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auth] {_filePath}: сессии сайта не прочитаны ({ex.Message})");
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
                System.IO.Directory.CreateDirectory(directory);

            var saved = _byHash.Values
                .OrderBy(session => session.PlayerId)
                .Select(session => new SavedSession
                {
                    TokenHash = session.TokenHash,
                    PlayerId = session.PlayerId,
                    Nick = session.Nick,
                    IssuedUtc = session.IssuedUtc,
                    LastSeenUtc = session.LastSeenUtc,
                    ExpiresUtc = session.ExpiresUtc,
                })
                .ToList();

            var json = JsonSerializer.Serialize(saved, JsonOptions);
            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[auth] {_filePath}: сессии сайта не сохранены ({ex.Message})");
        }
    }

    /// <summary>
    /// Свежий токен: 32 случайных байта, base64url. Криптографический генератор — это
    /// пропуск на сайт, угадывать токен не должны.
    /// </summary>
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
        public DateTime LastSeenUtc { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
}
