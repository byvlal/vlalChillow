using System.Security.Cryptography;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Сессия сайта: кто авторизовался по коду из лобби.
/// </summary>
/// <param name="Token">Токен из куки.</param>
/// <param name="PlayerId">Серверный ID игрока — тот же, что в лобби и в катке.</param>
/// <param name="Nick">Ник на момент входа.</param>
/// <param name="IssuedUtc">Когда обменяли код.</param>
/// <param name="LastSeenUtc">Последний запрос с этой сессией.</param>
/// <param name="DisplayId">
/// Публичный id, если админ заменил числовой (см. <c>PublicIdOverrides</c>).
/// <c>null</c> — показывать числовой.
/// </param>
public sealed record SiteSession(
    string Token,
    int PlayerId,
    string Nick,
    DateTime IssuedUtc,
    DateTime LastSeenUtc,
    string? DisplayId = null);

/// <summary>
/// Сессии сайта: обменял код → получил токен → работаешь.
/// </summary>
/// <remarks>
/// <para>
/// Правила (#51): авторизуются один раз, сессия живёт <b>до перезапуска сервера</b> —
/// никакого TTL и ничего на диске. Рестарт гасит все сессии и все коды: авторизацию
/// приходится проходить заново.
/// </para>
/// <para>
/// Ротация кода и перезаход в лобби сессию <b>не</b> убивают: иначе «авторизовался один
/// раз, больше не надо» перестало бы выполняться. Гасит сессию только выход (<c>/logout</c>)
/// и бан — его проверяет вызывающий на каждом запросе.
/// </para>
/// <para>
/// Одному игроку разрешено несколько сессий (телефон и компьютер), поэтому гасим сразу
/// все его токены: снять бан или выгнать человека иначе нельзя.
/// </para>
/// </remarks>
public sealed class SiteSessions
{
    /// <summary>
    /// Потолок сессий. Сессия создаётся только по коду из лобби, так что это предохранитель
    /// от бесконечного накопления, а не игровое правило.
    /// </summary>
    public const int MaxSessions = 5000;

    private readonly object _gate = new();
    private readonly Dictionary<string, SiteSession> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _byPlayer = new();

    /// <summary>Сколько сессий сейчас живо.</summary>
    public int Count
    {
        get { lock (_gate) return _byToken.Count; }
    }

    /// <summary>
    /// Создать сессию. Возвращает <c>null</c>, если место кончилось.
    /// </summary>
    public SiteSession? Create(int playerId, string nick, DateTime nowUtc)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
        {
            if (_byToken.Count >= MaxSessions)
                return null;

            var token = NewToken();
            var session = new SiteSession(token, playerId, nick, nowUtc, nowUtc);
            _byToken[token] = session;

            if (!_byPlayer.TryGetValue(playerId, out var tokens))
                _byPlayer[playerId] = tokens = new HashSet<string>(StringComparer.Ordinal);
            tokens.Add(token);

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

        lock (_gate)
        {
            if (!_byToken.TryGetValue(token, out var session))
                return null;

            var updated = session with { LastSeenUtc = nowUtc };
            _byToken[token] = updated;
            return updated;
        }
    }

    /// <summary>Погасить одну сессию (выход).</summary>
    public bool Drop(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return false;

        lock (_gate)
        {
            if (!_byToken.Remove(token, out var session))
                return false;

            if (_byPlayer.TryGetValue(session.PlayerId, out var tokens))
            {
                tokens.Remove(token);
                if (tokens.Count == 0)
                    _byPlayer.Remove(session.PlayerId);
            }

            return true;
        }
    }

    /// <summary>
    /// Погасить все сессии игрока — например, он забанен, пока сидел на сайте.
    /// </summary>
    /// <returns>Сколько сессий погашено.</returns>
    public int DropByPlayer(int playerId)
    {
        lock (_gate)
        {
            if (!_byPlayer.Remove(playerId, out var tokens))
                return 0;

            var dropped = 0;
            foreach (var token in tokens)
            {
                if (_byToken.Remove(token))
                    dropped++;
            }

            return dropped;
        }
    }

    /// <summary>
    /// Свежий токен: 32 случайных байта, base64url. Криптографический генератор — это
    /// пропуск на сайт, угадывать токен не должны.
    /// </summary>
    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);

        // base64url: в куке «+», «/» и «=» только мешают.
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
