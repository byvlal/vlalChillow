using System.Net;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Reports;
using StandChillow.LanServer.Stats;

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

    /// <summary>
    /// Страница с вставленным <c>&lt;base&gt;</c> — считается один раз, дальше отдаётся
    /// готовая строка (правка 208 КБ на каждый запрос не нужна).
    /// </summary>
    private string? _indexWithBase;

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

        // Telegram-бот: отдельный вход по постоянному ключу. Проверка стоит до всех
        // маршрутов — без ключа бот не видит даже того, какие пути существуют.
        if (IsBotPath(path))
            return BotRoute(request, path);

        // Страница и её данные — только чтение, доступны всем.
        if (request.Method == "GET" && (path == "/" || path == "/index.html"))
            return WebResponse.Html(IndexHtml());

        if (request.Method == "GET" && path == "/api/status")
            return WebResponse.Json(_status.Current);

        if (request.Method == "GET" && path == "/api/rooms")
            return WebResponse.Json(BuildRoomsJson(SessionOf(request)));

        // Каталог режимов и карт отдаёт сервер, а не страница: иначе копия в JS
        // разъедется с GameModeCatalog, и панель начнёт предлагать несуществующие карты.
        if (request.Method == "GET" && path == "/api/modes")
            return WebResponse.Json(BuildModesJson());

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

        // Статистика профиля: киллы/смерти/KD/время/матчи. Только по сессии.
        if (request.Method == "GET" && path == "/api/stats/me")
            return MyStats(request);

        // Публичный профиль и список администрации. Жалобы — только по сессии.
        if (request.Method == "GET" && path == "/api/player")
            return PublicPlayer(request);

        // Аватар для публичного профиля: кэш на диске, поэтому виден и после выхода.
        if (request.Method == "GET" && path == "/api/player/avatar")
            return PublicAvatar(request);

        // История матчей: список для профиля и одна катка целиком.
        if (request.Method == "GET" && path == "/api/matches")
            return MatchesList(request);

        if (request.Method == "GET" && TryParseMatchPath(path, out _))
            return MatchById(request, path);

        // Живая комната: публичное чтение, без сессии. Страница #/room/<n>.
        if (request.Method == "GET" && TryParseRoomPath(path, out _))
            return RoomById(request, path);

        if (request.Method == "GET" && path == "/api/administration")
            return Administration(request);

        if (request.Method == "POST" && path == "/api/reports")
            return SubmitReport(request);

        if (request.Method == "GET" && path == "/api/reports/mine")
            return MyReports(request);

        // Изменяющие админ-запросы требуют CSRF-токен сессии. Вход — исключение:
        // сессии ещё нет, её выдаёт сам /api/admin/login.
        if (request.Method == "POST"
            && path.StartsWith("/api/admin/", StringComparison.Ordinal)
            && path is not ("/api/admin/login" or "/api/admin/test-login")
            && AdminCsrfGate(request) is { } csrfDenied)
            return csrfDenied;

        // Единый затвор подтверждения паролем: любое изменение в админке, кроме
        // входа/выхода/самого подтверждения, требует свежий step-up. Проверка стоит
        // здесь, а не в каждом обработчике, поэтому новый опасный маршрут не сможет
        // появиться без пароля. Тестовый test.noAdminPassword отключает только её.
        if (request.Method == "POST"
            && path.StartsWith("/api/admin/", StringComparison.Ordinal)
            && path is not ("/api/admin/login" or "/api/admin/logout" or "/api/admin/stepup"
                or "/api/admin/test-login")
            && AdminMutationGate(request) is { } mutationDenied)
            return mutationDenied;

        // Админ панель: состояние (аноним видит только «не админ ты»), вход и выход —
        // по коду этого запуска. Все данные — только после тройного затвора (Admin.cs).
        // login-info — единственное исключение: экрану входа нужно знать, включён ли
        // тестовый режим, иначе он требует пароль, которого на тесте нет.
        if (request.Method == "GET" && path == "/api/admin/login-info")
            return AdminLoginInfo();

        if (request.Method == "GET" && path == "/api/admin")
            return AdminState(request);

        if (request.Method == "POST" && path == "/api/admin/login")
            return AdminLogin(request);

        if (request.Method == "POST" && path == "/api/admin/test-login")
            return AdminTestLogin(request);

        if (request.Method == "POST" && path == "/api/admin/logout")
            return AdminLogout(request);

        if (request.Method == "POST" && path == "/api/admin/stepup")
            return AdminStepUp(request);

        // Консоль: дашборд, журнал, игроки, комнаты, конфиг, состав админов,
        // безопасность. Каждый обработчик сам проверяет своё право.
        if (request.Method == "GET" && path == "/api/admin/dashboard")
            return AdminDashboard(request);

        if (request.Method == "GET" && path == "/api/admin/journal")
            return AdminJournal(request);

        if (request.Method == "POST" && path == "/api/admin/journal/undo")
            return AdminJournalUndo(request);

        if (request.Method == "GET" && path == "/api/admin/players")
            return AdminPlayers(request);

        if (request.Method == "POST" && path == "/api/admin/kick")
            return AdminKick(request);

        if (request.Method == "GET" && path == "/api/admin/rooms")
            return AdminRooms(request);

        if (request.Method == "POST" && path == "/api/admin/rooms/close")
            return AdminRoomClose(request);

        if (request.Method == "GET" && path == "/api/admin/config")
            return AdminConfigView(request);

        if (request.Method == "POST" && path == "/api/admin/config/set")
            return AdminConfigSet(request);

        if (request.Method == "POST" && path == "/api/admin/reload")
            return AdminReloadConfig(request);

        if (request.Method == "POST" && path == "/api/admin/add")
            return AdminAdd(request);

        if (request.Method == "POST" && path == "/api/admin/remove")
            return AdminRemove(request);

        if (request.Method == "POST" && path == "/api/admin/password")
            return AdminRegeneratePassword(request);

        if (request.Method == "GET" && path == "/api/admin/security")
            return AdminSecurity(request);

        if (request.Method == "POST" && path == "/api/admin/sessions/revoke")
            return AdminSessionRevoke(request);

        if (request.Method == "GET" && path == "/api/admin/lookup")
            return AdminLookupPlayer(request);

        if (request.Method == "GET" && path == "/api/admin/avatar")
            return AdminAvatar(request);

        if (request.Method == "GET" && path == "/api/admin/admins")
            return AdminList(request);

        if (request.Method == "POST" && path == "/api/admin/permissions")
            return AdminSetPermissions(request);

        if (request.Method == "POST" && path == "/api/admin/customid")
            return AdminSetCustomId(request);

        if (request.Method == "GET" && path == "/api/admin/bans")
            return AdminBansList(request);

        if (request.Method == "POST" && path == "/api/admin/ban")
            return AdminBan(request);

        if (request.Method == "POST" && path == "/api/admin/unban")
            return AdminUnban(request);

        // Модерация жалоб — тот же право-затвор, что у банов.
        if (request.Method == "GET" && path == "/api/admin/reports")
            return AdminReports(request);

        if (request.Method == "POST" && path == "/api/admin/reports/status")
            return AdminReportStatus(request);

        if (request.Method == "POST" && path == "/api/rooms")
            return CreateRoom(request);

        // «Завершить игру» у своей комнаты (#51). Путь с номером — поэтому отдельная
        // проверка, а не сравнение строкой.
        if (TryParseRoomEndPath(path, out _))
            return request.Method == "POST"
                ? EndRoom(request, path)
                : WithAllow("POST", WebResponse.Text("405", 405));

        if (request.Method == "GET" && path == "/healthz")
            return WebResponse.Text("ok");

        // Медиа сайта: видео hero, постер, шрифты. Только GET, остальное — 405.
        if (path.StartsWith("/media/", StringComparison.Ordinal))
            return request.Method == "GET"
                ? ServeMedia(request, path)
                : WithAllow("GET", WebResponse.Text("405", 405));

        // Метод не тот — это не «нет маршрута», и панель должна сказать честно.
        if (path is "/" or "/index.html" or "/api/status" or "/api/modes"
            or "/api/me" or "/api/avatar" or "/api/stats/me" or "/api/admin"
            or "/api/admin/login-info"
            or "/api/admin/lookup" or "/api/admin/avatar" or "/api/admin/bans"
            or "/api/admin/admins"
            or "/api/admin/dashboard" or "/api/admin/journal" or "/api/admin/players"
            or "/api/admin/rooms" or "/api/admin/config" or "/api/admin/security"
            or "/api/player" or "/api/player/avatar" or "/api/matches"
            or "/api/administration" or "/api/reports/mine"
            or "/api/admin/reports" or "/healthz")
            return WithAllow("GET", WebResponse.Text("405", 405));

        // /api/match/<id> — тоже только GET; номер разбираем тем же правилом.
        if (TryParseMatchPath(path, out _))
            return WithAllow("GET", WebResponse.Text("405", 405));

        if (TryParseRoomPath(path, out _))
            return WithAllow("GET", WebResponse.Text("405", 405));

        if (path is "/api/auth" or "/api/logout" or "/api/reports"
            or "/api/admin/login" or "/api/admin/logout"
            or "/api/admin/test-login"
            or "/api/admin/customid" or "/api/admin/ban" or "/api/admin/unban"
            or "/api/admin/permissions"
            or "/api/admin/stepup" or "/api/admin/journal/undo"
            or "/api/admin/kick" or "/api/admin/rooms/close"
            or "/api/admin/config/set" or "/api/admin/reload"
            or "/api/admin/add" or "/api/admin/remove" or "/api/admin/password"
            or "/api/admin/sessions/revoke"
            or "/api/admin/reports/status")
            return WithAllow("POST", WebResponse.Text("405", 405));

        if (path == "/api/rooms")
            return WithAllow("GET, POST", WebResponse.Text("405", 405));

        return WebResponse.Text("404", status: 404);
    }

    /// <summary>
    /// HTML страницы. Панель под подпутём (<c>example.com/vlalchillow/</c>) отдаёт её с
    /// <c>&lt;base href="/vlalchillow/"&gt;</c>.
    /// </summary>
    /// <remarks>
    /// Ссылки на медиа относительные (<c>media/hero.mp4</c>, шрифты, скриншоты), и без
    /// <c>&lt;base&gt;</c> они разрешаются от адреса страницы — этого достаточно, когда
    /// панель открыта по адресу со слешем на конце. <c>&lt;base&gt;</c> нужен для случая
    /// <c>example.com/vlalchillow</c> без слеша: тогда относительный путь уехал бы в
    /// корень домена, мимо прокси. В корне (<c>basePath</c> пуст) <c>&lt;base&gt;</c> не
    /// ставим: документ и так разрешает ссылки правильно, а подпути без <c>basePath</c>
    /// панель не видит и угадывать его не должна.
    /// </remarks>
    private string IndexHtml()
    {
        var html = _indexHtml();
        var basePath = _options.BasePath;
        if (string.IsNullOrEmpty(basePath)) return html;
        if (_indexWithBase is null)
            _indexWithBase = InjectBase(html, "/" + basePath.Trim().Trim('/') + "/");
        return _indexWithBase;
    }

    /// <summary>
    /// Вставить <c>&lt;base&gt;</c> сразу после <c>&lt;head&gt;</c>. Адрес приходит только
    /// из <c>config.json</c> (без пробелов и кавычек), но экранируем на всякий случай.
    /// </summary>
    private static string InjectBase(string html, string href)
    {
        var at = html.IndexOf("<head>", StringComparison.Ordinal);
        if (at < 0) return html;
        return html.Insert(at + "<head>".Length,
            "\n<base href=\"" + href.Replace("\"", "&quot;") + "\">");
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
                // Хост уводит в бой только часть каталога: страница приглушает
                // остальные и не даёт их выбрать (см. GameModeCatalog.IsImplemented).
                writer.WriteBoolean("implemented", GameModeCatalog.IsImplemented(mode.GameModeId));

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
    /// Аватар игрока: байты, которые клиент прислал в лобби, или кэш на диске.
    /// <c>null</c> — аватара нет. Формат определяет вызывающий по magic-байтам.
    /// </summary>
    public Func<int, byte[]?>? AvatarById { get; init; }

    /// <summary>
    /// Аватар для публичного профиля (без сессии): только кэш на диске, чтобы картинка
    /// не зависела от того, стоит ли игрок сейчас в лобби. <c>null</c> — 404.
    /// </summary>
    public Func<int, byte[]?>? PublicAvatarById { get; init; }

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
    /// Кто сейчас в лобби: (серверный id, ник).
    /// </summary>
    /// <remarks>
    /// Комната создаётся <b>за конкретным присутствующим человеком</b>, а не за
    /// строчкой текста. Без этой проверки можно было вписать любой ник (в том числе
    /// чужой или несуществующий) и получить комнату «за игрока», которого нет.
    /// <c>null</c> — проверка выключена (тесты реестра и режим без хоста).
    /// </remarks>
    public Func<IReadOnlyList<(int Id, string Nick)>>? OnlinePlayers { get; init; }

    /// <summary>
    /// Статистика игроков на диске. <c>null</c> — эндпоинт статистики отвечает, что
    /// статистики нет (пустые нули), а не падает.
    /// </summary>
    public PlayerStatsStore? Stats { get; init; }

    /// <summary>
    /// История матчей на диске. <c>null</c> — история не ведётся: API отдаёт пустой список.
    /// </summary>
    public MatchHistoryStore? Matches { get; init; }

    /// <summary>
    /// Серверный id → админ? Из конфига <c>admin.ids</c>. <c>null</c> — админов нет.
    /// </summary>
    public Func<int, bool>? IsAdminById { get; init; }

    // ── Жалобы и публичные профили ──────────────────────────────────────────────

    /// <summary>Хранилище жалоб. <c>null</c> — вкладка «Репорты» отвечает 503.</summary>
    public ReportStore? Reports { get; init; }

    /// <summary>
    /// Поиск игрока по числовому или кастомному ID для превью жалобы и публичного
    /// профиля. <c>null</c> — хост ещё не подключён.
    /// </summary>
    public Func<string, ReportTarget?>? ReportPlayerLookup { get; init; }

    /// <summary>
    /// Список администрации из живого <c>admin.ids</c>. <c>null</c> — хост ещё не
    /// подключён, страница не должна показывать пустоту как «админов нет».
    /// </summary>
    public Func<IReadOnlyList<ReportTarget>?>? AdministrationList { get; init; }

    // ── Telegram-бот ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Постоянный ключ бота (<c>auth/tgbot/password.txt</c>). <c>null</c> — вход
    /// <c>/api/bot/*</c> закрыт: сервер не настроен для бота (503).
    /// </summary>
    public BotKeyStore? BotKey { get; init; }

    // ── Админ панель ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Хранилище админов: пароли и права. <c>null</c> — вход в админку закрыт.
    /// </summary>
    public AdminStore? AdminStore { get; init; }

    /// <summary>
    /// Хранилище админ-сессий: кука с паролем, срок 7 дней, переживает рестарт.
    /// <c>null</c> — вход в админку закрыт (503).
    /// </summary>
    public AdminSessions? AdminSessionStore { get; init; }

    /// <summary>
    /// Тестовый режим: админам не нужен личный пароль. Читается из
    /// <c>config.json</c> на каждый запрос, чтобы <c>config reload</c> сразу менял ответ.
    /// </summary>
    public Func<bool>? TestNoAdminPassword { get; init; }

    /// <summary>
    /// Тестовый режим: аккаунту без админки выдаётся мега-админка. Читается из
    /// <c>config.json</c> на каждый запрос.
    /// </summary>
    public Func<bool>? TestAutoMegaAdmin { get; init; }

    /// <summary>
    /// Живой <c>admin.megaIds</c> в порядке конфига. Первый подходящий id становится
    /// тестовым админом, под которым панель входит без экрана входа.
    /// <c>null</c> — кандидатов нет.
    /// </summary>
    public Func<IReadOnlyList<int>>? MegaIds { get; init; }

    /// <summary>
    /// Живой <c>admin.ids</c>: запасные кандидаты тестового автовхода, если мега-админов
    /// нет. Права выбранного id не меняются — обычный админ остаётся обычным.
    /// </summary>
    public Func<IReadOnlyList<int>>? AdminIds { get; init; }

    /// <summary>
    /// Самый первый аккаунт (минимальный id). Последний кандидат тестового автовхода:
    /// когда в конфиге нет ни одного админа, а <c>test.autoMegaAdmin</c> включён,
    /// автовход выдаёт этому аккаунту мега-права в памяти.
    /// </summary>
    public Func<(int Id, string Nick)?>? FirstAccount { get; init; }

    /// <summary>
    /// Существует ли аккаунт с таким id. Нужен тестовой автовыдаче: нельзя выдать
    /// мега-права случайному числу, которого нет среди игроков.
    /// </summary>
    public Func<int, bool>? AccountExists { get; init; }

    /// <summary>
    /// Живая проверка админства: читает <b>текущий</b> конфиг, а не снимок со старта —
    /// перечитывание <c>admin.ids</c> сразу меняет и кнопку, и доступ.
    /// </summary>
    public Func<int, bool>? IsAdminLive { get; init; }

    /// <summary>Кастомный ID игрока (null — показываем обычный числовой).</summary>
    public Func<int, string?>? CustomIdGet { get; init; }

    /// <summary>
    /// Поставить/сбросить кастомный ID (пустая строка — сброс). Возвращает текст ошибки
    /// или <c>null</c> при успехе. <c>null</c>-делегат — функция закрыта (503).
    /// </summary>
    public Func<int, string?, string?>? CustomIdSet { get; init; }

    /// <summary>
    /// Поиск игрока по серверному id для превью в «Банах» (ник, статус, аватар).
    /// Не нашли — <c>null</c>. <c>null</c>-делегат — вкладка отвечает 503.
    /// </summary>
    public Func<int, PlayerLookupResult?>? AdminLookup { get; init; }

    /// <summary>Бан по id: (id, срок, причина, кто банит) → (ошибка, запись реестра).</summary>
    public Func<int, string, string, string, (string? Error, BanRecord? Record)>? WebBan { get; init; }

    /// <summary>Разбан по id: (id) → (ошибка, сколько записей снято).</summary>
    public Func<int, (string? Error, int Removed)>? WebUnban { get; init; }

    /// <summary>Активные баны для списка в админке. <c>null</c> — пусто.</summary>
    public Func<IReadOnlyList<BanRecord>>? ActiveBans { get; init; }

    /// <summary>Настройки банов: включены ли и диапазон сроков. <c>null</c> — выключены.</summary>
    public Func<(bool Enabled, int MinMinutes, int MaxDays)>? BanSettings { get; init; }

    // ── Консоль: игроки, комнаты, конфиг и безопасность ─────────────────────────

    /// <summary>Когда запущен сервер — для аптайма на дашборде.</summary>
    public DateTime? StartedUtc { get; init; }

    /// <summary>Сколько игроков сервер вообще помнит (справочник личностей).</summary>
    public Func<int>? KnownPlayersCount { get; init; }

    /// <summary>Живое состояние сервера: онлайн, комнаты, катка, режим.</summary>
    public Func<AdminServerState>? AdminServerState { get; init; }

    /// <summary>
    /// Поиск игроков по id, нику, кастомному id, IP или устройству. Возвращает
    /// онлайн-людей (лобби + катка) и офлайн-записи справочника. <c>null</c> — поиск
    /// выключен (503).
    /// </summary>
    public Func<string, int, IReadOnlyList<AdminPlayerRow>>? AdminSearchPlayers { get; init; }

    /// <summary>
    /// Кик игрока из лобби и/или катки. Возвращает текст ошибки или <c>null</c> при
    /// успехе. Себя кикнуть нельзя: вызывающий проверяет это до делегирования.
    /// </summary>
    public Func<int, string, string?>? AdminKickPlayer { get; init; }

    /// <summary>
    /// Принудительно закрыть комнату из админки. Возвращает текст ошибки или <c>null</c>.
    /// </summary>
    public Func<int, string, string?>? AdminCloseRoom { get; init; }

    /// <summary>Безопасная часть конфига для страницы «Конфиг». <c>null</c> — 503.</summary>
    public Func<AdminConfigSnapshot>? AdminConfig { get; init; }

    /// <summary>
    /// Изменить разрешённый ключ конфига. Первый аргумент — ключ, второй — значение
    /// в строковом виде. Возвращает текст ошибки или <c>null</c>.
    /// </summary>
    public Func<string, AdminConfigValueKind, string, string?>? AdminConfigSet { get; init; }

    /// <summary>Заменить <c>admin.ids</c> (без мега-админов). Возвращает текст ошибки.</summary>
    public Func<IReadOnlyList<int>, string?>? AdminSetIds { get; init; }

    /// <summary>Перечитать config.json немедленно. Возвращает текст ошибки или <c>null</c>.</summary>
    public Func<string?>? AdminReloadConfig { get; init; }
}
