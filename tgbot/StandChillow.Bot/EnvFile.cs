namespace StandChillow.Bot;

/// <summary>
/// Маленький разбор <c>.env</c>: <c>KEY=VALUE</c>, комментарии, кавычки.
/// </summary>
/// <remarks>
/// Без подстановок и многострочных значений: файл бота — это несколько строк, а не
/// язык конфигурации. Всё, что не похоже на пару ключ-значение, молча пропускается,
/// чтобы случайная строка не роняла запуск.
/// </remarks>
public static class EnvFile
{
    public static Dictionary<string, string> Load(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return result;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (line.StartsWith("export ", StringComparison.Ordinal))
                line = line[7..].TrimStart();

            var eq = line.IndexOf('=');
            if (eq <= 0)
                continue;

            var key = line[..eq].Trim();
            var value = line[(eq + 1)..].Trim();

            if (value.Length >= 2
                && ((value[0] == '"' && value[^1] == '"')
                    || (value[0] == '\'' && value[^1] == '\'')))
            {
                value = value[1..^1];
            }

            result[key] = value;
        }

        return result;
    }
}
