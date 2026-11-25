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
/// лежит только SHA-256 токена, срок — 30 дней. Смена пароля поднимает
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
    public const int TtlDays = 30;

    /// <summary>
    /// Сколько тестовых автовходов с одного адреса держим одновременно. Старые
    /// снимаются: браузер без кук не должен занимать весь запас хранилища.
    /// </summary>
    public const int MaxTestSessionsPerIp = 5;

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
    /// <remarks>
    /// Вместе с сессией создаётся токен устройства. Он уходит во вторую куку, а на
    /// диске лежит только его хеш: без этой куки сессия не находится.
    /// <para>
    /// <paramref name="testAuto"/> — вход тестового режима без экрана входа. Такие
    /// сессии умирают, как только флаг <c>test.noAdminPassword</c> выключат, и не
    /// могут вытеснить обычные сессии из переполненного хранилища.
    /// </para>
    /// </remarks>
    public AdminSession? Create(
        int playerId,
        string nick,
        int authVersion,
        DateTime nowUtc,
        string ip = "",
        string userAgent = "",
        bool testAuto = false)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
        {
            PruneExpiredLocked(nowUtc);

            // Переполнили хранилище — снимаем самую старую тестовую сессию, а не
            // отказываем живому админу: тестовый автовход не должен запирать вход.
            if (_byHash.Count >= MaxSessions && !DropOldestTestLocked())
                return null;

            // Один адрес не должен занимать весь запас тестовыми автовходами.
            if (testAuto)
                TrimTestSessionsForIpLocked(ip);

            var token = NewToken();
            var hash = HashToken(token);
            var deviceToken = NewToken();
            var session = new AdminSession(
                token, playerId, nick ?? "", nowUtc, nowUtc.Add(_ttl), authVersion, hash,
                NewToken(), ip ?? "", TrimAgent(userAgent), NewSessionId(),
                DeviceToken: deviceToken, DeviceHash: HashToken(deviceToken),
                TestAuto: testAuto);

            _byHash[hash] = session;

            if (!_byPlayer.TryGetValue(playerId, out var hashes))
                _byPlayer[playerId] = hashes = new HashSet<string>(StringComparer.Ordinal);
            hashes.Add(hash);
            SaveLocked();
            return session;
        }
    }

    /// <summary>
    /// Живая ли админ-сессия по токену из куки и токену устройства. Оба обязательны:
    /// скопированная админ-кука без куки устройства доступа не даёт.
    /// </summary>
    public AdminSession? Find(string? token, string? deviceToken, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(deviceToken))
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

            if (!DeviceMatches(session, deviceToken))
                return null;

            return session;
        }
    }

    /// <summary>
    /// Продлить сессию при заходе в панель. Браузер получает новые куки, а на диск
    /// продление пишется не чаще раза в 6 часов — иначе каждый GET переписывал бы файл.
    /// </summary>
    public AdminSession? Renew(string? tokenHash, DateTime nowUtc)
    {
        if (string.IsNullOrEmpty(tokenHash))
            return null;

        lock (_gate)
        {
            if (!_byHash.TryGetValue(tokenHash, out var session))
                return null;
            if (session.ExpiresUtc <= nowUtc || !VersionMatchesLocked(session))
                return null;

            var extended = session with { ExpiresUtc = nowUtc.Add(_ttl) };
            _byHash[tokenHash] = extended;
            if (session.ExpiresUtc - nowUtc < _ttl - TimeSpan.FromHours(6))
                SaveLocked();
            return extended;
        }
    }

    private static bool DeviceMatches(AdminSession session, string deviceToken)
    {
        if (string.IsNullOrEmpty(session.DeviceHash))
            return false;

        var candidate = HashToken(deviceToken);
        if (candidate.Length != session.DeviceHash.Length)
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(candidate), Encoding.UTF8.GetBytes(session.DeviceHash));
    }

    /// <summary>
    /// Подтвердить личность ещё раз для опасного действия: пароль только что
    /// проверен, и сессии разрешено менять конфиг/админов до указанного момента.
    /// </summary>
    public bool SetStepUp(string? token, DateTime untilUtc)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        var hash = HashToken(token);
        lock (_gate)
        {
            if (!_byHash.TryGetValue(hash, out var session))
                return false;
            if (session.ExpiresUtc <= DateTime.UtcNow || !VersionMatchesLocked(session))
                return false;

            _byHash[hash] = session with { StepUpUntilUtc = untilUtc };
            SaveLocked();
            return true;
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

    /// <summary>Живые сессии (без токенов) для страницы «Безопасность».</summary>
    public IReadOnlyList<AdminSession> List(DateTime nowUtc, int? playerId = null)
    {
        lock (_gate)
        {
            PruneExpiredLocked(nowUtc);
            return _byHash.Values
                .Where(session => playerId is null || session.PlayerId == playerId)
                .OrderByDescending(session => session.IssuedUtc)
                .ToList();
        }
    }

    /// <summary>Снять одну сессию по её публичному id (не токену).</summary>
    public bool DropBySessionId(string sessionId, int? ownerId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        lock (_gate)
        {
            var session = _byHash.Values.FirstOrDefault(s =>
                string.Equals(s.SessionId, sessionId, StringComparison.Ordinal)
                && (ownerId is null || s.PlayerId == ownerId));
            if (session is null)
                return false;

            var hash = session.TokenHash;
            RemoveLocked(hash, session.PlayerId);
            SaveLocked();
            return true;
        }
    }

    /// <summary>Сколько уникальных админов сейчас имеет живую сессию.</summary>
    public int ActiveAdminCount(DateTime nowUtc)
    {
        lock (_gate)
        {
            PruneExpiredLocked(nowUtc);
            return _byPlayer.Count;
        }
    }

    private static string NewSessionId() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();

    private static string TrimAgent(string? userAgent)
    {
        var value = (userAgent ?? "").Trim();
        return value.Length <= 180 ? value : value[..180];
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

    /// <summary>Снять самую старую тестовую сессию (освободить место живому входу).</summary>
    private bool DropOldestTestLocked()
    {
        var oldest = _byHash.Values
            .Where(session => session.TestAuto)
            .OrderBy(session => session.IssuedUtc)
            .FirstOrDefault();
        if (oldest is null)
            return false;

        RemoveLocked(oldest.TokenHash, oldest.PlayerId);
        return true;
    }

    /// <summary>Оставить с одного адреса не больше <see cref="MaxTestSessionsPerIp"/> тестовых сессий.</summary>
    private void TrimTestSessionsForIpLocked(string ip)
    {
        var mine = _byHash.Values
            .Where(session => session.TestAuto
                              && string.Equals(session.Ip, ip, StringComparison.OrdinalIgnoreCase))
            .OrderBy(session => session.IssuedUtc)
            .ToList();

        for (var i = 0; i <= mine.Count - MaxTestSessionsPerIp; i++)
            RemoveLocked(mine[i].TokenHash, mine[i].PlayerId);
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
                    || string.IsNullOrEmpty(item.DeviceHash)
                    || item.ExpiresUtc <= now)
                    continue;

                var session = new AdminSession(
                    "", item.PlayerId, item.Nick ?? "", item.IssuedUtc,
                    item.ExpiresUtc, item.AuthVersion, item.TokenHash,
                    string.IsNullOrWhiteSpace(item.CsrfToken) ? NewToken() : item.CsrfToken,
                    item.Ip ?? "",
                    TrimAgent(item.UserAgent),
                    string.IsNullOrWhiteSpace(item.SessionId) ? NewSessionId() : item.SessionId,
                    item.StepUpUntilUtc,
                    DeviceHash: item.DeviceHash,
                    TestAuto: item.TestAuto);
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
                    CsrfToken = session.CsrfToken,
                    Ip = session.Ip,
                    UserAgent = session.UserAgent,
                    SessionId = session.SessionId,
                    StepUpUntilUtc = session.StepUpUntilUtc,
                    DeviceHash = session.DeviceHash,
                    TestAuto = session.TestAuto,
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
        public string CsrfToken { get; set; } = "";
        public string Ip { get; set; } = "";
        public string UserAgent { get; set; } = "";
        public string SessionId { get; set; } = "";
        public DateTime? StepUpUntilUtc { get; set; }
        public string DeviceHash { get; set; } = "";

        /// <summary>Вход тестового режима: умирает при выключении <c>test.noAdminPassword</c>.</summary>
        public bool TestAuto { get; set; }
    }
}

/// <summary>Одна админ-сессия: кто и когда вошёл.</summary>
/// <param name="Token">Значение куки при создании; у загруженной сессии пустое.</param>
/// <param name="PlayerId">Числовой ID админа.</param>
/// <param name="Nick">Ник на момент входа.</param>
/// <param name="IssuedUtc">Момент входа.</param>
/// <param name="ExpiresUtc">Срок (30 дней, продлевается при заходе в панель).</param>
/// <param name="AuthVersion">Версия пароля на момент входа.</param>
/// <param name="TokenHash">SHA-256 токена — то, что лежит на диске.</param>
/// <param name="DeviceToken">Токен устройства при создании; у загруженной пусто.</param>
/// <param name="DeviceHash">SHA-256 токена устройства — привязка к браузеру.</param>
/// <param name="TestAuto">Вход тестового режима без экрана входа (умирает при выключении флага).</param>
public sealed record AdminSession(
    string Token,
    int PlayerId,
    string Nick,
    DateTime IssuedUtc,
    DateTime ExpiresUtc,
    int AuthVersion,
    string TokenHash,
    string CsrfToken = "",
    string Ip = "",
    string UserAgent = "",
    string SessionId = "",
    DateTime? StepUpUntilUtc = null,
    string DeviceToken = "",
    string DeviceHash = "",
    bool TestAuto = false);
