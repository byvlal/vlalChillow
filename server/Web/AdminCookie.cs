namespace StandChillow.LanServer.Web;

/// <summary>
/// Кука админ-сессии веб-панели: «пароль предъявлен». Без ASP.NET.
/// </summary>
/// <remarks>
/// <para>
/// Отдельная от <see cref="SiteCookie"/>: админ-доступ — не то же самое, что вход на сайт.
/// Токен и его срок (7 дней) лежат в <c>auth/sessions.json</c>, поэтому кука и сессия
/// переживают и рестарт сервера, и закрытие браузера.
/// </para>
/// <para>
/// Флаги как у куки сайта: <c>HttpOnly</c>, <c>SameSite=Lax</c>, <c>Secure</c> по флагу
/// (по LAN панель обычно открывают по http).
/// </para>
/// </remarks>
public static class AdminCookie
{
    /// <summary>Имя куки.</summary>
    public const string Name = "sc_admin";

    /// <summary>
    /// Достать токен админ-сессии из заголовка <c>Cookie</c>. Нет куки — <c>null</c>.
    /// </summary>
    /// <param name="headers">Заголовки запроса (регистр имени не важен).</param>
    public static string? ReadToken(IReadOnlyDictionary<string, string> headers)
    {
        if (headers is null)
            return null;
        if (!headers.TryGetValue("Cookie", out var raw) && !headers.TryGetValue("cookie", out raw))
            return null;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        foreach (var part in raw.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;

            var name = part[..eq].Trim();
            if (!name.Equals(Name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = part[(eq + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    /// <summary>Заголовок <c>Set-Cookie</c> с живым админ-токеном.</summary>
    /// <param name="token">Токен админ-сессии.</param>
    /// <param name="path">Префикс панели (<c>/vlalchillow</c> за прокси) или <c>/</c>.</param>
    /// <param name="secure">Ставить ли <c>Secure</c> (только https).</param>
    public static (string Name, string Value) Build(string token, string path, bool secure)
    {
        var scope = SiteCookie.NormalizePath(path);
        var value = $"{Name}={token}; Path={scope}; Max-Age={AdminSessions.TtlDays * 24 * 60 * 60}; " +
                    "HttpOnly; SameSite=Lax";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }

    /// <summary>Заголовок <c>Set-Cookie</c>, который гасит админ-куку.</summary>
    public static (string Name, string Value) BuildExpired(string path, bool secure)
    {
        var scope = SiteCookie.NormalizePath(path);
        var value = $"{Name}=; Path={scope}; HttpOnly; SameSite=Lax; Max-Age=0";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }
}
