using System.Text;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Раздача статики панели: <c>GET /media/имя</c> с поддержкой <c>Range</c>.
/// </summary>
/// <remarks>
/// <para>
/// Range нужен не для красоты: мобильный Safari играет видео только если сервер умеет
/// отдавать куски, а Chrome перематывает по нему. Поддержан ровно один диапазон —
/// столько шлёт любой браузер для <c>&lt;video&gt;</c>; список диапазонов играем как
/// обычный ответ целиком.
/// </para>
/// <para>
/// Файлы отдаются целиком в память (видео ~18 МБ) — предохранитель от частых запросов
/// уже в лимитах панели, а файл один и тот же.
/// </para>
/// </remarks>
public sealed partial class WebPanelRouter
{
    private WebResponse ServeMedia(WebRequestContext request, string path)
    {
        var name = path["/media/".Length..];
        var bytes = MediaAssets.Read(name);
        if (bytes is null)
            return WebResponse.Text("404", 404);

        var type = MediaAssets.ContentType(name) ?? "application/octet-stream";
        var headers = new List<(string, string)>
        {
            ("Accept-Ranges", "bytes"),
            ("Cache-Control", MediaAssets.CacheControl(name)),
        };

        var range = ParseRange(request.Headers, bytes.Length);
        if (range is null)
            return new WebResponse(200, type, bytes, 0, headers);

        var (from, to) = range.Value;
        if (from < 0 || from > to || from >= bytes.Length)
        {
            // Диапазон за краем файла — честный 416 с картой длины.
            headers.Add(("Content-Range", $"bytes */{bytes.Length}"));
            return new WebResponse(416, "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("416"), 0, headers);
        }

        var slice = new byte[to - from + 1];
        Array.Copy(bytes, from, slice, 0, slice.Length);
        headers.Add(("Content-Range", $"bytes {from}-{to}/{bytes.Length}"));
        return new WebResponse(206, type, slice, 0, headers);
    }

    /// <summary>
    /// Разобрать <c>Range: bytes=…</c>. Возвращает (от, до) включительно или <c>null</c>,
    /// если заголовка нет или он не «bytes=N-…» — такой файл играется целиком.
    /// </summary>
    private static (int From, int To)? ParseRange(
        IReadOnlyDictionary<string, string> headers, int total)
    {
        if (total <= 0)
            return null;
        if (!headers.TryGetValue("Range", out var raw) &&
            !headers.TryGetValue("range", out raw))
            return null;
        if (raw is null || !raw.StartsWith("bytes=", StringComparison.Ordinal))
            return null;

        var spec = raw["bytes=".Length..].Split(',')[0].Trim();

        int dash;
        if ((dash = spec.IndexOf('-')) < 0)
            return null;
        var startToken = spec[..dash].Trim();
        var endToken = spec[(dash + 1)..].Trim();

        // «bytes=-N» — последние N байт.
        if (startToken.Length == 0)
        {
            if (!int.TryParse(endToken, out var suffix) || suffix <= 0)
                return null;
            var from = Math.Max(0, total - suffix);
            return (from, total - 1);
        }

        if (!int.TryParse(startToken, out var first) || first < 0)
            return null;
        var last = endToken.Length == 0
            ? total - 1
            : Math.Min(int.TryParse(endToken, out var end) ? end : total - 1, total - 1);
        return (first, last);
    }
}
