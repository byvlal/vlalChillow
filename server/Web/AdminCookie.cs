namespace StandChillow.LanServer.Web;

/// <summary>
/// Кука админ-сессии веб-панели: «пароль предъявлен». Без ASP.NET.
/// </summary>
/// <remarks>
/// <para>
/// Отдельная от <see cref="SiteCookie"/>: админ-доступ — не то же самое, что вход на сайт.
/// Токен и его срок лежат в <c>auth/sessions.json</c>, поэтому кука и сессия
/// переживают и рестарт сервера, и закрытие браузера.
/// </para>
/// <para>
/// Вторая кука — <c>sc_admin_dev</c> — привязывает сессию к конкретному браузеру.
/// Без неё скопированная админ-кука не даёт доступа: сервер сверяет хеш устройства,
/// записанный при входе. Обе куки <c>HttpOnly</c>, <c>SameSite=Strict</c>, <c>Secure</c>
/// по флагу (по LAN панель обычно открывают по http).
/// </para>
/// </remarks>
public static class AdminCookie
{
    /// <summary>Имя куки админ-сессии.</summary>
    public const string Name = "sc_admin";

    /// <summary>Имя куки устройства, к которому привязана админ-сессия.</summary>
    public const string DeviceName = "sc_admin_dev";

    /// <summary>
    /// Достать токен админ-сессии из заголовка <c>Cookie</c>. Нет куки — <c>null</c>.
    /// </summary>
    /// <param name="headers">Заголовки запроса (регистр имени не важен).</param>
    public static string? ReadToken(IReadOnlyDictionary<string, string> headers) =>
        ReadCookie(headers, Name);

    /// <summary>Достать токен устройства из заголовка <c>Cookie</c>.</summary>
    public static string? ReadDeviceToken(IReadOnlyDictionary<string, string> headers) =>
        ReadCookie(headers, DeviceName);

    /// <summary>Заголовок <c>Set-Cookie</c> с живым админ-токеном.</summary>
    /// <param name="token">Токен админ-сессии.</param>
    /// <param name="path">Префикс панели (<c>/vlalchillow</c> за прокси) или <c>/</c>.</param>
    /// <param name="secure">Ставить ли <c>Secure</c> (только https).</param>
    public static (string Name, string Value) Build(string token, string path, bool secure) =>
        BuildCookie(Name, token, path, secure, AdminSessions.TtlDays);

    /// <summary>Заголовок <c>Set-Cookie</c> с живым токеном устройства.</summary>
    public static (string Name, string Value) BuildDevice(string token, string path, bool secure) =>
        BuildCookie(DeviceName, token, path, secure, AdminSessions.TtlDays);

    /// <summary>Заголовок <c>Set-Cookie</c>, который гасит админ-куку.</summary>
    public static (string Name, string Value) BuildExpired(string path, bool secure) =>
        BuildCookie(Name, "", path, secure, 0);

    /// <summary>Заголовок <c>Set-Cookie</c>, который гасит куку устройства.</summary>
    public static (string Name, string Value) BuildDeviceExpired(string path, bool secure) =>
        BuildCookie(DeviceName, "", path, secure, 0);

    private static (string Name, string Value) BuildCookie(
        string name, string token, string path, bool secure, int days)
    {
        var scope = SiteCookie.NormalizePath(path);
        var value = $"{name}={token}; Path={scope}; Max-Age={days * 24 * 60 * 60}; " +
                    "HttpOnly; SameSite=Strict";
        if (secure)
            value += "; Secure";

        return ("Set-Cookie", value);
    }

    private static string? ReadCookie(
        IReadOnlyDictionary<string, string> headers, string name)
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

            var cookieName = part[..eq].Trim();
            if (!cookieName.Equals(name, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = part[(eq + 1)..].Trim();
            return value.Length == 0 ? null : value;
        }

        return null;
    }
}
