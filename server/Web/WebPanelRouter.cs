using System.Net;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Маршруты панели и единственное место, где рождается JSON.
/// </summary>
/// <remarks>
/// <para>
/// Маршрутов ровно четыре: страница, сводка, список комнат и создание комнаты. Никакой
/// авторизации: спека требует «одна кнопка», поэтому защита — только лимиты запросов и
/// правила самого реестра (одна комната на ник, номер не переиспользуется).
/// </para>
/// <para>
/// Создание комнаты — <b>единственный</b> путь появления комнаты: команды
/// <c>/create</c> в лобби нет намеренно, комнаты живут только с сайта.
/// </para>
/// <para>
/// Роутер не знает о сокетах: ему дают функцию «создать игровую комнату» и реестр.
/// Поэтому все ветки проверяются тестами без сети.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private readonly RoomRegistry _rooms;
    private readonly Func<string> _indexHtml;
    private readonly StatusSnapshotCache _status;
    private readonly WebPanelRouterOptions _options;

    public WebPanelRouter(
        RoomRegistry rooms,
        Func<string> indexHtml,
        StatusSnapshotCache status,
        WebPanelRouterOptions options)
    {
        _rooms = rooms;
        _indexHtml = indexHtml;
        _status = status;
        _options = options;
    }

    /// <summary>Разобрать запрос и вернуть ответ. Никогда не бросает.</summary>
    public WebResponse Route(WebRequestContext request)
    {
        var path = NormalizePath(request.Path, _options.BasePath);

        // Кука уходит с запросом автоматически, поэтому POST без проверки источника
        // позволял бы чужой странице в той же сети банить людей «руками админа».
        // Origin браузер подставить не даёт; если заголовка нет (не браузер, curl),
        // требуем пользовательский X-Requested-With, который тоже не поставить
        // cross-origin без CORS. Отказ — до всякой авторизации.
        if (request.Method != "GET" && !SameOriginOrApiClient(request))
            return ApiError(403, "cross_origin", "запрос пришёл не со страницы панели");

        // Страница открыта всем: без неё некуда ввести код из лобби. Данные — нет:
        // владелец просил, чтобы без входа сервер не отдавал ни онлайна, ни комнат.
        if (request.Method == "GET" && (path == "/" || path == "/index.html"))
            return WebResponse.Html(_indexHtml());

        // /play и /admin — те же страницы-экраны, а не отдельные файлы: адрес нужен
        // для «Начать игру» и для закладки, а разметку держим в одном месте.
        if (request.Method == "GET" && (path == "/play" || path == "/admin"))
            return WebResponse.Html(_indexHtml());

        // Данные панели закрыты сессией. Отказ — 401 с кодом, по которому страница
        // понимает, что пора показать форму входа, а не пустой список.
        if (request.Method == "GET" && path == "/api/status")
        {
            if (SessionOf(request) is not { } statusSession)
                return ApiError(401, "no_auth", NeedAuthMessage);
            if (BannedRefusal(statusSession) is { } statusBanned)
                return statusBanned;
            return WebResponse.Json(_status.Current);
        }

        if (request.Method == "GET" && path == "/api/rooms")
        {
            var roomsSession = SessionOf(request);
            if (roomsSession is null)
                return ApiError(401, "no_auth", NeedAuthMessage);
            if (BannedRefusal(roomsSession) is { } roomsBanned)
                return roomsBanned;
            return WebResponse.Json(BuildRoomsJson(roomsSession));
        }

        // Каталог режимов и карт отдаёт сервер, а не страница: иначе копия в JS
        // разъедется с GameModeCatalog, и панель начнёт предлагать несуществующие карты.
        // Под гейтом, потому что нужен только форме создания комнаты — а она после входа.
        if (request.Method == "GET" && path == "/api/modes")
        {
            if (SessionOf(request) is null)
                return ApiError(401, "no_auth", NeedAuthMessage);
            return WebResponse.Json(BuildModesJson());
        }

        // Авторизация: обменять код из лобби на сессию (#51). Чтение доступно всем,
        // всё, что меняет состояние, — только по сессии (см. CreateRoom и RoomEnd).
        if (request.Method == "POST" && path == "/api/auth")
            return Authenticate(request);

        if (request.Method == "GET" && path == "/api/me")
            return WhoAmI(request);

        // Аватар — личная вещь: только по сессии, кэш только приватный.
        if (request.Method == "GET" && path == "/api/avatar")
            return Avatar(request);

        if (request.Method == "POST" && path == "/api/logout")
            return Logout(request);

        if (request.Method == "POST" && path == "/api/rooms")
            return CreateRoom(request);

        // «Завершить игру» у своей комнаты (#51). Путь с номером — поэтому отдельная
        // проверка, а не сравнение строкой.
        if (TryParseRoomEndPath(path, out _))
            return request.Method == "POST"
                ? EndRoom(request, path)
                : WithAllow("POST", WebResponse.Text("405", 405));

        // ── админка ──────────────────────────────────────────────────────────
        // Вход в админку: обычная сессия обязательна, код проверяется вторым шагом.
        if (request.Method == "POST" && path == "/api/admin/auth")
            return AdminAuthenticate(request);

        if (request.Method == "POST" && path == "/api/admin/logout")
            return AdminLogout(request);

        if (request.Method == "GET" && path == "/api/admin/me")
            return AdminWhoAmI(request);

        // Данные админки: только с ключом. Любой отказ — 403, включая чтение.
        if (request.Method == "GET" && path == "/api/admin/status")
            return AdminStatus(request);

        if (request.Method == "GET" && path == "/api/admin/players")
            return AdminPlayer(request);

        if (request.Method == "GET" && path == "/api/admin/bans")
            return AdminBans(request);

        if (request.Method == "GET" && path == "/api/admin/ids")
            return AdminIds(request);

        if (request.Method == "POST" && path == "/api/admin/bans")
            return AdminBan(request);

        if (request.Method == "POST" && path == "/api/admin/unban")
            return AdminUnban(request);

        if (request.Method == "POST" && path == "/api/admin/public-id")
            return AdminPublicId(request);

        // Шрифт и видео-фон. Это часть оформления страницы, а не данные: без них
        // сайт открывается, но выглядит не так, поэтому авторизация здесь не нужна —
        // ровно как не нужна для самого index.html.
        if (request.Method == "GET" && _options.Asset is not null && path.StartsWith("/assets/", StringComparison.Ordinal))
            return _options.Asset(path, HeaderValue(request.Headers, "Range"));

        if (request.Method == "GET" && path == "/healthz")
            return WebResponse.Text("ok");

        // Метод не тот — это не «нет маршрута», и панель должна сказать честно.
        if (path is "/" or "/index.html" or "/play" or "/admin" or "/api/status" or "/api/modes"
            or "/api/me" or "/api/avatar" or "/healthz"
            or "/api/admin/status" or "/api/admin/players" or "/api/admin/bans"
            or "/api/admin/ids" or "/api/admin/me")
            return WithAllow("GET", WebResponse.Text("405", 405));

        if (path is "/api/admin/auth" or "/api/admin/logout" or "/api/admin/unban"
            or "/api/admin/public-id")
            return WithAllow("POST", WebResponse.Text("405", 405));

        if (path is "/api/auth" or "/api/logout")
            return WithAllow("POST", WebResponse.Text("405", 405));

        if (path == "/api/rooms")
            return WithAllow("GET, POST", WebResponse.Text("405", 405));

        return WebResponse.Text("404", status: 404);
    }

    /// <summary>
    /// Срезать префикс подпути, если панель стоит за прокси (<c>/vlalchillow/api/rooms</c>).
    /// </summary>
    /// <remarks>
    /// Прокси со слэшем в <c>proxy_pass</c> обычно и так срезает префикс, но полагаться на
    /// это нельзя: конфиг nginx правит владелец сервера, а панель должна работать и с
    /// префиксом, и без него.
    /// </remarks>
    public static string NormalizePath(string path, string? basePath)
    {
        var result = string.IsNullOrEmpty(path) ? "/" : path;

        // Склеиваем слэши ДО среза префикса: прокси иногда отдаёт «//vlalchillow//api»,
        // и тогда префикс не совпал бы ни с чем.
        while (result.Contains("//", StringComparison.Ordinal))
            result = result.Replace("//", "/", StringComparison.Ordinal);

        if (!string.IsNullOrWhiteSpace(basePath))
        {
            var prefix = basePath.Trim();
            if (!prefix.StartsWith('/'))
                prefix = "/" + prefix;
            prefix = prefix.TrimEnd('/');

            if (prefix.Length > 0)
            {
                if (result.Equals(prefix, StringComparison.Ordinal))
                    result = "/";
                else if (result.StartsWith(prefix + "/", StringComparison.Ordinal))
                    result = result[prefix.Length..];
            }
        }

        var queryAt = result.IndexOf('?');
        if (queryAt >= 0)
            result = result[..queryAt];

        return result.Length == 0 ? "/" : result;
    }

    /// <summary>
    /// Режимы с их картами и алиасами — из <see cref="GameModeCatalog"/>, один в один.
    /// </summary>
    /// <remarks>
    /// Отдаём и <c>alias</c> (то, что вставляется в <c>/join</c>), и <c>modeId</c>
    /// (то, что понимает сервер): странице не нужно ничего додумывать.
    /// </remarks>
    private static string BuildModesJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("modes");
            foreach (var mode in GameModeCatalog.Modes)
            {
                writer.WriteStartObject();
                writer.WriteString("id", mode.GameModeId);
                writer.WriteString("name", mode.DisplayName);
                writer.WriteString("alias", GameModeCatalog.PrimaryAlias(mode));
                writer.WriteString("defaultLevel", GameModeCatalog.DefaultLevelFor(mode.GameModeId));

                // Размер команды: страница показывает выбор только там, где он есть.
                var shape = RoomCapacity.ForMode(mode.GameModeId);
                writer.WriteNumber("perTeam", shape.PerTeam);
                writer.WriteNumber("maxPlayers", shape.Total);
                writer.WriteString("teamLabel", RoomCapacity.Describe(mode.GameModeId, shape.PerTeam));
                writer.WriteStartArray("teamSizes");
                foreach (var size in shape.Choosable)
                    writer.WriteNumberValue(size);
                writer.WriteEndArray();
                writer.WriteStartArray("levels");
                foreach (var level in mode.Levels)
                    writer.WriteStringValue(level);
                writer.WriteEndArray();
                writer.WriteStartArray("aliases");
                foreach (var alias in mode.Aliases)
                    writer.WriteStringValue(alias);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Список комнат. Для авторизованного зрителя его собственная комната помечена
    /// <c>isMine</c> — страница поднимает её наверх и показывает «Моя комната» (#51).
    /// </summary>
    private string BuildRoomsJson(SiteSession? viewer)
    {
        var rooms = _rooms.Snapshot();
        var viewerId = viewer?.PlayerId ?? 0;
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("nextNumber", _rooms.NextNumber);
            writer.WriteNumber("count", rooms.Count);
            writer.WriteNumber("maxRooms", _rooms.MaxRooms);
            writer.WriteStartArray("rooms");
            foreach (var room in rooms)
            {
                writer.WriteStartObject();
                writer.WriteNumber("number", room.Number);
                writer.WriteString("mode", room.ModeId);
                writer.WriteString("modeName", DisplayModeName(room.ModeId));
                writer.WriteString("level", room.Level);
                writer.WriteString("name", room.Name);
                writer.WriteString("owner", room.OwnerNick);
                writer.WriteNumber("ownerId", room.OwnerId);
                writer.WriteString("state", room.State.ToString().ToLowerInvariant());
                writer.WriteNumber("players", room.Players);
                // Счёт CT * T из room.Flow (обновляется раз в секунду). Число игроков
                // в списке комнат больше не показываем: вместо него — счёт.
                writer.WriteNumber("scoreCt", room.ScoreCt);
                writer.WriteNumber("scoreTr", room.ScoreTr);
                writer.WriteNumber("perTeam", room.PerTeam);
                writer.WriteNumber("maxPlayers",
                    RoomCapacity.TotalFor(room.ModeId, room.PerTeam));
                writer.WriteString("teamLabel",
                    RoomCapacity.Describe(room.ModeId, room.PerTeam));
                writer.WriteString("joinCommand", FormatJoinCommand(room));
                writer.WriteString("createdUtc", room.CreatedUtc.ToString("O"));

                // Своя комната: только владельцу и только по сессии. Без авторизации
                // флага нет ни у кого — анонимный зритель не узнает, чья это комната.
                writer.WriteBoolean("isMine", viewerId > 0 && room.OwnerId == viewerId);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>Команда, которую панель даёт скопировать: ровно то, что понимает лобби.</summary>
    public static string FormatJoinCommand(RoomDescriptor room) =>
        $"/join {PrimaryModeToken(room.ModeId)} #{room.Number}";

    private static string PrimaryModeToken(string modeId) =>
        GameModeCatalog.TryGetMode(modeId, out var mode)
            ? GameModeCatalog.PrimaryAlias(mode)
            : modeId;

    private static string DisplayModeName(string modeId) =>
        GameModeCatalog.TryGetMode(modeId, out var mode) ? mode.DisplayName : modeId;

    private WebResponse CreateRoom(WebRequestContext request)
    {
        // Комнату создаёт только авторизованный (#51): без сессии панель не принимает
        // вообще ничего, что меняет состояние. Кто владелец, решаем по сессии, а не по
        // телу запроса: раньше ID присылала страница, и вписать можно было любой.
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "сначала введи код из лобби");

        // Бан проверяем и здесь: забаненный не создаёт комнату, даже если успел
        // авторизоваться до бана (#51).
        if (BannedRefusal(session) is { } banned)
            return banned;

        CreateRoomRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<CreateRoomRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null)
            return ApiError(400, "bad_json", "пустое тело запроса");

        var ownerNick = string.Empty;
        var online = _options.OnlinePlayers?.Invoke();
        if (online is not null)
        {
            // Комната — за живым человеком из лобби: сессия выдана по коду, но человек
            // мог уже выйти из игры. Без этой проверки комната висела бы за призраком.
            var found = false;
            foreach (var (id, nick) in online)
            {
                if (id != session.PlayerId)
                    continue;
                ownerNick = nick;
                found = true;
                break;
            }

            if (!found)
                return ApiError(409, "player_offline",
                    $"тебя (ID {session.PlayerId}) нет в лобби — зайди в игру и создай комнату оттуда");
        }

        var modeToken = string.IsNullOrWhiteSpace(parsed.Mode)
            ? _options.DefaultModeId
            : parsed.Mode;

        if (!_rooms.TryCreate(
                modeToken, parsed.Level, parsed.Name, ownerNick, out var room, out var error,
                perTeam: parsed.PerTeam, ownerId: session.PlayerId)
            || room is null)
        {
            var (status, code, message) = MapCreateError(error);
            return ApiError(status, code, message);
        }
        var created = _options.OnRoomCreated?.Invoke(room);
        if (created is { Length: > 0 })
        {
            // Комнату в реестре откатить нельзя «наполовину»: если игровая комната не
            // поднялась, честнее удалить запись, чем показывать кнопку в мёртвую комнату.
            _rooms.TryRemove(room.Number, out _);
            return ApiError(500, "room_not_created", created);
        }

        var json = BuildCreatedJson(room);
        return WebResponse.Json(json, 201);
    }

    private string BuildCreatedJson(RoomDescriptor room)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("number", room.Number);
            writer.WriteString("mode", room.ModeId);
            writer.WriteString("modeName", DisplayModeName(room.ModeId));
            writer.WriteString("level", room.Level);
            writer.WriteString("name", room.Name);
            writer.WriteString("owner", room.OwnerNick);
            writer.WriteNumber("ownerId", room.OwnerId);
            writer.WriteString("joinCommand", FormatJoinCommand(room));
            writer.WriteString("state", room.State.ToString().ToLowerInvariant());
            // Потолок и подпись размера — сразу, иначе только что созданная комната
            // рисуется с глобальными 16 и врёт про «есть места» в 1 на 1.
            writer.WriteNumber("perTeam", room.PerTeam);
            writer.WriteNumber("maxPlayers", RoomCapacity.TotalFor(room.ModeId, room.PerTeam));
            writer.WriteString("teamLabel", RoomCapacity.Describe(room.ModeId, room.PerTeam));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static (int Status, string Code, string Message) MapCreateError(RoomCreateError error) =>
        error switch
        {
            RoomCreateError.UnknownMode => (400, "unknown_mode", "неизвестный режим"),
            RoomCreateError.UnknownLevel => (400, "unknown_level", "такой карты у режима нет"),
            RoomCreateError.EmptyOwner => (400, "empty_owner", "не указан ID владельца"),
            RoomCreateError.DuplicateOwner => (409, "one_room_per_player",
                "у этого игрока уже есть комната"),
            RoomCreateError.TooManyRooms => (409, "too_many_rooms",
                "комнат больше, чем игроков онлайн"),
            RoomCreateError.RoomLimitReached => (503, "room_limit", "на сервере кончились комнаты"),
            RoomCreateError.UnknownTeamSize => (400, "unknown_team_size",
                "этот режим не поддерживает такой размер команды"),
            _ => (400, "bad_request", "не получилось"),
        };

    private static WebResponse ApiError(int status, string code, string message)
    {
        // Тот же писатель, что и у остальных ответов: иначе «аккаунт забанен» уедет
        // на страницу как \u0430\u043A… и человек увидит кракозябры вместо текста.
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()), status);
    }

    /// <summary>Значение заголовка без учёта регистра имени; пусто, если его нет.</summary>
    private static string HeaderValue(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var (key, value) in headers)
        {
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return string.Empty;
    }

    /// <summary>
    /// Запрос пришёл со страницы панели (или от не-браузерного клиента с явным маркером)?
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Origin</c> сравнивается с <c>Host</c> того же запроса: домен панели в коде не
    /// известен (LAN-адрес, прокси, localhost), а пара «Origin == Host» верна во всех
    /// этих случаях. Схему не сверяем: за https-прокси панель видит http.
    /// </para>
    /// <para>
    /// Заголовка <c>Origin</c> может не быть у не-браузерных клиентов (curl, скрипт) —
    /// их пускаем по <c>X-Requested-With</c>. Обычная HTML-форма ни того, ни другого
    /// поставить не может, а межсайтовый fetch без CORS не может добавить
    /// нестандартный заголовок.
    /// </para>
    /// </remarks>
    private static bool SameOriginOrApiClient(WebRequestContext request)
    {
        foreach (var (name, value) in request.Headers)
        {
            if (name.Equals("X-Requested-With", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (!request.Headers.TryGetValue("Origin", out var origin)
            || string.IsNullOrWhiteSpace(origin)
            || origin == "null")
        {
            return false;
        }

        if (!request.Headers.TryGetValue("Host", out var host) || string.IsNullOrWhiteSpace(host))
            return false;

        var hostPart = origin.Trim();
        var schemeEnd = hostPart.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd >= 0)
            hostPart = hostPart[(schemeEnd + 3)..];
        hostPart = hostPart.TrimEnd('/');

        return hostPart.Equals(host.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Выполнить админский маршрут под уже проверенной сессией сайта.
    /// </summary>
    /// <remarks>
    /// Сессию ищем здесь один раз: каждый админский маршрут начинается с «кто это»,
    /// и повторять одно и то же в шести местах — верный способ разойтись в поведении.
    /// </remarks>
    private WebResponse WithSession(WebRequestContext request, Func<WebRequestContext, WebResponse> handler)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", NeedAuthMessage);

        if (BannedRefusal(session) is { } banned)
            return banned;

        return handler(request);
    }

    private static WebResponse WithAllow(string allow, WebResponse response) => new(
        response.Status,
        response.ContentType,
        response.Body,
        response.RetryAfterSec,
        new List<(string, string)>(response.ExtraHeaders) { ("Allow", allow) });

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>Тело <c>POST /api/rooms</c>.</summary>
    /// <param name="Mode">Режим: алиас или id (<c>2v2</c>, <c>Duel</c>).</param>
    /// <param name="Level">Карта; пусто — карта по умолчанию для режима.</param>
    /// <param name="Name">Название комнаты; пусто — придумает реестр.</param>
    /// <param name="PerTeam">Размер команды, где он выбирается; <c>0</c> — «как в режиме».</param>
    /// <remarks>
    /// Владельца здесь нет и быть не может: он берётся из сессии, а не из присланного
    /// JSON (#51). Поля <c>OwnerId</c>/<c>Owner</c> принимаются и игнорируются — старая
    /// страница шлёт их по привычке, а решением они больше не являются.
    /// </remarks>
    private sealed record CreateRoomRequest(
        string? Mode,
        string? Level,
        string? Name,
        int PerTeam = 0,
        // Принимаем и игнорируем: старая страница шлёт ownerId/owner по привычке.
        // Решение принимает сессия, а не эти поля — подделать владельца нельзя.
        int OwnerId = 0,
        string? Owner = null);
}

/// <summary>
/// Бан по серверному id с сайта.
/// </summary>
/// <param name="playerId">Числовой id цели.</param>
/// <param name="duration">Срок теми же формами, что у <c>/ban</c> (<c>30m</c>, <c>6h</c>, <c>3d</c>).</param>
/// <param name="reason">Причина.</param>
/// <param name="bannedBy">Ник того, кто банит (из админской сессии, не из тела запроса).</param>
/// <param name="error">Причина отказа или <c>null</c>.</param>
/// <returns>Запись реестра или <c>null</c>, если бан не выдан.</returns>
/// <remarks>
/// Отдельный делегат, а не <c>Func</c>: причина отказа нужна странице текстом, а
/// <c>out</c>-параметр в <c>Func</c> не выразить. Тот же приём — у разбана ниже.
/// </remarks>
public delegate Bans.BanRecord? BanPlayerHandler(
    int playerId, string? duration, string? reason, string? bannedBy, out string? error);

/// <summary>Снять баны под серверным id.</summary>
/// <param name="playerId">Числовой id.</param>
/// <param name="removed">Сколько записей снято.</param>
/// <param name="error">Причина отказа или <c>null</c>.</param>
public delegate bool UnbanPlayerHandler(int playerId, out int removed, out string? error);

/// <summary>Что роутеру нужно от хоста и конфига.</summary>
public sealed class WebPanelRouterOptions
{
    /// <summary>Режим по умолчанию, если панель не прислала ни одного.</summary>
    public string DefaultModeId { get; init; } = LobbyPropKeys.DefaultGameModeId;

    /// <summary>
    /// Префикс подпути за прокси (например <c>/vlalchillow</c>). Пусто — панель в корне.
    /// </summary>
    public string BasePath { get; init; } = "";

    /// <summary>
    /// Поднять игровую комнату по записи реестра. Возвращает текст ошибки или <c>null</c>.
    /// </summary>
    public Func<RoomDescriptor, string?>? OnRoomCreated { get; init; }

    /// <summary>
    /// Сессии сайта. <c>null</c> — авторизация выключена (создание комнаты закрыто).
    /// </summary>
    /// <remarks>
    /// Хранилище одно на запуск: рестарт сервера гасит все сессии и все коды, и человек
    /// авторизуется заново (#51).
    /// </remarks>
    public SiteSessions? Sessions { get; init; }

    /// <summary>
    /// Аватар игрока: байты, которые клиент прислал в лобби. <c>null</c> — аватара нет
    /// (или игрока нет в лобби). Формат определяет вызывающий по magic-байтам.
    /// </summary>
    public Func<int, byte[]?>? AvatarById { get; init; }

    /// <summary>
    /// Обменять код из лобби на запись игрока. Возвращает <c>null</c>, если код чужой,
    /// протухший или кривой — вызывающий не обязан различать эти случаи.
    /// </summary>
    public Func<string, DateTime, SiteLoginCode?>? RedeemCode { get; init; }

    /// <summary>
    /// Забанен ли игрок с таким серверным ID.
    /// </summary>
    /// <remarks>
    /// Проверяется и при обмене кода, и на каждом авторизованном запросе (#51): человека
    /// могли забанить, пока он сидел на сайте, — тогда авторизация у него слетает.
    /// <c>null</c> — проверка выключена (тесты и режим без банов).
    /// </remarks>
    public Func<int, bool>? IsBanned { get; init; }

    /// <summary>
    /// Завершить катку в комнате досрочно (кнопка «Завершить игру» у «Моей комнаты»).
    /// </summary>
    /// <remarks>
    /// Первый аргумент — номер комнаты, второй — ID игрока, который просит. Возвращает
    /// текст ошибки или <c>null</c>, если катка уже пошла завершаться. Права проверяет
    /// и роутер, и сам хост: обе проверки дешёвые, а цена ошибки — чужая катка.
    /// </remarks>
    public Func<int, int, string?>? EndRoom { get; init; }

    /// <summary>
    /// Путь куки сессии. За прокси панель живёт в подпути, и кука обязана совпадать.
    /// </summary>
    public string CookiePath { get; init; } = "/";

    /// <summary>
    /// Ставить ли куке <c>Secure</c>. По LAN панель часто открывают по http, и с этим
    /// флагом браузер куку просто не сохранит.
    /// </summary>
    public bool SecureCookies { get; init; }

    /// <summary>
    /// Админские сессии сайта. <c>null</c> — админка выключена (все её маршруты 503).
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Sessions"/>: обычная сессия говорит «человек вошёл»,
    /// админская — «и предъявил код этого запуска». Выход с сайта не должен оставлять
    /// админские права, поэтому и хранилища разные.
    /// </remarks>
    public SiteAdminSessions? AdminSessions { get; init; }

    /// <summary>
    /// Числовой id в <c>admin.ids</c> — те же права, что у команд в чате.
    /// </summary>
    public Func<int, bool>? IsAdminId { get; init; }

    /// <summary>
    /// Код текущего запуска принят? Сравнение делает <c>AdminAuth</c> (постоянное время,
    /// без учёта регистра); сюда приходит только ответ.
    /// </summary>
    public Func<string, bool>? VerifyAdminCode { get; init; }

    /// <summary>
    /// Авторизовать адрес, который предъявил код на сайте: так же, как это делает
    /// ввод кода в чат лобби.
    /// </summary>
    public Func<System.Net.IPAddress?, bool>? AuthorizeAddress { get; init; }

    /// <summary>Что показать вместо числового id: подмена из админки или сам id.</summary>
    public Func<int, string>? PublicIdOf { get; init; }

    /// <summary>
    /// Подмены публичного id. Нужны админке, чтобы менять их напрямую, а не через id.
    /// </summary>
    public Bans.PublicIdOverrides? PublicIds { get; init; }

    /// <summary>Кто это по серверному id (ник, адреса, когда видели).</summary>
    public Func<int, Bans.SeenPlayer?>? LookupPlayer { get; init; }

    /// <summary>Активные баны.</summary>
    public Func<IReadOnlyList<Bans.BanRecord>>? ActiveBans { get; init; }

    /// <summary>Забанить по id с сайта. Возвращает запись или <c>null</c> с причиной.</summary>
    public BanPlayerHandler? BanPlayer { get; init; }

    /// <summary>Снять баны под id.</summary>
    public UnbanPlayerHandler? UnbanPlayer { get; init; }

    /// <summary>Адрес игрока, если он сейчас в лобби (для признака «онлайн»).</summary>
    public Func<int, System.Net.IPAddress?>? LobbyAddressOf { get; init; }

    /// <summary>Ник игрока по числовому id — для профиля на странице.</summary>
    public Func<int, string?>? NickById { get; init; }

    /// <summary>Список админских id — для сводки в админке.</summary>
    public Func<IReadOnlyList<int>>? AdminIdList { get; init; }

    /// <summary>
    /// Статический файл оформления (шрифт, видео-фон) по пути <c>/assets/…</c>.
    /// </summary>
    /// <remarks>
    /// Первый аргумент — путь, второй — заголовок <c>Range</c> (нужен видео, иначе
    /// браузер не даёт перематывать и иногда не начинает воспроизведение).
    /// Возвращает <c>404</c>, если такого файла нет.
    /// </remarks>
    public Func<string, string, WebResponse>? Asset { get; init; }

    /// <summary>Куда писать строки о действиях админов (консоль сервера).</summary>
    public Action<string>? Log { get; init; }

    /// <summary>
    /// Кто сейчас в лобби: (серверный id, ник).
    /// </summary>
    /// <remarks>
    /// Комната создаётся <b>за конкретным присутствующим человеком</b>, а не за
    /// строчкой текста. Без этой проверки можно было вписать любой ник (в том числе
    /// чужой или несуществующий) и получить комнату «за игрока», которого нет.
    /// <c>null</c> — проверка выключена (тесты реестра и режим без хоста).
    /// </remarks>
    public Func<IReadOnlyList<(int Id, string Nick)>>? OnlinePlayers { get; init; }
}
