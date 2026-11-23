using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Web;

/// <summary>
/// HTTP-сервер панели: обычный <see cref="TcpListener"/>, без фреймворков.
/// </summary>
/// <remarks>
/// <para>
/// Проект не тянет ASP.NET (одна зависимость — LiteNetLib), поэтому панель — маленький
/// HTTP/1.1 поверх TCP: разобрать строку запроса, отдать файл или JSON, закрыть
/// соединение. <c>Connection: close</c> всегда: keep-alive не нужен, а браузер с
/// двадцатью вкладками не должен держать двадцать сокетов.
/// </para>
/// <para>
/// Живёт на своём порту (по умолчанию 8080) и <b>никогда</b> не трогает игровые 7777/7778 —
/// это требование спеки: комнаты мультиплексируются на одном игровом сокете, а панель
/// лишь читает их состояние.
/// </para>
/// <para>
/// Все ответы отдаются из памяти: HTML вшит в сборку как <c>EmbeddedResource</c>, файлов
/// рядом с бинарником нет. Ошибка внутри обработчика не роняет сервер — клиент получает
/// 500, сервер пишет в лог.
/// </para>
/// </remarks>
public sealed class WebPanelServer : IDisposable
{
    private readonly WebPanelOptions _options;
    private readonly Func<WebRequestContext, WebResponse> _router;
    private readonly WebRateLimiter _limiter;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _connections = new();
    private readonly object _connGate = new();
    private readonly Dictionary<IPAddress, int> _activeByIp = new();
    private int _activeTotal;
    private readonly Dictionary<IPAddress, DateTime> _lastRefusalLog = new();
    private readonly Func<DateTime> _clock;

    private TcpListener? _listener;
    private Task? _acceptLoop;
    private long _requestsServed;
    private long _requestsRejected;

    public WebPanelServer(
        WebPanelOptions options,
        Func<WebRequestContext, WebResponse> router,
        Func<DateTime>? clock = null)
    {
        _options = options;
        _router = router;
        _clock = clock ?? (() => DateTime.UtcNow);
        _limiter = new WebRateLimiter
        {
            RequestsPerSecond = options.Limits.RequestsPerSecond,
            RefusalsBeforeBlock = options.Limits.RefusalsBeforeBlock,
            RefusalWindowSec = options.Limits.RefusalWindowSec,
            BlockDurationSec = options.Limits.BlockDurationSec,
        };
    }

    /// <summary>Порт, который реально слушается (0 до старта).</summary>
    public int Port { get; private set; }

    /// <summary>Адрес прослушивания.</summary>
    public string BindAddress => _options.BindAddress;

    public long RequestsServed => Interlocked.Read(ref _requestsServed);

    public long RequestsRejected => Interlocked.Read(ref _requestsRejected);

    /// <summary>
    /// Поднять сервер. Ошибка бинда <b>не</b> фатальна и не мешает игровым сокетам:
    /// возвращаем <c>false</c> и причину — вызывающий покажет её в консоли.
    /// </summary>
    public bool TryStart(out string error)
    {
        error = "";
        try
        {
            var address = ResolveBindAddress(_options.BindAddress);
            _listener = new TcpListener(address, _options.Port);
            _listener.Server.SetSocketOption(
                SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _listener = null;
            return false;
        }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_stop.Token));
        return true;
    }

    /// <summary>
    /// Обработать один запрос целиком (разбор + маршрут). Отдельно от сокета: тесты
    /// проверяют маршруты и заголовки без открытия порта.
    /// </summary>
    public WebResponse Handle(WebRequestContext context) => _router(context);

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // Сокет мог закрыться между циклом и Accept — выходим, если просят.
                if (token.IsCancellationRequested)
                    return;
                continue;
            }

            var task = Task.Run(() => ServeAsync(client, token), token);
            lock (_connGate)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        var peer = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;

        try
        {
            client.ReceiveTimeout = _options.Limits.RequestTimeoutSec * 1000;
            client.SendTimeout = _options.Limits.RequestTimeoutSec * 1000;

            // Предохранитель на весь сайт: сколько сокетов панель держит одновременно.
            // Адрес клиента станет известен только после чтения заголовков, а память
            // надо защищать раньше — поэтому этот кап первый и самый широкий.
            if (!TryAddConnectionTotal())
            {
                Interlocked.Increment(ref _requestsRejected);
                await WriteRawAsync(client, WebResponse.Throttled(2), token)
                    .ConfigureAwait(false);
                return;
            }

            try
            {
                var head = await ReadHeadAsync(client, token).ConfigureAwait(false);
                if (head is null)
                {
                    // Заголовки разобрать не удалось: чей это запрос, не знает никто,
                    // включая прокси. Отказ засчитываем прямому отправителю.
                    _limiter.NoteRefusal(peer);
                    Interlocked.Increment(ref _requestsRejected);
                    await WriteRawAsync(client, WebResponse.Text("400", 400), token)
                        .ConfigureAwait(false);
                    return;
                }

                // Адрес клиента: за прокси все сокеты приходят с адреса прокси, и считать
                // их одним клиентом нельзя — толпа выжигает общий лимит, и 429 получают
                // все, даже те, кто только открыл сайт. Прокси ставит реальный адрес в
                // X-Forwarded-For; WebClientAddress решает, когда ему можно верить.
                var remote = WebClientAddress.Resolve(
                    peer, head.Headers, _options.TrustedProxies);

                // Различаем ли мы посетителей вообще? Заголовок от доверенного прокси даёт
                // каждому гостю свой адрес; без него (панель открыта наружу мимо прокси, или
                // свой прокси пришёл с публичного адреса) за одним адресом стоят разные люди.
                // Такому адресу даём только посекундный лимит частоты и никогда не включаем
                // долгую паузу — иначе один поток погасил бы сайт всем, кто идёт через тот же
                // адрес. Это гарантия на стороне сервера: от конфига прокси она не зависит.
                var identifiesVisitor = WebClientAddress.IdentifiesVisitor(
                    peer, head.Headers, _options.TrustedProxies);

                // Тело больше лимита: это 413 «слишком большое», а не 400. В счётчик
                // отказов не идёт — панель просто так не принимает ничего крупного.
                if (head.TooLarge)
                {
                    Interlocked.Increment(ref _requestsRejected);
                    await WriteRawAsync(client, WebResponse.TooLarge(), token)
                        .ConfigureAwait(false);
                    return;
                }

                // Лимит частоты — до всего остального: он про людей, а не про сокеты, и
                // проверять его надо даже тогда, когда соединения с адреса уже заняты.
                var decision = _limiter.Check(remote, sharedBucket: !identifiesVisitor);
                if (!decision.Allowed)
                {
                    Interlocked.Increment(ref _requestsRejected);
                    LogRefusal(remote, decision);
                    await WriteRawAsync(client, WebResponse.Throttled(decision.RetryAfterSec), token)
                        .ConfigureAwait(false);
                    return;
                }

                // Соединений с одного клиента — не больше лимита. Браузер открывает
                // несколько параллельно (страница + её JSON), поэтому запас нужен;
                // скрипт с сотней сокетов в лимит не пролезет.
                if (!TryAddConnection(remote))
                {
                    Interlocked.Increment(ref _requestsRejected);
                    await WriteRawAsync(client, WebResponse.Throttled(1), token)
                        .ConfigureAwait(false);
                    return;
                }

                try
                {
                    var body = await ReadBodyAsync(client, head, token).ConfigureAwait(false);
                    if (body is null)
                    {
                        // Тело не пришло целиком (обрыв или таймаут) — виноват не сервер,
                        // поэтому это идёт в счётчик отказов. «Больше лимита» отсекается
                        // раньше, в ReadHeadAsync, и туда же пишет NoteRefusal.
                        _limiter.NoteRefusal(remote);
                        Interlocked.Increment(ref _requestsRejected);
                        await WriteRawAsync(client, WebResponse.Text("400", 400), token)
                            .ConfigureAwait(false);
                        return;
                    }

                    WebResponse response;
                    try
                    {
                        response = _router(new WebRequestContext(
                            head.Method,
                            head.Path,
                            head.Query,
                            body,
                            remote,
                            head.Headers));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[web] {head.Method} {head.Path} упал: {ex}");
                        response = WebResponse.Text("500", 500);
                    }

                    Interlocked.Increment(ref _requestsServed);

                    // Отказом засчитываем только решение самого лимита (уже сделано выше).
                    // Ответы API — 401 «нет входа», 404 «нет такой комнаты», 409 «уже есть
                    // комната» — штатные: аноним открывает страницу и сразу получает 401
                    // от /api/me и /api/avatar. Считать их упорством значило бы банить
                    // гостя за обычный заход, а именно это и давало массовые 429.
                    if (response.Status >= 500)
                        _limiter.NoteRefusal(remote);

                    await WriteRawAsync(client, response, token).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseConnection(remote);
                }
            }
            finally
            {
                ReleaseConnectionTotal();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException
                                       or OperationCanceledException
                                       or ObjectDisposedException)
        {
            // Клиент ушёл (закрыл вкладку, мобильная сеть) — это не ошибка сервера.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[web] соединение {peer}: {ex.Message}");
        }
        finally
        {
            try { client.Close(); }
            catch { /* уже закрыт */ }
        }
    }

    /// <summary>
    /// Строка об отказе — не чаще раза в секунду на адрес.
    /// </summary>
    /// <remarks>
    /// Флуд — это именно тот момент, когда писать в консоль на каждый запрос нельзя:
    /// синхронная запись превращает защиту от нагрузки в её источник. Один адрес виден
    /// в журнале раз в секунду, а первое попадание в паузу пишется всегда — по нему и
    /// видно, что блокировка сработала.
    /// </remarks>
    private void LogRefusal(IPAddress? address, WebRateLimiter.Decision decision)
    {
        var now = _clock();
        lock (_connGate)
        {
            if (address is not null
                && _lastRefusalLog.TryGetValue(address, out var last)
                && now - last < TimeSpan.FromSeconds(1))
                return;
            if (address is not null)
                _lastRefusalLog[address] = now;

            // Чистим редко и дёшево: словарь не должен расти до размера интернета.
            if (_lastRefusalLog.Count > 4096)
            {
                foreach (var stale in _lastRefusalLog
                             .Where(kv => now - kv.Value > TimeSpan.FromSeconds(60))
                             .Select(kv => kv.Key).ToList())
                    _lastRefusalLog.Remove(stale);
            }
        }

        Console.WriteLine(
            $"[web] {address} отказ ({decision.Reason}) — Retry-After {decision.RetryAfterSec}s");
    }

    private bool TryAddConnection(IPAddress? address)
    {
        var max = Math.Max(1, _options.Limits.MaxConnectionsPerIp);
        lock (_connGate)
        {
            if (address is not null)
            {
                _activeByIp.TryGetValue(address, out var count);
                if (count >= max)
                    return false;
                _activeByIp[address] = count + 1;
            }
            return true;
        }
    }

    /// <summary>
    /// Общий кап одновременных соединений — предохранитель всего сайта: адрес клиента
    /// известен только после чтения заголовков, а переполнение сокетами надо гасить
    /// раньше, чем оно съест память.
    /// </summary>
    private bool TryAddConnectionTotal()
    {
        var max = Math.Max(1, _options.Limits.MaxConnectionsTotal);
        lock (_connGate)
        {
            if (_activeTotal >= max)
                return false;
            _activeTotal++;
        }
        return true;
    }

    private void ReleaseConnectionTotal()
    {
        lock (_connGate)
        {
            if (_activeTotal > 0)
                _activeTotal--;
        }
    }

    private void ReleaseConnection(IPAddress? address)
    {
        if (address is null)
            return;
        lock (_connGate)
        {
            if (_activeByIp.TryGetValue(address, out var count))
            {
                if (count <= 1)
                    _activeByIp.Remove(address);
                else
                    _activeByIp[address] = count - 1;
            }
        }
    }

    /// <summary>Заголовки и первая строка запроса: всё, что нужно до проверки адреса.</summary>
    /// <param name="TooLarge">Тело больше лимита — это 413, а не 400.</param>
    private sealed record RawHead(
        string Method,
        string Path,
        string Query,
        Dictionary<string, string> Headers,
        int BodyLength,
        byte[] BodyPrefix,
        bool TooLarge = false);

    private async Task<RawHead?> ReadHeadAsync(TcpClient client, CancellationToken token)
    {
        var stream = client.GetStream();
        var headerLimit = Math.Max(1024, _options.Limits.MaxHeaderBytes);
        var buffer = new byte[headerLimit];
        var filled = 0;
        var headerEnd = -1;

        // Читаем, пока не увидим пустую строку или не упрёмся в лимит заголовков.
        while (filled < buffer.Length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.Limits.RequestTimeoutSec));

            int read;
            try
            {
                read = await stream.ReadAsync(buffer.AsMemory(filled), timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read <= 0)
                return null;
            filled += read;

            headerEnd = FindHeaderEnd(buffer, filled);
            if (headerEnd >= 0)
                break;
        }

        if (headerEnd < 0)
            return null;

        var headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        if (lines.Length == 0)
            return null;

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 2)
            return null;

        var method = requestLine[0].ToUpperInvariant();
        var target = requestLine[1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':');
            if (colon <= 0)
                continue;
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            if (name.Length == 0)
                continue;
            // Дубликаты не склеиваем: нам нужен только Content-Length.
            headers[name] = value;
        }

        var path = target;
        var query = "";
        var q = target.IndexOf('?');
        if (q >= 0)
        {
            path = target[..q];
            query = target[(q + 1)..];
        }
        // Абсолютный URI («GET http://host/path») — это запрос к прокси, а не к нам.
        // Принимать его значит показать чужой хост в своём логе и увести маршрут в /http:…
        if (target.Contains("://", StringComparison.Ordinal))
            return null;

        if (!path.StartsWith('/'))
            path = "/" + path;

        var bodyLength = 0;
        if (headers.TryGetValue("Content-Length", out var rawLength))
        {
            if (!int.TryParse(rawLength, out bodyLength) || bodyLength < 0)
                return null;
            if (bodyLength > _options.Limits.MaxBodyBytes)
            {
                return new RawHead(method, path, query, headers, bodyLength,
                    Array.Empty<byte>(), TooLarge: true);
            }
        }

        // Тело, дочитанное вместе с заголовками, отдаём дальше — из сокета оно уже ушло.
        var prefixLength = Math.Clamp(filled - (headerEnd + 4), 0, bodyLength);
        var prefix = prefixLength == 0 ? Array.Empty<byte>() : new byte[prefixLength];
        if (prefixLength > 0)
            Array.Copy(buffer, headerEnd + 4, prefix, 0, prefixLength);

        return new RawHead(method, path, query, headers, bodyLength, prefix);
    }

    /// <summary>
    /// Дочитать тело. <c>null</c> — тело не пришло целиком (клиент ушёл/таймаут);
    /// «больше лимита» отсекается ещё в <see cref="ReadHeadAsync"/>.
    /// </summary>
    private async Task<byte[]?> ReadBodyAsync(
        TcpClient client, RawHead head, CancellationToken token)
    {
        if (head.BodyLength == 0)
            return Array.Empty<byte>();

        var body = new byte[head.BodyLength];
        var have = head.BodyPrefix.Length;
        if (have > 0)
            Array.Copy(head.BodyPrefix, body, have);

        var stream = client.GetStream();
        while (have < body.Length)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.Limits.RequestTimeoutSec));
            int read;
            try
            {
                read = await stream.ReadAsync(body.AsMemory(have), timeout.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            if (read <= 0)
                return null;
            have += read;
        }

        return body;
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n'
                && buffer[i - 1] == '\r' && buffer[i] == '\n')
                return i - 3;
        }
        return -1;
    }

    private static async Task WriteRawAsync(
        TcpClient client, WebResponse response, CancellationToken token)
    {
        var header = new StringBuilder(256);
        header.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
            .Append(ReasonPhrase(response.Status)).Append("\r\n");
        header.Append("Content-Type: ").Append(response.ContentType).Append("\r\n");
        header.Append("Content-Length: ").Append(response.Body.Length).Append("\r\n");
        // Кэш: у API он не нужен (no-store), но у медиа и шрифтов есть свой честный
        // Cache-Control — явное правило сильнее дефолта, дубли не пишем.
        var hasOwnCacheControl = false;
        foreach (var (name, _) in response.ExtraHeaders)
        {
            if (name.Equals("Cache-Control", StringComparison.OrdinalIgnoreCase))
            {
                hasOwnCacheControl = true;
                break;
            }
        }
        if (!hasOwnCacheControl)
            header.Append("Cache-Control: no-store\r\n");
        header.Append("X-Content-Type-Options: nosniff\r\n");
        header.Append("Referrer-Policy: no-referrer\r\n");
        header.Append("Connection: close\r\n");

        if (response.RetryAfterSec > 0)
            header.Append("Retry-After: ").Append(response.RetryAfterSec).Append("\r\n");

        foreach (var (name, value) in response.ExtraHeaders)
            header.Append(name).Append(": ").Append(value).Append("\r\n");

        header.Append("\r\n");

        var stream = client.GetStream();
        var bytes = Encoding.UTF8.GetBytes(header.ToString());
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        if (response.Body.Length > 0)
            await stream.WriteAsync(response.Body, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private static string ReasonPhrase(int status) => status switch
    {
        200 => "OK",
        201 => "Created",
        204 => "No Content",
        206 => "Partial Content",
        400 => "Bad Request",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        503 => "Service Unavailable",
        _ => "Status",
    };

    private static IPAddress ResolveBindAddress(string bind)
    {
        if (string.IsNullOrWhiteSpace(bind))
            return IPAddress.Any;
        if (bind is "*" or "+" or "any")
            return IPAddress.Any;
        return IPAddress.TryParse(bind, out var parsed) ? parsed : IPAddress.Loopback;
    }

    /// <summary>Остановить сервер и дождаться соединений.</summary>
    public void Stop()
    {
        try { _stop.Cancel(); }
        catch { /* уже отменён */ }

        try { _listener?.Stop(); }
        catch { /* уже закрыт */ }

        try { _acceptLoop?.Wait(TimeSpan.FromSeconds(2)); }
        catch { /* отмена — норма */ }

        Task[] pending;
        lock (_connGate)
            pending = _connections.ToArray();
        try { Task.WaitAll(pending, TimeSpan.FromSeconds(2)); }
        catch { /* соединения могли упасть на отмене */ }
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
        _listener = null;
    }
}

/// <summary>
/// Всё, что панели нужно знать о запуске. Отдельный тип, а не <c>ServerConfig</c>:
/// веб-слой не должен зависеть от всего конфига игры.
/// </summary>
public sealed class WebPanelOptions
{
    /// <summary>Где слушать. По умолчанию все интерфейсы — иначе прокси не достучится.</summary>
    public string BindAddress { get; init; } = "0.0.0.0";

    public int Port { get; init; } = 8080;
    public WebLimits Limits { get; init; } = new();

    /// <summary>
    /// IP/CIDR прокси, которым доверяем <c>X-Forwarded-For</c>. Пусто — только loopback.
    /// Иначе LAN-клиент подделает адрес и получит мега-права чужого IP.
    /// </summary>
    public IReadOnlyList<string> TrustedProxies { get; init; } = Array.Empty<string>();

    /// <summary>Копия лимитов с уже применёнными ограничениями конфига.</summary>
    public sealed class WebLimits
    {
        public int RequestsPerSecond { get; init; } = 30;
        public int RefusalsBeforeBlock { get; init; } = 5;
        public int RefusalWindowSec { get; init; } = 10;
        public int BlockDurationSec { get; init; } = 60;
        public int MaxHeaderBytes { get; init; } = 8 * 1024;
        public int MaxBodyBytes { get; init; } = 4 * 1024;
        public int RequestTimeoutSec { get; init; } = 5;

        /// <summary>
        /// Не меньше числа параллельных запросов одной загрузки страницы
        /// (каталог, профиль, комнаты, сводка): иначе 429 получает первый же гость.
        /// </summary>
        public int MaxConnectionsPerIp { get; init; } = 8;
        public int MaxConnectionsTotal { get; init; } = 256;
    }
}

/// <summary>Входящий запрос в терминах панели.</summary>
/// <param name="Method"><c>GET</c>/<c>POST</c> (уже в верхнем регистре).</param>
/// <param name="Path">Путь без строки запроса, всегда начинается с <c>/</c>.</param>
/// <param name="Query">Строка после <c>?</c> без самого знака; пусто, если её нет.</param>
/// <param name="Body">Тело запроса (уже ограничено лимитом).</param>
/// <param name="RemoteAddress">Адрес клиента; <c>null</c> в тестах.</param>
/// <param name="Headers">Заголовки (регистр имени не важен).</param>
public sealed record WebRequestContext(
    string Method,
    string Path,
    string Query,
    byte[] Body,
    IPAddress? RemoteAddress,
    IReadOnlyDictionary<string, string> Headers);

/// <summary>Ответ панели.</summary>
public sealed record WebResponse(
    int Status,
    string ContentType,
    byte[] Body,
    int RetryAfterSec,
    IReadOnlyList<(string, string)> ExtraHeaders)
{
    public static WebResponse Html(string html) =>
        new(200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html), 0, Array.Empty<(string, string)>());

    public static WebResponse Json(string json, int status = 200) =>
        new(status, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), 0,
            Array.Empty<(string, string)>());

    public static WebResponse Text(string text, int status = 200) =>
        new(status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text), 0,
            Array.Empty<(string, string)>());

    /// <summary>Отказ лимита: тело — код, заголовок <c>Retry-After</c> — сколько ждать.</summary>
    public static WebResponse Throttled(int retryAfterSec) => new(
        429,
        "text/plain; charset=utf-8",
        Encoding.UTF8.GetBytes("429"),
        Math.Max(1, retryAfterSec),
        Array.Empty<(string, string)>());

    /// <summary>
    /// Тело больше лимита. <c>Retry-After</c> тоже отдаём: страница не должна повторять
    /// тот же запрос сразу, иначе она просто повторит ошибку.
    /// </summary>
    public static WebResponse TooLarge() => new(
        413,
        "text/plain; charset=utf-8",
        Encoding.UTF8.GetBytes("413"),
        2,
        Array.Empty<(string, string)>());
}
