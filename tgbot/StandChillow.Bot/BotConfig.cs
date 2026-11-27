using System.Text.RegularExpressions;

namespace StandChillow.Bot;

/// <summary>Ошибка конфигурации: показывается человеку один раз и без стека.</summary>
public sealed class BotConfigException : Exception
{
    public BotConfigException(string message) : base(message) { }
}

/// <summary>
/// Один игровой сервер, к которому умеет ходить бот: панель, ключ и адрес для игроков.
/// </summary>
/// <remarks>
/// Профили независимы: у локального стенда и у Новосибирска свои ключи, свои сессии
/// игроков и свои адреса. Бот держит по клиенту на профиль и выбирает сервер по коду
/// из лобби, а не по глобальному режиму.
/// </remarks>
public sealed class ServerProfile
{
    /// <summary>Ключ привязки в базе: <c>local</c> или <c>remote</c>.</summary>
    public required string Id { get; init; }

    /// <summary>Человеческое имя для сообщений: «локальный сервер», «Новосибирск».</summary>
    public required string Label { get; init; }

    /// <summary>Адрес входа бота, всегда с <c>/api/bot</c> на конце.</summary>
    public required string ApiBaseUrl { get; init; }

    /// <summary>Постоянный ключ из <c>auth/tgbot/password.txt</c>.</summary>
    public required string BotKey { get; init; }

    /// <summary>Адрес, который бот показывает игрокам в инструкции.</summary>
    public required string GameAddress { get; init; }

    /// <summary>Имя переменной с ключом — для честной подсказки владельцу.</summary>
    public required string KeyEnvName { get; init; }
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
/// Серверов может быть несколько: <c>local</c> и <c>remote</c> настраиваются
/// независимо, и бот сам выбирает сервер по коду из лобби. <c>BOT_SERVERS</c>
/// ограничивает список (например, <c>remote</c>), а без него включаются все серверы,
/// для которых задан ключ. Старый <c>BOT_MODE</c> понимается как <c>BOT_SERVERS</c>
/// из одного имени.
/// </para>
/// </remarks>
public sealed class BotConfig
{
    private static readonly Regex Hex64 = new("^[0-9a-fA-F]{64}$", RegexOptions.Compiled);

    public required string TelegramToken { get; init; }
    public string? TelegramProxy { get; init; }
    public required IReadOnlyList<ServerProfile> Servers { get; init; }
    public required string DbPath { get; init; }
    public required TimeZoneInfo TimeZone { get; init; }
    public required string TimeZoneId { get; init; }
    public TimeSpan ApiTimeout { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>Профиль по ключу привязки или <c>null</c>, если сервер убрали из .env.</summary>
    public ServerProfile? FindServer(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        foreach (var server in Servers)
        {
            if (server.Id.Equals(id, StringComparison.Ordinal))
                return server;
        }

        return null;
    }

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

        var selected = ParseServerList(get("BOT_SERVERS") ?? get("BOT_MODE"));
        var servers = new List<ServerProfile>(2);

        // Сначала настоящий сервер: если один и тот же код вдруг подойдёт обоим,
        // привязка уйдёт к Новосибирску, а не к тестовому стенду.
        AddRemote(servers, get, selected);
        AddLocal(servers, get, selected);

        if (servers.Count == 0)
        {
            throw new BotConfigException(
                "Не настроен ни один сервер.\n" +
                "Задай LOCAL_BOT_KEY и/или REMOTE_BOT_KEY в .env " +
                "(ключ лежит в auth/tgbot/password.txt на сервере).");
        }

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
            Servers = servers,
            DbPath = dbPath,
            TimeZone = tz,
            TimeZoneId = tzId,
            ApiTimeout = TimeSpan.FromSeconds(timeout),
        };
    }

    private static void AddRemote(
        List<ServerProfile> servers, Func<string, string?> get, HashSet<string>? selected)
    {
        var key = get("REMOTE_BOT_KEY");
        if (selected is null && string.IsNullOrWhiteSpace(key))
            return;
        if (selected is not null && !selected.Contains("remote"))
            return;

        var site = get("REMOTE_SITE_URL");
        if (string.IsNullOrWhiteSpace(site))
        {
            throw new BotConfigException(
                "Для удалённого сервера нужен REMOTE_SITE_URL, например\n" +
                "  REMOTE_SITE_URL=https://skam.su/vlalchillow");
        }

        var game = get("REMOTE_GAME_ADDRESS");
        if (string.IsNullOrWhiteSpace(game))
        {
            throw new BotConfigException(
                "Для удалённого сервера нужен REMOTE_GAME_ADDRESS, например\n" +
                "  REMOTE_GAME_ADDRESS=212.164.115.37:7778");
        }

        servers.Add(new ServerProfile
        {
            Id = "remote",
            Label = get("REMOTE_LABEL") ?? "Новосибирск",
            ApiBaseUrl = NormalizeApiBase(site, "REMOTE_SITE_URL"),
            BotKey = RequireKey(key, "REMOTE_BOT_KEY"),
            GameAddress = NormalizeAddress(game),
            KeyEnvName = "REMOTE_BOT_KEY",
        });
    }

    private static void AddLocal(
        List<ServerProfile> servers, Func<string, string?> get, HashSet<string>? selected)
    {
        var key = get("LOCAL_BOT_KEY");
        if (selected is null && string.IsNullOrWhiteSpace(key))
            return;
        if (selected is not null && !selected.Contains("local"))
            return;

        var api = get("LOCAL_API_URL") ?? "http://127.0.0.1:8080";
        var game = get("LOCAL_GAME_ADDRESS") ?? "127.0.0.1:7778";

        servers.Add(new ServerProfile
        {
            Id = "local",
            Label = get("LOCAL_LABEL") ?? "локальный сервер",
            ApiBaseUrl = NormalizeApiBase(api, "LOCAL_API_URL"),
            BotKey = RequireKey(key, "LOCAL_BOT_KEY"),
            GameAddress = NormalizeAddress(game),
            KeyEnvName = "LOCAL_BOT_KEY",
        });
    }

    private static HashSet<string>? ParseServerList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var id = raw.Trim().ToLowerInvariant();
            if (id is not ("local" or "remote"))
            {
                throw new BotConfigException(
                    $"BOT_SERVERS={value} — можно только local и remote через запятую.");
            }

            result.Add(id);
        }

        return result.Count == 0 ? null : result;
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
