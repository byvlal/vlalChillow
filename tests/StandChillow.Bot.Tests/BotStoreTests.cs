using Microsoft.Data.Sqlite;
using StandChillow.Bot;
using Xunit;

namespace StandChillow.Bot.Tests;

/// <summary>
/// Привязки Telegram → игрок: одна на человека, новый код заменяет старую.
/// </summary>
public sealed class BotStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly BotStore _store;

    public BotStoreTests()
    {
        _dir = Path.Combine(
            Path.GetTempPath(), "vlal-bot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        _store = new BotStore(Path.Combine(_dir, "bot.db"));
        _store.EnsureCreated();
    }

    public void Dispose()
    {
        _store.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог — не повод валить тест.
        }
    }

    [Fact]
    public void Upsert_ThenGet_ReturnsBinding()
    {
        _store.Upsert(42, 1000001, "влал", "token-1");

        var binding = _store.Get(42);

        Assert.NotNull(binding);
        Assert.Equal(42, binding!.TelegramId);
        Assert.Equal(1000001, binding.PlayerId);
        Assert.Equal("влал", binding.Nick);
        Assert.Equal("token-1", binding.SessionToken);
    }

    [Fact]
    public void Upsert_ReplacesTheSameBinding()
    {
        _store.Upsert(42, 1000001, "старый", "token-1");
        var first = _store.Get(42)!;

        _store.Upsert(42, 1000002, "новый", "token-2");
        var second = _store.Get(42)!;

        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.Equal(1000002, second.PlayerId);
        Assert.Equal("новый", second.Nick);
        Assert.Equal("token-2", second.SessionToken);
        Assert.True(second.UpdatedUtc >= first.UpdatedUtc);
    }

    [Fact]
    public void DifferentTelegramUsers_DoNotInterfere()
    {
        _store.Upsert(1, 1000001, "первый", "token-1");
        _store.Upsert(2, 1000002, "второй", "token-2");

        Assert.Equal("первый", _store.Get(1)!.Nick);
        Assert.Equal("второй", _store.Get(2)!.Nick);

        _store.Delete(1);
        Assert.Null(_store.Get(1));
        Assert.NotNull(_store.Get(2));
    }

    [Fact]
    public void Binding_SurvivesReopen()
    {
        _store.Upsert(42, 1000002, "новосибирск", "token-2");
        _store.Dispose();

        using var reopened = new BotStore(Path.Combine(_dir, "bot.db"));
        reopened.EnsureCreated();

        var binding = reopened.Get(42);
        Assert.NotNull(binding);
        Assert.Equal(1000002, binding!.PlayerId);
        Assert.Equal("token-2", binding.SessionToken);
    }

    [Fact]
    public void Delete_RemovesBinding()
    {
        _store.Upsert(42, 1000001, "влал", "token-1");

        _store.Delete(42);

        Assert.Null(_store.Get(42));
    }

    [Fact]
    public void UnknownUser_HasNoBinding()
    {
        Assert.Null(_store.Get(999));
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        _store.Upsert(42, 1000001, "влал", "token-1");

        _store.EnsureCreated();

        Assert.NotNull(_store.Get(42));
    }

    [Fact]
    public void LegacyTable_KeepsNewestBindingPerUser()
    {
        var path = Path.Combine(_dir, "legacy.db");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE users (
                    telegram_id   INTEGER NOT NULL,
                    server_id     TEXT    NOT NULL,
                    player_id     INTEGER NOT NULL,
                    nick          TEXT    NOT NULL,
                    session_token TEXT    NOT NULL,
                    created_utc   TEXT    NOT NULL,
                    updated_utc   TEXT    NOT NULL,
                    PRIMARY KEY (telegram_id, server_id)
                );
                INSERT INTO users VALUES
                    (42, 'local', 1000001, 'локальный', 'token-local',
                     '2026-01-01T00:00:00.0000000+00:00',
                     '2026-01-01T00:00:00.0000000+00:00'),
                    (42, 'remote', 1000002, 'новосибирск', 'token-remote',
                     '2026-01-02T00:00:00.0000000+00:00',
                     '2026-01-02T00:00:00.0000000+00:00'),
                    (7, 'local', 1000003, 'другой', 'token-7',
                     '2026-01-03T00:00:00.0000000+00:00',
                     '2026-01-03T00:00:00.0000000+00:00');
                """;
            command.ExecuteNonQuery();
        }

        using var store = new BotStore(path);
        store.EnsureCreated();

        var first = store.Get(42);
        Assert.NotNull(first);
        Assert.Equal(1000002, first!.PlayerId);
        Assert.Equal("новосибирск", first.Nick);
        Assert.Equal("token-remote", first.SessionToken);

        var second = store.Get(7);
        Assert.NotNull(second);
        Assert.Equal(1000003, second!.PlayerId);

        using var reopened = new SqliteConnection($"Data Source={path}");
        reopened.Open();
        using var info = reopened.CreateCommand();
        info.CommandText = "PRAGMA table_info(users);";
        using var reader = info.ExecuteReader();
        var columns = new List<string>();
        while (reader.Read())
            columns.Add(reader.GetString(1));

        Assert.Contains("telegram_id", columns);
        Assert.DoesNotContain("server_id", columns);
    }
}
