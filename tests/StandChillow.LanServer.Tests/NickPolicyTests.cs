using StandChillow.LanServer.Net;
using Xunit;

namespace StandChillow.LanServer.Tests;

/// <summary>
/// Белый список ника: что пускаем, что подсказываем и как показываем грязный ник,
/// чтобы клиент не нарисовал его как rich-text разметку.
/// </summary>
public sealed class NickPolicyTests
{
    [Theory]
    [InlineData("miro")]
    [InlineData("Влал")]
    [InlineData("Miro_2")]
    [InlineData("miro-2")]
    [InlineData("Иван Петров")]
    [InlineData("Ёжик")]
    [InlineData("я")]
    public void AllowedNicks_AreAccepted(string raw)
    {
        var check = NickPolicy.Check(raw);

        Assert.True(check.Ok, check.Reason);
        Assert.Equal(raw, check.Name);
    }

    /// <summary>Пробелы и невидимки — не повод для отказа: их снимает нормализация.</summary>
    [Theory]
    [InlineData("  Miro   Ivan  ", "Miro Ivan")]
    [InlineData("miro\u200B", "miro")]
    [InlineData("mi\u202Ero", "miro")]
    [InlineData("mi\nro", "mi ro")]
    public void NormalisedNicks_AreStoredInCanonicalForm(string raw, string expected)
    {
        var check = NickPolicy.Check(raw);

        Assert.True(check.Ok, check.Reason);
        Assert.Equal(expected, check.Name);
    }

    /// <summary>Ровно то, ради чего всё: разметка, эмодзи и мусор не проходят.</summary>
    [Theory]
    [InlineData("<color=red>miro")]
    [InlineData("miro<b>")]
    [InlineData("miro😀")]
    [InlineData("miro\u001B[31m")]
    [InlineData("miro,2")]
    [InlineData("miro.2")]
    [InlineData("---")]
    [InlineData("___")]
    [InlineData("   ")]
    [InlineData("")]
    public void ForbiddenNicks_AreRejected(string raw)
    {
        var check = NickPolicy.Check(raw);

        Assert.False(check.Ok);
        Assert.False(string.IsNullOrWhiteSpace(check.Reason));
    }

    [Fact]
    public void LengthIsCappedByTheConfig()
    {
        Assert.True(NickPolicy.Check(new string('a', 20)).Ok);
        Assert.False(NickPolicy.Check(new string('a', 21)).Ok);
        Assert.False(NickPolicy.Check(new string('a', 21), maxLength: 12).Ok);
        Assert.True(NickPolicy.Check(new string('a', 12), maxLength: 12).Ok);
    }

    /// <summary>Подсказка снимает разметку, а не выкидывает буквы вместе с ней.</summary>
    [Theory]
    [InlineData("<color=red>miro", "miro")]
    [InlineData("<size=200>Влал</size>", "Влал")]
    [InlineData("miro😀", "miro")]
    [InlineData("Miro,2", "Miro2")]
    public void Suggest_KeepsTheReadablePart(string raw, string expected)
    {
        Assert.Equal(expected, NickPolicy.Suggest(raw));
    }

    [Theory]
    [InlineData("😀😀")]
    [InlineData("<color=red>")]
    [InlineData("")]
    public void Suggest_IsEmptyWhenNothingReadableIsLeft(string raw)
    {
        Assert.Equal("", NickPolicy.Suggest(raw));
    }

    [Fact]
    public void Suggest_RespectsTheLengthLimit()
    {
        var suggested = NickPolicy.Suggest(new string('a', 30));

        Assert.Equal(new string('a', 20), suggested);
    }

    /// <summary>В сообщении старый ник виден, но угловые скобки уже не разметка.</summary>
    [Theory]
    [InlineData("<color=red>miro", "[color=red]miro")]
    [InlineData("mi\nro", "mi ro")]
    [InlineData("<size=200>Влал</size>", "[size=200]Влал[/size]")]
    public void Describe_ShowsTheNickWithoutRichText(string raw, string expected)
    {
        Assert.Equal(expected, NickPolicy.Describe(raw));
    }

    [Fact]
    public void Describe_IsTruncatedForTheChatLine()
    {
        var shown = NickPolicy.Describe(new string('a', 60));

        Assert.Equal(new string('a', 40) + "…", shown);
    }

    [Theory]
    [InlineData("привет <color=red>мир</color>", "привет мир")]
    [InlineData("<color=red>", "")]
    [InlineData("a\nb", "a b")]
    [InlineData("miro\u001B[31m", "miro [31m")]
    [InlineData("<3", "<3")]
    public void CleanChat_StripsOnlyRealMarkup(string raw, string expected)
    {
        Assert.Equal(expected, NickPolicy.CleanChat(raw));
    }
}
