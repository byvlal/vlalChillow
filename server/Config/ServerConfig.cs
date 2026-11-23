using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StandChillow.LanServer.Config;

/// <summary>Как разрешено начинать матч.</summary>
public enum MatchStartMode
{
    /// <summary>Только по команде <c>/start</c> / <c>/set start</c> (права по <c>admin</c>).</summary>
    Manual,

    /// <summary>Сам, когда в каждой команде набралось <c>auto.minPlayersPerTeam</c> игроков.</summary>
    Auto,

    /// <summary>Когда все игроки, находящиеся в катке, написали <c>/ready</c>.</summary>
    Ready,
}

/// <summary>Кто имеет право крутить серверные команды.</summary>
/// <remarks>
/// По умолчанию в списке стоит старый хеш владельца — <c>25354325</c> (ник «влал»).
/// После первого входа сервер заменит его на номер аккаунта (<c>1_000_000</c> и выше)
/// в памяти; файл не переписывается. Пустой <see cref="AdminOnlyCommands"/> +
/// <see cref="AnyoneCanStart"/> = true значит «команды доступны всем». Кто хочет
/// ограничить — вписывает номера аккаунтов в <see cref="Ids"/> и команды в
/// <see cref="AdminOnlyCommands"/>.
/// </remarks>
public sealed class AdminConfig
{
    /// <summary>
    /// Номера аккаунтов администраторов (числа из <c>/ids</c> или <c>/whoami</c>).
    /// В админских настройках только числа: список ников не читается.
    /// </summary>
    /// <remarks>
    /// <c>25354325</c> — старый хеш ника «влал»: владелец сервера админ «из коробки».
    /// Пока аккаунт ещё не создан, хеш работает как запасной ключ; при первом входе
    /// он заменяется на настоящий номер аккаунта. Явный <c>"ids": []</c> в config.json
    /// по-прежнему значит «админов нет».
    /// </remarks>
    public List<int> Ids { get; set; } = new() { 25_354_325 };

    /// <summary>Диапазон валидного номера (старый хеш тоже входит, пока не мигрировал).</summary>
    public const int MinId = 1;
    public const int MaxId = 100_000_000;

    /// <summary>Ключи внутри <c>admin</c>, которых сервер не знает (опечатки).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }

    /// <summary>
    /// Команды только для админов. По умолчанию <c>set</c> и <c>end</c>: они меняют важные
    /// параметры хоста (раунды, победу, состав команды) и обрывают идущую катку.
    /// </summary>
    /// <remarks>
    /// Запуск матча <b>специально</b> сюда не входит: <c>/mode</c>, <c>/map</c>, <c>/start</c>,
    /// <c>/play</c> остаются доступны всем — катку может начать кто угодно. Это же покрывает
    /// <see cref="AnyoneCanStart"/> для <c>/start</c> и <c>/set start</c>.
    /// </remarks>
    public List<string> AdminOnlyCommands { get; set; } = DefaultAdminOnlyCommands();

    /// <summary>Разрешить /start и /set start любому игроку (по умолчанию да).</summary>
    public bool AnyoneCanStart { get; set; } = true;

    /// <summary>Баны: срок, файл, кто может банить.</summary>
    public BanConfig Ban { get; set; } = new();

    /// <summary>Команды, закрытые админом «из коробки».</summary>
    public static List<string> DefaultAdminOnlyCommands() => new() { "set", "end" };
}

/// <summary>
/// Баны. По умолчанию <c>/ban</c>, <c>/unban</c> и <c>/bans</c> — только для админов
/// (независимо от <see cref="AdminConfig.AdminOnlyCommands"/>), админа забанить нельзя.
/// </summary>
/// <remarks>
/// Срок ограничен с двух сторон: минимум — чтобы «бан на секунду» не был способом
/// потроллить, максимум — потому что IP на LAN выдаёт DHCP и вечный бан адреса
/// рано или поздно достанется ни в чём не виноватому телефону.
/// </remarks>
public sealed class BanConfig
{
    /// <summary>Выключатель на весь модуль (команды отвечают «баны выключены»).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>true — /ban, /unban, /bans только админам (по умолчанию).</summary>
    public bool AdminOnly { get; set; } = true;

    /// <summary>Имя файла банов рядом с config.json (или полный путь).</summary>
    public string File { get; set; } = "banlist.json";

    /// <summary>Минимальный срок бана, минут.</summary>
    public int MinMinutes { get; set; } = 10;

    /// <summary>Максимальный срок бана, дней (14 — как в ТЗ).</summary>
    public int MaxDays { get; set; } = 14;

    /// <summary>
    /// Учить новые ники/IP/id при жёстком совпадении: userId или IP. Вход под
    /// забаненным ником с новыми IP и userId ничего не учит — так невинный тёзка
    /// не наследует чужой бан.
    /// </summary>
    public bool BindAliases { get; set; } = true;

    /// <summary>
    /// Банить по стабильному ключу 2.07-установки (<c>LB&lt;md5&gt;</c>): смена ника и VPN
    /// не помогают. Устройство учится при бане и при совпадении по IP, но не по нику.
    /// </summary>
    public bool ByDevice { get; set; } = true;

    /// <summary>Сообщать всем в лобби, что кого-то забанили.</summary>
    public bool NotifyEveryone { get; set; } = true;

    /// <summary>Как часто обновлять «Разбан через: …» в теневом лобби, секунд.</summary>
    public int ShadowRefreshSec { get; set; } = 60;

    /// <summary>Как часто проверять, у кого вышел срок, секунд.</summary>
    public int SweepSec { get; set; } = 5;

    /// <summary>
    /// Как часто матч (7777) перепроверяет сидящих акторов на бан, секунд. Два
    /// секунды — требование владельца: забанили во время игры — выкинуть, а не ждать
    /// выхода. Проверка дешёвая (несколько словарных lookup на пира).
    /// </summary>
    public int MatchSweepSec { get; set; } = 2;

    /// <summary>
    /// Пауза между повторными полными уведомлениями забаненному (на каждую его
    /// команду), секунд. Чтобы спам командами не превратился в спам сообщениями.
    /// </summary>
    public int NoticeCooldownSec { get; set; } = 2;

    /// <summary>
    /// Отдавать в матч серверный id вместо клиентского <c>uid</c> («Offline»): тогда в игре
    /// в списке игроков видно число, которое принимает <c>/ban</c>. Выключить — клиент
    /// снова покажет свою заглушку.
    /// </summary>
    public bool ExposeIds { get; set; } = true;
}

/// <summary>Настройки автостарта (<c>matchStart.mode = "auto"</c>).</summary>
public sealed class AutoStartConfig
{
    /// <summary>Минимум игроков в каждой команде для автозапуска.</summary>
    public int MinPlayersPerTeam { get; set; } = 1;
}

/// <summary>Настройки старта по готовности (<c>matchStart.mode = "ready"</c>).</summary>
public sealed class ReadyStartConfig
{
    /// <summary>Писать в чат «ник готов (N/M)».</summary>
    public bool AnnounceInChat { get; set; } = true;

    /// <summary>Снимать голос игрока, вышедшего из катки.</summary>
    public bool ResetOnLeave { get; set; } = true;

    /// <summary>Разрешить /unready.</summary>
    public bool AllowUnready { get; set; } = true;
}

/// <summary>Как начинается матч.</summary>
public sealed class MatchStartConfig
{
    /// <summary>manual | auto | ready. По умолчанию auto — матч стартует сам.</summary>
    public string Mode { get; set; } = "auto";

    public AutoStartConfig Auto { get; set; } = new();

    public ReadyStartConfig Ready { get; set; } = new();
}

/// <summary>
/// Блок «запуск»: то, что обычно пишут в командной строке.
/// Читает <b>только выделенный сервер</b>. Режим <c>--client</c> (проба) этот
/// блок не читает — у пробы свои аргументы и свои настройки.
/// </summary>
public sealed class LaunchConfig
{
    /// <summary>
    /// Доп. аргументы, как будто дописаны после exe:
    /// <c>--verbose</c>, <c>--no-dashboard</c>, <c>--debug-chat</c>.
    /// <c>--client</c> запрещён: конфиг не переключает сервер в пробу.
    /// </summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Дашборд: true/false (null — авто по терминалу).</summary>
    public bool? Dashboard { get; set; }

    /// <summary>Название лобби (null — «влал хостит рялна»).</summary>
    public string? Title { get; set; }

    /// <summary>Подробный лог пакетов.</summary>
    public bool Verbose { get; set; }
}

/// <summary>Имена, порты и лимиты лобби.</summary>
public sealed class ServerSectionConfig
{
    /// <summary>Приветствие в чат при входе ("" — не слать).</summary>
    public string? WelcomeMessage { get; set; }

    /// <summary>Максимум игроков в лобби.</summary>
    public int MaxMembers { get; set; } = 16;

    /// <summary>Порт лобби (UDP).</summary>
    public int LobbyPort { get; set; } = 7778;
}

/// <summary>Сеть: где слушать и пускать ли VPN/виртуальные адаптеры.</summary>
public sealed class NetworkConfig
{
    /// <summary>
    /// На каком адресе открывать сервер. <c>0.0.0.0</c> — все интерфейсы сразу
    /// (Wi‑Fi, Ethernet, VPN, Hamachi, Radmin). Пусто — авто-выбор LAN-адреса.
    /// </summary>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>
    /// true — не трогать VPN/виртуальные адаптеры (только Wi‑Fi/Ethernet).
    /// false — слушать везде: VPN, Hamachi, Radmin, ZeroTier и т.п.
    /// </summary>
    public bool IgnoreVpn { get; set; } = true;

    /// <summary>
    /// Какой IP писать телефонам в op9 (адрес матча). Пусто — авто по LAN.
    /// Нужен, когда играете через Hamachi/Radmin и авто берёт не тот адрес.
    /// </summary>
    public string? AdvertiseIp { get; set; }
}

/// <summary>
/// Правила ников: что пускаем в лобби и в катку.
/// </summary>
/// <remarks>
/// Ник уходит в чат текстом, а в катке становится именем актора. Клиент рисует его как
/// rich-text, поэтому «&lt;color=red&gt;miro» — это разметка, а не ник. Проверка одна на
/// оба входа: лобби 7778 и катка 7777 (клиент присылает ник дважды, и модифицированный
/// клиент может обойти лобби).
/// </remarks>
public sealed class NamesConfig
{
    /// <summary>
    /// false — проверка выключена целиком. Аварийный выключатель: если политика кому-то
    /// перекроет вход, владелец снимает её правкой конфига, а не пересборкой.
    /// </summary>
    public bool Enforce { get; set; } = true;

    /// <summary>Стандартная длина ника: столько же, сколько в шаблоне конфига.</summary>
    public const int DefaultMaxLength = 20;

    /// <summary>Максимум символов в нике (1..32).</summary>
    public int MaxLength { get; set; } = DefaultMaxLength;

    /// <summary>Сколько секунд нарушитель видит подсказку, прежде чем его кикнет (1..300).</summary>
    public int KickDelaySec { get; set; } = 10;

    /// <summary>Ключи внутри <c>names</c>, которых сервер не знает (опечатки).</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }
}

/// <summary>
/// Веб-панель: список комнат и одна кнопка «создать».
/// </summary>
/// <remarks>
/// <para>
/// Панель живёт <b>в том же исполняемом файле</b>, что и сервер, и поднимается раньше
/// игровых сокетов: если 7777 занят (старый процесс, чужой сервер), страница всё равно
/// открывается и честно говорит, что игровой порт не поднялся. Отдельного бинарника,
/// отдельного порта для игр и отдельного конфига нет.
/// </para>
/// <para>
/// Порт по умолчанию — 8080 и слушает только <c>127.0.0.1</c>: наружу его отдаёт nginx
/// по подпути. Менять <see cref="BindAddress"/> на <c>0.0.0.0</c> нужно только для
/// прямого доступа по локальной сети без прокси.
/// </para>
/// </remarks>
public sealed class WebConfig
{
    /// <summary>Поднимать ли панель вообще.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Где слушать. По умолчанию — все интерфейсы.
    /// </summary>
    /// <remarks>
    /// Loopback не годится как дефолт: обратный прокси на другой машине
    /// (nginx → <c>192.168.x.x:8080</c>) до неё не достучится, и сайт отдаст 502,
    /// хотя сервер жив. Порт панели — не игровой, доступ к нему даёт только тот,
    /// кто уже в сети; отдельного пароля у панели нет и не будет.
    /// </remarks>
    public string BindAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP-порт панели (не игровой; игровые порты не меняются никогда).</summary>
    public int Port { get; set; } = 8080;

    /// <summary>Сколько комнат разрешено держать одновременно.</summary>
    public int MaxRooms { get; set; } = 64;

    /// <summary>
    /// Через сколько секунд пустая комната удаляется и её номер освобождается.
    /// </summary>
    /// <remarks>
    /// Отсчёт идёт с момента, когда из комнаты ушёл последний игрок, а не с её создания.
    /// Три минуты — компромисс: за это время человек успевает переподключиться после
    /// обрыва мобильной сети, и при этом брошенные катки не копятся.
    /// </remarks>
    public int EmptyRoomTtlSec { get; set; } = 180;

    /// <summary>
    /// Публичный адрес сайта для ответа «создать можно здесь».
    /// Пусто — определить самому (внешний IP, затем LAN).
    /// </summary>
    /// <remarks>
    /// Настоящий домен сюда вписывают в <c>config.json</c> на машине владельца;
    /// в коде и репозитории его нет — это осознанно (CONTRIBUTING.md).
    /// </remarks>
    public string PublicBaseUrl { get; set; } = "";

    /// <summary>
    /// Подпуть, если панель стоит за обратным прокси (<c>/vlalchillow</c>).
    /// Пусто — панель в корне.
    /// </summary>
    /// <remarks>
    /// Отсюда же берётся путь куки сессии: за прокси кука обязана совпадать с подпутём,
    /// иначе браузер её не пришлёт и авторизация будет слетать на каждом запросе (#51).
    /// </remarks>
    public string BasePath { get; set; } = "";

    /// <summary>
    /// Ставить ли куке сессии флаг <c>Secure</c> (только https).
    /// </summary>
    /// <remarks>
    /// По умолчанию выключено: по LAN панель открывают по http, и с этим флагом браузер
    /// куку не сохранит — человек будет вечно «не авторизован». Включать, когда перед
    /// панелью есть https (nginx с сертификатом).
    /// </remarks>
    public bool SecureCookies { get; set; }

    /// <summary>
    /// IP/CIDR обратных прокси, которым доверяем <c>X-Forwarded-For</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Пусто — доверяем только loopback. Это важно для мега-админа: если верить
    /// заголовку от любого LAN-клиента, он назовётся IP из <c>megaIps</c> и получит
    /// полный доступ. Прокси на другой машине впиши сюда явно
    /// (<c>"192.168.1.10"</c> или <c>"192.168.1.0/24"</c>).
    /// </para>
    /// </remarks>
    public List<string> TrustedProxies { get; set; } = new();

    /// <summary>
    /// Сколько секунд живёт код входа из лобби (по умолчанию 30 минут).
    /// </summary>
    /// <remarks>
    /// Новый код выдаётся при следующем входе в лобби или по <c>/code</c>; сессия сайта
    /// от ротации кода не зависит (#51).
    /// </remarks>
    public int CodeTtlSec { get; set; } = 1800;

    /// <summary>Лимиты запросов (флуд-защита без паролей и куки).</summary>
    public WebLimitsConfig Limits { get; set; } = new();
}

/// <summary>
/// Лимиты панели. Значения подобраны так, чтобы человек с одним браузером их не замечал,
/// а скрипт — упирался сразу.
/// </summary>
public sealed class WebLimitsConfig
{
    /// <summary>Запросов в секунду с одного адреса; больше — 429 с Retry-After.</summary>
    /// <remarks>
    /// Бакет — на адрес, а не на человека: за одним NAT (мобильный оператор, клуб,
    /// общежитие) сидят несколько игроков сразу, и каждому нужно своё обновление раз в
    /// две секунды. 30 — это примерно дюжина человек за одним адресом с запасом на
    /// одновременную загрузку страниц (она открывает четыре соединения сразу); одиночный
    /// гость до него не дотянется (два запроса в секунду).
    /// </remarks>
    public int RequestsPerSecond { get; set; } = 30;

    /// <summary>После стольких отказов за окно адрес получает паузу.</summary>
    public int RefusalsBeforeBlock { get; set; } = 5;

    /// <summary>Длина окна подсчёта отказов, секунды.</summary>
    public int RefusalWindowSec { get; set; } = 10;

    /// <summary>Сколько длится пауза, секунды.</summary>
    public int BlockDurationSec { get; set; } = 60;

    /// <summary>Максимальный размер заголовков запроса.</summary>
    public int MaxHeaderBytes { get; set; } = 8 * 1024;

    /// <summary>Максимальный размер тела запроса.</summary>
    public int MaxBodyBytes { get; set; } = 4 * 1024;

    /// <summary>Сколько секунд ждём запрос целиком.</summary>
    public int RequestTimeoutSec { get; set; } = 5;

    /// <summary>
    /// Одновременных соединений с одного клиента. За прокси клиент — это реальный
    /// адрес из <c>X-Forwarded-For</c> (см. <see cref="Web.WebClientAddress"/>),
    /// а не адрес машины с прокси, иначе кап делили бы все посетители сайта.
    /// </summary>
    /// <remarks>
    /// Значение не может быть меньше, чем число запросов одной загрузки страницы:
    /// страница открывает каталог, профиль, комнаты и сводку параллельно, и кап в 2
    /// отдавал 429 уже первому гостю. 8 — это две вкладки разом плюс запас.
    /// </remarks>
    public int MaxConnectionsPerIp { get; set; } = 8;

    /// <summary>
    /// Одновременных соединений на весь сайт — предохранитель, чтобы наплыв людей
    /// (или скрипт) не съел память панели. Отдельно от адресного капа, потому что
    /// адрес клиента виден только после чтения заголовков.
    /// </summary>
    public int MaxConnectionsTotal { get; set; } = 256;
}

/// <summary>Длительности фаз матча в секундах.</summary>
public sealed class TimingsConfig
{
    /// <summary>WarmUp C2=21.</summary>
    public int Warmup { get; set; } = 3;

    /// <summary>PreStart C2=22.</summary>
    public int PreStart { get; set; } = 3;

    /// <summary>Prep C2=31 (закупка).</summary>
    public int Prep { get; set; } = 10;

    /// <summary>Длина раунда, сек.</summary>
    public int RoundTime { get; set; } = 109;

    /// <summary>Пауза после RoundEnd.</summary>
    public int RoundEndPause { get; set; } = 6;

    /// <summary>Фитиль бомбы.</summary>
    public int BombFuse { get; set; } = 40;

    /// <summary>MR-N: раундов в серии.</summary>
    public int TotalRounds { get; set; } = 8;

    /// <summary>Победа до N раундов (0 — дефолт режима).</summary>
    public int WinsNeeded { get; set; }

    /// <summary>Стартовые деньги.</summary>
    public int RoundStartMoney { get; set; } = 800;
}

/// <summary>Горячие ручки хоста: темп цикла, бэкап, пул пакетов.</summary>
public sealed class TuningConfig
{
    /// <summary>Период игрового цикла хоста, мс.</summary>
    public int TickMs { get; set; } = 33;

    /// <summary>Частота рассылки поз, мс.</summary>
    public int StateIntervalMs { get; set; } = 50;

    /// <summary>Порог очереди надёжных пакетов (дропаем только косметику).</summary>
    public int ReliableQueueSoftLimit { get; set; } = 256;

    /// <summary>Пул пакетов LiteNetLib.</summary>
    public int PacketPoolSize { get; set; } = 4096;

    /// <summary>Таймаут отключения пира, мс.</summary>
    public int DisconnectTimeoutMs { get; set; } = 10000;

    /// <summary>UpdateTime LiteNetLib, мс.</summary>
    public int NetUpdateTimeMs { get; set; } = 15;

    /// <summary>PingInterval LiteNetLib, мс.</summary>
    public int PingIntervalMs { get; set; } = 1000;

    /// <summary>Интервал строки [health], сек (0 — выключить).</summary>
    public int HealthIntervalSec { get; set; } = 10;
}

/// <summary>
/// Отладка. Действует <b>только на выделенном сервере</b>:
/// режим <c>--client</c> (проба) этот блок не читает и не может его изменить.
/// </summary>
public sealed class DebugConfig
{
    /// <summary>Подробный per-packet лог.</summary>
    public bool Verbose { get; set; }

    /// <summary>Писать capture-файлы.</summary>
    public bool Captures { get; set; }

    /// <summary>Каталог capture-файлов (null — <c>captures</c> рядом с exe).</summary>
    public string? CapturesDir { get; set; }

    /// <summary>Максимум capture-файлов за запуск (0 — без лимита).</summary>
    public int CapturesMaxFiles { get; set; } = 4000;

    /// <summary>Максимум мегабайт capture-файлов (0 — без лимита).</summary>
    public int CapturesMaxBytesMb { get; set; } = 64;

    /// <summary>Отладочные сообщения хоста в чат.</summary>
    public bool MatchChat { get; set; }

    /// <summary>Строка [health].</summary>
    public bool Health { get; set; } = true;

    /// <summary>Логировать каждый принятый пакет (очень шумно).</summary>
    public bool LogPackets { get; set; }

    /// <summary>Логировать каждый discovery-probe.</summary>
    public bool LogDiscovery { get; set; }
}

/// <summary>
/// Корневой конфиг сервера: <c>config.json</c> рядом с бинарником.
/// Нет файла — создаётся шаблон, работа продолжается на дефолтах.
/// </summary>
public sealed class ServerConfig
{
    public const string FileName = "config.json";

    /// <summary>Запуск: аргументы, дашборд, название.</summary>
    public LaunchConfig Launch { get; set; } = new();

    /// <summary>Лобби: приветствие, лимит игроков.</summary>
    public ServerSectionConfig Server { get; set; } = new();

    /// <summary>Сеть: адрес прослушивания, VPN-адаптеры, адрес в op9.</summary>
    public NetworkConfig Network { get; set; } = new();

    /// <summary>Ники: белый список символов, длина, кик нарушителя.</summary>
    public NamesConfig Names { get; set; } = new();

    public AdminConfig Admin { get; set; } = new();

    /// <summary>
    /// Ключи, которых сервер не знает: опечатка в имени («admins» вместо admin.ids) —
    /// самая частая причина «настроил, а сервер не видит».
    /// </summary>
    /// <remarks>
    /// Заполняет <c>System.Text.Json</c> через <see cref="JsonExtensionDataAttribute"/>: всё,
    /// что не совпало ни с одним свойством, попадает сюда, и <see cref="Normalize"/> пишет об
    /// этом в <see cref="Issues"/>. Раньше такие ключи молча игнорировались.
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }

    public MatchStartConfig MatchStart { get; set; } = new();

    public TimingsConfig Timings { get; set; } = new();

    public TuningConfig Tuning { get; set; } = new();

    public DebugConfig Debug { get; set; } = new();

    /// <summary>Веб-панель комнат (тот же исполняемый файл, отдельный TCP-порт).</summary>
    public WebConfig Web { get; set; } = new();

    /// <summary>
    /// Замечание к конфигу: какое значение недопустимо и что применилось вместо него.
    /// </summary>
    /// <remarks>
    /// Раньше «битое» значение молча заменялось стандартным, и понять, почему сервер
    /// играет не так, как написано в файле, было невозможно. Теперь каждое такое
    /// исправление попадает в лог одной строкой.
    /// </remarks>
    public readonly record struct ConfigIssue(
        string Key,
        string Detail,
        string Applied,
        bool RequiresRestart = false);

    private readonly List<ConfigIssue> _issues = new();

    /// <summary>Что пришлось исправить в этом конфиге (пусто — всё как написано).</summary>
    /// <remarks>
    /// <see cref="JsonIgnoreAttribute"/> обязателен: без него <c>System.Text.Json</c> при
    /// десериализации попытается заполнить это свойство без сеттера и бросит исключение —
    /// ровно так «конфиг никогда не читается» и выглядело.
    /// </remarks>
    [JsonIgnore]
    public IReadOnlyList<ConfigIssue> Issues => _issues;

    /// <summary>Откуда конфиг загружен в последний раз.</summary>
    public static string LastLoadedPath { get; private set; } = DefaultPath;

    /// <summary>Последний загруженный конфиг.</summary>
    public static ServerConfig Current { get; private set; } = new();

    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, FileName);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        // В шаблоне есть // — их надо пропускать, иначе первый запуск = дефолты.
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Читает конфиг; нет файла — пишет шаблон. Никогда не бросает.</summary>
    public static ServerConfig Load(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? DefaultPath : path;
        LastLoadedPath = target;

        if (!File.Exists(target))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target) ?? ".");
                File.WriteAllText(target, Template);
                Console.WriteLine($"[config] создан шаблон: {target}");
                Console.WriteLine(
                    "[config] команды открыты всем; ограничить — admin.ids + " +
                    "admin.adminOnlyCommands");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[config] не смог создать '{target}': {ex.Message}");
            }

            // Только что созданный файл = новый шаблон; Normalize нужен, чтобы
            // действующие значения совпадали с тем, что написано на диске.
            Current = new ServerConfig();
            Current.Normalize();
            return Current;
        }

        if (TryLoad(target, out var cfg, out var error))
        {
            Current = cfg;
            Console.WriteLine($"[config] загружен: {target}");
            return cfg;
        }

        Console.WriteLine($"[config] ошибка чтения '{target}': {error}");
        Console.WriteLine("[config] работаю на дефолтах — файл НЕ изменён");
        Current = new ServerConfig();
        Current.Normalize();
        return Current;
    }

    /// <summary>
    /// Пытается прочитать существующий файл. <c>false</c> — файла нет, он недоступен или
    /// это не валидный JSON; в этом случае <paramref name="config"/> не используется.
    /// </summary>
    /// <remarks>
    /// Отдельно от <see cref="Load"/> нужно живому перечитыванию: если файл сохранился
    /// «полузаписанным», сервер обязан остаться на прежних настройках, а не заменить
    /// весь конфиг дефолтами.
    /// </remarks>
    public static bool TryLoad(string path, out ServerConfig config, out string error)
    {
        config = new ServerConfig();
        error = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                error = "файл не найден";
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ServerConfig>(
                File.ReadAllText(path), JsonOptions);
            if (parsed is null)
            {
                error = "файл пустой";
                return false;
            }

            parsed.Normalize();
            parsed.LogIssues();
            config = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Проверяет значения и заменяет недопустимые стандартными.
    /// Каждая замена попадает в <see cref="Issues"/> — молча ничего не исправляется.
    /// </summary>
    public void Normalize()
    {
        _issues.Clear();

        Launch ??= new LaunchConfig();
        Launch.Args ??= new List<string>();
        Server ??= new ServerSectionConfig();
        Network ??= new NetworkConfig();
        Admin ??= new AdminConfig();
        Admin.Ids ??= new List<int>();
        Admin.AdminOnlyCommands ??= new List<string>();

        // Админы — только по серверному id. Мусор (0, отрицательные, числа вне
        // диапазона) не применяется молча: каждое значение видно в логе конфига.
        var cleanIds = new List<int>();
        foreach (var id in Admin.Ids)
        {
            if (id < AdminConfig.MinId || id > AdminConfig.MaxId)
            {
                Report("admin.ids", id.ToString(), "пропущен",
                    $"вне диапазона {AdminConfig.MinId}..{AdminConfig.MaxId}");
                continue;
            }
            if (!cleanIds.Contains(id))
                cleanIds.Add(id);
        }
        Admin.Ids = cleanIds;
        Admin.Ban ??= new BanConfig();
        MatchStart ??= new MatchStartConfig();
        MatchStart.Auto ??= new AutoStartConfig();
        MatchStart.Ready ??= new ReadyStartConfig();
        Timings ??= new TimingsConfig();
        Tuning ??= new TuningConfig();
        Debug ??= new DebugConfig();
        Web ??= new WebConfig();
        Web.Limits ??= new WebLimitsConfig();
        Names ??= new NamesConfig();

        // Опечатки в именах ключей — до всего остального: из-за них «настроил, а не работает».
        ReportUnknownKeys();

        // Прова в пробу конфигом запрещена — иначе весь блок debug «утечёт» в пробу.
        // Регистр не важен: --client / -c / ConnectAsClient / --connectasclient.
        Launch.Args.RemoveAll(IsProbeFlag);

        NormalizeWeb();

        MatchStart.Auto.MinPlayersPerTeam = Check(
            MatchStart.Auto.MinPlayersPerTeam, 1, 16, 1, "matchStart.auto.minPlayersPerTeam");

        MigrateLegacyAdminGate();

        // Баны: 10 минут — 14 дней по умолчанию; границы правятся в конфиге.
        Admin.Ban.MinMinutes = Check(
            Admin.Ban.MinMinutes, 1, 1440, 10, "admin.ban.minMinutes");
        Admin.Ban.MaxDays = Check(
            Admin.Ban.MaxDays, 1, 365, 14, "admin.ban.maxDays");
        if (Admin.Ban.MaxDays * 1440 < Admin.Ban.MinMinutes)
        {
            var fixedDays = (Admin.Ban.MinMinutes + 1439) / 1440;
            Report("admin.ban.maxDays", Admin.Ban.MaxDays.ToString(), fixedDays.ToString(),
                $"меньше минимального срока ({Admin.Ban.MinMinutes} мин) — поднял до него");
            Admin.Ban.MaxDays = fixedDays;
        }
        Admin.Ban.ShadowRefreshSec = Check(
            Admin.Ban.ShadowRefreshSec, 10, 600, 60, "admin.ban.shadowRefreshSec");
        Admin.Ban.SweepSec = Check(
            Admin.Ban.SweepSec, 1, 300, 5, "admin.ban.sweepSec");
        Admin.Ban.MatchSweepSec = Check(
            Admin.Ban.MatchSweepSec, 1, 30, 2, "admin.ban.matchSweepSec");
        Admin.Ban.NoticeCooldownSec = Check(
            Admin.Ban.NoticeCooldownSec, 1, 60, 2, "admin.ban.noticeCooldownSec");
        Admin.Ban.File = string.IsNullOrWhiteSpace(Admin.Ban.File)
            ? "banlist.json"
            : Admin.Ban.File.Trim();

        // Ники: 20 символов по умолчанию (в нике видно и чат, и табло катки),
        // 10 секунд на подсказку. Обе границы — чтобы опечатка в конфиге не
        // выключила проверку молча.
        Names.MaxLength = Check(Names.MaxLength, 1, 32, NamesConfig.DefaultMaxLength, "names.maxLength");
        Names.KickDelaySec = Check(Names.KickDelaySec, 1, 300, 10, "names.kickDelaySec");

        // Пустая строка — «как в дефолте» (auto), а не тихий откат в manual:
        // иначе правка конфига руками молча выключала автостарт.
        if (!TryParseMode(MatchStart.Mode, out var startMode))
        {
            Report("matchStart.mode", MatchStart.Mode, "auto",
                "не manual/auto/ready — старт по игрокам");
            MatchStart.Mode = "auto";
        }
        else
        {
            MatchStart.Mode = startMode switch
            {
                MatchStartMode.Auto => "auto",
                MatchStartMode.Ready => "ready",
                _ => "manual",
            };
        }

        // Лобби: порт меняется только перезапуском — слушатель создаётся один раз.
        Server.MaxMembers = Check(Server.MaxMembers, 1, 255, 16, "server.maxMembers");
        Server.LobbyPort = CheckRestart(Server.LobbyPort, 1, 65535, 7778, "server.lobbyPort");

        // Пустой bindAddress — «как раньше»: 0.0.0.0 (все интерфейсы).
        var bind = (Network.BindAddress ?? string.Empty).Trim();
        if (bind.Length == 0)
            Network.BindAddress = "0.0.0.0";
        else if (!IPAddress.TryParse(bind, out _))
        {
            Report("network.bindAddress", Network.BindAddress, "0.0.0.0",
                "не IP-адрес — слушаю все интерфейсы", requiresRestart: true);
            Network.BindAddress = "0.0.0.0";
        }
        else
            Network.BindAddress = bind;

        if (!string.IsNullOrWhiteSpace(Network.AdvertiseIp))
        {
            var advertise = Network.AdvertiseIp.Trim();
            if (IPAddress.TryParse(advertise, out _))
            {
                Network.AdvertiseIp = advertise;
            }
            else
            {
                Report("network.advertiseIp", Network.AdvertiseIp, "(авто)",
                    "не IP-адрес — адрес матча выбирается автоматически", requiresRestart: true);
                Network.AdvertiseIp = null;
            }
        }

        Timings.Warmup = Check(Timings.Warmup, 0, 120, 3, "timings.warmup");
        Timings.PreStart = Check(Timings.PreStart, 1, 120, 3, "timings.preStart");
        Timings.Prep = Check(Timings.Prep, 1, 120, 10, "timings.prep");
        Timings.RoundTime = Check(Timings.RoundTime, 10, 600, 109, "timings.roundTime");
        Timings.RoundEndPause = Check(Timings.RoundEndPause, 1, 30, 6, "timings.roundEndPause");
        Timings.BombFuse = Check(Timings.BombFuse, 5, 120, 40, "timings.bombFuse");
        Timings.TotalRounds = Check(Timings.TotalRounds, 1, 64, 8, "timings.totalRounds");
        Timings.WinsNeeded = Check(Timings.WinsNeeded, 0, 64, 0, "timings.winsNeeded");
        Timings.RoundStartMoney = Check(Timings.RoundStartMoney, 0, 16000, 800, "timings.roundStartMoney");

        Tuning.TickMs = Check(Tuning.TickMs, 5, 200, 33, "tuning.tickMs");
        Tuning.StateIntervalMs = Check(Tuning.StateIntervalMs, 10, 500, 50, "tuning.stateIntervalMs");
        Tuning.ReliableQueueSoftLimit = Check(
            Tuning.ReliableQueueSoftLimit, 16, 4096, 256, "tuning.reliableQueueSoftLimit");
        // Пул пакетов и таймауты зашиты в LiteNetLib при создании слушателя — только рестарт.
        Tuning.PacketPoolSize = CheckRestart(
            Tuning.PacketPoolSize, 64, 65536, 4096, "tuning.packetPoolSize");
        Tuning.DisconnectTimeoutMs = CheckRestart(
            Tuning.DisconnectTimeoutMs, 2000, 60000, 10000, "tuning.disconnectTimeoutMs");
        Tuning.NetUpdateTimeMs = CheckRestart(
            Tuning.NetUpdateTimeMs, 1, 100, 15, "tuning.netUpdateTimeMs");
        Tuning.PingIntervalMs = CheckRestart(
            Tuning.PingIntervalMs, 100, 10000, 1000, "tuning.pingIntervalMs");
        Tuning.HealthIntervalSec = Check(
            Tuning.HealthIntervalSec, 0, 3600, 10, "tuning.healthIntervalSec");

        Debug.CapturesMaxFiles = Check(
            Debug.CapturesMaxFiles, 0, 1_000_000, 4000, "debug.capturesMaxFiles");
        Debug.CapturesMaxBytesMb = Check(
            Debug.CapturesMaxBytesMb, 0, 65536, 64, "debug.capturesMaxBytesMb");
    }

    /// <summary>Разбирает JSON конфига тем же парсером, что и <see cref="Load"/>.</summary>
    /// <remarks>Нужен тестам: проверяют и шаблон, и «битые» файлы без обращения к диску.</remarks>
    public static ServerConfig ParseForTests(string json) =>
        JsonSerializer.Deserialize<ServerConfig>(json, JsonOptions) ?? new ServerConfig();

    /// <summary>
    /// Печатает замечания к конфигу. Ничего не «проглатывается»: оператор видит и
    /// ключ, и что там было написано, и что применилось вместо этого.
    /// </summary>
    public void LogIssues(Action<string>? log = null)
    {
        log ??= Console.WriteLine;

        if (_issues.Count == 0)
        {
            log("[config] все значения корректны");
            return;
        }

        log($"[config] некорректных значений: {_issues.Count} — применены стандартные");
        foreach (var issue in _issues)
        {
            var restart = issue.RequiresRestart ? " (нужен перезапуск)" : "";
            log($"[config]   {issue.Key}: {issue.Detail} → {issue.Applied}{restart}");
        }
    }

    /// <summary>
    /// Опечатки в именах ключей. «Настроил админов, а сервер их не видит» почти всегда
    /// значит, что ключ назван не так: <c>admins</c> в корне вместо <c>admin.ids</c>.
    /// </summary>
    private void ReportUnknownKeys()
    {
        if (UnknownKeys is { Count: > 0 })
        {
            foreach (var key in UnknownKeys.Keys)
            {
                var likelyAdmin = key.Contains("admin", StringComparison.OrdinalIgnoreCase);
                _issues.Add(new ConfigIssue(
                    key,
                    likelyAdmin
                        ? "неизвестный ключ в корне; админские id читаются из admin.ids"
                        : "неизвестный ключ в корне — сервер его не читает",
                    "проигнорирован"));
            }
        }

        if (Admin?.UnknownKeys is { Count: > 0 })
        {
            foreach (var key in Admin.UnknownKeys.Keys)
                _issues.Add(new ConfigIssue(
                    "admin." + key,
                    "неизвестный ключ внутри admin — сервер его не читает",
                    "проигнорирован"));
        }

        if (Names?.UnknownKeys is { Count: > 0 })
        {
            foreach (var key in Names.UnknownKeys.Keys)
                _issues.Add(new ConfigIssue(
                    "names." + key,
                    "неизвестный ключ внутри names — сервер его не читает",
                    "проигнорирован"));
        }
    }

    /// <summary>
    /// Проверяет число: вне диапазона → стандартное значение + запись в <see cref="Issues"/>.
    /// </summary>
    private int Check(int value, int min, int max, int fallback, string key)
    {
        if (value >= min && value <= max)
            return value;

        _issues.Add(new ConfigIssue(
            key,
            $"{value} вне диапазона {min}..{max}",
            fallback.ToString()));
        return fallback;
    }

    /// <summary>Как <see cref="Check"/>, но значение применяется только после перезапуска.</summary>
    private int CheckRestart(int value, int min, int max, int fallback, string key)
    {
        if (value >= min && value <= max)
            return value;

        _issues.Add(new ConfigIssue(
            key,
            $"{value} вне диапазона {min}..{max}",
            fallback.ToString(),
            RequiresRestart: true));
        return fallback;
    }

    /// <summary>Записывает замечание по конфигу (значение уже заменено вызывающим).</summary>
    private void Report(string key, string? raw, string applied, string detail,
        bool requiresRestart = false)
    {
        var shown = string.IsNullOrWhiteSpace(raw) ? "(пусто)" : $"'{raw}'";
        _issues.Add(new ConfigIssue(key, $"{shown} {detail}", applied, requiresRestart));
    }

    /// <summary>
    /// Старые версии писали в <c>config.json</c> «админ-гейт по умолчанию»: список админов
    /// пустой, а <c>adminOnlyCommands</c> — полный набор команд с <c>anyoneCanStart: false</c>.
    /// Такая комбинация блокирует команды <b>у всех сразу</b> (админов нет — значит нет и того,
    /// кому они разрешены), поэтому это не намеренная настройка, а дефект прошлого дефолта.
    /// </summary>
    /// <remarks>
    /// Важно: <see cref="Load"/> не перезаписывает существующий файл, поэтому без этой
    /// миграции у того, кто уже запускал сервер, старые значения остались бы навсегда —
    /// и «поменять дефолты» ничего бы ему не дало. Пользовательские настройки не затираются:
    /// миграция срабатывает только на точном старом наборе и только когда админов нет.
    /// </remarks>
    /// <summary>
    /// Панель: границы портов и лимитов. Значения вне диапазона не «молча применяются»,
    /// а подтягиваются с записью в <see cref="Issues"/> — иначе панель «не открывается»,
    /// и непонятно почему.
    /// </summary>
    private void NormalizeWeb()
    {
        Web.Port = Check(Web.Port, 1, 65535, 8080, "web.port");
        Web.MaxRooms = Check(Web.MaxRooms, 1, 1024, 64, "web.maxRooms");
        Web.EmptyRoomTtlSec = Check(Web.EmptyRoomTtlSec, 30, 86400, 180, "web.emptyRoomTtlSec");

        // Код входа с сайта: минуты — мало (человек ещё в лобби), сутки — много.
        Web.CodeTtlSec = Check(Web.CodeTtlSec, 60, 86400, 1800, "web.codeTtlSec");

        Web.BindAddress = string.IsNullOrWhiteSpace(Web.BindAddress)
            ? "0.0.0.0"
            : Web.BindAddress.Trim();

        // Подпуть за прокси: «/vlalchillow/», «vlalchillow» и « vlalchillow » — одно и то же.
        // Храним без слешей по краям; слеш добавляют роутер и кука.
        Web.BasePath = (Web.BasePath ?? string.Empty).Trim().Trim('/');

        // Пустая строка — «определи сам», а не ошибка.
        Web.PublicBaseUrl = (Web.PublicBaseUrl ?? string.Empty).Trim();

        Web.Limits.RequestsPerSecond = Check(
            Web.Limits.RequestsPerSecond, 1, 1000, 30, "web.limits.requestsPerSecond");
        Web.Limits.RefusalsBeforeBlock = Check(
            Web.Limits.RefusalsBeforeBlock, 1, 1000, 5, "web.limits.refusalsBeforeBlock");
        Web.Limits.RefusalWindowSec = Check(
            Web.Limits.RefusalWindowSec, 1, 3600, 10, "web.limits.refusalWindowSec");
        Web.Limits.BlockDurationSec = Check(
            Web.Limits.BlockDurationSec, 1, 86400, 60, "web.limits.blockDurationSec");
        Web.Limits.MaxHeaderBytes = Check(
            Web.Limits.MaxHeaderBytes, 1024, 65536, 8192, "web.limits.maxHeaderBytes");
        Web.Limits.MaxBodyBytes = Check(
            Web.Limits.MaxBodyBytes, 256, 65536, 4096, "web.limits.maxBodyBytes");
        Web.Limits.RequestTimeoutSec = Check(
            Web.Limits.RequestTimeoutSec, 1, 120, 5, "web.limits.requestTimeoutSec");
        Web.Limits.MaxConnectionsPerIp = Check(
            Web.Limits.MaxConnectionsPerIp, 1, 64, 8, "web.limits.maxConnectionsPerIp");
        Web.Limits.MaxConnectionsTotal = Check(
            Web.Limits.MaxConnectionsTotal, 16, 4096, 256, "web.limits.maxConnectionsTotal");

        NormalizeTrustedProxies();
    }

    /// <summary>
    /// Список доверенных прокси: IP или CIDR. Невалидная запись не «молча живёт»:
    /// она выкидывается с записью в <see cref="Issues"/> — иначе человек думает, что
    /// заголовку <c>X-Forwarded-For</c> верят, хотя это не так (или наоборот).
    /// </summary>
    /// <remarks>
    /// CIDR приводится к адресу сети: <c>192.168.1.5/24</c> → <c>192.168.1.0/24</c>.
    /// Пустые строки просто отбрасываются: это не ошибка, а след ручной правки JSON.
    /// </remarks>
    private void NormalizeTrustedProxies()
    {
        var source = Web.TrustedProxies ?? new List<string>();
        var normalized = new List<string>(source.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in source)
        {
            var text = (raw ?? string.Empty).Trim();
            if (text.Length == 0)
                continue;

            if (!TryNormalizeProxyEntry(text, out var entry))
            {
                Report("web.trustedProxies", text, "пропущен",
                    "— не IP и не CIDR (пример: 192.168.1.10 или 192.168.1.0/24)");
                continue;
            }

            if (seen.Add(entry))
                normalized.Add(entry);
        }

        Web.TrustedProxies = normalized;
    }

    /// <summary>Одна запись списка прокси → канонический вид; <c>false</c> — мусор.</summary>
    private static bool TryNormalizeProxyEntry(string text, out string entry)
    {
        entry = string.Empty;
        var slash = text.IndexOf('/');
        if (slash < 0)
        {
            if (!IPAddress.TryParse(text, out var address))
                return false;
            if (address.IsIPv4MappedToIPv6)
                address = address.MapToIPv4();
            entry = address.ToString();
            return true;
        }

        if (!IPAddress.TryParse(text[..slash].Trim(), out var network)
            || !int.TryParse(text[(slash + 1)..].Trim(), out var prefix))
            return false;

        if (network.IsIPv4MappedToIPv6)
            network = network.MapToIPv4();

        var maxBits = network.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => 0,
        };
        if (maxBits == 0 || prefix < 0 || prefix > maxBits)
            return false;

        // Хвост сети обнуляем: 192.168.1.5/24 и 192.168.1.0/24 — одна и та же сеть.
        var bytes = network.GetAddressBytes();
        var fullBytes = prefix / 8;
        var remainder = prefix % 8;
        if (remainder != 0)
            bytes[fullBytes] &= (byte)(0xFF << (8 - remainder));
        for (var i = fullBytes + (remainder == 0 ? 0 : 1); i < bytes.Length; i++)
            bytes[i] = 0;

        entry = $"{new IPAddress(bytes)}/{prefix}";
        return true;
    }

    private void MigrateLegacyAdminGate()
    {
        if (Admin.Ids.Count != 0)
            return;

        if (Admin.AnyoneCanStart)
            return;

        if (!IsLegacyDefaultAdminOnlySet())
            return;

        // Новый дефолт: закрыты только set и end — они меняют параметры хоста и
        // обрывают катку. Запуск матча (/mode /map /start /play) остаётся всем.
        Admin.AdminOnlyCommands.Clear();
        Admin.AdminOnlyCommands.AddRange(AdminConfig.DefaultAdminOnlyCommands());
        Admin.AnyoneCanStart = true;
        Console.WriteLine(
            "[config] миграция: старый дефолт «все команды только для админов» при пустом " +
            "admin.ids блокировал команды всем — теперь админские только " +
            $"{string.Join(", ", Admin.AdminOnlyCommands)}, запуск катки доступен всем");
    }

    /// <summary>Точный набор команд из прежнего шаблона (<c>mode,map,set,play,end,start</c>).</summary>
    private bool IsLegacyDefaultAdminOnlySet()
    {
        string[] legacy = ["mode", "map", "set", "play", "end", "start"];
        if (Admin.AdminOnlyCommands.Count != legacy.Length)
            return false;

        var actual = new HashSet<string>(
            Admin.AdminOnlyCommands
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.Trim().ToLowerInvariant()),
            StringComparer.Ordinal);

        return legacy.All(actual.Contains);
    }

    /// <summary>Флаг, который включил бы режим пробы (запрещён в <c>launch.args</c>).</summary>
    public static bool IsProbeFlag(string? arg)
    {
        var a = arg?.Trim();
        return a is not null
            && (a.Equals("--client", StringComparison.OrdinalIgnoreCase)
                || a.Equals("-c", StringComparison.OrdinalIgnoreCase)
                || a.Equals("connectasclient", StringComparison.OrdinalIgnoreCase)
                || a.Equals("--connectasclient", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Разобранный режим старта. Непонятное значение — <c>auto</c> (тот же фолбэк, что и в
    /// <see cref="Normalize"/>): тихий уход в <c>manual</c> означал «матч сам не начнётся»,
    /// и это выглядело как будто сервер игнорирует игроков.
    /// </summary>
    public MatchStartMode StartMode => TryParseMode(MatchStart.Mode, out var mode)
        ? mode
        : MatchStartMode.Auto;

    public static bool TryParseMode(string? raw, out MatchStartMode mode)
    {
        mode = MatchStartMode.Manual;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        switch (raw.Trim().ToLowerInvariant())
        {
            case "manual":
            case "manually":
            case "ручной":
                mode = MatchStartMode.Manual;
                return true;
            case "auto":
            case "automatic":
            case "авто":
                mode = MatchStartMode.Auto;
                return true;
            case "ready":
            case "rdy":
            case "готовность":
                mode = MatchStartMode.Ready;
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Админ ли этот серверный id. Права сверяются только с <c>admin.ids</c>;
    /// id считает сервер из состояния сессии, а не из пакета клиента.
    /// </summary>
    public bool IsAdmin(int id) => id >= AdminConfig.MinId && Admin.Ids.Contains(id);

    /// <summary>Список админов для логов и ответов: только id через запятую.</summary>
    public string DescribeAdmins() =>
        Admin.Ids.Count == 0 ? "(нет)" : string.Join(", ", Admin.Ids);

    /// <summary>
    /// Приводит ник к сравнимому виду: обрезка, NFC-нормализация Unicode, удаление
    /// невидимых символов (zero-width, BOM, мягкие переносы) и регистронезависимое
    /// сравнение на стороне вызывающего.
    /// </summary>
    public static string NormalizeNick(string? nick)
    {
        if (string.IsNullOrWhiteSpace(nick))
            return string.Empty;

        var text = nick.Normalize(System.Text.NormalizationForm.FormC);
        var buffer = new System.Text.StringBuilder(text.Length);
        var pendingSpace = false;
        foreach (var ch in text)
        {
            // Cf (format) = zero-width space/joiner, BOM, soft hyphen и подобное.
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch)
                == System.Globalization.UnicodeCategory.Format)
                continue;

            if (char.IsWhiteSpace(ch))
            {
                // Внутренние пробелы схлопываем в один, по краям не оставляем вовсе.
                pendingSpace = buffer.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                buffer.Append(' ');
                pendingSpace = false;
            }

            buffer.Append(ch);
        }

        return buffer.ToString();
    }

    /// <summary>Нужны ли права на эту команду.</summary>
    public bool IsAdminOnly(string? command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var trimmed = command.Trim().TrimStart('/');
        foreach (var candidate in Admin.AdminOnlyCommands)
        {
            if (string.Equals(candidate?.Trim().TrimStart('/'), trimmed,
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public string DescribeStartMode() => StartMode switch
    {
        MatchStartMode.Auto =>
            $"auto (старт сам, в каждой команде ≥ {MatchStart.Auto.MinPlayersPerTeam})",
        MatchStartMode.Ready =>
            $"ready (старт по /ready; сброс при выходе: {MatchStart.Ready.ResetOnLeave})",
        _ => "manual (старт только по /start или /set start)",
    };

    /// <summary>Шаблон, который сервер пишет при первом запуске.</summary>
    public const string Template = """
{
  // влалChillow — config.json
  // Применить: перезапуск или команда  config reload
  // // — комментарий.

  // ===== ЗАПУСК =====
  // Только для выделенного сервера.
  // Проба (--client) этот блок не читает.
  "launch": {
    // Доп. аргументы, как будто дописаны после exe.
    // Пример: [ "--verbose", "--no-dashboard" ]
    // --client запрещён: конфиг не переключает сервер в пробу.
    "args": [],

    // Дашборд: true / false / null (null — авто по терминалу)
    "dashboard": null,

    // Название лобби (null — дефолтное)
    "title": null,

    // Подробный лог пакетов
    "verbose": false
  },

  // ===== СЕРВЕР =====
  "server": {
    // Приветствие в чат при входе ("" — не слать)
    "welcomeMessage": "добро пожаловать",

    // Максимум игроков
    "maxMembers": 16,

    // Порт лобби. Меняй, только если понимаешь, что делаешь.
    "lobbyPort": 7778
  },

  // ===== НИКИ =====
  // Ник виден в чате и на табло катки, а клиент рисует его как rich-text.
  // Разрешены: латиница, кириллица, цифры, пробел, _ и -; хотя бы одна буква или цифра.
  // Нарушителя пускают в скрытое лобби, пишут подсказку и кикают через kickDelaySec.
  "names": {
    // false — пускать любые ники (проверка выключена)
    "enforce": true,

    // Максимум символов (1..32)
    "maxLength": 20,

    // Сколько секунд показать подсказку, потом кик (1..300)
    "kickDelaySec": 10
  },

  // ===== СЕТЬ =====
  "network": {
    // Где открывать сервер:
    //   "0.0.0.0" — все интерфейсы сразу (Wi-Fi, Ethernet, VPN, Hamachi)
    //   ""        — авто-выбор LAN-адреса
    //   "192.168.1.5" — только этот адрес
    "bindAddress": "0.0.0.0",

    // true — не трогать VPN/виртуальные адаптеры (Wi-Fi/Ethernet)
    // false — слушать везде: VPN, Hamachi, Radmin, ZeroTier
    "ignoreVpn": true,

    // IP для op9 (адрес матча). null — авто.
    // Задай вручную, если играете через Hamachi/Radmin.
    "advertiseIp": null,
  },

  // ===== АДМИНЫ =====
  "admin": {
    // Админы — ТОЛЬКО по номеру аккаунта (число из /ids или /whoami).
    // Первый аккаунт — 1000000, дальше 1000001, 1000002, ...
    // Ниже стоит старый хеш ника «влал»: он работает, пока аккаунта ещё нет,
    // а при первом входе сам заменится на номер аккаунта (файл не перепишется).
    // Как вписать ещё: добавь через запятую, например
    //   "ids": [ 1000000, 1000001, 1000002 ],
    // Пусто [] — админов нет, тогда /ban, /set, /end из чата не доступны никому.
    // Диапазон: 1..100000000, дубли и мусор сервер уберёт с записью в лог.
    "ids": [ 25354325 ],

    // Команды только для админов. По умолчанию set и end: они меняют параметры
    // хоста (раунды, победу, состав) и обрывают катку.
    // /mode /map /start /play НЕ входят — запускать матч может кто угодно.
    "adminOnlyCommands": [ "set", "end" ],

    // /start и /set start может любой (даже если set закрыт админом)
    "anyoneCanStart": true,

    // ===== БАНЫ =====
    // /ban <id> <срок> [причина]    срок: 30m, 6h, 3d (от 10 минут до 14 дней)
    // /unban <id|IP>                снять бан
    // /bans                         кто забанен и сколько осталось
    // Админов банить нельзя. Проверка на входе в лобби, в матч и на реконнекте.
    "ban": {
      "enabled": true,
      "adminOnly": true,          // false — /ban сможет любой (не советую)
      "file": "banlist.json",    // рядом с config.json; переживает перезапуск
      "minMinutes": 10,
      "maxDays": 14,
      "bindAliases": true,        // учить новые ключи при жёстком совпадении (userId/IP/аккаунт, не ник)
      "byDevice": true,           // банить по стабильному ключу 2.07-установки (VPN/ник не спасают)
      "notifyEveryone": true,     // всем в лобби: «X забанен на 30м (причина)»
      "shadowRefreshSec": 60,     // как часто обновлять «Разбан через: …»
      "sweepSec": 5,              // как часто проверять, у кого вышел срок
      "exposeIds": true           // в игре вместо uid «Offline» — номер аккаунта
    }
  },

  // ===== СТАРТ МАТЧА =====
  "matchStart": {
    // manual — по /start, auto — по игрокам, ready — по /ready
    "mode": "auto",

    "auto": {
      // Минимум игроков в каждой команде
      "minPlayersPerTeam": 1
    },

    "ready": {
      "announceInChat": true,
      "resetOnLeave": true,
      "allowUnready": true
    }
  },

  // ===== ТАЙМИНГИ (секунды) =====
  "timings": {
    "warmup": 3,        // WarmUp C2=21
    "preStart": 3,      // PreStart C2=22
    "prep": 10,         // Prep/закупка C2=31
    "roundTime": 109,   // длина раунда (клиент 1:49)
    "roundEndPause": 6, // пауза после RoundEnd
    "bombFuse": 40,     // фитиль бомбы

    "totalRounds": 8,   // MR-N (Escalation)
    "winsNeeded": 0,    // победа до N; 0 — дефолт режима

    "roundStartMoney": 800
  },

  // ===== ПРОИЗВОДИТЕЛЬНОСТЬ =====
  "tuning": {
    "tickMs": 33,            // цикл хоста, мс
    "stateIntervalMs": 50,   // рассылка поз, мс

    // Очередь надёжных пакетов: после неё дропаем только косметику
    "reliableQueueSoftLimit": 256,

    "packetPoolSize": 4096,
    "disconnectTimeoutMs": 10000,
    "netUpdateTimeMs": 15,
    "pingIntervalMs": 1000,

    // Строка [health], сек (0 — выключить)
    "healthIntervalSec": 10
  },

  // ===== ОТЛАДКА =====
  // Только для выделенного сервера.
  // Проба (--client) это не читает.
  "debug": {
    "verbose": false,     // лог каждого пакета
    "captures": false,    // писать capture-файлы
    "capturesDir": null,  // null — captures/ рядом с exe
    "capturesMaxFiles": 4000,
    "capturesMaxBytesMb": 64,

    "matchChat": false,   // отладочные сообщения в чат
    "health": true,       // строка [health]

    "logPackets": false,  // каждый принятый пакет
    "logDiscovery": false // каждый probe
  },

  // ===== ВЕБ-ПАНЕЛЬ =====
  // Тот же исполняемый файл, отдельный TCP-порт. Игровые порты (7777/7778/5056)
  // не меняются никогда.
  "web": {
    "enabled": true,
    "bindAddress": "0.0.0.0",    // все интерфейсы: и LAN с телефона, и nginx с другой машины
                                 // "127.0.0.1" — только если nginx на этом же хосте
    "port": 8080,
    "maxRooms": 64,              // предохранитель; правило — одна комната на игрока
    "emptyRoomTtlSec": 180,      // пустая комната закрывается через 3 минуты, номер освобождается
    "publicBaseUrl": "",         // адрес сайта для «создать можно здесь»; пусто — определить
    "basePath": "",              // подпуть за прокси (nginx: location /vlalchillow/)
    "secureCookies": false,      // true — только если перед панелью есть https
    "trustedProxies": [],        // IP/CIDR прокси, которым верим X-Forwarded-For;
                                 // пусто — только loopback (иначе LAN подделает мега-IP)
    "codeTtlSec": 1800,          // сколько живёт код входа из лобби (30 минут)

    "limits": {
      "requestsPerSecond": 30,   // больше — 429 с Retry-After (на адрес, а не на человека)
      "refusalsBeforeBlock": 5,
      "refusalWindowSec": 10,
      "blockDurationSec": 60,    // пауза для адреса, который долбит
      "maxConnectionsPerIp": 8,  // одновременных сокетов с одного клиента (страница шлёт 5)
      "maxConnectionsTotal": 256 // одновременных сокетов на весь сайт (наплыв людей)
    }
  }
}
""";
}
