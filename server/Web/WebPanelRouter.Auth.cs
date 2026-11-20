using System.Globalization;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Net.Lobby;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Авторизация панели: обмен кода из лобби на сессию, профиль и выход (#51).
/// </summary>
/// <remarks>
/// <para>
/// До авторизации сайт ничего не может: создание комнаты требует сессии, и владелец
/// берётся из неё, а не из тела запроса. Прочитать список комнат можно и без неё —
/// иначе «публичные комнаты» перестанут быть публичными.
/// </para>
/// <para>
/// Код приходит из лобби и живёт 30 минут; обменявший его получает куку-сессию, которая
/// действует до перезапуска сервера. Ошибка про код одна и та же для «не было» и
/// «протух»: сайт не должен подсказывать, какие коды существуют.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    /// <summary>Текст отказа по коду — тот, что просил владелец (#51).</summary>
    private const string CodeRejectedMessage =
        "код не существует или неактивен — перепроверьте, правильно ли он написан, " +
        "или перезайдите в лобби для получения нового кода";

    /// <summary>Текст отказа забаненному (#51).</summary>
    private const string BannedMessage = "аккаунт забанен";

    /// <summary>
    /// Проверить, не забанен ли автор запроса. Бан «на лету» гасит все его сессии:
    /// человека могли забанить, пока он сидел на сайте, и продолжать он не должен.
    /// </summary>
    /// <returns>Готовый ответ-отказ или <c>null</c>, если всё в порядке.</returns>
    private WebResponse? BannedRefusal(SiteSession session)
    {
        if (_options.IsBanned?.Invoke(session.PlayerId) != true)
            return null;

        _options.Sessions?.DropByPlayer(session.PlayerId);
        return ApiError(403, "banned", BannedMessage);
    }

    private WebResponse Authenticate(WebRequestContext request)
    {
        if (_options.Sessions is null || _options.RedeemCode is null)
            return ApiError(503, "auth_disabled", "авторизация не настроена на этом сервере");

        AuthRequest? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<AuthRequest>(request.Body, JsonOptions);
        }
        catch (JsonException)
        {
            return ApiError(400, "bad_json", "тело запроса — не JSON");
        }

        if (parsed is null || string.IsNullOrWhiteSpace(parsed.Code))
            return ApiError(400, "empty_code", "введи код из лобби");

        var now = DateTime.UtcNow;
        var entry = _options.RedeemCode(parsed.Code, now);
        if (entry is null)
        {
            // Неверный и протухший код не различимы — и не должны быть.
            return ApiError(401, "bad_code", CodeRejectedMessage);
        }

        // Забаненный не авторизуется: попытка сразу говорит причину (#51).
        if (_options.IsBanned?.Invoke(entry.PlayerId) == true)
        {
            Console.WriteLine($"[web] авторизация отклонена: ID {entry.PlayerId} забанен");
            return ApiError(403, "banned", BannedMessage);
        }

        var session = _options.Sessions.Create(entry.PlayerId, entry.Nick, now);
        if (session is null)
            return ApiError(503, "too_many_sessions", "на сервере кончились сессии — попробуй позже");

        Console.WriteLine(
            $"[web] авторизация: '{entry.Nick}' (ID {entry.PlayerId}) обменял код " +
            $"с {request.RemoteAddress}");

        return WithCookie(
            WebResponse.Json(BuildProfileJson(session)),
            SiteCookie.Build(session.Token, _options.CookiePath, _options.SecureCookies));
    }

    private WebResponse WhoAmI(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        return BannedRefusal(session) ?? WebResponse.Json(BuildProfileJson(session));
    }

    /// <summary>
    /// Аватар игрока — ровно те байты, что клиент прислал в лобби.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Формат подтверждён захватом (<c>captures/chat.zip</c>, JoinResponse от телефона):
    /// это JPEG 256×256. Но выдаём мы ровно то, что лежит в профиле, и тип определяем по
    /// magic-байтам: угадывать «это JPEG» без проверки значило бы врать, если клиент
    /// когда-нибудь пришлёт другое.
    /// </para>
    /// <para>
    /// Аватар — личная вещь, поэтому только по сессии и с <c>private</c> в кэше.
    /// </para>
    /// </remarks>
    private WebResponse Avatar(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        if (BannedRefusal(session) is { } banned)
            return banned;

        var bytes = _options.AvatarById?.Invoke(session.PlayerId);
        var type = AvatarCache.ImageContentType(bytes);
        if (type is null)
            return WebResponse.Text("нет аватара", 404);

        return new WebResponse(200, type, bytes!, 0,
            new List<(string, string)> { ("Cache-Control", "private, max-age=300") });
    }

    private WebResponse Logout(WebRequestContext request)
    {
        var token = SiteCookie.ReadToken(request.Headers);
        if (token is { Length: > 0 })
            _options.Sessions?.Drop(token);

        // Куку гасим всегда: если её и не было, пусть браузер точно её забудет.
        return WithCookie(
            WebResponse.Json("{\"ok\":true}"),
            SiteCookie.BuildExpired(_options.CookiePath, _options.SecureCookies));
    }

    /// <summary>
    /// Сессия запроса или <c>null</c>. Единственный источник «кто это» для всех маршрутов.
    /// </summary>
    private SiteSession? SessionOf(WebRequestContext request)
    {
        var store = _options.Sessions;
        if (store is null)
            return null;

        return store.Find(SiteCookie.ReadToken(request.Headers), DateTime.UtcNow);
    }

    private string BuildProfileJson(SiteSession session)
    {
        var custom = _options.CustomIdGet?.Invoke(session.PlayerId);
        var display = custom ?? session.PlayerId.ToString(CultureInfo.InvariantCulture);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", session.PlayerId);
            writer.WriteString("nick", session.Nick);
            writer.WriteString("customId", custom);
            writer.WriteString("displayId", display);
            writer.WriteString("since", session.IssuedUtc.ToString("O"));
            writer.WriteBoolean("admin", _options.IsAdminById?.Invoke(session.PlayerId) ?? false);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static WebResponse WithCookie(WebResponse response, (string Name, string Value) cookie)
    {
        var headers = new List<(string, string)>(response.ExtraHeaders) { cookie };
        return new WebResponse(
            response.Status,
            response.ContentType,
            response.Body,
            response.RetryAfterSec,
            headers);
    }

    /// <summary>Тело <c>POST /api/auth</c>.</summary>
    private sealed record AuthRequest(string? Code);
}
