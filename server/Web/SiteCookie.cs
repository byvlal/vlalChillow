namespace StandChillow.LanServer.Web;

/// <summary>
/// Кука сессии сайта: прочитать из запроса и собрать для ответа. Без ASP.NET.
/// </summary>
/// <remarks>
/// <para>
/// Кука живёт 30 дней и переживает закрытие браузера: срок и хеш токена лежат в
/// <c>auth/site-sessions.json</c>, а не только в памяти.
/// </para>
/// <para>
/// <c>HttpOnly</c> — чтобы скрипт на странице не вытащил токен; <c>SameSite=Lax</c> — чтобы
/// кука не уезжала на чужие сайты, но переживала переход по ссылке на панель.
/// <c>Secure</c> включается флагом: по LAN панель часто открывают по http, и с ним кука
/// просто не применится.
/// </para>
/// </remarks>
public static class SiteCookie
{
    /// <summary>Имя куки.</summary>
    public const string Name = "sc_session";

    /// <summary>
    /// Достать токен сессии из заголовка <c>Cookie</c>. Нет куки — <c>null</c>.
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

        // Заголовок — «name=value; name2=value2». Нужна только наша кука.
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

    /// <summary>
    /// Заголовок <c>Set-Cookie</c> с живым токеном.
    /// </summary>
    /// <param name="token">Токен сессии.</param>
    /// <param name="path">Префикс панели (<c>/vlalchillow</c> за прокси) или <c>/</c>.</param>
    /// <param name="secure">Ставить ли <c>Secure</c> (только https).</param>
    public static (string Name, string Value) Build(string token, string path, bool secure)
    {
        var scope = NormalizePath(path);
        var value = $"{Name}={token}; Path={scope}; Max-Age={SiteSessions.TtlDays * 24 * 60 * 60}; " +
                    "HttpOnly; SameSite=Lax";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }

    /// <summary>
    /// Заголовок <c>Set-Cookie</c>, который гасит куку (пустое значение, истёкший срок).
    /// </summary>
    public static (string Name, string Value) BuildExpired(string path, bool secure)
    {
        var scope = NormalizePath(path);
        var value = $"{Name}=; Path={scope}; HttpOnly; SameSite=Lax; Max-Age=0";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }

    /// <summary>
    /// Путь куки: за прокси панель живёт в подпути, и кука обязана совпадать с ним —
    /// иначе браузер её не пришлёт.
    /// </summary>
    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var text = path.Trim();
        if (!text.StartsWith('/'))
            text = "/" + text;

        return text.Length == 1 ? "/" : text.TrimEnd('/');
    }
}
