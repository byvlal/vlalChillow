using System.Net;
using System.Security.Cryptography;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Админская сессия сайта: кто предъявил код из <c>auth.txt</c>.
/// </summary>
/// <param name="Token">Токен из куки.</param>
/// <param name="PlayerId">Числовой серверный id — тот, что в <c>admin.ids</c>.</param>
/// <param name="Nick">Ник на момент входа.</param>
/// <param name="Address">Адрес, с которого предъявили код.</param>
/// <param name="IssuedUtc">Когда код приняли.</param>
public sealed record SiteAdminSession(
    string Token,
    int PlayerId,
    string Nick,
    IPAddress? Address,
    DateTime IssuedUtc);

/// <summary>
/// Админские сессии сайта — отдельно от обычных.
/// </summary>
/// <remarks>
/// <para>
/// Обычная сессия (<see cref="SiteSessions"/>) говорит только «этот человек авторизован
/// на сайте». Права администратора — это <b>вторая</b> вещь: id обязан быть в
/// <c>admin.ids</c> <i>и</i> предъявить код текущего запуска, ровно как в игре
/// (<c>GameNetHost.IsAdminAuthorized</c>). Поэтому админ-ключ живёт в своей куке и своим
/// хранилищем: выход из сайта не должен оставлять админские права, а смена кода
/// (<c>auth reset</c>) должна гасить их все разом.
/// </para>
/// <para>
/// Живёт до перезапуска сервера — как сам код. На диск не пишется ничего: код в
/// <c>auth.txt</c> и так одноразовый на запуск.
/// </para>
/// <para>
/// Ключ привязан к адресу: код предъявляют с того же IP, с которого работают дальше.
/// Это не замена проверке id, а вторая створка — та же, что у игры.
/// </para>
/// </remarks>
public sealed class SiteAdminSessions
{
    /// <summary>Потолок: админов на LAN единицы, это предохранитель, а не правило.</summary>
    public const int MaxSessions = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, SiteAdminSession> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _byPlayer = new();

    /// <summary>Сколько админских сессий живо.</summary>
    public int Count
    {
        get { lock (_gate) return _byToken.Count; }
    }

    /// <summary>
    /// Открыть админскую сессию. Код проверяет вызывающий: здесь только запись.
    /// </summary>
    /// <returns>Сессия или <c>null</c>, если место кончилось.</returns>
    public SiteAdminSession? Create(int playerId, string nick, IPAddress? address, DateTime nowUtc)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
        {
            if (_byToken.Count >= MaxSessions)
                return null;

            var token = NewToken();
            var session = new SiteAdminSession(token, playerId, nick, address, nowUtc);
            _byToken[token] = session;

            if (!_byPlayer.TryGetValue(playerId, out var tokens))
                _byPlayer[playerId] = tokens = new HashSet<string>(StringComparer.Ordinal);
            tokens.Add(token);

            return session;
        }
    }

    /// <summary>
    /// Найти админскую сессию по токену.
    /// </summary>
    /// <param name="token">Токен из куки.</param>
    /// <param name="address">
    /// Адрес запроса. Если он известен и не совпадает с тем, с которого предъявляли код,
    /// сессия не действует: чужой браузер в той же сети не должен унаследовать права.
    /// </param>
    public SiteAdminSession? Find(string? token, IPAddress? address)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        lock (_gate)
        {
            if (!_byToken.TryGetValue(token, out var session))
                return null;

            if (address is not null && session.Address is not null
                && !SameAddress(session.Address, address))
            {
                return null;
            }

            return session;
        }
    }

    /// <summary>Погасить одну админскую сессию.</summary>
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
    /// Погасить всё: <c>auth reset</c> выдал новый код, старые ключи умерли вместе с ним.
    /// </summary>
    /// <returns>Сколько сессий погашено.</returns>
    public int DropAll()
    {
        lock (_gate)
        {
            var count = _byToken.Count;
            _byToken.Clear();
            _byPlayer.Clear();
            return count;
        }
    }

    /// <summary>Погасить сессии конкретного игрока (например, его id убрали из <c>admin.ids</c>).</summary>
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

    private static bool SameAddress(IPAddress a, IPAddress b)
    {
        // Один и тот же адрес приходит и как 127.0.0.1, и как ::ffff:127.0.0.1.
        var left = a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
        var right = b.IsIPv4MappedToIPv6 ? b.MapToIPv4() : b;
        return left.Equals(right);
    }

    /// <summary>32 случайных байта base64url: угадать ключ администратора нельзя.</summary>
    private static string NewToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }
}
