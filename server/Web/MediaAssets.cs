using System.Reflection;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Статические файлы панели: видео hero, постер и шрифты (<c>/media/имя</c>).
/// </summary>
/// <remarks>
/// <para>
/// Файлы лежат в <c>Web/Media</c> и вкомпилированы в сборку, поэтому один exe несёт
/// сайт целиком. Перед сборкой сначала проверяется папка <c>webmedia</c> рядом с exe:
/// владелец может положить туда свой <c>hero.mp4</c> и не пересобирать сервер.
/// </para>
/// <para>
/// Содержимое отдаёт роутер (<see cref="WebPanelRouter"/>): для видео обязателен
/// <c>Range</c> — мобильный Safari без него отказывается играть файл до конца.
/// </para>
/// </remarks>
public static class MediaAssets
{
    private const string Prefix = "web.media.";
    private static readonly Assembly Self = typeof(MediaAssets).Assembly;

    /// <summary>Папка рядом с exe, из которой файлы берутся в приоритете.</summary>
    public static string? DiskDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "webmedia");

    /// <summary>
    /// Прочитать файл по имени. Только имя: каталогов и переходов «..» здесь нет.
    /// </summary>
    public static byte[]? Read(string name)
    {
        if (!IsSafeName(name))
            return null;

        var disk = DiskDirectory is null ? null : Path.Combine(DiskDirectory, name);
        if (disk is not null && File.Exists(disk))
        {
            try { return File.ReadAllBytes(disk); }
            catch (IOException) { /* упали на диск — попробуем ресурс */ }
        }

        try
        {
            using var stream = Self.GetManifestResourceStream(Prefix + name);
            if (stream is null)
                return null;
            using var buffer = new MemoryStream((int)stream.Length);
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Тип содержимого по расширению. Неизвестное — <c>null</c>: не угадываем.</summary>
    public static string? ContentType(string name)
    {
        var ext = Path.GetExtension(name).ToLowerInvariant();
        return ext switch
        {
            ".mp4" => "video/mp4",
            ".webm" => "video/webm",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".woff2" => "font/woff2",
            ".woff" => "font/woff",
            _ => null,
        };
    }

    /// <summary>
    /// Кэш на стороне браузера: медиа и шрифты меняются только с новой сборкой,
    /// поэтому можно кэшировать смело. Владелец, подменивший файл в <c>webmedia</c>,
    /// увидит новый файл после перезапуска вкладки (сутки — потолок).
    /// </summary>
    public static string CacheControl(string name) =>
        name.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
            ? "public, max-age=31536000, immutable"
            : "public, max-age=86400";

    /// <summary>Имя без пути: одна секция, без «..», без разделителей каталога.</summary>
    private static bool IsSafeName(string name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100)
            return false;
        if (name.Contains('/') || name.Contains('\\'))
            return false;
        return !name.Contains("..", StringComparison.Ordinal);
    }
}
