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
/// Режимы не смешиваются: <c>local</c> ходит на свою панель, <c>remote</c> — на сайт
/// Новосибирска. У каждого свой ключ и свой игровой адрес; привязки игроков в базе
/// тоже разделены по <see cref="ServerId"/>.
/// </para>
/// </remarks>
public sealed class BotConfig
{
    private static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public required string TelegramToken { get; init; }
    public string? TelegramProxy { get; init; }
    public required string Mode { get; init; }
    public required string ServerLabel { get; init; }
    public required string ApiBaseUrl { get; init; }
    public required string BotKey { get; init; }
    public required string GameAddress { get; init; }
    public required string DbPath { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public required string TimeZoneId { get; init; }
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Ключ базы: local и remote не должны делить привязки.</summary>
    public string ServerId => Mode;

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

        var token = Get("TELEGRAM_BOT_TOKEN");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new BotConfigException(
                "Не задан TELEGRAM_BOT_TOKEN.\n" +
                "Создай бота у @BotFather и впиши токен в .env:\n" +
                "  TELEGRAM_BOT_TOKEN=123456:ABC...\n" +
                $"Файл: {envPath}");
        }

        var mode = (Get("BOT_MODE") ?? "local").ToLowerInvariant();
        if (mode is not ("local" or "remote"))
        {
            throw new BotConfigException(
                $"BOT_MODE={mode} — можно только local или remote.");
        }

        var proxy = Get("TELEGRAM_PROXY");
        if (!string.IsNullOrWhiteSpace(proxy))
        {
            if (!Uri.TryCreate(proxy, UriKind.Absolute, out var proxyUri)
                || proxyUri.Scheme is not ("http" or "https"))
            {
                throw new BotConfigException(
                    "TELEGRAM_PROXY должен быть http:// или https:// (например " +
                    "http://127.0.0.1:14883). SOCKS5 напрямую не поддерживается — " +
                    "у MargyCode тот же порт принимает и HTTP.");
            }
        }

        var timeout = 20;
        if (int.TryParse(Get("BOT_API_TIMEOUT_SEC"), out var parsedTimeout))
            timeout = Math.Clamp(parsedTimeout, 5, 120);

        string apiBase;
        string botKey;
        string gameAddress;
        string label;

        if (mode == "local")
        {
            var api = Get("LOCAL_API_URL") ?? "http://127.0.0.1:8080";
            apiBase = NormalizeApiBase(api, "LOCAL_API_URL");
            botKey = RequireKey(Get("LOCAL_BOT_KEY"), "LOCAL_BOT_KEY");
            gameAddress = NormalizeAddress(
                Get("LOCAL_GAME_ADDRESS") ?? "127.0.0.1:7778");
            label = Get("BOT_SERVER_LABEL") ?? "локальный сервер";
        }
        else
        {
            var site = Get("REMOTE_SITE_URL");
            if (string.IsNullOrWhiteSpace(site))
            {
                throw new BotConfigException(
                    "Для BOT_MODE=remote нужен REMOTE_SITE_URL, например\n" +
                    "  REMOTE_SITE_URL=https://skam.su/vlalchillow");
            }

            apiBase = NormalizeApiBase(site, "REMOTE_SITE_URL");
            botKey = RequireKey(Get("REMOTE_BOT_KEY"), "REMOTE_BOT_KEY");
            var game = Get("REMOTE_GAME_ADDRESS");
            if (string.IsNullOrWhiteSpace(game))
            {
                throw new BotConfigException(
                    "Для BOT_MODE=remote нужен REMOTE_GAME_ADDRESS, например\n" +
                    "  REMOTE_GAME_ADDRESS=212.164.115.37:7778");
            }

            gameAddress = NormalizeAddress(game);
            label = Get("BOT_SERVER_LABEL") ?? "Новосибирск";
        }

        var dbPath = Get("BOT_DB_PATH") ?? "data/bot.db";
        dbPath = Path.GetFullPath(dbPath);

        var tzId = Get("BOT_TIMEZONE") ?? "Asia/Novosibirsk";
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
            Mode = mode,
            ServerLabel = label,
            ApiBaseUrl = apiBase,
            BotKey = botKey,
            GameAddress = gameAddress,
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
