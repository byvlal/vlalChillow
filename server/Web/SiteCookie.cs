namespace StandChillow.LanServer.Web;

/// <summary>
/// Кука сессии сайта: прочитать из запроса и собрать для ответа. Без ASP.NET.
/// </summary>
/// <remarks>
/// <para>
/// Кука — сессионная (без <c>Max-Age</c>/<c>Expires</c>): браузер забудет её при закрытии,
/// а сервер и так держит сессии только в памяти и до перезапуска (#51).
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
    /// <summary>Имя куки сессии.</summary>
    public const string Name = "sc_session";

    /// <summary>
    /// Имя куки админского ключа. Отдельная кука, а не поле в сессии: ключ от
    /// <c>auth.txt</c> — это вторая створка прав, и она должна сниматься отдельно.
    /// </summary>
    public const string AdminName = "sc_admin";

    /// <summary>
    /// Достать токен сессии из заголовка <c>Cookie</c>. Нет куки — <c>null</c>.
    /// </summary>
    /// <param name="headers">Заголовки запроса (регистр имени не важен).</param>
    public static string? ReadToken(IReadOnlyDictionary<string, string> headers) =>
        Read(headers, Name);

    /// <summary>Достать токен админского ключа из заголовка <c>Cookie</c>.</summary>
    public static string? ReadAdminToken(IReadOnlyDictionary<string, string> headers) =>
        Read(headers, AdminName);

    private static string? Read(IReadOnlyDictionary<string, string>? headers, string cookieName)
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
            if (!name.Equals(cookieName, StringComparison.OrdinalIgnoreCase))
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
    public static (string Name, string Value) Build(string token, string path, bool secure) =>
        Build(Name, token, path, secure);

    /// <summary>Заголовок <c>Set-Cookie</c> с живым админским ключом.</summary>
    public static (string Name, string Value) BuildAdmin(string token, string path, bool secure) =>
        Build(AdminName, token, path, secure);

    private static (string Name, string Value) Build(
        string cookieName, string token, string path, bool secure)
    {
        var scope = NormalizePath(path);
        var value = $"{cookieName}={token}; Path={scope}; HttpOnly; SameSite=Lax";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }

    /// <summary>
    /// Заголовок <c>Set-Cookie</c>, который гасит куку (пустое значение, истёкший срок).
    /// </summary>
    public static (string Name, string Value) BuildExpired(string path, bool secure) =>
        BuildExpired(Name, path, secure);

    /// <summary>Заголовок <c>Set-Cookie</c>, который гасит админский ключ.</summary>
    public static (string Name, string Value) BuildAdminExpired(string path, bool secure) =>
        BuildExpired(AdminName, path, secure);

    private static (string Name, string Value) BuildExpired(
        string cookieName, string path, bool secure)
    {
        var scope = NormalizePath(path);
        var value = $"{cookieName}=; Path={scope}; HttpOnly; SameSite=Lax; Max-Age=0";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }

    /// <summary>
    /// Путь куки: за прокси панель живёт в подпути, и кука обязана совпадать с ним —
    /// иначе браузер её не пришлёт.
    /// </summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        var text = path.Trim();
        if (!text.StartsWith('/'))
            text = "/" + text;

        return text.Length == 1 ? "/" : text.TrimEnd('/');
    }
}
