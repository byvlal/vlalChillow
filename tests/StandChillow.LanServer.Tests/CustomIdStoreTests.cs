using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Кастомный ID админа: буквенно-цифровая подпись вместо номера аккаунта.
/// Чисто цифровой ID запрещён — иначе его не отличить от настоящего номера.
/// </summary>
public sealed class CustomIdStoreTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "standchillow-customid-tests", Guid.NewGuid() + ".json");

    [Fact]
    public void DigitsOnlyAreRejected()
    {
        var store = new CustomIdStore(TempPath());

        var error = store.Set(1_000_000, "влал", "1000000");

        Assert.NotNull(error);
        Assert.Null(store.Get(1_000_000));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void LetterAndDigitsAreAccepted()
    {
        var store = new CustomIdStore(TempPath());

        var error = store.Set(1_000_000, "влал", "vlal2077");

        Assert.Null(error);
        Assert.Equal("vlal2077", store.Get(1_000_000));
    }

    [Fact]
    public void OneLetterIsEnough()
    {
        var store = new CustomIdStore(TempPath());

        Assert.Null(store.Set(1_000_000, "влал", "a1"));
        Assert.Equal("a1", store.Get(1_000_000));
    }

    [Fact]
    public void NonLatinOrPunctuationIsRejected()
    {
        var store = new CustomIdStore(TempPath());

        Assert.NotNull(store.Set(1_000_000, "влал", "влал"));
        Assert.NotNull(store.Set(1_000_000, "влал", "vlal-1"));
        Assert.NotNull(store.Set(1_000_000, "влал", "vlal 1"));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void TooLongValueIsRejectedBeforeThePattern()
    {
        var store = new CustomIdStore(TempPath());

        var error = store.Set(1_000_000, "влал", new string('a', CustomIdStore.MaxLength + 1));

        Assert.Contains("максимум", error);
        Assert.Equal(0, store.Count);
    }
}
