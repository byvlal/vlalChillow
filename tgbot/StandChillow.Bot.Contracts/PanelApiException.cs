namespace StandChillow.Bot.Contracts;

/// <summary>
/// Сервер ответил ошибкой в формате панели (<c>{"code":"...","message":"..."}</c>).
/// </summary>
/// <remarks>
/// <para>
/// Код — машинный, по нему бот решает, что делать: <c>no_auth</c> — попросить новый код
/// из лобби, <c>bad_bot_key</c> — сказать владельцу про ключ, <c>player_offline</c> —
/// отправить человека в игру. Текст сервера уже русский и показывается как есть.
/// </para>
/// <para>
/// <see cref="RetryAfterSec"/> приходит из <c>Retry-After</c>: для лимитов жалоб это
/// единственное честное «когда можно снова».
/// </para>
/// </remarks>
public sealed class PanelApiException : Exception
{
    public PanelApiException(
        int statusCode,
        string code,
        string message,
        int retryAfterSec = 0,
        Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Code = code;
        RetryAfterSec = retryAfterSec;
    }

    /// <summary>HTTP-статус ответа.</summary>
    public int StatusCode { get; }

    /// <summary>Машинный код панели.</summary>
    public string Code { get; }

    /// <summary>Сколько секунд ждать до повтора (0 — сервер не сказал).</summary>
    public int RetryAfterSec { get; }

    /// <summary>Сессия игрока больше не действует — нужен новый код из лобби.</summary>
    public bool IsAuthLost => StatusCode == 401;

    /// <summary>Ключ бота не подошёл или не создан.</summary>
    public bool IsBotKeyProblem =>
        Code is "bad_bot_key" or "bot_disabled" or "bot_key_unavailable";

    /// <summary>Сервер не готов отдавать данные (не настроен, ещё поднимается).</summary>
    public bool IsNotReady => StatusCode == 503;
}
