using System.Security.Cryptography;
using System.Text;

namespace StandChillow.LanServer.Auth;

/// <summary>
/// Персональный пароль админа: формат <c>&lt;id&gt;-&lt;hex64&gt;</c>, на диске только
/// SHA-256. Пароль не печатается в консоль и не попадает в JSON — только в файл
/// <c>auth/&lt;id&gt;/password.txt</c>.
/// </summary>
public static class AdminPassword
{
    /// <summary>Длина случайной части в hex-символах (32 байта).</summary>
    public const int SecretLength = 64;

    /// <summary>Сгенерировать случайную часть пароля: 64 hex-символа в нижнем регистре.</summary>
    public static string GenerateSecret() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretLength / 2)).ToLowerInvariant();

    /// <summary>Собрать пароль целиком: <c>&lt;id&gt;-&lt;secret&gt;</c>.</summary>
    public static string Format(int id, string secret) => $"{id}-{secret}";

    /// <summary>
    /// Проверить и нормализовать введённый пароль. Принимаем полный вид
    /// <c>&lt;id&gt;-&lt;hex64&gt;</c> и «голый» hex: id в веб-форме уже введён отдельно,
    /// а в чате пароль вставляют целиком. Чужой id не принимается никогда.
    /// </summary>
    public static bool TryParse(int id, string? input, out string normalized)
    {
        normalized = string.Empty;
        if (id <= 0 || string.IsNullOrWhiteSpace(input))
            return false;

        var text = input.Trim();
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            if (!int.TryParse(text[..dash], out var parsedId) || parsedId != id)
                return false;
            text = text[(dash + 1)..].Trim();
        }

        if (text.Length != SecretLength)
            return false;
        foreach (var c in text)
        {
            if (!Uri.IsHexDigit(c))
                return false;
        }

        normalized = Format(id, text.ToLowerInvariant());
        return true;
    }

    /// <summary>SHA-256 пароля в hex — то, что лежит в <c>admins.json</c>.</summary>
    public static string Hash(string password) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(password))).ToLowerInvariant();

    /// <summary>
    /// Совпадает ли введённый пароль с сохранённым хешем. Сравнение постоянное по
    /// времени: иначе по задержке можно подбирать hash побайтно.
    /// </summary>
    public static bool Matches(int id, string? input, string? storedHash)
    {
        if (!TryParse(id, input, out var normalized)
            || string.IsNullOrWhiteSpace(storedHash)
            || storedHash.Length != SecretLength)
            return false;

        var candidate = Hash(normalized);
        try
        {
            return CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(candidate),
                Convert.FromHexString(storedHash));
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
