using System.Text.RegularExpressions;

namespace StandChillow.Bot;

/// <summary>Ошибка конфигурации: показывается человеку один раз и без стека.</summary>
public sealed class BotConfigException : Exception
{
    public BotConfigException(string message) : base(message) { }
}

/// <summary>
/// Настройки бота из <c>.env</c> и переменных окружения.
/// </summary>
/// <remarks>
/// <para>
/// Файл ищется рядом с запуском (текущий каталог), затем рядом с бинарником;
/// <c>BOT_ENV_FILE</c> задаёт путь явно. Переменная окружения сильнее файла — так
/// systemd или ручной запуск могут переопределить одно значение, не трогая остальные.
/// </para>
/// <para>
/// Бот обслуживает один сервер — Новосибирск. Адрес панели, ключ и адрес для игроков
/// задаются один раз; локального стенда в боте нет.
/// </para>
/// </remarks>
public sealed class BotConfig
{
    private static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public const string DefaultSiteUrl = "https://skam.su/vlalchillow";
    public const string DefaultGameAddress = "212.164.115.37:7778";
    public const string DefaultLabel = "Новосибирск";

    public required string TelegramToken { get; init; }
    public string? TelegramProxy { get; init; }

    /// <summary>Адрес входа бота, всегда с <c>/api/bot</c> на конце.</summary>
    public required string ApiBaseUrl { get; init; }

    /// <summary>Постоянный ключ из <c>auth/tgbot/password.txt</c>.</summary>
    public required string BotKey { get; init; }

    /// <summary>Адрес, который бот показывает игрокам в инструкции.</summary>
    public required string GameAddress { get; init; }

    /// <summary>Человеческое имя сервера для сообщений.</summary>
    public required string Label { get; init; }

    public required string DbPath { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public required string TimeZoneId { get; init; }
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(20);

    public static BotConfig Load()
    {
        var envPath = FindEnvFile();
        var file = EnvFile.Load(envPath);

        string? Get(string key)
        {
            var env = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(env))
                return env.Trim();
            return file.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }

        return Parse(Get, envPath);
    }

    /// <summary>Разбор значений без чтения файла — чтобы конфигурацию можно было тестировать.</summary>
    internal static BotConfig Parse(Func<string, string?> get, string envPath)
    {
        var token = get("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new BotConfigException(
                "Не задан TELEGRAM_BOT_TOKEN.\n" +
                "Создай бота у @BotFather и впиши токен в .env:\n" +
                "  TELEGRAM_BOT_TOKEN=123456:ABC...\n" +
                $"Файл: {envPath}");
        }

        var proxy = get("TELEGRAM_PROXY");
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri)
                || proxyUri.Scheme is not ("http" or "https"))
            {
                throw new BotConfigException(
                    "TELEGRAM_PROXY должен быть http:// или https:// (например " +
                    "http://127.0.0.1:14883). SOCKS5 напрямую не поддерживается — " +
                    "тот же порт обычно принимает и HTTP.");
            }
        }

        var timeout = 20;
        if (int.TryParse(get("BOT_API_TIMEOUT_SEC"), out var parsedTimeout))
            timeout = Math.Clamp(parsedTimeout, 5, 120);

        var site = get("SITE_URL") ?? DefaultSiteUrl;
        var game = get("GAME_ADDRESS") ?? DefaultGameAddress;
        var label = get("BOT_LABEL") ?? DefaultLabel;
        var botKey = RequireKey(get("BOT_KEY"), "BOT_KEY");

        var dbPath = Path.GetFullPath(get("BOT_DB_PATH") ?? "data/bot.db");

        var tzId = get("BOT_TIMEZONE") ?? "Asia/Novosibirsk";
        TimeZoneInfo tz;
        try
        {
            tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch (TimeZoneNotFoundException)
        {
            tz = TimeZoneInfo.Utc;
            tzId = "UTC";
        }
        catch (InvalidTimeZoneException)
        {
            tz = TimeZoneInfo.Utc;
            tzId = "UTC";
        }

        return new BotConfig
        {
            TelegramToken = token,
            TelegramProxy = string.IsNullOrWhiteSpace(proxy) ? null : proxy,
            ApiBaseUrl = NormalizeApiBase(site, "SITE_URL"),
            BotKey = botKey,
            GameAddress = NormalizeAddress(game),
            Label = label,
            DbPath = dbPath,
            TimeZone = tz,
            TimeZoneId = tzId,
            ApiTimeout = TimeSpan.FromSeconds(timeout),
        };
    }

    private static string FindEnvFile()
    {
        var explicitPath = Environment.GetEnvironmentVariable("BOT_ENV_FILE");
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var local = Path.Combine(Directory.GetCurrentDirectory(), ".env");
        if (File.Exists(local))
            return local;

        var nearBinary = Path.Combine(AppContext.BaseDirectory, ".env");
        if (File.Exists(nearBinary))
            return nearBinary;

        return local;
    }

    private static string NormalizeApiBase(string value, string key)
    {
        var text = value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
        {
            throw new BotConfigException($"{key}={value} — нужен полный http(s)-адрес.");
        }

        if (text.EndsWith("/api/bot", StringComparison.OrdinalIgnoreCase))
            return text;
        return text + "/api/bot";
    }

    private static string RequireKey(string? value, string key)
    {
        if (string.IsNullOrWhiteSpace(value) || !Hex64.IsMatch(value.Trim()))
        {
            throw new BotConfigException(
                $"{key} должен быть 64 hex-символа (ключ из auth/tgbot/password.txt).\n" +
                "На сервере: tgbot resetkey — команда покажет новый ключ.");
        }

        return value.Trim().ToLowerInvariant();
    }

    private static string NormalizeAddress(string value)
    {
        var text = value.Trim();
        if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            text = text[7..];
        else if (text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = text[8..];
        return text.TrimEnd('/');
    }
}
