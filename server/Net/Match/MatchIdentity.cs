using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net.Match;

/// <summary>
/// Кто это на самом деле: устойчивый ключ игрока для матча (issue #39).
/// </summary>
/// <remarks>
/// 2.07 handshake-токен имеет вид <c>CLB&lt;md5(lobby user id)&gt;_&lt;guid сессии&gt;</c>
/// (форма зафиксирована в <see cref="MatchCodec.BuildHandshake207Request"/> и в комментариях
/// к живым capture: «CLB + device hash + session hash»). Гуид пересоздаётся на каждое
/// подключение, поэтому сравнивать полный токен нельзя: после потери сети тот же телефон
/// приходит с новым токеном, хост считает его новым игроком и выдаёт второй actor —
/// клиент не может это разобрать (<c>NullReferenceException</c> в <c>OnJoinRoomResponse</c>).
/// <para>
/// Устойчивая часть — всё до <c>_</c> (после снятия ведущей <c>C</c>; ровно так же хост
/// уже формирует actor <c>uid</c> для gap). Это <b>не</b> значение на проводе: ключ живёт
/// только в памяти сервера и нужен для поиска «того же игрока», пока его старый сокет
/// ещё не отвалился по таймауту.
/// </para>
/// </remarks>
public static class MatchIdentity
{
    /// <summary>Устойчивый ключ из 2.07 auth-токена (<c>CLB…_…</c>) — часть до <c>_</c>.</summary>
    public static string StableKeyFrom207Token(string? authToken)
    {
        if (string.IsNullOrWhiteSpace(authToken))
            return "";

        var token = authToken.Trim();
        // 2.07 gap identity writes the same token without its leading C (CLB… → LB…).
        if (token.Length > 1 && token[0] == 'C')
            token = token[1..];

        var cut = token.IndexOf('_');
        return cut > 0 ? token[..cut] : token;
    }

    /// <summary>Устойчивый ключ для legacy handshake (2.06-style userId) — сам userId.</summary>
    public static string StableKeyFromUserId(string? userId) =>
        string.IsNullOrWhiteSpace(userId) ? "" : userId.Trim();

    /// <summary>
    /// Ключ устройства из 2.07-токена: <c>CLB&lt;md5&gt;_&lt;guid&gt;</c> → <c>LB&lt;md5&gt;</c>.
    /// Возвращает пустую строку для legacy userId и любых строк не формата 2.07:
    /// такие значения нельзя использовать как device-бан.
    /// </summary>
    public static string DeviceKeyFrom207Token(string? authToken) =>
        BanRecord.NormalizeDevice(StableKeyFrom207Token(authToken));

    /// <summary>Один и тот же игрок? Регистр hex-части не должен решать.</summary>
    public static bool SamePlayer(string? a, string? b) =>
        !string.IsNullOrEmpty(a)
        && !string.IsNullOrEmpty(b)
        && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ключ владельца актора для реконнекта: устройство + ник. Устройство одно на
    /// установку, а аккаунтов на ней может быть несколько (разные ники). Без ника
    /// второй профиль «усыновил» бы актор первого и забрал его аккаунт.
    /// </summary>
    public static string RejoinKey(string? identityKey, string? nick)
    {
        var identity = (identityKey ?? string.Empty).Trim();
        if (identity.Length == 0)
            return string.Empty;

        var name = ServerConfig.NormalizeNick(nick);
        return name.Length == 0 ? identity : identity + "\u0000" + name;
    }

    /// <summary>
    /// Совпадает ли сохранённый ключ владельца актора с входящим ключом реконнекта.
    /// </summary>
    /// <remarks>
    /// Обычный случай — ключи равны. Отдельная ветка для legacy-клиента, который не
    /// прислал ник (входящий ключ без ника): раньше такой реконнект находил актор по
    /// одному устройству, и это поведение нельзя терять. Если ник пришёл, различаем
    /// профили строго: чужой ник не должен забрать актор.
    /// </remarks>
    public static bool SameRejoinPlayer(string? storedKey, string? incomingKey)
    {
        if (SamePlayer(storedKey, incomingKey))
            return true;

        if (string.IsNullOrEmpty(storedKey) || string.IsNullOrEmpty(incomingKey))
            return false;

        // Входящий ключ с ником — уже сравнили выше; фолбэк только для безника.
        if (incomingKey.IndexOf('\u0000') >= 0)
            return false;

        var cut = storedKey.IndexOf('\u0000');
        var storedIdentity = cut >= 0 ? storedKey[..cut] : storedKey;
        return SamePlayer(storedIdentity, incomingKey);
    }
}
