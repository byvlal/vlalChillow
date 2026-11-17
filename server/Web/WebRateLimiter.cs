using System.Net;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Флуд-защита панели: лимит запросов на IP и временная блокировка за упорство.
/// </summary>
/// <remarks>
/// <para>
/// Паролей и куки у панели нет — «один клик и играть». Поэтому защита считает только то,
/// что видно на уровне TCP: адрес. Лимит частоты плюс пауза за серию отказов отсекают
/// скрипт, но не мешают странице с её двумя запросами в секунду.
/// </para>
/// <para>
/// Время инжектируется (<c>clock</c>), поэтому тесты не спят: правило можно проверить
/// без ожидания реальных секунд и без сокетов.
/// </para>
/// <para>
/// Память ограничена: адреса, о которых давно ничего не слышно, вычищаются при обращении.
/// </para>
/// </remarks>
public sealed class WebRateLimiter
{
    private sealed class Bucket
    {
        public DateTime WindowStartUtc;
        public int RequestsInWindow;
        public DateTime? BlockedUntilUtc;
        public readonly List<DateTime> Refusals = new();
        public DateTime LastSeenUtc;

        /// <summary>Когда за эту серию отказов уже начислен «штраф».</summary>
        public DateTime? RefusalChargedAtUtc;
    }

    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, Bucket> _buckets = new();
    private readonly Func<DateTime> _clock;
    private DateTime _lastSweepUtc;


    public WebRateLimiter(Func<DateTime>? clock = null)
    {
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    /// <summary>Запросов в секунду с одного адреса.</summary>
    public int RequestsPerSecond { get; init; } = 10;

    /// <summary>Сколько отказов подряд (в окне) включают паузу.</summary>
    public int RefusalsBeforeBlock { get; init; } = 5;

    /// <summary>Окно подсчёта отказов.</summary>
    public int RefusalWindowSec { get; init; } = 10;

    /// <summary>Длительность паузы.</summary>
    public int BlockDurationSec { get; init; } = 60;

    /// <summary>Сколько помнить адрес без активности.</summary>
    public int IdleEvictionSec { get; init; } = 600;

    /// <summary>
    /// Как часто засчитывается новый штраф за отказы. Параллельный залп запросов одной
    /// страницы приходит в пределах этой секунды — значит это один штраф, а не пять.
    /// </summary>
    public static readonly TimeSpan RefusalChargeInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Результат проверки запроса.
    /// </summary>
    /// <param name="Allowed">Пускать ли запрос.</param>
    /// <param name="RetryAfterSec">Сколько ждать (для заголовка <c>Retry-After</c>).</param>
    /// <param name="Reason">Причина отказа для лога; пусто при успехе.</param>
    public readonly record struct Decision(bool Allowed, int RetryAfterSec, string Reason)
    {
        public static Decision Allow() => new(true, 0, "");
        public static Decision Deny(int retry, string reason) => new(false, retry, reason);
    }

    /// <summary>
    /// Учесть запрос и решить, пропускать ли его.
    /// </summary>
    /// <param name="address">Адрес, по которому считается лимит (см. <see cref="WebClientAddress"/>).</param>
    /// <param name="sharedBucket">
    /// <c>true</c> — за этим адресом стоят разные люди, которых мы не различаем (прокси без
    /// заголовка, прямой заход из интернета мимо прокси). Такому адресу даём только
    /// посекундный лимит частоты, но <b>никогда</b> не включаем долгую паузу: иначе один
    /// поток запросов погасил бы сайт для всех, кто идёт через тот же адрес. Это и есть
    /// обещание «никто извне не сломает сайт остальным».
    /// </param>
    public Decision Check(IPAddress? address, bool sharedBucket = false)
    {
        if (address is null)
            return Decision.Allow();

        var now = _clock();
        lock (_gate)
        {
            SweepIfDueLocked(now);

            if (!_buckets.TryGetValue(address, out var bucket))
            {
                bucket = new Bucket
                {
                    WindowStartUtc = now,
                    LastSeenUtc = now,
                };
                _buckets[address] = bucket;
            }

            bucket.LastSeenUtc = now;

            // Заблокирован — только напоминаем, сколько ждать.
            if (bucket.BlockedUntilUtc is { } until && now < until)
                return Decision.Deny(
                    Math.Max(1, (int)Math.Ceiling((until - now).TotalSeconds)),
                    "blocked");

            // Окно в секунду: частота.
            if (now - bucket.WindowStartUtc >= TimeSpan.FromSeconds(1))
            {
                bucket.WindowStartUtc = now;
                bucket.RequestsInWindow = 0;
            }

            bucket.RequestsInWindow++;
            if (bucket.RequestsInWindow > Math.Max(1, RequestsPerSecond))
            {
                if (!sharedBucket)
                    RegisterRefusalLocked(bucket, now);
                return Decision.Deny(1, "rate");
            }

            // Успешный проход сбрасывает серию отказов: человек, который пару раз
            // обновил страницу, не должен копить «штраф» всю минуту.
            bucket.Refusals.Clear();
            bucket.RefusalChargedAtUtc = null;
            return Decision.Allow();
        }
    }

    /// <summary>
    /// Записать отказ от инфраструктуры (400, 413, таймаут чтения) в тот же счётчик.
    /// </summary>
    /// <remarks>
    /// Сюда попадают только случаи, где виноват сам запрос, а не решение сервера: обычный
    /// посетитель их не создаёт. Ответы API (401 «нет входа», 404 «нет такого») сюда не
    /// идут — это штатные ответы, и считать их «упорством» значило бы блокировать
    /// анонимного гостя за то, что он просто открыл страницу (см. <see cref="ServeAsync"/>).
    /// </remarks>
    public void NoteRefusal(IPAddress? address)
    {
        if (address is null)
            return;

        var now = _clock();
        lock (_gate)
        {
            if (!_buckets.TryGetValue(address, out var bucket))
            {
                bucket = new Bucket { WindowStartUtc = now };
                _buckets[address] = bucket;
            }

            bucket.LastSeenUtc = now;
            RegisterRefusalLocked(bucket, now);
        }
    }

    /// <summary>Сколько адресов сейчас в памяти (для тестов и диагностики).</summary>
    public int TrackedCount
    {
        get
        {
            lock (_gate)
                return _buckets.Count;
        }
    }

    /// <summary>
    /// Засчитать один отказ за серию, а не за каждый запрос.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Браузер открывает несколько соединений сразу (страница опрашивает статус, список и
    /// профиль параллельно), поэтому одна загрузка страницы может дать несколько ответов
    /// 401/404 одновременно. Считать каждый из них отдельным «упорством» нельзя: пять
    /// таких ответов — это один заход одного человека, а не пять попыток подряд.
    /// </para>
    /// <para>
    /// Поэтому штраф начисляется не чаще одного раза в секунду: параллельный залп одной
    /// загрузки — это один штраф, а скрипт, который долбит непрерывно, набирает пять
    /// штрафов за пять секунд и всё равно получает паузу.
    /// </para>
    /// </remarks>
    private void RegisterRefusalLocked(Bucket bucket, DateTime now)
    {
        var window = TimeSpan.FromSeconds(Math.Max(1, RefusalWindowSec));
        bucket.Refusals.RemoveAll(t => now - t > window);
        bucket.Refusals.Add(now);

        if (bucket.RefusalChargedAtUtc is { } charged && now - charged < RefusalChargeInterval)
            return;
        bucket.RefusalChargedAtUtc = now;

        if (bucket.Refusals.Count >= Math.Max(1, RefusalsBeforeBlock))
        {
            bucket.BlockedUntilUtc = now.AddSeconds(Math.Max(1, BlockDurationSec));
            bucket.Refusals.Clear();
            bucket.RefusalChargedAtUtc = null;
        }
    }

    private void SweepIfDueLocked(DateTime now)
    {
        if (now - _lastSweepUtc < TimeSpan.FromSeconds(30))
            return;
        _lastSweepUtc = now;

        var idle = TimeSpan.FromSeconds(Math.Max(60, IdleEvictionSec));
        var dead = _buckets
            .Where(kv => now - kv.Value.LastSeenUtc > idle
                         && (kv.Value.BlockedUntilUtc is null || now >= kv.Value.BlockedUntilUtc))
            .Select(kv => kv.Key)
            .ToList();
        foreach (var key in dead)
            _buckets.Remove(key);

        // Верхняя граница памяти: адресов не должно накопиться больше, чем нужно городу.
        if (_buckets.Count > 10_000)
        {
            foreach (var key in _buckets
                         .OrderBy(kv => kv.Value.LastSeenUtc)
                         .Take(_buckets.Count - 5_000)
                         .Select(kv => kv.Key)
                         .ToList())
                _buckets.Remove(key);
        }
    }
}
