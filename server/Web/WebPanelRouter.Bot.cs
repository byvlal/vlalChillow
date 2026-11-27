using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Вход Telegram-бота: всё под <c>/api/bot/*</c> — та же панель, но только после
/// проверки постоянного ключа из <c>auth/tgbot/password.txt</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ключ приходит в заголовке <c>X-Bot-Key</c>. Проверка стоит первой: без неё
/// <c>/api/bot/*</c> не отвечает даже 404, поэтому перебором путей нельзя узнать,
/// какие маршруты существуют. Ключ сравнивается constant-time
/// (<see cref="BotKeyStore.Matches"/>), в логи и ответы не попадает.
/// </para>
/// <para>
/// После проверки префикс <c>/api/bot</c> срезается, и запрос уходит в обычный
/// <see cref="Route"/> — JSON остаётся ровно тем же, что у сайта. Игрок при этом
/// по-прежнему авторизуется кукой <c>sc_session</c>: ключ бота — не сессия игрока.
/// </para>
/// <para>
/// Админские пути под ботом закрыты намеренно: бот повторяет сайт без админки,
/// и утёкший ключ бота не должен открывать даже вход в неё.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    /// <summary>Заголовок с ключом бота.</summary>
    public const string BotKeyHeader = "X-Bot-Key";

    /// <summary>Префикс ботовских маршрутов.</summary>
    public const string BotPrefix = "/api/bot";

    /// <summary>Версия ботовского API: меняется только при несовместимой правке.</summary>
    public const int BotApiVersion = 1;

    /// <summary>Это путь бота? Проверяется до всех остальных маршрутов.</summary>
    internal static bool IsBotPath(string path) =>
        path == BotPrefix || path.StartsWith(BotPrefix + "/", StringComparison.Ordinal);

    /// <summary>
    /// Затвор бота: проверить ключ, ответить на рукопожатие или отдать запрос
    /// обычному роутеру со срезанным префиксом.
    /// </summary>
    private WebResponse BotRoute(WebRequestContext request, string path)
    {
        if (_options.BotKey is null)
            return ApiError(503, "bot_disabled", "бот не настроен на этом сервере");

        // Файл ключа не создался (права, диск) — это не «неверный ключ», а поломка
        // сервера: бот должен сказать об этом владельцу, а не просить новый ключ.
        if (_options.BotKey.Current is null)
            return ApiError(503, "bot_key_unavailable",
                "ключ бота не создан — проверь права на auth/tgbot");

        request.Headers.TryGetValue(BotKeyHeader, out var provided);
        if (!_options.BotKey.Matches(provided))
            return ApiError(403, "bad_bot_key",
                "ключ бота не подходит — сбрось его на сервере командой tgbot resetkey");

        // Рукопожатие: бот проверяет адрес и ключ до того, как что-то показывать.
        if (path == BotPrefix)
        {
            return request.Method == "GET"
                ? WebResponse.Json(HandshakeJson())
                : WithAllow("GET", WebResponse.Text("405", 405));
        }

        var inner = path[BotPrefix.Length..];
        if (inner is "" or "/")
            return WithAllow("GET", WebResponse.Text("405", 405));

        // Админка боту не положена: даже вход. Отвечаем так же, как на несуществующий
        // путь, чтобы не подсказывать, что он вообще есть.
        if (inner == "/api/admin" || inner.StartsWith("/api/admin/", StringComparison.Ordinal))
            return ApiError(403, "bot_admin_forbidden", "боту админка не положена");

        // Обычный маршрут, но с путём без префикса. Заголовки и тело не трогаем:
        // кука сессии игрока должна дойти до обработчиков как есть.
        return Route(request with { Path = inner });
    }

    private static string HandshakeJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("service", "vlalchillow");
            writer.WriteNumber("api", BotApiVersion);
            writer.WriteString("serverTime", DateTime.UtcNow.ToString("O"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
