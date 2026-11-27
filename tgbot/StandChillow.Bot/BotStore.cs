using Microsoft.Data.Sqlite;

namespace StandChillow.Bot;

/// <summary>Привязка Telegram-пользователя к игроку.</summary>
public sealed record UserBinding(
    long TelegramId,
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
/// Ключ — <c>telegram_id</c>: у человека одна активная привязка. Новый код из лобби
/// заменяет её, а не добавляет вторую.
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
        using var transaction = connection.BeginTransaction();

        // Старая версия хранила по строке на сервер. Оставляем самую свежую:
        // у человека теперь одна привязка, а не набор серверов.
        if (HasLegacyServerColumn(connection, transaction))
            MigrateLegacy(connection, transaction);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS users (
                    telegram_id   INTEGER NOT NULL PRIMARY KEY,
                    player_id     INTEGER NOT NULL,
                    nick          TEXT    NOT NULL,
                    session_token TEXT    NOT NULL,
                    created_utc   TEXT    NOT NULL,
                    updated_utc   TEXT    NOT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        TrySetMode(_path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public UserBinding? Get(long telegramId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT telegram_id, player_id, nick, session_token,
                   created_utc, updated_utc
            FROM users
            WHERE telegram_id = $tg;
            """;
        command.Parameters.AddWithValue("$tg", telegramId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBinding(reader) : null;
    }

    public void Upsert(
        long telegramId, int playerId, string nick, string sessionToken)
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users (
                telegram_id, player_id, nick, session_token,
                created_utc, updated_utc)
            VALUES ($tg, $player, $nick, $token, $now, $now)
            ON CONFLICT(telegram_id) DO UPDATE SET
                player_id = excluded.player_id,
                nick = excluded.nick,
                session_token = excluded.session_token,
                updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$tg", telegramId);
        command.Parameters.AddWithValue("$player", playerId);
        command.Parameters.AddWithValue("$nick", nick);
        command.Parameters.AddWithValue("$token", sessionToken);
        command.Parameters.AddWithValue("$now", now);
        command.ExecuteNonQuery();
    }

    public void Delete(long telegramId)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM users WHERE telegram_id = $tg;";
        command.Parameters.AddWithValue("$tg", telegramId);
        command.ExecuteNonQuery();
    }

    private static bool HasLegacyServerColumn(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(users);";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (string.Equals(reader.GetString(1), "server_id", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void MigrateLegacy(
        SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DROP TABLE IF EXISTS users_new;

            CREATE TABLE users_new (
                telegram_id   INTEGER NOT NULL PRIMARY KEY,
                player_id     INTEGER NOT NULL,
                nick          TEXT    NOT NULL,
                session_token TEXT    NOT NULL,
                created_utc   TEXT    NOT NULL,
                updated_utc   TEXT    NOT NULL
            );

            INSERT INTO users_new (
                telegram_id, player_id, nick, session_token, created_utc, updated_utc)
            SELECT telegram_id, player_id, nick, session_token, created_utc, updated_utc
            FROM users AS u
            WHERE u.rowid = (
                SELECT x.rowid
                FROM users AS x
                WHERE x.telegram_id = u.telegram_id
                ORDER BY x.updated_utc DESC, x.rowid DESC
                LIMIT 1
            );

            DROP TABLE users;
            ALTER TABLE users_new RENAME TO users;
            """;
        command.ExecuteNonQuery();
    }

    private static UserBinding ReadBinding(SqliteDataReader reader) =>
        new(
            reader.GetInt64(0),
            reader.GetInt32(1),
            reader.GetString(2),
            reader.GetString(3),
            DateTimeOffset.Parse(reader.GetString(4), null,
                System.Globalization.DateTimeStyles.RoundtripKind),
            DateTimeOffset.Parse(reader.GetString(5), null,
                System.Globalization.DateTimeStyles.RoundtripKind));

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
