using System.Globalization;

namespace StandChillow.LanServer.Web;

/// <summary>
/// «Завершить игру» у своей комнаты: <c>POST /api/rooms/{номер}/end</c> (#51).
/// </summary>
/// <remarks>
/// <para>
/// Запрос принимается только от владельца комнаты, и владелец берётся из сессии —
/// из куки, а не из тела запроса. Чужой (или вовсе анонимный) получает отказ: «Моя
/// комната» потому и называется моей.
/// </para>
/// <para>
/// Само завершение делает игровой хост: <c>C2=201</c> FinalHud по текущему счёту,
/// выдержка, <c>C2=255</c>, закрытие комнаты. Роутер только проверяет права и отдаёт
/// ответ — в нём нет ни строчки про провод.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private const string RoomEndPrefix = "/api/rooms/";
    private const string RoomEndSuffix = "/end";

    /// <summary>
    /// Разобрать путь <c>/api/rooms/12/end</c> → 12. Всё остальное — не этот маршрут.
    /// </summary>
    internal static bool TryParseRoomEndPath(string path, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(path))
            return false;
        if (!path.StartsWith(RoomEndPrefix, StringComparison.Ordinal)
            || !path.EndsWith(RoomEndSuffix, StringComparison.Ordinal))
            return false;

        // Короткие пути вроде «/api/rooms/end»: префикс и суффикс накладываются,
        // и резать нечего — это не наш маршрут, а не ошибка разбора.
        if (path.Length <= RoomEndPrefix.Length + RoomEndSuffix.Length)
            return false;

        var body = path[RoomEndPrefix.Length..^RoomEndSuffix.Length];
        return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out number)
               && number > 0;
    }

    private WebResponse EndRoom(WebRequestContext request, string path)
    {
        if (!TryParseRoomEndPath(path, out var number))
            return ApiError(404, "unknown_path", "нет такого маршрута");

        // Завершать катку может только авторизованный — как и создавать комнату.
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "сначала введи код из лобби");

        // Чужая комната: отвечаем без подробностей, но честно. Проверка по реестру,
        // а не по присланным данным — номер пришёл в пути, владельца знает реестр.
        var ownerId = _rooms.OwnerIdOf(number);
        if (ownerId <= 0)
            return ApiError(404, "no_room", $"комнаты #{number} нет");

        if (ownerId != session.PlayerId)
            return ApiError(403, "not_owner", "это не твоя комната — завершить её может только владелец");

        // Забаненный владелец катку не закрывает (#51).
        if (BannedRefusal(session) is { } banned)
            return banned;

        if (_options.EndRoom is null)
            return ApiError(503, "not_supported", "завершение с сайта не настроено на этом сервере");

        var error = _options.EndRoom(number, session.PlayerId);
        if (error is { Length: > 0 })
            return ApiError(409, "not_ended", error);

        return WebResponse.Json($"{{\"ok\":true,\"number\":{number}}}");
    }
}
