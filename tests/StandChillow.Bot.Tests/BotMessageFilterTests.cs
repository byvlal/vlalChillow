using StandChillow.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Xunit;

namespace StandChillow.Bot.Tests;

/// <summary>
/// В личке бот отвечает на текст, в группе — только на команду, упоминание или
/// ответ на своё сообщение. Старше 60 секунд — не отвечает вообще.
/// </summary>
public sealed class BotMessageFilterTests
{
    private const string Bot = "vlalikoffcChillowbot";

    private static MessageEntity Mention(int offset, int length) => new()
    {
        Type = MessageEntityType.Mention,
        Offset = offset,
        Length = length,
    };

    private static bool Take(
        string? text,
        ChatType chat,
        out string cleaned,
        bool replyToBot = false,
        IReadOnlyList<MessageEntity>? entities = null) =>
        BotMessageFilter.TryTakeAddressedText(
            text, chat, replyToBot, entities, Bot, out cleaned);

    [Fact]
    public void PrivateChat_AnyTextIsAccepted()
    {
        Assert.True(Take("привет", ChatType.Private, out var text));
        Assert.Equal("привет", text);
    }

    [Fact]
    public void Group_PlainText_IsIgnored()
    {
        Assert.False(Take("привет", ChatType.Group, out _));
        Assert.False(Take("привет", ChatType.Supergroup, out _));
    }

    [Fact]
    public void Group_Command_IsAccepted()
    {
        Assert.True(Take("/start", ChatType.Group, out var text));
        Assert.Equal("/start", text);
    }

    [Fact]
    public void Group_CommandForThisBot_IsAccepted()
    {
        Assert.True(Take("/start@" + Bot, ChatType.Group, out var text));
        Assert.Equal("/start@" + Bot, text);
    }

    [Fact]
    public void Group_CommandForAnotherBot_IsIgnored()
    {
        Assert.False(Take("/start@otherbot", ChatType.Group, out _));
    }

    [Fact]
    public void Group_CommandWithArguments_IsAccepted()
    {
        Assert.True(Take("/start 123", ChatType.Group, out var text));
        Assert.Equal("/start 123", text);
    }

    [Fact]
    public void Group_SlashPath_IsNotACommand()
    {
        Assert.False(Take("/home/user", ChatType.Group, out _));
        Assert.False(Take("/etc/passwd", ChatType.Group, out _));
        Assert.False(Take("//start", ChatType.Group, out _));
    }

    [Fact]
    public void Group_Mention_IsAcceptedAndStripped()
    {
        var text = "@" + Bot + " 123456";
        var entities = new[] { Mention(0, Bot.Length + 1) };

        Assert.True(Take(text, ChatType.Group, out var cleaned, entities: entities));
        Assert.Equal("123456", cleaned);
    }

    [Fact]
    public void Group_MentionAtTheEnd_IsAcceptedAndStripped()
    {
        var text = "123456 @" + Bot;
        var entities = new[] { Mention(7, Bot.Length + 1) };

        Assert.True(Take(text, ChatType.Group, out var cleaned, entities: entities));
        Assert.Equal("123456", cleaned);
    }

    [Fact]
    public void Group_BareMention_BecomesMenu()
    {
        var entities = new[] { Mention(0, Bot.Length + 1) };

        Assert.True(Take("@" + Bot, ChatType.Group, out var cleaned, entities: entities));
        Assert.Equal("/menu", cleaned);
    }

    [Fact]
    public void Group_MentionOfAnotherBot_IsIgnored()
    {
        var text = "@otherbot 123456";
        var entities = new[] { Mention(0, 9) };

        Assert.False(Take(text, ChatType.Group, out _, entities: entities));
    }

    [Fact]
    public void Group_ReplyToBot_IsAccepted()
    {
        Assert.True(Take("123456", ChatType.Group, out var text, replyToBot: true));
        Assert.Equal("123456", text);
    }

    [Fact]
    public void Group_ReplyToSomeoneElse_IsIgnored()
    {
        Assert.False(Take("123456", ChatType.Group, out _, replyToBot: false));
    }

    [Fact]
    public void Channel_IsIgnored()
    {
        Assert.False(Take("привет", ChatType.Channel, out _));
    }

    [Fact]
    public void EmptyText_IsIgnored()
    {
        Assert.False(Take(null, ChatType.Private, out _));
        Assert.False(Take("   ", ChatType.Private, out _));
    }

    [Fact]
    public void Stale_IsOlderThan60Seconds()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        Assert.False(BotMessageFilter.IsStale(now.AddSeconds(-59), now));
        Assert.False(BotMessageFilter.IsStale(now.AddSeconds(-60), now));
        Assert.True(BotMessageFilter.IsStale(now.AddSeconds(-61), now));
    }
}
