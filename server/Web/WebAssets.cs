using System.Globalization;
using System.Reflection;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Статические файлы панели: шрифт и видео-фон из встроенных ресурсов сборки.
/// </summary>
/// <remarks>
/// <para>
/// Рядом с бинарником файлов нет и быть не должно (см. WebPanelHost: HTML тоже
/// <c>EmbeddedResource</c>) — иначе «скачал exe, а сайт без шрифта и без фона».
/// Поэтому ресурс читается из сборки и режется по <c>Range</c>.
/// </para>
/// <para>
/// <c>Range</c> для видео не роскошь: без ответа <c>206</c> с
/// <c>Content-Range</c> часть мобильных браузеров не воспроизводит файл вовсе, а
/// перемотка невозможна. Поддерживаем один диапазон — этого достаточно любому
/// браузеру для progressive-воспроизведения.
/// </para>
/// <para>
/// Кэш долгий и <c>immutable</c>: имена файлов не меняются, а версия сервера — да.
/// При обновлении бинарника меняется и содержимое; для LAN-панели это нормально.
/// </para>
/// </remarks>
public sealed class WebAssets
{
    private readonly Assembly _assembly;
    private readonly Dictionary<string, (string Resource, string ContentType)> _files;
    private readonly object _gate = new();
    private readonly Dictionary<string, byte[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    public WebAssets(
        Assembly assembly, IReadOnlyDictionary<string, (string Resource, string ContentType)> files)
    {
        _assembly = assembly;
        _files = new Dictionary<string, (string, string)>(files, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Есть ли такой файл в сборке.</summary>
    public bool Exists(string path) => _files.ContainsKey(path);

    /// <summary>
    /// Отдать файл: <c>200</c> целиком или <c>206</c> по запрошенному диапазону.
    /// </summary>
    /// <param name="path">Путь вида <c>/assets/media/hero.mp4</c>.</param>
    /// <param name="rangeHeader">Заголовок <c>Range</c>; пусто — отдать целиком.</param>
    public WebResponse Serve(string path, string? rangeHeader)
    {
        if (!_files.TryGetValue(path, out var file))
            return WebResponse.Text("404", 404);

        var bytes = Load(file.Resource);
        if (bytes is null)
            return WebResponse.Text("404", 404);

        var headers = new List<(string, string)>
        {
            ("Accept-Ranges", "bytes"),
            ("Cache-Control", "public, max-age=86400"),
            ("X-Content-Type-Options", "nosniff"),
        };

        // Диапазон разбираем только если он валиден; кривой Range по стандарту
        // игнорируется, а не превращается в ошибку — так делают все веб-серверы.
        if (TryParseRange(rangeHeader, bytes.Length, out var start, out var end))
        {
            var length = end - start + 1;
            var slice = new byte[length];
            Buffer.BlockCopy(bytes, start, slice, 0, length);

            headers.Add(("Content-Range", $"bytes {start}-{end}/{bytes.Length}"));
            return new WebResponse(206, file.ContentType, slice, 0, headers);
        }

        return new WebResponse(200, file.ContentType, bytes, 0, headers);
    }

    private byte[]? Load(string resource)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(resource, out var cached))
                return cached;

            using var stream = _assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                Console.WriteLine($"[web] FATAL: ресурс '{resource}' не найден в сборке");
                return null;
            }

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            var bytes = buffer.ToArray();
            _cache[resource] = bytes;
            return bytes;
        }
    }

    /// <summary>
    /// Разобрать <c>Range: bytes=start-end</c>. Поддерживается один диапазон.
    /// </summary>
    /// <remarks>
    /// <c>bytes=500-</c> (до конца) и <c>bytes=-500</c> (последние 500 байт) — обе формы
    /// встречаются у медиаплееров, поэтому поддержаны обе.
    /// </remarks>
    internal static bool TryParseRange(
        string? header, int totalLength, out int start, out int end)
    {
        start = 0;
        end = totalLength - 1;

        if (string.IsNullOrWhiteSpace(header) || totalLength <= 0)
            return false;

        var text = header.Trim();
        const string prefix = "bytes=";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var spec = text[prefix.Length..];
        var dash = spec.IndexOf('-');
        if (dash < 0)
            return false;

        var left = spec[..dash].Trim();
        var right = spec[(dash + 1)..].Trim();

        if (left.Length == 0)
        {
            // bytes=-N: последние N байт.
            if (!int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out var suffix)
                || suffix <= 0)
            {
                return false;
            }

            suffix = Math.Min(suffix, totalLength);
            start = totalLength - suffix;
            end = totalLength - 1;
            return true;
        }

        if (!int.TryParse(left, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
            return false;

        if (right.Length == 0)
        {
            end = totalLength - 1;
        }
        else if (!int.TryParse(right, NumberStyles.Integer, CultureInfo.InvariantCulture, out end))
        {
            return false;
        }

        if (start >= totalLength)
        {
            start = 0;
            end = totalLength - 1;
            return false;
        }

        if (end >= totalLength)
            end = totalLength - 1;

        return end >= start;
    }
}
