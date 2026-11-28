using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Auth;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using StandChillow.LanServer.Lan;
using StandChillow.LanServer.Net;
using StandChillow.LanServer.Net.Lobby;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Reports;
using StandChillow.LanServer.Stats;

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
    /// Сессии сайта. Живут 30 дней и переживают рестарт: на диске только SHA-256 токенов.
    /// </summary>
    private readonly SiteSessions _sessions;

    /// <summary>
    /// Админ-сессии панели: «пароль предъявлен». Срок 7 дней, переживают рестарт.
    /// </summary>
    private readonly AdminSessions _adminSessions;

    /// <summary>Админы, пароли и права. Один на процесс: им пользуются сайт и игра.</summary>
    private readonly AdminStore _adminStore;

    /// <summary>Постоянный ключ Telegram-бота: вход <c>/api/bot/*</c> и сброс из консоли.</summary>
    private readonly BotKeyStore _botKey;
    private readonly SiteAddressResolver _addresses;
    private readonly WebPanelServer _server_;
    private readonly WebPanelRouter _router;
    private readonly CancellationTokenSource _tickCts = new();
    private readonly Func<string> _indexHtml;
    private readonly AvatarCache _avatars;

    private GameNetHost? _host;
    private Task? _tickLoop;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    /// <summary>Счётчик секундных тиков: статистика семплируется не каждый.</summary>
    private int _statsTick;

    /// <summary>Статистика игроков на диске (киллы/смерти/время/матчи).</summary>
    private readonly PlayerStatsStore _stats;

    /// <summary>Жалобы игроков на диске — переживают рестарт.</summary>
    private readonly ReportStore _reports;

    /// <summary>История сыгранных матчей на диске.</summary>
    private readonly MatchHistoryStore _history;

    /// <summary>Раз в сколько секунд семплируем комнаты для статистики.</summary>
    private const int StatsSampleSeconds = 15;

    /// <summary>Файл статистики — рядом с исполняемым файлом, как banlist.json.</summary>
    private const string StatsFileName = "stats.json";

    /// <summary>Файл жалоб — рядом с остальными данными сервера.</summary>
    private const string ReportsFileName = "reports.json";

    /// <summary>Файл истории матчей — отдельно от агрегатов stats.json.</summary>
    private const string MatchHistoryFileName = "matches.json";

    public WebPanelHost(
        ServerConfig server,
        AvatarCache? avatars = null,
        AdminStore? adminStore = null,
        BotKeyStore? botKey = null)
    {
        _server = server;
        _config = server.Web;
        _rooms = new RoomRegistry { MaxRooms = Math.Clamp(_config.MaxRooms, 1, 1024) };
        // Кэш аватаров общий с игровым хостом: лобби кладёт, сайт читает.
        _avatars = avatars
            ?? new AvatarCache(Path.Combine(AppContext.BaseDirectory, "avatars"));

        // Админы: пароли и права лежат в auth/ рядом с config.json. Игра получает
        // тот же объект, поэтому смена пароля сразу видна обеим сторонам.
        var authDirectory = AdminStore.ResolveDirectory(ServerConfig.LastLoadedPath);
        _adminStore = adminStore ?? new AdminStore(authDirectory, Console.WriteLine);
        _adminStore.Initialize(server.Admin.Ids, server.Admin.MegaIds, DateTime.UtcNow);
        _sessions = new SiteSessions(Path.Combine(authDirectory, "site-sessions.json"));
        _adminSessions = new AdminSessions(
            Path.Combine(authDirectory, "sessions.json"), _adminStore);

        // Ключ бота: auth/tgbot/password.txt. Создаётся при первом старте, сброс —
        // командой tgbot resetkey. В логи попадает только путь, не сам ключ.
        _botKey = botKey ?? new BotKeyStore(
            Path.Combine(authDirectory, BotKeyStore.DirectoryName), Console.WriteLine);
        _botKey.LoadOrCreate();

        // Статистика живёт на диске рядом с exe: рестарт ничего не теряет.
        _stats = new PlayerStatsStore(Path.Combine(AppContext.BaseDirectory, StatsFileName));
        Console.WriteLine($"[stats] хранилище: {_stats.FilePath} (в памяти {_stats.Count} игроков)");

        _reports = new ReportStore(Path.Combine(AppContext.BaseDirectory, ReportsFileName));
        Console.WriteLine($"[report] хранилище: {_reports.FilePath} (в памяти {_reports.Count} жалоб)");

        _history = new MatchHistoryStore(Path.Combine(AppContext.BaseDirectory, MatchHistoryFileName));
        Console.WriteLine($"[history] хранилище: {_history.FilePath} (в памяти {_history.Count} матчей)");

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

                // Аватар — те же байты, что клиент прислал в лобби (или кэш на диске).
                AvatarById = AvatarOfPlayer,
                // Публичный профиль: тот же кэш, но без требования сессии.
                PublicAvatarById = CachedAvatarOfPlayer,

                // Префикс подпути: со слэшем в proxy_pass прокси его срежет, но
                // конфиг правит владелец — роутер обязан понимать оба варианта.
                BasePath = _config.BasePath,

                // Статистика для профиля и админ-флаг из конфига (admin.ids).
                // Живая проверка: перечитывание конфига сразу меняет кнопку и доступ,
                // а не после рестарта (хост ещё не подключён — старый снимок лучше, чем ничего).
                Stats = _stats,
                IsAdminById = id => _host?.IsAdminLive(id) ?? _server.IsAdmin(id),

                // Жалобы: хранилище общее с игровым хостом, поиск цели — серверный.
                Reports = _reports,
                ReportPlayerLookup = q => _host?.LookupReportTarget(q),
                AdministrationList = () => _host?.AdministrationForWeb(),

                // История матчей: последние катки для профилей.
                Matches = _history,

                // Вход Telegram-бота: постоянный ключ, проверяется до всех данных.
                BotKey = _botKey,

                // ── Админ панель ──
                AdminStore = _adminStore,
                AdminSessionStore = _adminSessions,
                IsAdminLive = id => _host?.IsAdminLive(id) ?? _server.IsAdmin(id),
                TestNoAdminPassword = () =>
                    (_host?.CurrentConfig ?? _server).Test.NoAdminPassword,
                TestAutoMegaAdmin = () =>
                    (_host?.CurrentConfig ?? _server).Test.AutoMegaAdmin,
                AccountExists = id => _host?.Accounts?.FindById(id) is not null,

                // Тестовый автовход: кого пускать без экрана входа. Порядок конфига
                // решает, кто именно: первый мега → первый админ → первый аккаунт.
                MegaIds = () => (_host?.CurrentConfig ?? _server).Admin.MegaIds,
                AdminIds = () => (_host?.CurrentConfig ?? _server).Admin.Ids,
                FirstAccount = () =>
                {
                    var account = _host?.Accounts?.Export().FirstOrDefault();
                    return account is null ? null : (account.Id, account.Nick ?? "");
                },

                // Кастомизация: только свой ID, проверяет хранилище (цифры+англ., уникальность).
                CustomIdGet = id => _host?.CustomIdOf(id),
                CustomIdSet = (id, value) => _host is null ? "хост не подключён" : _host.SetCustomId(id, value),

                // Баны: поиск для превью, бан/разбан — тем же путём, что /ban из чата.
                AdminLookup = id => _host?.LookupPlayerForAdmin(id),
                WebBan = (id, durationToken, reason, bannedBy) =>
                    _host?.TryWebBan(id, durationToken, reason, bannedBy) ?? ("хост не подключён", null),
                WebUnban = id => _host?.TryWebUnban(id) ?? ("хост не подключён", 0),
                ActiveBans = () => _host?.ActiveBanRecords() ?? Array.Empty<BanRecord>(),
                BanSettings = () => _host?.BanRange ?? (false, 10, 14),

                // ── Консоль: журнал, игроки, комнаты, конфиг, безопасность ──
                StartedUtc = _startedUtc,
                KnownPlayersCount = () => _host?.KnownPlayersCount ?? 0,
                AdminServerState = BuildAdminServerState,
                AdminSearchPlayers = (query, limit) =>
                    _host?.SearchPlayersForAdmin(query, limit) ?? Array.Empty<AdminPlayerRow>(),
                AdminKickPlayer = (id, reason) =>
                    _host?.KickPlayerForAdmin(id, reason) ?? "игровой сервер ещё не поднялся",
                AdminCloseRoom = CloseRoomAsAdmin,
                AdminConfig = BuildAdminConfig,
                AdminConfigSet = SetAdminConfigValue,
                AdminSetIds = SetAdminIds,
                AdminReloadConfig = ReloadConfigFromDisk,
            });

        _server_ = new WebPanelServer(
            new WebPanelOptions
            {
                BindAddress = string.IsNullOrWhiteSpace(_config.BindAddress)
                    ? "0.0.0.0"
                    : _config.BindAddress.Trim(),
                Port = Math.Clamp(_config.Port, 1, 65535),
                Limits = limits,
                TrustedProxies = _config.TrustedProxies ?? new List<string>(),
            },
            _router.Route);
    }

    /// <summary>Реестр комнат — то, что видит и панель, и <c>/join</c>.</summary>
    public RoomRegistry Rooms => _rooms;

    /// <summary>Порт панели; 0, если не поднялась.</summary>
    public int Port => _server_.Port;

    /// <summary>Откуда брать адрес сайта для «создать можно здесь».</summary>
    public SiteAddressResolver SiteAddress => _addresses;

    /// <summary>Ключ бота — тот же объект, что проверяет <c>/api/bot/*</c>.</summary>
    public BotKeyStore BotKey => _botKey;

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

        // Файлы, накопленные со старых хешей ника (статистика, история, жалобы,
        // аватары), переводим на номера аккаунтов. Аккаунты к этому моменту уже
        // созданы и засеяны в InitBans — AttachHost вызывается после конструктора хоста.
        MigrateLegacyIds(host);

        // Статистика: боевые пути матча отдают киллы/смерти сюда, семплер панели
        // раз в 15 секунд забирает «кто и в каких фазах».
        host.Match.Stats = _stats;

        // История матчей: хост сам снимает составы раундов и пишет запись на финале.
        host.Match.History = _history;

        // «Комнат не больше, чем игроков онлайн»: квота реестра спрашивает у хоста,
        // сколько людей сейчас в лобби и в катке. Пока хост не подключён, считаем 0.
        _rooms.OnlinePlayerCount = () => host.JoinedCount + host.Match.HumanActorCount;
        host.Match.PendingRooms.Warning += msg => Console.WriteLine(msg);

        // Жалобы учатся устройствам: как только сервер увидел устройство игрока,
        // старые жалобы начинают находиться и по нему — смена ника не обнуляет.
        host.PlayerDevicesSeen += (id, devices) =>
        {
            try
            {
                _reports.LearnDevices(id, devices, DateTime.UtcNow);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[report] WARN устройства ID {id}: {ex.Message}");
            }
        };

        // Разовый догон: устройства, которые справочник уже знал до появления жалоб.
        try
        {
            foreach (var player in host.Directory.Export())
                _reports.LearnDevices(player.Id, player.Devices, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[report] WARN догон устройств: {ex.Message}");
        }

        // Срок кода — из конфига (горячая перезагрузка подхватит при следующей выдаче).
        host.SiteCodes.Ttl = TimeSpan.FromSeconds(_config.CodeTtlSec);

        Console.WriteLine(
            $"[web] игровой хост подключён: онлайн {_rooms.OnlinePlayerCount()}, " +
            $"квота комнат по игрокам активна");
    }

    /// <summary>
    /// Перевести статистику, историю матчей, жалобы и аватары со старых хешей ника
    /// на постоянные номера аккаунтов.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Миграция разовая: записи, которые уже лежат на номерах аккаунтов, не трогаются.
    /// Ник в записи помогает опознать человека, но если его нет — номер находится
    /// сканом старых хешей в <c>accounts.json</c>. Неизвестные аватары не удаляются,
    /// а уезжают в <c>avatars/legacy/</c>.
    /// </para>
    /// <para>
    /// Если в <c>accounts.json</c> лежит карта перенумерации, номера переводятся по ней,
    /// а не поиском по нику: старый номер мог уже достаться другому аккаунту. Каждый
    /// файл отмечается выполненным только после записи, поэтому обрыв на середине
    /// продолжается с того же места. Когда отмечены все шаги, карта удаляется.
    /// </para>
    /// </remarks>
    private void MigrateLegacyIds(GameNetHost host)
    {
        try
        {
            var accounts = host.Accounts;
            var renumber = accounts is { HasPendingRenumber: true };
            Func<int, string, int> map = renumber
                ? host.MapRenumberedAccountId
                : host.MapLegacyAccountId;

            if (!renumber || !accounts!.IsRenumberStepDone("stats"))
            {
                var stats = _stats.RemapAccountIds(map);
                if (stats > 0)
                    Console.WriteLine(
                        $"[account] {(renumber ? "перенумерация: " : "")}статистика — {stats} записей");
                if (renumber)
                    accounts!.MarkRenumberStepDone("stats");
            }

            if (!renumber || !accounts!.IsRenumberStepDone("history"))
            {
                var history = _history.RemapAccountIds(map);
                if (history > 0)
                    Console.WriteLine(
                        $"[account] {(renumber ? "перенумерация: " : "")}история матчей — {history} матчей");
                if (renumber)
                    accounts!.MarkRenumberStepDone("history");
            }

            if (!renumber || !accounts!.IsRenumberStepDone("reports"))
            {
                var reports = _reports.RemapAccountIds(map);
                if (reports > 0)
                    Console.WriteLine(
                        $"[account] {(renumber ? "перенумерация: " : "")}жалобы — {reports} записей");
                if (renumber)
                    accounts!.MarkRenumberStepDone("reports");
            }

            if (!renumber || !accounts!.IsRenumberStepDone("avatars"))
            {
                _avatars.RemapIds(host.MapRenumberedAccountId);
                if (renumber)
                    accounts!.MarkRenumberStepDone("avatars");
            }

            if (renumber && accounts!.AllRenumberStepsDone)
                accounts.FinishRenumber();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[account] WARN миграция старых файлов: {ex.Message}");
        }
    }

    /// <summary>Остановить панель (сокет + фоновая задача).</summary>
    public void Stop()
    {
        try { _tickCts.Cancel(); }
        catch { /* уже отменён */ }
        try { _tickLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* отмена — норма */ }
        _server_.Stop();

        // Статистика и жалобы на диск до выхода — что насчитали, то и сохранили.
        try { _stats.Flush(); }
        catch { /* сохранение уже залогировало свою беду */ }
        try { _reports.Flush(); }
        catch { /* сохранение уже залогировало свою беду */ }
        try { _history.Flush(); }
        catch { /* сохранение уже залогировало свою беду */ }
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

                // Статистика: раз в 15 секунд берём фазы комнат и ники людей.
                _statsTick++;
                if (_statsTick % StatsSampleSeconds == 0)
                    _stats.SampleRooms(
                        _host?.Match.SnapshotRoomsForStats() ?? Array.Empty<StatsRoomSample>(),
                        StatsSampleSeconds);
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
        var config = host?.CurrentConfig ?? _server;
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
            // Тревога — только при реальном провале перебинда. gameListening=false после
            // конца последней катки — норма (порт паркуется до следующей комнаты).
            writer.WriteBoolean("gamePortFailed", host?.Match.ListenFailed ?? false);
            writer.WriteBoolean("matchRunning", host?.MatchStarted ?? false);
            writer.WriteString("mode", host?.Session.GameModeId ?? "");
            writer.WriteString("levels", string.Join(", ", host?.Session.SelectedLevels
                ?? new List<string>()));

            // Доступ извне: сайт показывает рекомендуемый адрес, если владелец
            // настроил playit/туннель. Локальные порты остаются рядом — для
            // «напрямую» и для честной подписи «сервер слушает 7778».
            writer.WriteString("publicAddress", host?.PublicAddress
                ?? config.PublicAccess.Address ?? string.Empty);
            writer.WriteNumber("publicLobbyPort", host?.AdvertisedLobbyPort
                ?? config.PublicAccess.LobbyPortOr(config.Server.LobbyPort));
            writer.WriteNumber("publicMatchPort", host?.AdvertisedMatchPort
                ?? config.PublicAccess.MatchPortOr(GameMatchHost.DefaultMatchPort));
            writer.WriteNumber("localLobbyPort", config.Server.LobbyPort);
            writer.WriteNumber("localMatchPort", GameMatchHost.DefaultMatchPort);

            // Кто сейчас в сети — списком, а не только счётчиком: главная показывает
            // ники. Комната важнее лобби: игрок в катке остаётся joined в лобби, и без
            // дедупликации по id он висел бы в списке дважды.
            var seen = new HashSet<int>();
            writer.WriteStartArray("players");
            foreach (var room in rooms)
            {
                if (room.LivePlayers is null)
                    continue;
                foreach (var player in room.LivePlayers)
                {
                    if (player.Id <= 0 || player.Left || !seen.Add(player.Id))
                        continue;
                    WriteOnlinePlayer(writer, player.Id, player.Nick, room.Number, player.Team);
                }
            }
            foreach (var (id, nick) in ListOnlinePlayers())
            {
                if (id <= 0 || !seen.Add(id))
                    continue;
                WriteOnlinePlayer(writer, id, nick, 0, "");
            }
            writer.WriteEndArray();

            writer.WriteString("serverUtc", DateTime.UtcNow.ToString("O"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// Одна строка живого онлайна для главной: id, ник, где сидит (0 — лобби, N — комната).
    /// </summary>
    /// <remarks>
    /// Отображаемый ID и админ-бейдж берутся из живых источников панели, как и на
    /// странице комнаты: кастомные ID и <c>admin.ids</c> не должны дублироваться в статусе.
    /// </remarks>
    private void WriteOnlinePlayer(Utf8JsonWriter writer, int id, string nick, int room, string team)
    {
        writer.WriteStartObject();
        writer.WriteNumber("id", id);
        writer.WriteString("nick", nick);
        writer.WriteString("displayId",
            _host?.CustomIdOf(id) ?? id.ToString(CultureInfo.InvariantCulture));
        writer.WriteBoolean("admin", _host?.IsAdminLive(id) ?? _server.IsAdmin(id));
        writer.WriteNumber("room", room);
        writer.WriteString("team", team);
        writer.WriteEndObject();
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
    /// Кто сейчас в лобби: номер аккаунта и ник. Пусто, пока хост не подключён.
    /// </summary>
    /// <remarks>
    /// Номер выдаётся при входе — тот же, что даёт <c>uid</c> в катке и строку
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
            if (player.PublicId > 0)
                list.Add((player.PublicId, player.Name));
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
    /// Аватар игрока: живые байты из лобби, а если игрок уже вышел — из кэша на диске.
    /// Кэш переживает выход и рестарт, поэтому профиль не теряет картинку.
    /// </summary>
    private byte[]? AvatarOfPlayer(int playerId)
    {
        if (playerId <= 0)
            return null;

        var host = _host;
        if (host is not null)
        {
            foreach (var player in host.Session.JoinedPlayers())
            {
                if (player.PublicId != playerId) continue;
                if (player.Avatar is { Length: > 0 })
                    return player.Avatar;
                break;
            }
        }

        return _avatars.TryRead(playerId);
    }

    /// <summary>Аватар из кэша без привязки к лобби — для публичного профиля.</summary>
    private byte[]? CachedAvatarOfPlayer(int playerId) => _avatars.TryRead(playerId);

    /// <summary>Кэш аватаров: лобби пишет сюда при входе.</summary>
    public AvatarCache Avatars => _avatars;

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
    /// <summary>Живое состояние сервера для дашборда консоли.</summary>
    private AdminServerState BuildAdminServerState()
    {
        var config = _host?.CurrentConfig ?? _server;
        var (lobby, match) = _host?.Presence ?? (0, 0);
        var rooms = _rooms.Snapshot();
        var listening = _host?.Match.IsListening == true;
        var running = listening && _host?.Match.HasMatchPeers == true;

        return new AdminServerState(
            lobby,
            match,
            rooms.Count,
            _rooms.MaxRooms,
            running,
            listening,
            _host?.Match.ListenFailed == true,
            config.MatchStart.Mode,
            _host?.SelectedLevels ?? Array.Empty<string>());
    }

    /// <summary>
    /// Принудительно закрыть комнату из консоли: катка завершается, сокеты 7777
    /// отключаются, номер сразу освобождается. Не ждём TTL.
    /// </summary>
    private string? CloseRoomAsAdmin(int number, string reason)
    {
        RoomDescriptor? room = null;
        foreach (var candidate in _rooms.Snapshot())
        {
            if (candidate.Number != number)
                continue;
            room = candidate;
            break;
        }

        if (room is null)
            return $"комнаты #{number} уже нет — обнови страницу";

        var host = _host;
        if (host is not null)
        {
            // Закрывает и катку, и реестр, и бронь. Для комнаты, которая ещё не
            // стартовала, TryCloseRoom вернёт false — тогда снимаем запись сами.
            host.Match.TryCloseRoom(room.PasswordKey);
            host.Match.PendingRooms.Release(room.PasswordKey);
        }

        _rooms.TryGet(number, out _);
        if (_rooms.Snapshot().Any(r => r.Number == number))
            _rooms.TryRemove(number, out _);

        Console.WriteLine(
            $"[admin] комната #{number} «{room.Name}» закрыта принудительно" +
            (string.IsNullOrWhiteSpace(reason) ? "" : $": {reason}"));
        return null;
    }

    // ── Безопасный редактор config.json ───────────────────────────────────────

    /// <summary>
    /// Снимок только тех ключей, которые панель имеет право менять. Всё остальное
    /// (пароли, пути, порты, сеть, прокси) остаётся за владельцем файла.
    /// </summary>
    private AdminConfigSnapshot BuildAdminConfig()
    {
        var path = ServerConfig.LastLoadedPath;
        DateTime? lastWrite = null;
        var issues = new List<string>();
        ServerConfig config;

        try
        {
            if (File.Exists(path))
                lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            // Время файла — украшение, не повод падать.
        }

        if (!ServerConfig.TryLoad(path, out var fresh, out var error))
        {
            issues.Add(error);
            fresh = _host?.CurrentConfig ?? _server;
        }

        config = fresh;
        foreach (var issue in config.Issues)
            issues.Add($"{issue.Key}: {issue.Detail} → применено «{issue.Applied}»");

        var mega = new HashSet<int>(config.Admin.MegaIds);
        var ids = config.Admin.Ids
            .Where(id => !mega.Contains(id))
            .Distinct()
            .OrderBy(id => id)
            .ToList();

        var options = new List<AdminConfigOption>
        {
            Str("server.welcomeMessage", "Приветствие в чате", "Лобби",
                config.Server.WelcomeMessage, "Пусто — приветствие по умолчанию."),
            Int("server.maxMembers", "Максимум игроков", "Лобби",
                config.Server.MaxMembers, 1, 64),
            Bool("names.enforce", "Проверка ников", "Ники",
                config.Names.Enforce),
            Int("names.maxLength", "Максимум символов в нике", "Ники",
                config.Names.MaxLength, 1, 32),
            Int("names.kickDelaySec", "Кик за плохой ник, сек", "Ники",
                config.Names.KickDelaySec, 1, 300),
            Choice("matchStart.mode", "Как начинается матч", "Матч",
                config.MatchStart.Mode, new[] { "manual", "auto", "ready" }),
            Int("timings.warmup", "WarmUp, сек", "Матч",
                config.Timings.Warmup, 0, 120),
            Int("timings.preStart", "PreStart, сек", "Матч",
                config.Timings.PreStart, 0, 120),
            Int("timings.prep", "Закупка, сек", "Матч",
                config.Timings.Prep, 1, 120),
            Int("timings.roundTime", "Раунд, сек", "Матч",
                config.Timings.RoundTime, 10, 600),
            Int("timings.roundEndPause", "Пауза после раунда, сек", "Матч",
                config.Timings.RoundEndPause, 0, 60),
            Int("timings.bombFuse", "Фитиль бомбы, сек", "Матч",
                config.Timings.BombFuse, 5, 180),
            Int("timings.totalRounds", "Раундов в матче", "Матч",
                config.Timings.TotalRounds, 1, 30),
            Int("timings.winsNeeded", "Побед до (0 — по режиму)", "Матч",
                config.Timings.WinsNeeded, 0, 30),
            Bool("admin.ban.enabled", "Баны включены", "Баны",
                config.Admin.Ban.Enabled),
            Bool("admin.ban.adminOnly", "Баны только админам", "Баны",
                config.Admin.Ban.AdminOnly),
            Int("admin.ban.minMinutes", "Минимальный бан, минут", "Баны",
                config.Admin.Ban.MinMinutes, 1, 10080),
            Int("admin.ban.maxDays", "Максимальный бан, дней", "Баны",
                config.Admin.Ban.MaxDays, 1, 365),
            Bool("admin.ban.bindAliases", "Учить ники и IP при бане", "Баны",
                config.Admin.Ban.BindAliases),
            Bool("admin.ban.byDevice", "Банить по устройству", "Баны",
                config.Admin.Ban.ByDevice),
            Bool("admin.ban.notifyEveryone", "Объявлять баны всем", "Баны",
                config.Admin.Ban.NotifyEveryone),
            Bool("admin.ban.exposeIds", "Показывать ID в катке", "Баны",
                config.Admin.Ban.ExposeIds),
            Int("web.maxRooms", "Максимум комнат", "Панель",
                config.Web.MaxRooms, 1, 1024, restart: true),
            Int("web.emptyRoomTtlSec", "Пустая комната живёт, сек", "Панель",
                config.Web.EmptyRoomTtlSec, 30, 86400),
            Bool("debug.matchChat", "Отладка матча в чат", "Отладка",
                config.Debug.MatchChat),
            Bool("debug.health", "Строка [health]", "Отладка",
                config.Debug.Health),
            Bool("debug.verbose", "Подробный лог пакетов", "Отладка",
                config.Debug.Verbose, restart: true),
        };

        return new AdminConfigSnapshot(
            path, options, ids, config.Admin.MegaIds.OrderBy(id => id).ToList(),
            issues, lastWrite);
    }

    private static AdminConfigOption Str(
        string key, string label, string section, string? value, string? help = null) =>
        new(key, label, AdminConfigValueKind.String, value ?? "", section, Help: help);

    private static AdminConfigOption Bool(
        string key, string label, string section, bool value, bool restart = false) =>
        new(key, label, AdminConfigValueKind.Bool, value ? "true" : "false", section,
            RestartRequired: restart);

    private static AdminConfigOption Int(
        string key, string label, string section, int value, int min, int max,
        bool restart = false) =>
        new(key, label, AdminConfigValueKind.Int,
            value.ToString(System.Globalization.CultureInfo.InvariantCulture), section,
            min, max, RestartRequired: restart);

    private static AdminConfigOption Choice(
        string key, string label, string section, string value, IReadOnlyList<string> choices) =>
        new(key, label, AdminConfigValueKind.String, value, section, Choices: choices);

    /// <summary>
    /// Записать разрешённый ключ. Ключ и тип ещё раз сверяются с белым списком:
    /// запрос из панели — не повод доверять ему путь в JSON.
    /// </summary>
    private string? SetAdminConfigValue(
        string key, AdminConfigValueKind kind, string value)
    {
        // Тестовые флаги — только вручную в файле. Даже если кто-то добавит их в
        // белый список, этот запрет должен остаться.
        if (key.StartsWith("test.", StringComparison.OrdinalIgnoreCase)
            || key.Equals("test", StringComparison.OrdinalIgnoreCase))
            return "тестовые ключи меняются только вручную в config.json";

        var snapshot = BuildAdminConfig();
        var option = snapshot.Options.FirstOrDefault(o =>
            string.Equals(o.Key, key, StringComparison.Ordinal));
        if (option is null)
            return "этот ключ не разрешён в панели";
        if (option.Kind != kind)
            return "тип значения не совпадает с ключом";

        var path = key.Split('.');
        if (path.Length < 2 || path.Any(part =>
                part.Length == 0 || !part.All(ch => char.IsLetterOrDigit(ch) || ch == '_')))
            return "некорректный путь ключа";

        var scalar = kind switch
        {
            AdminConfigValueKind.Bool => ConfigMigration.ScalarKind.Bool,
            AdminConfigValueKind.Int => ConfigMigration.ScalarKind.Int,
            _ => ConfigMigration.ScalarKind.String,
        };

        if (!ConfigMigration.TrySetScalar(
                ServerConfig.LastLoadedPath, path, scalar, value, out var error))
            return error;

        return ReloadConfigFromDisk();
    }

    /// <summary>
    /// Записать <c>admin.ids</c> (без мега-админов: они остаются только в
    /// <c>admin.megaIds</c>) и сразу применить к живому серверу.
    /// </summary>
    private string? SetAdminIds(IReadOnlyList<int> ids)
    {
        var snapshot = BuildAdminConfig();
        if (ids.Count == 0 && snapshot.MegaIds.Count == 0)
            return "нельзя оставить сервер без админов";

        if (!ConfigMigration.TrySetAdminIds(
                ServerConfig.LastLoadedPath, ids, out var error))
            return error;

        return ReloadConfigFromDisk();
    }

    /// <summary>Перечитать config.json в живом игровом хосте (панель читает файл сама).</summary>
    private string? ReloadConfigFromDisk()
    {
        var host = _host;
        if (host is null)
            return "игровой сервер ещё не поднялся — настройка сохранена, но применится после старта";

        host.ReloadConfig(_ => { });
        return null;
    }

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
