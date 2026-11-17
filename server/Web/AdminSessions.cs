using System.Security.Cryptography;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Админ-сессии веб-панели: «кто уже ввёл код этого запуска».
/// </summary>
/// <remarks>
/// <para>
/// Живут только в памяти процесса: рестарт или выключение сервера — и доступ
/// пропал, код из auth.txt вводится заново. Так же, как авторизация по IP в
/// игровом лобби (<see cref="Net.AdminAuth"/>): тот же код, тот же срок жизни.
/// </para>
/// <para>
/// Отдельно от <see cref="SiteSessions"/> намеренно: сессия сайта — «кто ты»,
/// сессия админа — «ты подтвердил код этого запуска». Угнанная сессия сайта
/// без админ-куки в панель не пускает, и наоборот.
/// </para>
/// </remarks>
public sealed class AdminSessions
{
    /// <summary>
    /// Потолок. Админ-сессия выдаётся только по коду из auth.txt, так что это
    /// предохранитель от накопления мусора, а не игровое правило.
    /// </summary>
    public const int MaxSessions = 100;

    private readonly object _gate = new();
    private readonly Dictionary<string, AdminSession> _byToken = new(StringComparer.Ordinal);
    private readonly Dictionary<int, HashSet<string>> _byPlayer = new();

    /// <summary>Сколько админ-сессий сейчас живо.</summary>
    public int Count
    {
        get { lock (_gate) return _byToken.Count; }
    }

    /// <summary>
    /// Открыть админ-сессию игроку, который только что верно ввёл код.
    /// </summary>
    public AdminSession? Create(int playerId, string nick, DateTime nowUtc)
    {
        if (playerId <= 0)
            return null;

        lock (_gate)
        {
            if (_byToken.Count >= MaxSessions)
                return null;

            var token = NewToken();
            var session = new AdminSession(token, playerId, nick, nowUtc);
            _byToken[token] = session;

            if (!_byPlayer.TryGetValue(playerId, out var tokens))
                _byPlayer[playerId] = tokens = new HashSet<string>(StringComparer.Ordinal);
            tokens.Add(token);

            return session;
        }
    }

    /// <summary>Живая ли админ-сессия по токену из куки.</summary>
    public AdminSession? Find(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;

        lock (_gate)
            return _byToken.TryGetValue(token, out var session) ? session : null;
    }

    /// <summary>Выйти из админ-панели (одна конкретная кука).</summary>
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

    /// <summary>Снять все админ-сессии игрока (смена кода, бан админа и т.п.).</summary>
    public int DropByPlayer(int playerId)
    {
        lock (_gate)
        {
            if (!_byPlayer.Remove(playerId, out var tokens))
                return 0;
            foreach (var token in tokens)
                _byToken.Remove(token);
            return tokens.Count;
        }
    }

    /// <summary>Снять всё: новый код этого запуска (auth reset) обнуляет доступ.</summary>
    public int DropAll()
    {
        lock (_gate)
        {
            var n = _byToken.Count;
            _byToken.Clear();
            _byPlayer.Clear();
            return n;
        }
    }

    private static string NewToken()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}

/// <summary>Одна админ-сессия: токен куки + кто и когда открыл.</summary>
/// <param name="Token">Значение куки <see cref="AdminCookie.Name"/>.</param>
/// <param name="PlayerId">Числовой ID админа (из admin.ids) — на ком открыта.</param>
/// <param name="Nick">Ник на момент входа — только для отображения.</param>
/// <param name="IssuedUtc">Момент ввода кода.</param>
public sealed record AdminSession(string Token, int PlayerId, string Nick, DateTime IssuedUtc);
