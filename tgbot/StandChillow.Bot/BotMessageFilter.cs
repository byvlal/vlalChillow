using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace StandChillow.Bot;

/// <summary>
/// Что считать адресованным боту. В личке — любое текстовое сообщение; в группе —
/// только команда, упоминание бота или ответ на его сообщение.
/// </summary>
/// <remarks>
/// Без этого фильтра бот с выключенным privacy mode отвечает на каждую реплику
/// в группе. Телеграм не присылает «адресовано ли это мне» отдельным флагом,
/// поэтому правило живёт здесь и проверяется тестами.
/// </remarks>
internal static class BotMessageFilter
{
    /// <summary>Сообщения старше этого возраста считаются старыми и не обрабатываются.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(60);

    public static bool IsSupportedChat(ChatType type) =>
        type is ChatType.Private or ChatType.Group or ChatType.Supergroup;

    public static bool IsStale(DateTime sentUtc, DateTime nowUtc) =>
        AsUtc(nowUtc) - AsUtc(sentUtc) > MaxAge;

    /// <summary>
    /// Вернуть текст, который бот должен обработать, или <c>false</c>, если сообщение
    /// не ему. В группе упоминание бота из текста вырезается; если кроме упоминания
    /// ничего не было, получается <c>/menu</c>.
    /// </summary>
    public static bool TryTakeAddressedText(
        string? text,
        ChatType chatType,
        bool isReplyToBot,
        IReadOnlyList<MessageEntity>? entities,
        string? botUsername,
        out string cleaned)
    {
        cleaned = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (chatType == ChatType.Private)
        {
            cleaned = text;
            return true;
        }

        if (chatType is not (ChatType.Group or ChatType.Supergroup))
            return false;

        var trimmed = text.Trim();

        if (trimmed.StartsWith('/'))
        {
            // «/home/user» — это путь, а не команда: на такое бот не отвечает.
            if (!LooksLikeCommand(trimmed))
                return false;
            if (CommandTargetsAnotherBot(trimmed, botUsername))
                return false;
            cleaned = trimmed;
            return true;
        }

        if (MentionsBot(trimmed, entities, botUsername))
        {
            cleaned = RemoveMention(trimmed, botUsername!);
            if (cleaned.Length == 0)
                cleaned = "/menu";
            return true;
        }

        if (isReplyToBot)
        {
            cleaned = trimmed;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Первое слово — команда вида <c>/start</c> или <c>/start@bot</c>. Слэш в
    /// середине (путь, ссылка) командой не считается.
    /// </summary>
    private static bool LooksLikeCommand(string text)
    {
        var token = text.Split(' ', 2)[0];
        if (token.Length < 2 || token[0] != '/')
            return false;

        var at = token.IndexOf('@');
        var name = at < 0 ? token[1..] : token[1..at];
        if (!IsCommandWord(name))
            return false;

        return at < 0 || IsCommandWord(token[(at + 1)..]);
    }

    private static bool IsCommandWord(string value)
    {
        if (value.Length is 0 or > 32)
            return false;
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static bool CommandTargetsAnotherBot(string text, string? botUsername)
    {
        var token = text.Split(' ', 2)[0];
        var at = token.IndexOf('@');
        if (at < 0)
            return false;

        var target = token[(at + 1)..];
        return botUsername is null
            || !target.Equals(botUsername, StringComparison.OrdinalIgnoreCase);
    }

    private static bool MentionsBot(
        string text, IReadOnlyList<MessageEntity>? entities, string? botUsername)
    {
        if (string.IsNullOrEmpty(botUsername))
            return false;

        if (entities is not null)
        {
            foreach (var entity in entities)
            {
                if (entity.Type != MessageEntityType.Mention)
                    continue;
                if (entity.Offset < 0 || entity.Length <= 0)
                    continue;
                if (entity.Offset + entity.Length > text.Length)
                    continue;

                var mention = text.Substring(entity.Offset, entity.Length);
                if (mention.Equals("@" + botUsername, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        return text.Contains("@" + botUsername, StringComparison.OrdinalIgnoreCase);
    }

    private static string RemoveMention(string text, string botUsername) =>
        text.Replace("@" + botUsername, string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
