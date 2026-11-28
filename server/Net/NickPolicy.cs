using System.Text;
using System.Text.RegularExpressions;
using StandChillow.LanServer.Config;

namespace StandChillow.LanServer.Net;

/// <summary>
/// Правила ника: что пускаем в лобби и в катку, что подсказать нарушителю и как показать
/// грязный ник в чате, не отдав его клиенту как rich-text разметку.
/// </summary>
/// <remarks>
/// <para>
/// Ник — не просто подпись: он уходит в чат текстом, а в катке становится именем актора.
/// Клиент рисует чат через TMP, поэтому «&lt;color=red&gt;miro» — это не ник, а разметка:
/// цвет, размер, спрайт. Белый список символов закрывает и это, и невидимки, и мусор,
/// который нельзя прочитать.
/// </para>
/// <para>
/// Проверка одна на оба входа (лобби 7778 и катка 7777): клиент присылает ник дважды, и
/// модифицированный клиент может обойти лобби. Нормализация — та же
/// <see cref="ServerConfig.NormalizeNick"/>, что у банов и аккаунтов, иначе один и тот же
/// человек получал бы разные id.
/// </para>
/// </remarks>
public static class NickPolicy
{
    /// <summary>Сколько символов в нике пускаем по умолчанию (конфиг: <c>names.maxLength</c>).</summary>
    public const int DefaultMaxLength = NamesConfig.DefaultMaxLength;

    /// <summary>
    /// Разметка TMP: <c>&lt;color=red&gt;</c>, <c>&lt;size=200&gt;</c>, <c>&lt;/color&gt;</c>.
    /// Ограничение длины внутри тега — чтобы регулярка не жевала мегабайтную строку.
    /// </summary>
    private static readonly Regex TagPattern = new("<[^<>]{0,200}>", RegexOptions.Compiled);

    /// <summary>Управляющие символы: ESC (ANSI в консоли), перевод строки, NUL.</summary>
    private static readonly Regex ControlPattern = new(@"[\u0000-\u001F\u007F-\u009F]", RegexOptions.Compiled);

    /// <summary>
    /// Разрешённые символы: латиница, кириллица, цифры, пробел, подчёркивание и дефис.
    /// </summary>
    /// <remarks>
    /// Диапазон <c>А..я</c> — это U+0410..U+044F (обе раскладки), <c>Ё</c>/<c>ё</c> стоят
    /// отдельно. Латинские буквы с диакритикой и кириллические «похожие» (Ѕ, І, ї) сюда не
    /// входят намеренно: ими подделывают чужие ники.
    /// </remarks>
    public static bool IsAllowedChar(char c) =>
        c is (>= 'A' and <= 'Z')
            or (>= 'a' and <= 'z')
            or (>= 'А' and <= 'я')
            or 'Ё' or 'ё'
            or (>= '0' and <= '9')
            or ' ' or '_' or '-';

    /// <summary>Ник в том виде, в каком его хранят сессия, аккаунты и баны.</summary>
    public static string Canonical(string? raw) => ServerConfig.NormalizeNick(raw);

    /// <summary>
    /// Проверить ник. <c>Name</c> — уже нормализованный ник (его и надо сохранять),
    /// <c>Reason</c> — почему отказ, для лога.
    /// </summary>
    public static NickCheck Check(string? raw, int maxLength = DefaultMaxLength)
    {
        var name = Canonical(raw);
        if (name.Length == 0)
            return new NickCheck(false, "", "пустой ник");

        if (name.Length > maxLength)
            return new NickCheck(false, name, $"длиннее {maxLength} символов");

        var hasLetterOrDigit = false;
        foreach (var c in name)
        {
            if (!IsAllowedChar(c))
                return new NickCheck(false, name, $"запрещённый символ '{c}' (U+{(int)c:X4})");
            if (char.IsLetterOrDigit(c))
                hasLetterOrDigit = true;
        }

        // «___» и «---» проходят по символам, но это не ник: букв и цифр в нём нет.
        return hasLetterOrDigit
            ? new NickCheck(true, name, "")
            : new NickCheck(false, name, "нет ни букв, ни цифр");
    }

    /// <summary>
    /// Проверка на входе: обычные правила плюс запрет выдавать себя за влала.
    /// </summary>
    /// <param name="allowVlal">
    /// <c>true</c> для админов и мега-админов: им можно любой ник, включая «Влал».
    /// Права определяет вызывающий по <c>admin.ids</c>/<c>admin.megaIds</c>, не хардкод.
    /// </param>
    public static NickCheck CheckForJoin(
        string? raw,
        int maxLength = DefaultMaxLength,
        bool allowVlal = false)
    {
        var check = Check(raw, maxLength);
        if (allowVlal)
            return check;

        // Маскировку проверяем и для ников с запрещёнными символами: «v|a|l»
        // должен получить причину «выдаёт себя за влала», а не просто «символ |».
        if (LooksLikeVlal(check.Name))
            return new NickCheck(false, check.Name, "выдаёт себя за влала", Impersonation: true);

        return check;
    }

    /// <summary>
    /// Похож ли ник на «влала» после снятия маскировки: другая раскладка, leet,
    /// цифры, разделители, повторы букв.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>vlal</c> — уникальное сочетание, его запрещаем в любой позиции. <c>vlad</c> —
    /// обычное имя, поэтому мягче: только в начале и только если после него конец,
    /// цифра или разделитель. «Владимир» и «Vladislav» остаются допустимыми.
    /// </para>
    /// <para>
    /// Сравнение идёт по свёрнутой строке: кириллица переводится в латиницу
    /// (<c>в→v, л→l, а→a, д→d</c>), leet-цифры — в буквы, разделители выкидываются,
    /// повторы схлопываются. Поэтому «В л а л», «v1a1», «VVLLLAALL» — один и тот же ник.
    /// </para>
    /// </remarks>
    public static bool LooksLikeVlal(string? raw)
    {
        var name = Canonical(raw);
        if (name.Length == 0)
            return false;

        var folded = new StringBuilder(name.Length);
        var boundary = new List<bool>(name.Length);
        var pendingBoundary = false;

        foreach (var source in name.Normalize(NormalizationForm.FormKC).ToLowerInvariant())
        {
            if (!char.IsLetterOrDigit(source))
            {
                // Пробел, _, -, точка и прочие разделители: следующая буква начинает
                // новый сегмент, а не продолжает «vlad» вплотную.
                pendingBoundary = true;
                continue;
            }

            var mapped = FoldForImpersonation(source);
            if (mapped.Length == 0)
                continue;

            var isBoundary = pendingBoundary || char.IsDigit(source);
            foreach (var c in mapped)
            {
                if (folded.Length > 0 && folded[^1] == c)
                {
                    // Повтор не добавляет букву, но граница после него важна.
                    if (isBoundary)
                        boundary[^1] = true;
                    continue;
                }

                folded.Append(c);
                boundary.Add(isBoundary);
                isBoundary = false;
            }

            pendingBoundary = false;
        }

        var text = folded.ToString();
        if (text.Contains("vlal", StringComparison.Ordinal))
            return true;

        if (!text.StartsWith("vlad", StringComparison.Ordinal))
            return false;

        // «vlad» целиком или «vlad» + цифра/разделитель. «Vladimir» не трогаем.
        return text.Length == 4 || boundary[4];
    }

    /// <summary>Одна буква/цифра в свёрнутом виде для сравнения с «vlal»/«vlad».</summary>
    private static string FoldForImpersonation(char c)
    {
        if (c is >= '0' and <= '9')
        {
            return c switch
            {
                '0' => "o",
                '1' => "l",
                '3' => "e",
                '4' => "a",
                '5' => "s",
                '6' => "b",
                '7' => "t",
                '8' => "b",
                '9' => "g",
                _ => c.ToString(),
            };
        }

        if (c is >= 'a' and <= 'z')
            return c.ToString();

        return c switch
        {
            'а' => "a",
            'б' => "b",
            'в' => "v",
            'г' => "g",
            'д' => "d",
            'е' => "e",
            'ё' => "e",
            'ж' => "zh",
            'з' => "z",
            'и' => "i",
            'й' => "i",
            'к' => "k",
            'л' => "l",
            'м' => "m",
            'н' => "n",
            'о' => "o",
            'п' => "p",
            'р' => "r",
            'с' => "s",
            'т' => "t",
            'у' => "u",
            'ф' => "f",
            'х' => "h",
            'ц' => "c",
            'ч' => "ch",
            'ш' => "sh",
            'щ' => "sch",
            'ъ' => "",
            'ы' => "y",
            'ь' => "",
            'э' => "e",
            'ю' => "yu",
            'я' => "ya",
            _ => c.ToString(),
        };
    }

    /// <summary>
    /// Ближайший допустимый ник для подсказки: снять разметку, выкинуть запрещённые символы.
    /// <c>&lt;color=red&gt;miro</c> → <c>miro</c>. Пусто — подсказать нечего.
    /// </summary>
    public static string Suggest(string? raw, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        var text = TagPattern.Replace(raw, "");
        var buffer = new StringBuilder(text.Length);
        foreach (var c in Canonical(text))
            if (IsAllowedChar(c))
                buffer.Append(c);

        var name = Canonical(buffer.ToString());
        if (name.Length > maxLength)
            name = name[..maxLength].TrimEnd();

        // Не предлагаем ник, который сам выдаёт себя за влала: подсказка должна
        // вести к полностью другому имени, а не к соседнему написанию.
        if (LooksLikeVlal(name))
            return "";

        return Check(name, maxLength).Ok ? name : "";
    }

    /// <summary>
    /// Показать грязный ник человеку так, чтобы чат не нарисовал его как разметку:
    /// <c>&lt;color=red&gt;miro</c> → <c>[color=red]miro</c>.
    /// </summary>
    public static string Describe(string? raw, int maxLength = 40)
    {
        var text = Canonical(ControlPattern.Replace(raw ?? "", " "));
        if (text.Length == 0)
            return "(пусто)";

        var buffer = new StringBuilder(text.Length);
        foreach (var c in text)
            buffer.Append(c switch { '<' => '[', '>' => ']', _ => c });

        var shown = buffer.ToString();
        return shown.Length <= maxLength ? shown : shown[..maxLength] + "…";
    }

    /// <summary>
    /// Текст чата без rich-text разметки и управляющих символов. Ники и сообщения игроков
    /// сервер пересылает от своего имени, поэтому чистить надо здесь, а не в клиенте.
    /// </summary>
    public static string CleanChat(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        var cleaned = TagPattern.Replace(text, "");
        cleaned = ControlPattern.Replace(cleaned, " ");
        return cleaned.Trim();
    }
}

/// <summary>Результат <see cref="NickPolicy.Check"/>: пускать или нет и почему.</summary>
/// <param name="Impersonation">
/// Отказ именно за маскировку под влала: подсказка не поможет, нужен полностью другой ник.
/// </param>
public readonly record struct NickCheck(
    bool Ok,
    string Name,
    string Reason,
    bool Impersonation = false);
