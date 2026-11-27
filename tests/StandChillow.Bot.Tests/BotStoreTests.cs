using StandChillow.Bot;
using Xunit;

namespace StandChillow.Bot.Tests;

/// <summary>
/// Привязки Telegram → игрок: одна активная на человека, перепривязка кодом
/// с другого сервера убивает старую.
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
        _store.Upsert(42, "local", 1000001, "влал", "token-1");

        var binding = _store.Get(42, "local");

        Assert.NotNull(binding);
        Assert.Equal(42, binding!.TelegramId);
        Assert.Equal("local", binding.ServerId);
        Assert.Equal(1000001, binding.PlayerId);
        Assert.Equal("влал", binding.Nick);
        Assert.Equal("token-1", binding.SessionToken);
    }

    [Fact]
    public void Upsert_UpdatesTheSameBinding()
    {
        _store.Upsert(42, "local", 1000001, "старый", "token-1");
        var first = _store.Get(42, "local")!;

        _store.Upsert(42, "local", 1000002, "новый", "token-2");
        var second = _store.Get(42, "local")!;

        Assert.Equal(first.CreatedUtc, second.CreatedUtc);
        Assert.Equal(1000002, second.PlayerId);
        Assert.Equal("новый", second.Nick);
        Assert.Equal("token-2", second.SessionToken);
        Assert.True(second.UpdatedUtc >= first.UpdatedUtc);
    }

    [Fact]
    public void GetActive_ReturnsNewestServer()
    {
        _store.Upsert(42, "local", 1000001, "локальный", "token-1");
        Thread.Sleep(5);
        _store.Upsert(42, "remote", 1000002, "новосибирск", "token-2");

        var active = _store.GetActive(42);

        Assert.NotNull(active);
        Assert.Equal("remote", active!.ServerId);
        Assert.Equal(1000002, active.PlayerId);
    }

    [Fact]
    public void DeleteOthers_LeavesOnlyTheChosenServer()
    {
        _store.Upsert(42, "local", 1000001, "локальный", "token-1");
        _store.Upsert(42, "remote", 1000002, "новосибирск", "token-2");

        _store.DeleteOthers(42, "remote");

        Assert.Null(_store.Get(42, "local"));
        Assert.NotNull(_store.Get(42, "remote"));
        Assert.Equal("remote", _store.GetActive(42)!.ServerId);
    }

    [Fact]
    public void Rebind_FromLocalToRemote_KeepsOneBinding()
    {
        _store.Upsert(42, "local", 1000001, "локальный", "token-1");
        _store.Upsert(42, "remote", 1000002, "новосибирск", "token-2");
        _store.DeleteOthers(42, "remote");

        Assert.Null(_store.Get(42, "local"));
        Assert.Equal("remote", _store.GetActive(42)!.ServerId);
    }

    [Fact]
    public void DifferentTelegramUsers_DoNotInterfere()
    {
        _store.Upsert(1, "local", 1000001, "первый", "token-1");
        _store.Upsert(2, "remote", 1000002, "второй", "token-2");

        Assert.Equal("local", _store.GetActive(1)!.ServerId);
        Assert.Equal("remote", _store.GetActive(2)!.ServerId);

        _store.DeleteOthers(1, "local");
        Assert.NotNull(_store.Get(2, "remote"));
    }

    [Fact]
    public void Binding_SurvivesReopen()
    {
        _store.Upsert(42, "remote", 1000002, "новосибирск", "token-2");
        _store.Dispose();

        using var reopened = new BotStore(Path.Combine(_dir, "bot.db"));
        reopened.EnsureCreated();

        var active = reopened.GetActive(42);
        Assert.NotNull(active);
        Assert.Equal("remote", active!.ServerId);
        Assert.Equal("token-2", active.SessionToken);
    }

    [Fact]
    public void Delete_RemovesOnlyOneServer()
    {
        _store.Upsert(42, "local", 1000001, "локальный", "token-1");
        _store.Upsert(42, "remote", 1000002, "новосибирск", "token-2");

        _store.Delete(42, "local");

        Assert.Null(_store.Get(42, "local"));
        Assert.NotNull(_store.Get(42, "remote"));
    }

    [Fact]
    public void UnknownUser_HasNoBinding()
    {
        Assert.Null(_store.GetActive(999));
        Assert.Null(_store.Get(999, "local"));
    }
}
