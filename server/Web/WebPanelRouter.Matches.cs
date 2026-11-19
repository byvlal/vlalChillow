using System.Globalization;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Stats;

namespace StandChillow.LanServer.Web;

/// <summary>
/// История матчей: список для профиля и полная таблица одной катки.
/// </summary>
/// <remarks>
/// <para>
/// Данные отдаёт <see cref="MatchHistoryStore"/> — тот же, что пишет игровой хост на
/// финале. Роутер ничего не считает и не досчитывает: чего нет в записи, того нет и в
/// ответе.
/// </para>
/// <para>
/// Отображаемый ID и админ-бейдж подставляются из живых источников панели: запись
/// хранит только серверный id и ник, чтобы баны/кастомные ID не дублировались в файле.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private const string MatchPrefix = "/api/match/";

    // ── маршруты ─────────────────────────────────────────────────────────────

    private WebResponse MatchesList(WebRequestContext request)
    {
        var rawPlayer = QueryValue(request.Query, "player");
        if (!int.TryParse(rawPlayer, NumberStyles.None, CultureInfo.InvariantCulture, out var playerId)
            || playerId <= 0)
            return ApiError(400, "bad_player", "укажи числовой ID игрока");

        var rawLimit = QueryValue(request.Query, "limit");
        var limit = 20;
        if (int.TryParse(rawLimit, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 100);

        var matches = _options.Matches?.ForPlayer(playerId, limit)
                      ?? (IReadOnlyList<MatchHistoryRecord>)Array.Empty<MatchHistoryRecord>();

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteNumber("player", playerId);
            writer.WriteStartArray("matches");
            foreach (var match in matches)
                WriteMatchSummary(writer, match, playerId);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    private WebResponse MatchById(WebRequestContext request, string path)
    {
        if (!TryParseMatchPath(path, out var id))
            return ApiError(404, "unknown_path", "нет такого маршрута");

        var match = _options.Matches?.Get(id);
        if (match is null)
            return ApiError(404, "not_found", $"матча #{id} нет в истории");

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteStartObject("match");
            WriteMatchMeta(writer, match);
            writer.WriteStartArray("players");
            foreach (var player in match.Players)
            {
                writer.WriteStartObject();
                WriteMatchPlayer(writer, player, full: true);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    internal static bool TryParseMatchPath(string path, out int id)
    {
        id = 0;
        if (string.IsNullOrEmpty(path)
            || !path.StartsWith(MatchPrefix, StringComparison.Ordinal))
            return false;

        var body = path[MatchPrefix.Length..];
        if (body.Length == 0 || body.Contains('/'))
            return false;

        return int.TryParse(body, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }

    // ── JSON ─────────────────────────────────────────────────────────────────

    /// <summary>Короткая запись для списка: без полного состава, только «ты в этой катке».</summary>
    private void WriteMatchSummary(Utf8JsonWriter writer, MatchHistoryRecord match, int playerId)
    {
        writer.WriteStartObject();
        WriteMatchMeta(writer, match);

        var you = match.Player(playerId);
        if (you is not null)
        {
            writer.WriteStartObject("you");
            WriteMatchPlayer(writer, you, full: false);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    private static void WriteMatchMeta(Utf8JsonWriter writer, MatchHistoryRecord match)
    {
        writer.WriteNumber("id", match.Id);
        writer.WriteString("started", match.StartedUtc.ToString("O"));
        writer.WriteString("ended", match.EndedUtc.ToString("O"));
        writer.WriteString("mode", match.ModeId);
        writer.WriteString("map", match.Map);
        writer.WriteNumber("scoreCt", match.ScoreCt);
        writer.WriteNumber("scoreTr", match.ScoreTr);
        writer.WriteString("winner", match.Winner);
        writer.WriteNumber("rounds", match.Rounds);
        writer.WriteNumber("room", match.RoomNumber);
        writer.WriteString("roomName", match.RoomName ?? "");
    }

    private void WriteMatchPlayer(Utf8JsonWriter writer, MatchHistoryPlayer player, bool full)
    {
        writer.WriteNumber("id", player.Id);
        if (full)
        {
            writer.WriteString("nick", player.Nick);
            var custom = _options.CustomIdGet?.Invoke(player.Id);
            writer.WriteString("displayId",
                string.IsNullOrWhiteSpace(custom)
                    ? player.Id.ToString(CultureInfo.InvariantCulture)
                    : custom);
            writer.WriteBoolean("admin", _options.IsAdminById?.Invoke(player.Id) ?? false);
        }

        writer.WriteString("team", player.Team);
        writer.WriteNumber("kills", player.Kills);
        writer.WriteNumber("assists", player.Assists);
        writer.WriteNumber("deaths", player.Deaths);
        writer.WriteNumber("score", player.Score);
        writer.WriteNumber("money", player.Money);
        writer.WriteNumber("ping", player.Ping);
        writer.WriteBoolean("left", player.Left);
    }
}
