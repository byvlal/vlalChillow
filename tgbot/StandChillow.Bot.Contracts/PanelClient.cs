using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace StandChillow.Bot.Contracts;

/// <summary>Куда и с каким ключом ходить за данными панели.</summary>
public sealed class PanelClientOptions
{
    /// <summary>Адрес входа бота, например <c>http://127.0.0.1:8080/api/bot</c>.</summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>Постоянный ключ из <c>auth/tgbot/password.txt</c>.</summary>
    public required string BotKey { get; init; }

    /// <summary>Сколько ждать ответа панели.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Клиент панели через ботовский вход <c>/api/bot</c>.
/// </summary>
/// <remarks>
/// <para>
/// Клиент не хранит сессию игрока: токен передаётся в каждый метод явно. Один процесс
/// бота обслуживает много Telegram-аккаунтов, и «текущий игрок» в поле класса был бы
/// гонкой — два человека одновременно увидели бы чужой профиль.
/// </para>
/// <para>
/// Панель ходит напрямую (<c>UseProxy = false</c>): прокси нужен только Telegram.
/// </para>
/// </remarks>
public sealed class PanelClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private readonly HttpClient _http;
    private readonly Uri _botBase;
    private readonly Uri _handshakeUri;
    private readonly string _botKey;
    private readonly bool _ownsHttp;

    public PanelClient(PanelClientOptions options, HttpMessageHandler? handler = null)
    {
        if (options.BaseUrl.Scheme is not ("http" or "https"))
            throw new ArgumentException("адрес панели должен быть http или https", nameof(options));

        _botKey = options.BotKey;
        _handshakeUri = new Uri(options.BaseUrl.AbsoluteUri.TrimEnd('/'));
        _botBase = new Uri(_handshakeUri.AbsoluteUri + "/");

        if (handler is null)
        {
            _http = new HttpClient(new SocketsHttpHandler
            {
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
            })
            {
                Timeout = options.Timeout,
            };
            _ownsHttp = true;
        }
        else
        {
            _http = new HttpClient(handler, disposeHandler: false)
            {
                Timeout = options.Timeout,
            };
            _ownsHttp = true;
        }
    }

    // ── рукопожатие ─────────────────────────────────────────────────────────

    public async Task<Handshake> HandshakeAsync(CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, _handshakeUri, token: null, body: null, ct);
        return await ReadAsync<Handshake>(response, ct);
    }

    // ── вход игрока ─────────────────────────────────────────────────────────

    /// <summary>Обменять код из лобби на профиль и токен сессии.</summary>
    public async Task<AuthResult> AuthenticateAsync(string code, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, UriFor("api/auth"), token: null,
            body: new { code }, ct);

        var bytes = await ReadBytesAsync(response, ct);
        var profile = Deserialize<Profile>(bytes);
        var token = ReadSessionCookie(response);
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new PanelApiException(
                500, "no_session_cookie",
                "сервер не выдал сессию — попробуй ещё раз");
        }

        return new AuthResult(profile, token);
    }

    public async Task<Profile> WhoAmIAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor("api/me"), token, body: null, ct);
        return await ReadAsync<Profile>(response, ct);
    }

    public async Task LogoutAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, UriFor("api/logout"), token, body: null, ct);
        _ = await ReadBytesAsync(response, ct);
    }

    // ── чтение ──────────────────────────────────────────────────────────────

    public async Task<StatusSnapshot> GetStatusAsync(CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor("api/status"), token: null, body: null, ct);
        return await ReadAsync<StatusSnapshot>(response, ct);
    }

    public async Task<ModesResponse> GetModesAsync(CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor("api/modes"), token: null, body: null, ct);
        return await ReadAsync<ModesResponse>(response, ct);
    }

    /// <summary>Комнаты. Токен необязателен: с ним сервер помечает свою комнату.</summary>
    public async Task<RoomsResponse> GetRoomsAsync(
        string? token = null, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor("api/rooms"), token, body: null, ct);
        return await ReadAsync<RoomsResponse>(response, ct);
    }

    public async Task<RoomCard> GetRoomAsync(int number, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor($"api/rooms/{number}"), token: null, body: null, ct);
        var wrapper = await ReadAsync<RoomResponse>(response, ct);
        return wrapper.Room;
    }

    public async Task<MyStats> GetMyStatsAsync(string token, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor("api/stats/me"), token, body: null, ct);
        return await ReadAsync<MyStats>(response, ct);
    }

    public async Task<PublicPlayer> GetPlayerAsync(string query, CancellationToken ct = default)
    {
        var path = "api/player?q=" + Uri.EscapeDataString(query);
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor(path), token: null, body: null, ct);
        return await ReadAsync<PublicPlayer>(response, ct);
    }

    public async Task<MatchesResponse> GetMatchesAsync(
        int playerId, int limit = 20, CancellationToken ct = default)
    {
        var path = $"api/matches?player={playerId}&limit={Math.Clamp(limit, 1, 100)}";
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor(path), token: null, body: null, ct);
        return await ReadAsync<MatchesResponse>(response, ct);
    }

    public async Task<MatchFull> GetMatchAsync(int id, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor($"api/match/{id}"), token: null, body: null, ct);
        var wrapper = await ReadAsync<MatchResponse>(response, ct);
        return wrapper.Match;
    }

    public async Task<MyReportsResponse> GetMyReportsAsync(
        string token, int page = 1, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor($"api/reports/mine?page={Math.Max(1, page)}"),
            token, body: null, ct);
        return await ReadAsync<MyReportsResponse>(response, ct);
    }

    public async Task<AdministrationResponse> GetAdministrationAsync(
        CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Get, UriFor("api/administration"), token: null, body: null, ct);
        return await ReadAsync<AdministrationResponse>(response, ct);
    }

    // ── действия ────────────────────────────────────────────────────────────

    public async Task<CreatedRoom> CreateRoomAsync(
        string token, CreateRoomRequest request, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, UriFor("api/rooms"), token, request, ct);
        return await ReadAsync<CreatedRoom>(response, ct);
    }

    public async Task EndRoomAsync(string token, int number, CancellationToken ct = default)
    {
        using var response = await SendRawAsync(
            HttpMethod.Post, UriFor($"api/rooms/{number}/end"), token, body: null, ct);
        _ = await ReadAsync<EndRoomResponse>(response, ct);
    }

    public async Task<ReportCreated> SubmitReportAsync(
        string token, int targetId, string category, string reason,
        CancellationToken ct = default)
    {
        var body = new ReportRequest
        {
            Target = targetId.ToString(CultureInfo.InvariantCulture),
            Category = category,
            Reason = reason,
        };
        using var response = await SendRawAsync(
            HttpMethod.Post, UriFor("api/reports"), token, body, ct);
        return await ReadAsync<ReportCreated>(response, ct);
    }

    // ── транспорт ───────────────────────────────────────────────────────────

    private Uri UriFor(string relative) => new(_botBase, relative);

    private async Task<HttpResponseMessage> SendRawAsync(
        HttpMethod method,
        Uri uri,
        string? token,
        object? body,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, uri);
        request.Headers.TryAddWithoutValidation("X-Bot-Key", _botKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.TryAddWithoutValidation("Cookie", "sc_session=" + token);

        if (body is not null)
        {
            var json = JsonSerializer.Serialize(body, JsonOptions);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static async Task<byte[]> ReadBytesAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw ErrorFrom(response, bytes);
        return bytes;
    }

    private static async Task<T> ReadAsync<T>(
        HttpResponseMessage response, CancellationToken ct)
    {
        var bytes = await ReadBytesAsync(response, ct);
        return Deserialize<T>(bytes);
    }

    private static T Deserialize<T>(byte[] bytes)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                   ?? throw new PanelApiException(
                       500, "empty_response", "сервер прислал пустой ответ");
        }
        catch (JsonException ex)
        {
            throw new PanelApiException(
                500, "bad_response",
                "сервер прислал неожиданный ответ — возможно, обновился и бот отстал",
                inner: ex);
        }
    }

    private static PanelApiException ErrorFrom(HttpResponseMessage response, byte[] bytes)
    {
        var status = (int)response.StatusCode;
        string? code = null;
        string? message = null;

        if (bytes.Length > 0)
        {
            try
            {
                var error = JsonSerializer.Deserialize<ErrorBody>(bytes, JsonOptions);
                if (!string.IsNullOrWhiteSpace(error?.Code))
                    code = error.Code;
                if (!string.IsNullOrWhiteSpace(error?.Message))
                    message = error.Message;
            }
            catch (JsonException)
            {
                // Не JSON — ниже подставим общий текст.
            }
        }

        var retry = 0;
        if (response.Headers.RetryAfter?.Delta is { } delta)
            retry = (int)Math.Clamp(delta.TotalSeconds, 0, 86400);
        else if (response.Headers.RetryAfter?.Date is { } date)
            retry = (int)Math.Clamp((date - DateTimeOffset.UtcNow).TotalSeconds, 0, 86400);

        code ??= status == 429 ? "rate_limited" : $"http_{status}";
        message ??= status == 429
            ? "слишком много запросов — подожди немного"
            : $"сервер ответил {status}";

        return new PanelApiException(status, code, message, retry);
    }

    private static string? ReadSessionCookie(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values))
            return null;

        foreach (var header in values)
        {
            foreach (var part in header.Split(';'))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;
                if (!part[..eq].Trim().Equals("sc_session", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = part[(eq + 1)..].Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}

/// <summary>Профиль и токен, полученные при обмене кода из лобби.</summary>
public sealed record AuthResult(Profile Profile, string Token);
