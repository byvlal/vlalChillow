using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Кому разрешена команда из чата. Чистая функция — без сокетов и без состояния,
/// чтобы матрицу прав можно было проверить тестом, а не «на глазок» в игре.
/// </summary>
/// <remarks>
/// <para>
/// Порядок решения: справка/готовность (всегда всем) → запуск матча
/// (<c>admin.anyoneCanStart</c>) → команда не в <c>adminOnlyCommands</c> (всем) →
/// админ. Всё остальное — отказ с причиной, которую вызывающий обязан
/// показать игроку и записать в лог.
/// </para>
/// <para>
/// «Админ» здесь — серверный id из <c>admin.ids</c> плюс личный пароль, введённый
/// с адреса (делегат <c>hasAdminRights</c> у <see cref="IsAllowed"/>, см.
/// <c>GameNetHost.AdminAuth</c>). Ники в правах не участвуют: id считает сервер из
/// состояния сессии.
/// Один и тот же гейт применяется и к лобби-чату (7778), и к командам из матч-чата
/// (ChatManager, 7777) — канал у игрока меняется, а права нет.
/// </para>
/// </remarks>
public static class CommandAccessPolicy
{
    /// <summary>Разобранная команда из строки чата.</summary>
    public readonly record struct Parsed(string Command, string Args, bool StartsMatch);

    /// <summary>
    /// Команды, которые знает диспетчер. Нужны для запасного префикса <c>!</c>: некоторые
    /// клиенты съедают ведущий <c>/</c> локально, и тогда команда просто не долетает до
    /// сервера. <c>!привет</c> командой не станет — только известное имя.
    /// </summary>
    private static readonly HashSet<string> KnownCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "help", "?", "команды", "mode", "режим", "map", "maps", "level", "levels", "карта",
        "join", "войти", "комната", "code", "код", "set", "auth", "whoami", "ктоя",
        "ban", "бан", "banid", "банид", "unban", "разбан", "unbanid", "ids", "айди",
        "bans", "баны", "ready", "r", "готов", "г", "unready", "nr", "неготов", "нг",
        "end", "stopmatch", "стопматч", "start", "startmatch", "старт", "play",
        "status", "roster", "plugins", "config", "quit", "chattest", "чаттест", "testchat",
    };

    /// <summary>
    /// Команда ли это. <c>/</c> — всегда; <c>!</c> — только перед известной командой.
    /// </summary>
    public static bool IsCommandText(string? text)
    {
        var body = (text ?? string.Empty).TrimStart();
        if (body.Length == 0)
            return false;
        if (body[0] == '/')
            return true;
        if (body[0] != '!')
            return false;
        return KnownCommands.Contains(Parse(body).Command);
    }

    /// <summary>Разбирает <c>/set start</c>, <c>!set start</c>, <c>set round 16</c>.</summary>
    public static Parsed Parse(string message)
    {
        var body = message.Trim();
        if (body.StartsWith('/') || body.StartsWith('!'))
            body = body[1..].Trim();

        if (body.Length == 0)
            return new Parsed(string.Empty, string.Empty, false);

        var space = body.IndexOf(' ');
        var command = space < 0 ? body : body[..space].Trim();
        var args = space < 0 ? string.Empty : body[(space + 1)..].Trim();

        // Запуск матча: /start и его синонимы, а также /set start.
        var startsMatch =
            IsStartAlias(command)
            || (command.Equals("set", StringComparison.OrdinalIgnoreCase)
                && args.Equals("start", StringComparison.OrdinalIgnoreCase));

        return new Parsed(command, args, startsMatch);
    }

    /// <summary>Команды, которые никогда не требуют прав.</summary>
    public static bool IsPublic(Parsed parsed) =>
        parsed.Command.Equals("help", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("?", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("команды", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("join", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("войти", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("комната", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("ready", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("unready", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("готов", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("неготов", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("г", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("нг", StringComparison.OrdinalIgnoreCase)
        // Код для сайта — свой, поэтому команда доступна каждому в лобби.
        || parsed.Command.Equals("code", StringComparison.OrdinalIgnoreCase)
        || parsed.Command.Equals("код", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Можно ли игроку выполнить команду. <paramref name="denyReason"/> заполняется только
    /// при отказе — «молча проигнорировать» здесь невозможно by design.
    /// </summary>
    /// <param name="playerId">
    /// Серверный id написавшего: считается из состояния сессии (<c>LegacyNickHash</c>),
    /// а не из ника/uid, присланного клиентом.
    /// </param>
    /// <param name="hasAdminRights">
    /// Настоящие админ-права написавшего. По умолчанию это проверка id по <c>admin.ids</c>,
    /// но живые каналы (лобби 7778 и ChatManager 7777) обязаны передавать сюда
    /// <c>GameNetHost.IsAdminAuthorized</c> — id плюс личный пароль, предъявленный
    /// адресом. Без делегата клиент, назвавшийся админским ником, получил бы тот же id
    /// и <c>/ban</c>, хотя <c>/whoami</c> честно отвечал бы «пароль не введён».
    /// Гейт принимает только <c>int</c> — админских ников в конфиге больше нет.
    /// </param>
    public static bool IsAllowed(
        ServerConfig config,
        int playerId,
        string message,
        out string denyReason,
        Func<int, bool>? hasAdminRights = null,
        Func<int, string, bool>? hasPermission = null)
    {
        denyReason = string.Empty;

        var parsed = Parse(message);
        if (parsed.Command.Length == 0)
            return true;

        if (IsPublic(parsed))
            return true;

        // Запуск матча открыт всем — это и есть «неадминские /mode /map /start /play».
        if (parsed.StartsMatch && config.Admin.AnyoneCanStart)
            return true;

        // Id в списке и права — разные вещи: права даёт личный пароль, введённый с адреса.
        var adminId = config.IsAdmin(playerId);
        var admin = hasAdminRights is null ? adminId : hasAdminRights(playerId);
        bool Allowed(string permission) =>
            hasPermission is null ? admin : admin && hasPermission(playerId, permission);

        // Баны — отдельный гейт: право «bans», независимо от admin.adminOnlyCommands.
        // Разбан randomplayer'ом — это не «мелкая опция».
        if (config.Admin.Ban.AdminOnly && IsBanCommand(parsed.Command))
        {
            if (Allowed(AdminPermissions.Bans))
                return true;

            denyReason = AdminDenied(config, parsed.Command, playerId,
                adminId && !admin, AdminPermissions.Bans);
            return false;
        }

        // Диагностика матч-чата шлёт кадры от имени чужого актора — только с правом
        // «config» и тоже независимо от admin.adminOnlyCommands.
        if (IsChatTestCommand(parsed.Command))
        {
            if (Allowed(AdminPermissions.Config))
                return true;

            denyReason = AdminDenied(config, parsed.Command, playerId,
                adminId && !admin, AdminPermissions.Config);
            return false;
        }

        if (!config.IsAdminOnly(parsed.Command) && !IsAdminOnlyAlias(config, parsed.Command))
            return true;

        // /set и /end меняют катку и конфиг — это право «config», а не «админ вообще».
        if (Allowed(AdminPermissions.Config))
            return true;

        denyReason = AdminDenied(config, parsed.Command, playerId,
            adminId && !admin, AdminPermissions.Config);
        return false;
    }

    /// <summary>
    /// Отказ с ответом на вопрос «почему я не админ»: сервер показывает id, который он
    /// сам посчитал для написавшего, и список админов из конфига (частый случай — пуст).
    /// </summary>
    /// <remarks>
    /// Два разных случая, и молчать о них нельзя: id в <c>admin.ids</c>, но пароль не
    /// введён — и пароль принят, но права на действие не выдано. Игрок видит себя в
    /// списке и иначе решает, что команда «сломалась». Подсказка — та же, что в
    /// <c>/whoami</c>.
    /// </remarks>
    private static string AdminDenied(
        ServerConfig config,
        string command,
        int playerId,
        bool adminIdWithoutPassword,
        string? missingPermission)
    {
        if (adminIdWithoutPassword)
            return $"команда /{command} только для админов: id #{playerId} в " +
                   "admin.ids есть, но этот адрес не ввёл личный пароль — " +
                   "прав пока нет (/whoami; пароль спрашивают при входе в лобби)";

        if (missingPermission is not null && config.IsAdmin(playerId))
            return $"команда /{command} только для админов: id #{playerId} авторизован, " +
                   $"но нет права «{missingPermission}» — его выдаёт мега-админ";

        var admins = config.Admin.Ids.Count == 0
            ? "в config.json admin.ids пусто — админ-команд ни у кого нет"
            : $"админы (id): {string.Join(", ", config.Admin.Ids)}";
        return $"команда /{command} только для админов (твой id #{playerId}; {admins})";
    }

    /// <summary>
    /// Алиасы админских команд. <c>stopmatch</c>/<c>стопматч</c> делают то же, что
    /// <c>end</c>, и обязаны требовать тех же прав — иначе гейт обходится синонимом.
    /// </summary>
    private static bool IsAdminOnlyAlias(ServerConfig config, string command)
    {
        if (config.IsAdminOnly("end")
            && (command.Equals("stopmatch", StringComparison.OrdinalIgnoreCase)
                || command.Equals("стопматч", StringComparison.OrdinalIgnoreCase)))
            return true;

        return false;
    }

    private static bool IsStartAlias(string command) =>
        command.Equals("start", StringComparison.OrdinalIgnoreCase)
        || command.Equals("startmatch", StringComparison.OrdinalIgnoreCase)
        || command.Equals("старт", StringComparison.OrdinalIgnoreCase);

    /// <summary>Команды банов: <c>/ban</c>, <c>/unban</c>, <c>/bans</c> и русские формы.</summary>
    public static bool IsBanCommand(string? command) =>
        command is not null
        && (command.Equals("ban", StringComparison.OrdinalIgnoreCase)
            || command.Equals("бан", StringComparison.OrdinalIgnoreCase)
            || command.Equals("banid", StringComparison.OrdinalIgnoreCase)
            || command.Equals("банид", StringComparison.OrdinalIgnoreCase)
            || command.Equals("unban", StringComparison.OrdinalIgnoreCase)
            || command.Equals("разбан", StringComparison.OrdinalIgnoreCase)
            || command.Equals("unbanid", StringComparison.OrdinalIgnoreCase)
            || command.Equals("bans", StringComparison.OrdinalIgnoreCase)
            || command.Equals("баны", StringComparison.OrdinalIgnoreCase)
            || command.Equals("ids", StringComparison.OrdinalIgnoreCase)
            || command.Equals("айди", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Диагностика матч-чата (<c>/chattest</c>): шлёт один и тот же текст четырьмя кадрами —
    /// автор (хост/живой игрок) × <c>field</c> (1/2). Все значения из капчур, но кадры
    /// подписаны чужим актором, поэтому команда админская.
    /// </summary>
    public static bool IsChatTestCommand(string? command) =>
        command is not null
        && (command.Equals("chattest", StringComparison.OrdinalIgnoreCase)
            || command.Equals("чаттест", StringComparison.OrdinalIgnoreCase)
            || command.Equals("testchat", StringComparison.OrdinalIgnoreCase));
}
