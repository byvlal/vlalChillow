using System.Net;
using StandChillow.LanServer.Auth;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Лимит попыток входа в админку: 5 неудач за 5 минут с одного IP. Успешный вход
/// очищает счётчик. Отдельно от общего лимита запросов — брутфорс пароля не должен
/// зависеть от того, сколько страниц открыл браузер.
/// </summary>
public sealed class AdminLoginLimiter
{
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly Dictionary<string, List<DateTime>> _attempts = new(StringComparer.Ordinal);

    /// <summary>Можно ли попробовать пароль сейчас.</summary>
    public bool TryBegin(IPAddress? ip, DateTime nowUtc)
    {
        var key = Key(ip);
        lock (_gate)
        {
            var list = Clean(key, nowUtc);
            return list.Count < MaxAttempts;
        }
    }

    /// <summary>Записать неудачную попытку.</summary>
    public void Fail(IPAddress? ip, DateTime nowUtc)
    {
        var key = Key(ip);
        lock (_gate)
        {
            var list = Clean(key, nowUtc);
            list.Add(nowUtc);
        }
    }

    /// <summary>Успешный вход — счётчик больше не нужен.</summary>
    public void Reset(IPAddress? ip)
    {
        var key = Key(ip);
        lock (_gate)
            _attempts.Remove(key);
    }

    /// <summary>Сколько секунд осталось до следующей попытки (0 — можно).</summary>
    public int RetryAfterSec(IPAddress? ip, DateTime nowUtc)
    {
        var key = Key(ip);
        lock (_gate)
        {
            var list = Clean(key, nowUtc);
            if (list.Count < MaxAttempts)
                return 0;
            var wait = list[0].Add(Window) - nowUtc;
            return Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        }
    }

    private List<DateTime> Clean(string key, DateTime nowUtc)
    {
        if (!_attempts.TryGetValue(key, out var list))
            _attempts[key] = list = new List<DateTime>();

        var threshold = nowUtc - Window;
        list.RemoveAll(t => t <= threshold);
        if (list.Count == 0)
            _attempts.Remove(key);
        if (!_attempts.TryGetValue(key, out list))
            _attempts[key] = list = new List<DateTime>();
        return list;
    }

    private static string Key(IPAddress? ip) =>
        AdminStore.NormalizeIp(ip) is { Length: > 0 } text ? text : "unknown";
}
