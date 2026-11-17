using System.Reflection;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Панель целиком: реестр комнат, страница, API, фоновое обновление сводки.
/// </summary>
/// <remarks>
/// <para>
/// Один объект на процесс. Создаётся <b>до</b> игровых сокетов: если 7777 занят, страница
/// всё равно открывается и честно показывает состояние, а не «502 Bad Gateway». Поэтому
/// <see cref="Start"/> не зависит от <see cref="GameNetHost"/> и терпит его отсутствие.
/// </para>
/// <para>
/// Игровой хост подключается позже через <see cref="AttachHost"/>: до этого панель
/// показывает ноль игроков, но кнопка создания уже отвечает внятной ошибкой, а не
/// зависает.
/// </para>
/// <para>
/// HTML лежит в сборке как <c>EmbeddedResource</c> — рядом с бинарником нет ни папки
/// <c>wwwroot</c>, ни файлов, которые можно потерять при копировании.
/// </para>
/// </remarks>
public sealed class WebPanelHost : IDisposable
{
    /// <summary>Имя ресурса со страницей.</summary>
    public const string IndexResourceName = "StandChillow.LanServer.Web.wwwroot.index.html";

    private readonly WebConfig _config;
    private readonly ServerConfig _server;
    private readonly RoomRegistry _rooms;
    private readonly StatusSnapshotCache _status = new();

    /// <summary>
    /// Сессии сайта. Одни на запуск сервера: перезапуск гасит все сессии и коды (#51).
    /// </summary>
    private readonly SiteSessions _sessions = new();
    private readonly SiteAddressResolver _addresses;
    private readonly WebPanelServer _server_;
    private readonly WebPanelRouter _router;
    private readonly CancellationTokenSource _tickCts = new();
    private readonly Func<string> _indexHtml;

    private GameNetHost? _host;
    private Task? _tickLoop;

    public WebPanelHost(ServerConfig server)
    {
        _server = server;
        _config = server.Web;
        _rooms = new RoomRegistry { MaxRooms = Math.Clamp(_config.MaxRooms, 1, 1024) };

        _indexHtml = LoadEmbeddedIndex;
        _addresses = new SiteAddressResolver
        {
            ConfiguredBaseUrl = _config.PublicBaseUrl ?? "",
        };

        var limits = new WebPanelOptions.WebLimits
        {
            RequestsPerSecond = Math.Clamp(_config.Limits.RequestsPerSecond, 1, 1000),
            RefusalsBeforeBlock = Math.Clamp(_config.Limits.RefusalsBeforeBlock, 1, 1000),
            RefusalWindowSec = Math.Clamp(_config.Limits.RefusalWindowSec, 1, 3600),
            BlockDurationSec = Math.Clamp(_config.Limits.BlockDurationSec, 1, 86400),
            MaxHeaderBytes = Math.Clamp(_config.Limits.MaxHeaderBytes, 1024, 64 * 1024),
            MaxBodyBytes = Math.Clamp(_config.Limits.MaxBodyBytes, 256, 64 * 1024),
            RequestTimeoutSec = Math.Clamp(_config.Limits.RequestTimeoutSec, 1, 120),
            MaxConnectionsPerIp = Math.Clamp(_config.Limits.MaxConnectionsPerIp, 1, 64),
            MaxConnectionsTotal = Math.Clamp(_config.Limits.MaxConnectionsTotal, 16, 4096),
        };

        _router = new WebPanelRouter(
            _rooms,
            () => _indexHtml(),
            _status,
            new WebPanelRouterOptions
            {
                OnRoomCreated = CreateGameRoom,

                // Кто сейчас в лобби — источник правды для «комната за этим ID».
                // Ник берётся отсюда, а не из тела запроса: вписать чужой нельзя.
                OnlinePlayers = ListOnlinePlayers,

                // Авторизация: код из лобби меняем на сессию сайта (#51).
                Sessions = _sessions,
                RedeemCode = RedeemSiteLoginCode,
                CookiePath = CookiePathFor(_config.BasePath),
                SecureCookies = _config.SecureCookies,

                // «Завершить игру» — только владельцу комнаты.
                EndRoom = EndRoomByOwner,

                // Бан по ID игрока: гасит авторизацию и все запросы (#51).
                IsBanned = IsPlayerBanned,

                // Аватар — те же байты, что клиент прислал в лобби.
                AvatarById = AvatarOfPlayer,

                // Префикс подпути: со слэшем в proxy_pass прокси его срежет, но
                // конфиг правит владелец — роутер обязан понимать оба варианта.
                BasePath = _config.BasePath,
            });

        _server_ = new WebPanelServer(
            new WebPanelOptions
            {
                BindAddress = string.IsNullOrWhiteSpace(_config.BindAddress)
                    ? "0.0.0.0"
                    : _config.BindAddress.Trim(),
                Port = Math.Clamp(_config.Port, 1, 65535),
                Limits = limits,
            },
            _router.Route);
    }

    /// <summary>Реестр комнат — то, что видит и панель, и <c>/join</c>.</summary>
    public RoomRegistry Rooms => _rooms;

    /// <summary>Порт панели; 0, если не поднялась.</summary>
    public int Port => _server_.Port;

    /// <summary>Откуда брать адрес сайта для «создать можно здесь».</summary>
    public SiteAddressResolver SiteAddress => _addresses;

    /// <summary>
    /// Поднять панель. Провал бинда — не катастрофа: возвращаем <c>false</c> и причину,
    /// игра продолжает работать (спека: панель не должна мешать серверу, и наоборот).
    /// </summary>
    public bool TryStart(out string error)
    {
        if (!_config.Enabled)
        {
            error = "web.enabled = false";
            return false;
        }

        if (!_server_.TryStart(out error))
            return false;

        PrintAddresses();

        _tickLoop = Task.Run(() => TickLoopAsync(_tickCts.Token));
        return true;
    }

    /// <summary>
    /// Напечатать, где панель реально доступна. Запуск не должен требовать поиска
    /// адреса в конфиге: строка «открой с телефона вот это» — единственное, что
    /// нужно человеку, чтобы начать.
    /// </summary>
    private void PrintAddresses()
    {
        var port = _server_.Port;
        Console.WriteLine(
            $"[web] панель на порту {port} " +
            $"(комнат до {_rooms.MaxRooms}, лимит {_config.Limits.RequestsPerSecond} запр/с с IP)");

        // ВАЖНО: не зовём Resolve() — он может пойти в интернет за внешним IP и
        // подвесить старт на пару секунд. На запуске нужен только локальный адрес.
        var lan = LanAddress();
        if (!string.IsNullOrEmpty(lan))
            Console.WriteLine($"[web]   с телефона (та же сеть): http://{lan}:{port}/");
        Console.WriteLine($"[web]   на этой машине:         http://127.0.0.1:{port}/");

        if (_server_.BindAddress is "127.0.0.1" or "localhost" or "::1")
        {
            // Честно предупреждаем: с других машин и от обратного прокси этот адрес
            // недоступен. Молчание здесь стоило бы часа поисков «почему 502».
            Console.WriteLine(
                "[web]   внимание: bindAddress = " + _server_.BindAddress +
                " — с других машин и от nginx панель недоступна. " +
                "Для LAN и прокси нужен \"0.0.0.0\" (это дефолт).");
        }
    }

    /// <summary>
    /// LAN-адрес этой машины или пустая строка. Без обращений в интернет.
    /// </summary>
    private string LanAddress()
    {
        try
        {
            foreach (var lan in LanInterfacePicker.GetLanIpv4Endpoints())
            {
                var text = lan.Address.ToString();
                if (!string.IsNullOrEmpty(text))
                    return text;
            }
        }
        catch
        {
            // Нет интерфейсов — не повод падать на старте.
        }

        return string.Empty;
    }

    /// <summary>
    /// Подключить игровой хост: с этого момента панель видит игроков, а создание комнаты
    /// реально открывает её на 7777.
    /// </summary>
    public void AttachHost(GameNetHost host)
    {
        _host = host;
        host.SiteAddress = _addresses;
        host.Match.Rooms = _rooms;

        // «Комнат не больше, чем игроков онлайн»: квота реестра спрашивает у хоста,
        // сколько людей сейчас в лобби и в катке. Пока хост не подключён, считаем 0.
        _rooms.OnlinePlayerCount = () => host.JoinedCount + host.Match.HumanActorCount;
        host.Match.PendingRooms.Warning += msg => Console.WriteLine(msg);

        // Срок кода — из конфига (горячая перезагрузка подхватит при следующей выдаче).
        host.SiteCodes.Ttl = TimeSpan.FromSeconds(_config.CodeTtlSec);

        Console.WriteLine(
            $"[web] игровой хост подключён: онлайн {_rooms.OnlinePlayerCount()}, " +
            $"квота комнат по игрокам активна");
    }

    /// <summary>Остановить панель (сокет + фоновая задача).</summary>
    public void Stop()
    {
        try { _tickCts.Cancel(); }
        catch { /* уже отменён */ }
        try { _tickLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* отмена — норма */ }
        _server_.Stop();
    }

    public void Dispose()
    {
        Stop();
        _server_.Dispose();
        _tickCts.Dispose();
    }

    // ── фоновая сводка ───────────────────────────────────────────────────────

    private async Task TickLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                // Раз в секунду: заодно закрывает комнаты, из которых все ушли.
                _status.Refresh(BuildStatusJson);
                SweepExpiredRooms();

                // Протухшие коды выбрасываем, чтобы не копить записи за ушедшими игроками.
                // Дёшево: активных кодов столько же, сколько людей онлайн.
                _host?.SiteCodes.Prune(DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[web] фоновая задача: {ex.Message}");
            }

            try { await Task.Delay(StatusSnapshotCache.RefreshInterval, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>
    /// Удалить комнаты, которые больше не нужны: пустые дольше TTL и завершённые.
    /// </summary>
    /// <remarks>
    /// Удаление обязательно снимает и бронь ника: иначе игрок, чья комната закрылась,
    /// не смог бы создать новую, а <c>/join</c> вёл бы в мёртвый ключ.
    /// </remarks>
    private void SweepExpiredRooms()
    {
        // Нижняя граница 30 с — та же, что в NormalizeWeb: конфиг уже проверен,
        // второй, более строгий предел здесь только мешал бы (перебивал настройку).
        var emptyTtl = TimeSpan.FromSeconds(Math.Clamp(_config.EmptyRoomTtlSec, 30, 86400));
        var expired = _rooms.FindExpired(DateTime.UtcNow, emptyTtl, emptyTtl);
        if (expired.Count == 0)
            return;

        foreach (var number in expired)
        {
            if (!_rooms.TryRemove(number, out var removed))
                continue;

            // Убрать из реестра мало: комната живёт ещё и в матч-хосте, откуда её
            // продолжали бы находить /join и бронь. Закрываем в обеих структурах.
            _host?.Match.TryCloseRoom(removed.PasswordKey);
            _host?.Match.PendingRooms.Release(removed.PasswordKey);

            Console.WriteLine(
                $"[web] комната #{removed.Number} «{removed.Name}» закрыта и номер " +
                $"освобождён ({(removed.Players > 0 ? "катка закончилась" : "никто не пришёл")})");
        }
    }

    // ── JSON для панели ──────────────────────────────────────────────────────

    private string BuildStatusJson()
    {
        var host = _host;
        var lobby = host?.JoinedCount ?? 0;
        var match = host?.Match.HumanActorCount ?? 0;
        var rooms = _rooms.Snapshot();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("onlineLobby", lobby);
            writer.WriteNumber("onlineMatch", match);
            writer.WriteNumber("onlineTotal", lobby + match);
            writer.WriteNumber("roomsCount", rooms.Count);
            writer.WriteNumber("roomsMax", _rooms.MaxRooms);
            writer.WriteNumber("nextNumber", _rooms.NextNumber);
            writer.WriteBoolean("gameListening", host?.Match.IsListening ?? false);
            writer.WriteBoolean("matchRunning", host?.MatchStarted ?? false);
            writer.WriteString("mode", host?.Session.GameModeId ?? "");
            writer.WriteString("levels", string.Join(", ", host?.Session.SelectedLevels
                ?? new List<string>()));
            writer.WriteString("serverUtc", DateTime.UtcNow.ToString("O"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // ── создание игровой комнаты ─────────────────────────────────────────────

    /// <summary>
    /// Превратить запись реестра в настоящую комнату матч-хоста. Возвращает текст
    /// ошибки или <c>null</c> при успехе.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Комната создаётся уже «armed»: из панели её открывают, чтобы играть, ручного
    /// <c>/set start</c> в этом сценарии нет.
    /// </para>
    /// <para>
    /// Брони на создателя здесь <b>нет</b>, и это принципиально. Раньше она ставилась
    /// сразу при создании — и любой вход в лобби по этому нику получал в
    /// <c>JoinRoomResponse</c> адрес 7777: клиент прыгал в комнату, даже не написав
    /// <c>/join</c>. Вход в катку — только явная команда игрока.
    /// </para>
    /// </remarks>
    private string? CreateGameRoom(RoomDescriptor room)
    {
        var host = _host;
        if (host is null)
            return "игровой сервер ещё не поднялся — попробуй через пару секунд";

        var created = host.Match.TryCreatePanelRoom(
            room.PasswordKey,
            room.ModeId,
            room.Level,
            room.Number,
            BuildRoomLabel(room),
            room.PerTeam);

        if (created is null)
            return "комната с таким номером уже существует — обнови страницу";

        host.Match.ArmRoomByPasswordKey(
            room.PasswordKey, $"создана с сайта, #{room.Number}");

        // Комната с сайта обязана сразу быть играбельной: клиент узнаёт адрес матча
        // только из op9 с hasHosting=1. Без этого /join клал бронь, отвечал
        // «заходи в игру» — и игрок навсегда оставался в лобби (peers=0 на 7777).
        if (!host.OpenMatchPortForPanelRoom($"комната #{room.Number} с сайта"))
        {
            Console.WriteLine(
                $"[web] комната #{room.Number} создана, но игровой порт не открылся — " +
                "игроки не смогут войти; проверь, не занят ли UDP 7777");
        }

        Console.WriteLine(
            $"[web] создана комната #{room.Number} «{BuildRoomLabel(room)}» " +
            $"({room.ModeId} / {room.Level}) владелец '{room.OwnerNick}' " +
            $"(ID {room.OwnerId}) — " +
            $"ждать: /join {GameModeCatalogAlias(room.ModeId)} #{room.Number}");
        return null;
    }

    /// <summary>
    /// Кто сейчас в лобби: серверный id и ник. Пусто, пока хост не подключён.
    /// </summary>
    /// <remarks>
    /// id считается той же функцией, что даёт <c>uid</c> в катке и строку
    /// «Ты &lt;ник&gt; · ID: …» при входе в лобби, поэтому человек называет ровно
    /// то число, которое сервер и так про него знает.
    /// </remarks>
    private IReadOnlyList<(int Id, string Nick)> ListOnlinePlayers()
    {
        var host = _host;
        if (host is null)
            return Array.Empty<(int, string)>();

        var players = host.Session.JoinedPlayers();
        var list = new List<(int Id, string Nick)>(players.Count);
        foreach (var player in players)
        {
            var id = MatchPublicId.ForNick(player.Name);
            if (id > 0)
                list.Add((id, player.Name));
        }

        return list;
    }

    /// <summary>
    /// Обменять код из лобби на запись игрока. Код чужой или протухший — <c>null</c>:
    /// сайт не различает эти случаи и отвечает одним и тем же текстом (#51).
    /// </summary>
    private SiteLoginCode? RedeemSiteLoginCode(string code, DateTime nowUtc)
    {
        var host = _host;
        if (host is null)
            return null;

        return host.SiteCodes.TryFind(code, nowUtc, out var entry) ? entry : null;
    }

    /// <summary>
    /// Аватар игрока: ровно те байты, что его клиент прислал при входе в лобби
    /// (по захвату — JPEG 256×256). Игрока в лобби нет — аватара тоже нет.
    /// </summary>
    private byte[]? AvatarOfPlayer(int playerId)
    {
        var host = _host;
        if (host is null || playerId <= 0)
            return null;

        foreach (var player in host.Session.JoinedPlayers())
        {
            if (MatchPublicId.ForNick(player.Name) != playerId) continue;
            return player.Avatar;
        }

        return null;
    }

    /// <summary>
    /// Забанен ли игрок с таким ID. Пока хост не подключён — считаем, что нет: банов
    /// просто нечем проверять, а ломать панель из-за этого незачем.
    /// </summary>
    private bool IsPlayerBanned(int playerId) =>
        _host?.IsPlayerBannedById(playerId) ?? false;

    /// <summary>
    /// Завершить катку в комнате по просьбе владельца (кнопка «Завершить игру», #51).
    /// </summary>
    /// <returns>Текст ошибки или <c>null</c>, если завершение пошло.</returns>
    private string? EndRoomByOwner(int number, int playerId)
    {
        var ownerId = _rooms.OwnerIdOf(number);
        if (ownerId <= 0)
            return $"комнаты #{number} нет — обнови страницу";
        if (ownerId != playerId)
            return "это не твоя комната";

        RoomDescriptor? room = null;
        foreach (var candidate in _rooms.Snapshot())
        {
            if (candidate.Number != number) continue;
            room = candidate;
            break;
        }

        if (room is null)
            return $"комнаты #{number} нет — обнови страницу";

        var host = _host;
        if (host is null)
            return "игровой сервер ещё не поднялся";

        // Катка кончается как по-настоящему: C2=201 FinalHud по текущему счёту →
        // выдержка → C2=255 → закрытие комнаты и освобождение номера.
        var finished = host.Match.TryFinishRoomEarly(room.PasswordKey, $"веб: владелец ID {playerId}");

        return finished
            ? null
            : "эту катку уже завершили — обнови страницу";
    }

    /// <summary>
    /// Путь куки: за прокси панель живёт в подпути, и кука обязана совпадать с ним.
    /// </summary>
    internal static string CookiePathFor(string? basePath) =>
        string.IsNullOrWhiteSpace(basePath) ? "/" : "/" + basePath.Trim().Trim('/');

    /// <summary>Подпись комнаты для лога и страницы: имя или «Комната #N».</summary>
    public static string BuildRoomLabel(RoomDescriptor room) =>
        string.IsNullOrWhiteSpace(room.Name) ? $"Комната #{room.Number}" : room.Name;

    private static string GameModeCatalogAlias(string modeId) =>
        GameModeCatalog.TryGetMode(modeId, out var mode)
            ? GameModeCatalog.PrimaryAlias(mode)
            : modeId;

    // ── страница ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Прочитать вшитый HTML. Файла рядом с бинарником нет — только ресурс сборки.
    /// </summary>
    private static string LoadEmbeddedIndex()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(IndexResourceName);
        if (stream is null)
        {
            var known = string.Join(", ",
                assembly.GetManifestResourceNames()
                    .Where(n => n.Contains("index.html", StringComparison.OrdinalIgnoreCase)));
            Console.WriteLine(
                $"[web] FATAL: ресурс '{IndexResourceName}' не найден " +
                $"(html-ресурсы: {known}) — панель отдаст заглушку");
            return FallbackPage;
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Заглушка, если ресурс не вшит. Не пустая страница: человек должен понять, что
    /// сервер жив, а сломан именно HTML.
    /// </summary>
    private const string FallbackPage =
        "<!doctype html><html lang=\"ru\"><meta charset=\"utf-8\">" +
        "<title>влалChillow</title>" +
        "<body style=\"font:16px system-ui;padding:24px\">" +
        "<h1>влалChillow</h1>" +
        "<p>Сервер работает, но страница панели не вшита в сборку " +
        "(<code>EmbeddedResource wwwroot/index.html</code>).</p></body></html>";
}
