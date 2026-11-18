using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Статистика профиля (<c>GET /api/stats/me</c>).
/// </summary>
/// <remarks>
/// <para>
/// Статистика читается из <see cref="PlayerStatsStore"/> — того же хранилища на диске,
/// куда пишут боевые пути матча. Нет записи — честные нули, а не ошибка: игрок просто
/// ещё не играл.
/// </para>
/// <para>
/// Админ панель переехала в <see cref="WebPanelRouter.Admin"/> — там же тройной затвор
/// доступа (сессия сайта → живой admin.ids → админ-кука этого запуска).
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private WebResponse MyStats(WebRequestContext request)
    {
        var session = SessionOf(request);
        if (session is null)
            return ApiError(401, "no_auth", "нужно ввести код из лобби");

        if (BannedRefusal(session) is { } banned)
            return banned;

        var snap = _options.Stats?.Get(session.PlayerId);
        var kills = snap?.Kills ?? 0;
        var deaths = snap?.Deaths ?? 0;

        // KD: на ноль делить нельзя — при нулях смертях и нулях киллов это 0,
        // а с киллами и без смертей показываем сами киллы (5:0 → 5.00).
        var kd = deaths > 0
            ? Math.Round((double)kills / deaths, 2)
            : kills > 0 ? (double)kills : 0.0;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, JsonText.WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", session.PlayerId);
            writer.WriteString("nick", session.Nick);
            var custom = _options.CustomIdGet?.Invoke(session.PlayerId);
            writer.WriteString("customId", custom);
            writer.WriteString("displayId",
                custom ?? session.PlayerId.ToString(CultureInfo.InvariantCulture));
            writer.WriteNumber("kills", kills);
            writer.WriteNumber("deaths", deaths);
            writer.WriteNumber("kd", kd);
            writer.WriteNumber("seconds", snap?.Seconds ?? 0);
            writer.WriteNumber("matches", snap?.Matches ?? 0);
            writer.WriteEndObject();
        }

        return WebResponse.Json(Encoding.UTF8.GetString(stream.ToArray()));
    }
}
