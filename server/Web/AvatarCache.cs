using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Аватары игроков на диске: папка <c>avatars/</c> рядом с exe.
/// </summary>
/// <remarks>
/// <para>
/// Сервер отдаёт сайту ровно те байты, что клиент прислал в лобби, но живут они только
/// пока игрок в лобби. После выхода профиль оставался без картинки — хотя человек её уже
/// загрузил. Поэтому каждый успешный вход в лобби кладёт аватар в кэш: он переживает
/// выход, рестарт и перезагрузку страницы.
/// </para>
/// <para>
/// Размер: всё, что больше 1 МБ, пережимается в JPEG не больше 512 px по длинной стороне
/// (качество 85; если всё ещё больше — ниже). Маленькие файлы не трогаем: повторное
/// кодирование только портит картинку. WebP ImageSharp 2.x не декодирует — такой файл
/// больше 1 МБ просто не кэшируем, а не отдаём гиганта.
/// </para>
/// <para>
/// Потолок — <see cref="MaxFiles"/> файлов / <see cref="MaxBytes"/> байт: старые
/// вытесняются по времени изменения. Запись атомарная (tmp → замена), имя —
/// <c>&lt;id&gt;.&lt;ext&gt;</c>; при обновлении старые расширения удаляются.
/// </para>
/// </remarks>
public sealed class AvatarCache
{
    /// <summary>Сколько файлов держим максимум.</summary>
    public const int MaxFiles = 2_000;

    /// <summary>Общий потолок папки — 128 МБ.</summary>
    public const long MaxBytes = 128L * 1024 * 1024;

    /// <summary>Максимальный размер одного аватара после пережатия — 1 МБ.</summary>
    public const int MaxStoredBytes = 1024 * 1024;

    /// <summary>Длинная сторона пережатой картинки.</summary>
    public const int MaxSide = 512;

    /// <summary>Качество JPEG по умолчанию; ниже — только если всё ещё больше лимита.</summary>
    public const int JpegQuality = 85;

    /// <summary>
    /// Потолок исходника: больше 36 МП не декодируем вовсе. Аватар в игре — 256×256,
    /// а «фото 20000×20000» в маленьком PNG съело бы гигабайты памяти прямо в потоке лобби.
    /// </summary>
    private const long MaxSourcePixels = 36_000_000;

    private static readonly string[] Extensions = { ".jpg", ".jpeg", ".png", ".webp" };

    private readonly object _gate = new();
    private readonly string _dir;

    public AvatarCache(string dir)
    {
        _dir = dir;
        try
        {
            Directory.CreateDirectory(_dir);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[avatar] WARN не создал {_dir}: {ex.Message}");
        }

        Console.WriteLine(
            $"[avatar] кэш: {_dir} (лимит {MaxFiles} файлов / {MaxBytes / (1024 * 1024)} МБ)");
    }

    /// <summary>Папка кэша — для логов.</summary>
    public string DirectoryPath => _dir;

    /// <summary>Сколько аватаров сейчас на диске.</summary>
    public int Count
    {
        get
        {
            try { return Directory.EnumerateFiles(_dir).Count(); }
            catch { return 0; }
        }
    }

    /// <summary>
    /// Положить аватар игрока в кэш. <c>null</c>/пусто — ничего не делаем: старый
    /// аватар остаётся, пока клиент не пришлёт новый.
    /// </summary>
    public void Store(int playerId, byte[]? bytes)
    {
        if (playerId <= 0 || bytes is null || bytes.Length == 0)
            return;

        var type = ImageContentType(bytes);
        if (type is null)
            return;

        try
        {
            var (data, ext) = Prepare(bytes, type);
            if (data is null || ext is null)
                return;

            lock (_gate)
            {
                // Старые расширения того же игрока — в мусор: <id>.jpg и <id>.png
                // одновременно означали бы «какой из них настоящий?».
                foreach (var existing in Extensions)
                {
                    var path = Path.Combine(_dir, playerId + existing);
                    if (existing == ext || !File.Exists(path))
                        continue;
                    try { File.Delete(path); } catch { /* best effort */ }
                }

                var target = Path.Combine(_dir, playerId + ext);
                var tmp = target + ".tmp";
                File.WriteAllBytes(tmp, data);
                File.Move(tmp, target, overwrite: true);
            }

            Prune();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[avatar] WARN не сохранил id {playerId}: {ex.Message}");
        }
    }

    /// <summary>Есть ли аватар в кэше (файл на диске).</summary>
    public bool Has(int playerId)
    {
        if (playerId <= 0)
            return false;

        foreach (var ext in Extensions)
        {
            try
            {
                if (File.Exists(Path.Combine(_dir, playerId + ext)))
                    return true;
            }
            catch
            {
                // Недоступный файл — считаем, что нет: это только бейдж/превью.
            }
        }

        return false;
    }

    /// <summary>Аватар игрока из кэша или <c>null</c>, если его там нет.</summary>
    public byte[]? TryRead(int playerId)
    {
        if (playerId <= 0)
            return null;

        foreach (var ext in Extensions)
        {
            var path = Path.Combine(_dir, playerId + ext);
            try
            {
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
            catch
            {
                // Файл заняли/удалили параллельно — пробуем следующее расширение.
            }
        }

        return null;
    }

    /// <summary>
    /// Перевести имена файлов со старых id (хеш ника) на аккаунты. Неизвестные
    /// переносятся в <c>legacy/</c>, а не удаляются: там их ещё можно опознать руками.
    /// Возвращает число перенесённых файлов.
    /// </summary>
    public int RemapIds(Func<int, int> map)
    {
        var moved = 0;
        List<FileInfo> files;
        lock (_gate)
            files = new DirectoryInfo(_dir).EnumerateFiles().ToList();

        foreach (var file in files)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file.Name), out var oldId)
                || oldId <= 0)
                continue;

            // Старый хеш ника может быть больше FirstId и выглядеть как номер аккаунта,
            // поэтому map зовём всегда. Настоящий номер она возвращает без изменений,
            // и файл пропускается — повторная миграция ничего не двигает.
            var mapped = map(oldId);
            if (mapped == oldId)
                continue;

            var legacyDir = Path.Combine(_dir, "legacy");
            try
            {
                if (mapped is < StandChillow.LanServer.Bans.AccountStore.FirstId
                    or > StandChillow.LanServer.Bans.AccountStore.MaxId)
                {
                    Directory.CreateDirectory(legacyDir);
                    var fallback = Path.Combine(legacyDir, file.Name);
                    if (!File.Exists(fallback))
                        File.Move(file.FullName, fallback);
                    else
                        File.Delete(file.FullName);
                    moved++;
                    continue;
                }

                var target = Path.Combine(_dir, mapped + file.Extension);
                if (File.Exists(target))
                {
                    // Новый id уже занят более свежим аватаром — старый в архив.
                    Directory.CreateDirectory(legacyDir);
                    var fallback = Path.Combine(legacyDir, file.Name);
                    if (!File.Exists(fallback))
                        File.Move(file.FullName, fallback);
                    else
                        File.Delete(file.FullName);
                }
                else
                {
                    File.Move(file.FullName, target);
                }

                moved++;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[avatar] WARN не перенёс {file.Name}: {ex.Message}");
            }
        }

        if (moved > 0)
            Console.WriteLine($"[avatar] переведено на аккаунты: {moved} файлов ({_dir})");
        return moved;
    }

    /// <summary>
    /// Привести присланные байты к виду «не больше 1 МБ». Маленькие файлы возвращаются
    /// как есть; тяжёлые JPEG/PNG пережимаются. Не удалось — <c>(null, null)</c>.
    /// </summary>
    private static (byte[]? Data, string? Ext) Prepare(byte[] bytes, string contentType)
    {
        var ext = contentType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => null,
        };
        if (ext is null)
            return (null, null);

        if (bytes.Length <= MaxStoredBytes)
            return (bytes, ext);

        try
        {
            // Сначала только заголовок: огромную картинку нельзя даже декодировать —
            // это память и время прямо в потоке лобби.
            var info = Image.Identify(bytes);
            if (info.Width <= 0 || info.Height <= 0
                || info.Width * (long)info.Height > MaxSourcePixels)
            {
                Console.WriteLine(
                    $"[avatar] пропустил {info.Width}×{info.Height} — больше {MaxSourcePixels} пикселей");
                return (null, null);
            }

            using var image = Image.Load(bytes);
            if (image.Width > MaxSide || image.Height > MaxSide)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Mode = ResizeMode.Max,
                    Size = new Size(MaxSide, MaxSide),
                }));
            }

            foreach (var quality in new[] { JpegQuality, 75, 65, 50 })
            {
                using var ms = new MemoryStream();
                image.SaveAsJpeg(ms, new JpegEncoder { Quality = quality });
                if (ms.Length <= MaxStoredBytes)
                    return (ms.ToArray(), ".jpg");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[avatar] WARN не пережал {contentType} {bytes.Length} Б: {ex.Message}");
        }

        return (null, null);
    }

    /// <summary>
    /// Вытеснить старые аватары, если папка вышла за потолок. Считаем по времени
    /// изменения: аватар обновляют редко, так что «самый старый» ≈ «самый давний».
    /// </summary>
    private void Prune()
    {
        try
        {
            List<FileInfo> files;
            lock (_gate)
                files = new DirectoryInfo(_dir).EnumerateFiles().ToList();

            long total = files.Sum(f => f.Length);
            if (files.Count <= MaxFiles && total <= MaxBytes)
                return;

            var ordered = files.OrderBy(f => f.LastWriteTimeUtc).ToList();
            foreach (var file in ordered)
            {
                if (files.Count <= MaxFiles && total <= MaxBytes)
                    break;

                try
                {
                    var length = file.Length;
                    file.Delete();
                    total -= length;
                    files.Remove(file);
                }
                catch
                {
                    // Файл уже удалён/занят — просто идём дальше.
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[avatar] WARN не почистил кэш: {ex.Message}");
        }
    }

    /// <summary>
    /// Тип картинки по magic-байтам — тот же набор, что отдаёт панель. Неизвестное —
    /// <c>null</c>: не угадываем и в кэш не кладём.
    /// </summary>
    internal static string? ImageContentType(byte[]? bytes)
    {
        if (bytes is null || bytes.Length < 12)
            return null;

        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";

        if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return "image/png";

        if (bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F'
            && bytes[3] == (byte)'F' && bytes[8] == (byte)'W' && bytes[9] == (byte)'E')
            return "image/webp";

        return null;
    }
}
