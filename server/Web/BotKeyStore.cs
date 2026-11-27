using System.Security.Cryptography;
using System.Text;

namespace StandChillow.LanServer.Web;

/// <summary>
/// Постоянный ключ связи с Telegram-ботом: один секрет на сервер.
/// </summary>
/// <remarks>
/// <para>
/// Ключ лежит в <c>auth/tgbot/password.txt</c> рядом с паролями админов: каталог
/// <c>auth</c> закрыт (700), файл — 600. Наружу он не отдаётся ни страницей, ни API:
/// веб-роутер умеет только <c>/</c> и <c>/media/*</c>, а <c>/api/bot/*</c> требует
/// этот ключ в заголовке <c>X-Bot-Key</c>.
/// </para>
/// <para>
/// Ключ — не авторизация игрока. Игрок по-прежнему вводит код из лобби и получает
/// куку <c>sc_session</c>; ключ лишь говорит серверу, что запрос пришёл от доверенного
/// бота, а не от случайного клиента.
/// </para>
/// <para>
/// Ротация атомарная: новый файл пишется во временный и переименовывается. После
/// <see cref="Rotate"/> старый ключ перестаёт совпадать сразу — <see cref="Matches"/>
/// читает текущее значение из памяти.
/// </para>
/// </remarks>
public sealed class BotKeyStore
{
    /// <summary>Подкаталог <c>auth</c>, в котором живёт ключ бота.</summary>
    public const string DirectoryName = "tgbot";

    /// <summary>Имя файла с ключом.</summary>
    public const string FileName = "password.txt";

    /// <summary>Длина ключа в hex-символах: 32 случайных байта.</summary>
    public const int KeyHexLength = 64;

    private const UnixFileMode DirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode SecretFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly Action<string>? _log;
    private string? _current;

    public BotKeyStore(string directory, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(directory))
            throw new ArgumentException("каталог ключа бота не может быть пустым", nameof(directory));

        _directory = Path.GetFullPath(directory);
        _log = log;
    }

    /// <summary>Каталог <c>auth/tgbot</c>.</summary>
    public string DirectoryPath => _directory;

    /// <summary>Полный путь к <c>password.txt</c>.</summary>
    public string FilePath => Path.Combine(_directory, FileName);

    /// <summary>Текущий ключ или <c>null</c>, если файл не удалось создать.</summary>
    public string? Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    /// <summary>
    /// Прочитать ключ с диска; если файла нет или он битый — создать новый.
    /// </summary>
    /// <returns><c>true</c> — ключ есть и пригоден.</returns>
    public bool LoadOrCreate()
    {
        lock (_gate)
        {
            EnsureDirectory();

            if (File.Exists(FilePath))
            {
                try
                {
                    var text = File.ReadAllText(FilePath, Encoding.UTF8).Trim().ToLowerInvariant();
                    if (IsValid(text))
                    {
                        _current = text;
                        Log($"[tgbot] ключ загружен: {FilePath}");
                        return true;
                    }

                    Log($"[tgbot] {FilePath}: не hex64 — переименую в .bad и создам новый");
                    TryMoveBad();
                }
                catch (Exception ex)
                {
                    Log($"[tgbot] не смог прочитать {FilePath}: {ex.Message}");
                }
            }

            var key = GenerateKey();
            if (!TryWrite(key, out var error))
            {
                _current = null;
                Log($"[tgbot] не смог создать ключ: {error}");
                return false;
            }

            _current = key;
            Log($"[tgbot] ключ создан: {FilePath}");
            return true;
        }
    }

    /// <summary>
    /// Сгенерировать новый ключ и записать его вместо старого.
    /// </summary>
    /// <remarks>
    /// При ошибке записи старый ключ остаётся действующим: лучше сохранить связь,
    /// чем оставить сервер без ключа.
    /// </remarks>
    public BotKeyRotation Rotate()
    {
        lock (_gate)
        {
            EnsureDirectory();

            var key = GenerateKey();
            if (!TryWrite(key, out var error))
                return new BotKeyRotation(false, null, FilePath, error);

            _current = key;
            Log($"[tgbot] ключ сброшен: {FilePath}");
            return new BotKeyRotation(true, key, FilePath, null);
        }
    }

    /// <summary>Совпадает ли присланный ключ с текущим (constant-time).</summary>
    public bool Matches(string? provided)
    {
        var expected = Current;
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided))
            return false;

        var actual = Encoding.UTF8.GetBytes(provided.Trim());
        var wanted = Encoding.UTF8.GetBytes(expected);
        return actual.Length == wanted.Length
               && CryptographicOperations.FixedTimeEquals(actual, wanted);
    }

    /// <summary>64 hex-символа в нижнем регистре.</summary>
    public static bool IsValid(string? key)
    {
        if (key is not { Length: KeyHexLength })
            return false;

        foreach (var c in key)
        {
            if (c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))
                return false;
        }

        return true;
    }

    /// <summary>32 случайных байта → 64 hex-символа.</summary>
    public static string GenerateKey() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private bool TryWrite(string key, out string? error)
    {
        try
        {
            EnsureDirectory();
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, key + Environment.NewLine, new UTF8Encoding(false));
            TrySetMode(temp, SecretFileMode);
            File.Move(temp, FilePath, overwrite: true);
            TrySetMode(FilePath, SecretFileMode);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void EnsureDirectory()
    {
        Directory.CreateDirectory(_directory);
        TrySetMode(_directory, DirectoryMode);
    }

    private void TryMoveBad()
    {
        try
        {
            File.Move(FilePath, FilePath + ".bad", overwrite: true);
        }
        catch (Exception ex)
        {
            Log($"[tgbot] не смог переименовать битый {FilePath}: {ex.Message}");
        }
    }

    private void Log(string message) => _log?.Invoke(message);

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (Exception)
        {
            // FAT/exFAT и некоторые разделы не умеют chmod — это не повод падать.
        }
    }
}

/// <summary>Результат ротации ключа бота.</summary>
/// <param name="Ok">Удалось ли записать новый ключ.</param>
/// <param name="Key">Новый ключ; <c>null</c> при ошибке.</param>
/// <param name="Path">Путь к файлу ключа.</param>
/// <param name="Error">Текст ошибки; <c>null</c> при успехе.</param>
public readonly record struct BotKeyRotation(bool Ok, string? Key, string Path, string? Error);
