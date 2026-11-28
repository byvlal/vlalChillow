using System.Globalization;
using System.Text;
using System.Text.Json;
using StandChillow.LanServer.Stats;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Таблица лидеров: <c>GET /api/top?sort=kills|kd|seconds|matches&amp;limit=50</c>.
/// </summary>
/// <remarks>
/// <para>
/// Источник — <see cref="PlayerStatsStore"/>: те же цифры, что видит игрок в своём
/// профиле, поэтому рейтинг не может «насчитать» лишнего. Роутер ничего не досчитывает,
/// кроме K/D (тем же правилом, что и публичный профиль).
/// </para>
/// <para>
/// Чтение свободное, без сессии: рейтинг — публичная страница. Баненые в таблицу не
/// попадают: в рейтинге не должно быть того, кого сервер не пускает играть.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private WebResponse TopPlayers(WebRequestContext request)
    {
        var rawSort = QueryValue(request.Query, "sort");
        var sort = ParseLeaderboardSort(rawSort);
        if (sort is null)
            return ApiError(400, "bad_sort", "sort: kills, kd, seconds или matches");

        var limit = Math.Clamp(QueryInt(request.Query, "limit", 50), 1, 100);

        // Берём максимум и уже потом режем: иначе баненные «съедали» места в десятке,
        // и таблица молча становилась короче запрошенной.
        var all = _options.Stats?.Top(sort.Value, 100)
                  ?? (IReadOnlyList<PlayerStatsSnapshot>)Array.Empty<PlayerStatsSnapshot>();
        var banned = _options.IsBanned;
        var admin = _options.IsAdminById;
        var custom = _options.CustomIdGet;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("ok", true);
            writer.WriteString("sort", SortKey(sort.Value));
            writer.WriteNumber("limit", limit);

            var written = 0;
            writer.WriteStartArray("players");
            foreach (var row in all)
            {
                if (written >= limit)
                    break;
                if (banned?.Invoke(row.Id) == true)
                    continue;

                written++;
                writer.WriteStartObject();
                writer.WriteNumber("place", written);
                writer.WriteNumber("id", row.Id);
                writer.WriteString("nick", row.Nick);
                writer.WriteString("displayId", custom?.Invoke(row.Id) ?? row.Id.ToString(CultureInfo.InvariantCulture));
                writer.WriteBoolean("admin", admin?.Invoke(row.Id) == true);
                writer.WriteNumber("kills", row.Kills);
                writer.WriteNumber("deaths", row.Deaths);
                writer.WriteNumber("kd", PlayerStatsStore.Kd(row));
                writer.WriteNumber("seconds", row.Seconds);
                writer.WriteNumber("matches", row.Matches);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("count", written);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }

    /// <summary>Строка запроса → метрика. Неизвестная (и пустая) — <c>null</c>.</summary>
    private static LeaderboardSort? ParseLeaderboardSort(string? raw) =>
        (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "" or "kills" => LeaderboardSort.Kills,
            "kd" => LeaderboardSort.Kd,
            "seconds" => LeaderboardSort.Seconds,
            "matches" => LeaderboardSort.Matches,
            _ => null,
        };

    private static string SortKey(LeaderboardSort sort) => sort switch
    {
        LeaderboardSort.Kd => "kd",
        LeaderboardSort.Seconds => "seconds",
        LeaderboardSort.Matches => "matches",
        _ => "kills",
    };
}
