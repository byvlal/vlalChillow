using System.Security.Cryptography;
using System.Text;
using StandChillow.LanServer.Bans;
using StandChillow.LanServer.Config;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Старый «id из ника»: детерминированная функция без состояния. Боевые id теперь выдаёт
/// <see cref="AccountStore"/>, а хеш остался для миграции старых файлов и как fallback,
/// пока аккаунт ещё не создан.
/// </summary>
public sealed class LegacyNickHashTests
{
    [Theory]
    // Зафиксированные значения: если алгоритм/нормализация случайно изменятся,
    // у всех игроков разом поменяются id — тест не даст этого не заметить.
    // Как проверить вручную: printf 'KTULXY' | sha256sum → a0e6302b6118a8d2…
    // → первые 8 байт little-endian → 15179409349667776160 → % 100000000 + 1 = 67776161.
    [InlineData("Ktulxy", 67776161)]
    [InlineData("Ktulxy2", 67196325)]
    [InlineData("влал", 25354325)]
    [InlineData("  влал  ", 25354325)]
    [InlineData("Ktu lxy", 87780421)]
    public void IdIsPinnedAcrossVersions(string nick, int expected)
    {
        Assert.Equal(expected, LegacyNickHash.ForNick(nick));
    }

    [Fact]
    public void WireUidIsTheDecimalId()
    {
        // Клиент присылает uid строкой «Offline» — сервер отдаёт то же самое поле числом.
        Assert.True(LegacyNickHash.TryWireUid("Ktulxy", out var latin));
        Assert.Equal("67776161", latin);

        Assert.True(LegacyNickHash.TryWireUid("влал", out var cyrillic));
        Assert.Equal("25354325", cyrillic);
    }

    [Fact]
    public void WireUidIsRefusedForEmptyNick()
    {
        Assert.False(LegacyNickHash.TryWireUid(null, out var none));
        Assert.Equal(string.Empty, none);
        Assert.False(LegacyNickHash.TryWireUid("   ", out _));
    }

    [Fact]
    public void IndependentComputationAgreesForFiveHundredNicks()
    {
        // Вторая реализация того же контракта: тот же SHA-256, но uint64 собирается из
        // digest побайтово, без BinaryPrimitives. Ловит ошибку порядка байт и «плывущую»
        // нормализацию — то есть ровно то, из-за чего id разошёлся бы между серверами.
        for (var i = 0; i < 500; i++)
        {
            var nick = $"proof-{i}";
            var normalized = ServerConfig.NormalizeNick(nick).ToUpperInvariant();
            normalized = normalized.Normalize(NormalizationForm.FormC);

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
            ulong value = 0;
            for (var b = 7; b >= 0; b--)
                value = (value << 8) | hash[b];

            var expected = (int)(value % 100_000_000UL) + 1;
            Assert.Equal(expected, LegacyNickHash.ForNick(nick));
        }
    }

    [Fact]
    public void IdSurvivesServerReinstall()
    {
        // Hash — чистая функция: переустановка сервера и пустой справочник ничего не
        // меняют, поэтому старые файлы можно переводить на аккаунты и после рестарта.
        var before = LegacyNickHash.ForNick("влал");
        Assert.Equal(25354325, before);
        Assert.Equal(before, LegacyNickHash.ForNick("влал"));
    }

    [Fact]
    public void SameNickAlwaysGivesSameId()
    {
        var first = LegacyNickHash.ForNick("Ktulxy");
        for (var i = 0; i < 100; i++)
            Assert.Equal(first, LegacyNickHash.ForNick("Ktulxy"));
    }

    [Fact]
    public void IdIsStableForTheSameNickAfterNormalization()
    {
        // Пробелы по краям и невидимые символы — тот же ник.
        var plain = LegacyNickHash.ForNick("влал");
        Assert.Equal(plain, LegacyNickHash.ForNick("  влал  "));
        Assert.Equal(plain, LegacyNickHash.ForNick("вла\u200Bл"));
        Assert.Equal(plain, LegacyNickHash.ForNick("влал\uFEFF"));
    }

    [Fact]
    public void SpacesInsideNickAreKeptAndChangeTheId()
    {
        Assert.NotEqual(
            LegacyNickHash.ForNick("Ktu lxy"),
            LegacyNickHash.ForNick("Ktulxy"));
        Assert.Equal(LegacyNickHash.ForNick("a  b"), LegacyNickHash.ForNick("a b"));
    }

    [Fact]
    public void UnicodeEmojiAndCustomSymbolsArePartOfTheId()
    {
        var ids = new[]
        {
            LegacyNickHash.ForNick("😈Читер😈"),
            LegacyNickHash.ForNick("Ω≈ç√∫˜µ≤≥÷"),
            LegacyNickHash.ForNick("日本語のニック"),
            LegacyNickHash.ForNick("|/\\[]{}#$%^&*()"),
        };

        Assert.All(ids, id => Assert.InRange(id, LegacyNickHash.Min, LegacyNickHash.Max));
        Assert.Equal(4, ids.Distinct().Count());
    }

    [Fact]
    public void DifferentNickDifferentId()
    {
        Assert.NotEqual(LegacyNickHash.ForNick("Ktulxy"), LegacyNickHash.ForNick("Ktulxy2"));
        Assert.NotEqual(LegacyNickHash.ForNick("влал"), LegacyNickHash.ForNick("влал2"));
    }

    [Fact]
    public void CaseDoesNotSplitThePerson()
    {
        // Бан по нику и так сравнивается без учёта регистра, поэтому id обязан означать
        // того же человека: «Влал» и «влал» — один id.
        Assert.Equal(LegacyNickHash.ForNick("влал"), LegacyNickHash.ForNick("Влал"));
        Assert.Equal(LegacyNickHash.ForNick("Ktulxy"), LegacyNickHash.ForNick("KTULXY"));
    }

    [Fact]
    public void CaseFoldSurvivesSurroundingNoise()
    {
        Assert.Equal(
            LegacyNickHash.ForNick("Ktulxy"),
            LegacyNickHash.ForNick("  kTuLxY\u200B "));
    }

    [Fact]
    public void RangeIsOneToOneHundredMillion()
    {
        for (var i = 0; i < 2000; i++)
        {
            var id = LegacyNickHash.ForNick($"player-{i}");
            Assert.InRange(id, 1, 100_000_000);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\u200B")]
    public void EmptyNickHasNoId(string? nick)
    {
        Assert.Equal(0, LegacyNickHash.ForNick(nick));
    }

    [Fact]
    public void TwoThousandsNicksCollideRarely()
    {
        // 2000 ников в 100 млн бакетов: ожидаемое число коллизий ~0.02.
        var ids = new HashSet<int>();
        var collisions = 0;
        for (var i = 0; i < 2000; i++)
            if (!ids.Add(LegacyNickHash.ForNick($"ban-test-{i}")))
                collisions++;

        Assert.InRange(collisions, 0, 5);
    }
}
