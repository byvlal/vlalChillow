using System.Globalization;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Net.Match.Kernel;
using StandChillow.LanServer.Stats;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Публичная страница живой комнаты: <c>GET /api/room/&lt;номер&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// Ровно те данные, которые матч-хост раз в секунду кладёт в <see cref="RoomRegistry"/>:
/// состояние, счёт, раунд и состав. Денег здесь нет вовсе — это публичная страница, а не
/// итоги катки; <c>#/match/&lt;id&gt;</c> по-прежнему показывает полную таблицу.
/// </para>
/// <para>
/// Чтение свободное, без сессии: страницу комнаты можно открыть с телефона, не заходя в
/// игру. Отображаемый ID и админ-бейдж подставляются из живых источников панели, чтобы
/// кастомные ID и <c>admin.ids</c> не дублировались в реестре.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private const string RoomPrefix = "/api/room/";

    /// <summary>Разобрать <c>/api/room/12</c> → 12. Всё остальное — не этот маршрут.</summary>
    internal static bool TryParseRoomPath(string path, out int number)
    {
        number = 0;
        if (string.IsNullOrEmpty(path)
            || !path.StartsWith(RoomPrefix, StringComparison.Ordinal))
            return false;

        var body = path[RoomPrefix.Length..];
        if (body.Length == 0 || body.Contains('/'))
            return false;

        return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out number)
               && number > 0 && number <= RoomLimits.MaxRoomNumber;
    }

    private WebResponse RoomById(WebRequestContext request, string path)
    {
        if (!TryParseRoomPath(path, out var number))
            return ApiError(404, "unknown_path", "нет такого маршрута");

        if (!_rooms.TryGet(number, out var room))
            return ApiError(404, "room_closed", $"комнаты #{number} нет — она закрыта");

        // Ссылка на итоги — только у завершённой комнаты и только на катку, которая
        // началась после её создания: номер комнаты переиспользуется, старая запись
        // не должна выдавать себя за эту.
        MatchHistoryRecord? match = null;
        if (room.State == RoomState.Finished)
        {
            var latest = _options.Matches?.LatestForRoom(number);
            if (latest is not null && latest.EndedUtc >= room.CreatedUtc)
                match = latest;
        }

        var players = room.LivePlayers ?? Array.Empty<RoomLivePlayer>();
        var admin = _options.IsAdminById;
        var custom = _options.CustomIdGet;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartObject("room");
            writer.WriteNumber("number", room.Number);
            writer.WriteString("mode", room.ModeId);
            writer.WriteString("modeName", DisplayModeName(room.ModeId));
            writer.WriteString("map", room.Level);
            writer.WriteString("name", room.Name);
            writer.WriteString("state", room.State.ToString().ToLowerInvariant());
            writer.WriteNumber("playerCount", room.Players);
            writer.WriteNumber("scoreCt", room.ScoreCt);
            writer.WriteNumber("scoreTr", room.ScoreTr);
            writer.WriteNumber("round", room.Round);
            writer.WriteNumber("perTeam", room.PerTeam);
            writer.WriteNumber("maxPlayers", RoomCapacity.TotalFor(room.ModeId, room.PerTeam));
            writer.WriteString("teamLabel", RoomCapacity.Describe(room.ModeId, room.PerTeam));
            writer.WriteString("createdUtc", room.CreatedUtc.ToString("O"));

            writer.WriteStartObject("owner");
            writer.WriteNumber("id", room.OwnerId);
            writer.WriteString("nick", room.OwnerNick);
            writer.WriteString("displayId", custom?.Invoke(room.OwnerId) ?? room.OwnerId.ToString());
            writer.WriteBoolean("admin", admin?.Invoke(room.OwnerId) == true);
            writer.WriteEndObject();

            if (match is not null)
                writer.WriteNumber("matchId", match.Id);

            writer.WriteStartArray("players");
            foreach (var player in players)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", player.Id);
                writer.WriteString("nick", player.Nick);
                writer.WriteString("displayId", custom?.Invoke(player.Id) ?? player.Id.ToString());
                writer.WriteBoolean("admin", admin?.Invoke(player.Id) == true);
                writer.WriteString("team", player.Team);
                writer.WriteNumber("kills", player.Kills);
                writer.WriteNumber("assists", player.Assists);
                writer.WriteNumber("deaths", player.Deaths);
                writer.WriteNumber("score", player.Score);
                writer.WriteNumber("ping", player.Ping);
                writer.WriteBoolean("left", player.Left);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
