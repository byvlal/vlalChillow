using Microsoft.Data.Sqlite;

namespace StandChillow.Bot;

/// <summary>Привязка Telegram-пользователя к игроку на конкретном сервере.</summary>
public sealed record UserBinding(
    long TelegramId,
    string ServerId,
    int PlayerId,
    string Nick,
    string SessionToken,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

/// <summary>
/// SQLite-хранилище привязок «Telegram → игрок».
/// </summary>
/// <remarks>
/// <para>
/// Ключ — пара <c>(telegram_id, server_id)</c>: один и тот же человек может быть
/// привязан и к локальному серверу, и к Новосибирску, и это две разные сессии.
/// </para>
/// <para>
/// Токен сессии лежит рядом с привязкой: бот переживает перезапуск и не просит код
/// из лобби каждый раз. Сессия на сервере живёт 30 дней и продлевается при чтении;
/// когда сервер ответит 401, бот удалит привязку и попросит новый код.
/// </para>
/// </remarks>
public sealed class BotStore : IDisposable
{
    private readonly string _connectionString;
    private readonly string _path;

    public BotStore(string path)
    {
        _path = path;
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(dir))
        {
            Directory.CreateDirectory(dir);
            TrySetMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
        }.ToString();
    }

    public string DatabasePath => _path;

    public void EnsureCreated()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS users (
                telegram_id   INTEGER NOT NULL,
                server_id     TEXT    NOT NULL,
                player_id     INTEGER NOT NULL,
                nick          TEXT    NOT NULL,
                session_token TEXT    NOT NULL,
                created_utc   TEXT    NOT NULL,
                updated_utc   TEXT    NOT NULL,
                PRIMARY KEY (telegram_id, server_id)
            );
            """;
        command.ExecuteNonQuery();

        TrySetMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public UserBinding? Get(long telegramId, string serverId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT telegram_id, server_id, player_id, nick, session_token,
                   created_utc, updated_utc
            FROM users
            WHERE telegram_id = $tg AND server_id = $server;
            """;
        command.Parameters.AddWithValue("$tg", telegramId);
        command.Parameters.AddWithValue("$server", serverId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    /// <summary>
    /// Самая свежая привязка Telegram-пользователя — сервер, который он выбрал кодом
    /// из лобби последним.
    /// </summary>
    /// <remarks>
    /// После входа бот оставляет ровно одну привязку (<see cref="DeleteOthers"/>), но
    /// в базе могли остаться строки от старой версии: тогда порядок по
    /// <c>updated_utc</c> честно выбирает последнюю.
    /// </remarks>
    public UserBinding? GetActive(long telegramId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT telegram_id, server_id, player_id, nick, session_token,
                   created_utc, updated_utc
            FROM users
            WHERE telegram_id = $tg
            ORDER BY updated_utc DESC
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tg", telegramId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    /// <summary>Оставить только привязку к выбранному серверу: один человек — один сервер.</summary>
    public void DeleteOthers(long telegramId, string keepServerId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM users WHERE telegram_id = $tg AND server_id <> $server;";
        command.Parameters.AddWithValue("$tg", telegramId);
        command.Parameters.AddWithValue("$server", keepServerId);
        command.ExecuteNonQuery();
    }

    private static UserBinding ReadBinding(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            reader.GetString(4),
            DateTimeOffset.Parse(reader.GetString(5), null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(6), null,
                System.Globalization.DateTimeStyles.RoundtripKind));

    public void Upsert(
        long telegramId, string serverId, int playerId, string nick, string sessionToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (
                telegram_id, server_id, player_id, nick, session_token,
                created_utc, updated_utc)
            VALUES ($tg, $server, $player, $nick, $token, $now, $now)
            ON CONFLICT(telegram_id, server_id) DO UPDATE SET
                player_id = excluded.player_id,
                nick = excluded.nick,
                session_token = excluded.session_token,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$tg", telegramId);
        command.Parameters.AddWithValue("$server", serverId);
        command.Parameters.AddWithValue("$player", playerId);
        command.Parameters.AddWithValue("$nick", nick);
        command.Parameters.AddWithValue("$token", sessionToken);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    public void Delete(long telegramId, string serverId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM users WHERE telegram_id = $tg AND server_id = $server;";
        command.Parameters.AddWithValue("$tg", telegramId);
        command.Parameters.AddWithValue("$server", serverId);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static void TrySetMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (IOException)
        {
            // Права — гигиена, а не условие работы: на чужой ФС может не быть chmod.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
    }
}
