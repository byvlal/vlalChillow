using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Config;

/// <summary>
/// Байтовая миграция <c>config.json</c>: добавляет <c>admin.megaIds</c>, не трогая
/// комментарии и форматирование вокруг. Файл переписывается только если секции
/// действительно нет; повторный запуск ничего не меняет.
/// </summary>
/// <remarks>
/// <para>
/// Обычный <c>JsonSerializer</c> здесь не годится: он теряет комментарии и порядок
/// ключей. Поэтому файл разбирается <see cref="Utf8JsonReader"/> ради позиций, а
/// правки вставляются в исходные байты.
/// </para>
/// <para>
/// Старый дефолт <c>[25354325]</c> и пустой <c>ids</c> заменяются на владельца
/// <c>1000000</c> только вместе с добавлением <c>megaIds</c> — то есть один раз,
/// при переходе со старого формата. Если <c>megaIds</c> уже есть, список <c>ids</c>
/// остаётся ровно таким, каким его написал владелец.
/// </para>
/// </remarks>
internal static class ConfigMigration
{
    /// <summary>Владелец сервера по умолчанию — и админ, и мега-админ.</summary>
    internal const int DefaultOwnerId = 1_000_000;

    /// <summary>Старый хеш ника «влал»: до миграции он стоял в шаблоне.</summary>
    internal const int LegacyOwnerId = 25_354_325;

    /// <summary>
    /// Убедиться, что в файле есть <c>admin.megaIds</c>. <c>false</c> — файл уже
    /// мигрирован, битый, недоступен или не содержит <c>admin</c>; ошибок не бросает.
    /// </summary>
    internal static bool EnsureMegaSection(string path, out string message)
    {
        message = string.Empty;

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch
        {
            return false;
        }

        var bom = bytes.Length >= 3
                  && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;

        var span = bytes.AsSpan(bom);
        var layout = Scan(span);
        if (!layout.Valid || layout.HasMegaIds)
            return false;

        var newline = DetectNewline(span);
        var edits = new List<Edit>();

        var rootIndent = layout.FirstRootProperty >= 0
            ? IndentAt(span, layout.FirstRootProperty)
            : "  ";
        if (rootIndent.Length == 0)
            rootIndent = "  ";

        if (layout.HasAdmin)
        {
            if (layout.HasIds && layout.IdsStart >= 0 && layout.IdsEnd > layout.IdsStart)
            {
                var indent = IndentAt(span, layout.IdsPropertyStart);
                if (indent.Length == 0)
                    indent = rootIndent + "  ";

                if (layout.IdsIsDefault)
                {
                    edits.Add(new Edit(
                        layout.IdsStart,
                        layout.IdsEnd,
                        Encoding.UTF8.GetBytes($"[ {DefaultOwnerId} ]")));
                }

                var insert = BuildMegaBlock(indent, newline);
                edits.Add(new Edit(layout.IdsEnd, layout.IdsEnd,
                    Encoding.UTF8.GetBytes(insert)));
                message = layout.IdsIsDefault
                    ? $"admin.ids → [ {DefaultOwnerId} ], добавлен admin.megaIds [ {DefaultOwnerId} ]"
                    : $"добавлен admin.megaIds [ {DefaultOwnerId} ]";
            }
            else
            {
                var indent = layout.FirstAdminProperty >= 0
                    ? IndentAt(span, layout.FirstAdminProperty)
                    : rootIndent + "  ";
                if (indent.Length == 0)
                    indent = rootIndent + "  ";

                var block = new StringBuilder();
                block.Append(newline);
                block.Append(indent).Append("// ===== МЕГА-АДМИНЫ =====").Append(newline);
                block.Append(indent).Append("// Мега-админ может всё и выдаёт права остальным.").Append(newline);
                block.Append(indent).Append("// Если id есть и в \"ids\", и здесь — он мега.").Append(newline);
                block.Append(indent).Append($"\"megaIds\": [ {DefaultOwnerId} ]");
                if (layout.AdminHasProperties)
                    block.Append(',');

                edits.Add(new Edit(layout.AdminStart + 1, layout.AdminStart + 1,
                    Encoding.UTF8.GetBytes(block.ToString())));
                message = $"добавлен admin.megaIds [ {DefaultOwnerId} ]";
            }
        }
        else
        {
            var insertAt = LastContentByte(span, layout.RootEnd);
            if (insertAt < 0)
                return false;

            var block = new StringBuilder();
            if (layout.RootHasProperties)
                block.Append(',');
            block.Append(newline).Append(newline);
            block.Append(rootIndent).Append("\"admin\": {").Append(newline);
            block.Append(rootIndent).Append("  ").Append($"\"ids\": [ {DefaultOwnerId} ],").Append(newline);
            block.Append(rootIndent).Append("  ").Append("// ===== МЕГА-АДМИНЫ =====").Append(newline);
            block.Append(rootIndent).Append("  ").Append("// Мега-админ может всё и выдаёт права остальным.").Append(newline);
            block.Append(rootIndent).Append("  ").Append($"\"megaIds\": [ {DefaultOwnerId} ]").Append(newline);
            block.Append(rootIndent).Append('}');

            edits.Add(new Edit(insertAt, insertAt, Encoding.UTF8.GetBytes(block.ToString())));
            message = $"добавлена секция admin с ids и megaIds [ {DefaultOwnerId} ]";
        }

        if (edits.Count == 0)
            return false;

        var updated = Apply(span, edits);
        var result = new byte[bom + updated.Length];
        if (bom > 0)
            Buffer.BlockCopy(bytes, 0, result, 0, bom);
        Buffer.BlockCopy(updated, 0, result, bom, updated.Length);

        try
        {
            WriteAtomic(path, result);
        }
        catch
        {
            message = string.Empty;
            return false;
        }

        return true;
    }

    /// <summary>Блок <c>megaIds</c> после <c>ids</c>; запятая перед ним уже стоит в файле.</summary>
    private static string BuildMegaBlock(string indent, string newline)
    {
        var sb = new StringBuilder();
        sb.Append(',').Append(newline).Append(newline);
        sb.Append(indent).Append("// ===== МЕГА-АДМИНЫ =====").Append(newline);
        sb.Append(indent).Append("// Мега-админ может всё и выдаёт права остальным.").Append(newline);
        sb.Append(indent).Append("// Если id есть и в \"ids\", и здесь — он мега.").Append(newline);
        sb.Append(indent).Append($"\"megaIds\": [ {DefaultOwnerId} ]");
        return sb.ToString();
    }

    private readonly record struct Edit(int Start, int End, byte[] Replacement);

    /// <summary>Применить правки к исходным байтам без BOM, с конца — чтобы не сбить позиции.</summary>
    private static byte[] Apply(ReadOnlySpan<byte> source, List<Edit> edits)
    {
        edits.Sort((a, b) => b.Start.CompareTo(a.Start));
        var result = source.ToArray();
        foreach (var edit in edits)
        {
            if (edit.Start < 0 || edit.End < edit.Start || edit.End > result.Length)
                continue;

            var tail = result.AsSpan(edit.End);
            var next = new byte[result.Length - (edit.End - edit.Start) + edit.Replacement.Length];
            result.AsSpan(0, edit.Start).CopyTo(next);
            edit.Replacement.CopyTo(next.AsSpan(edit.Start));
            tail.CopyTo(next.AsSpan(edit.Start + edit.Replacement.Length));
            result = next;
        }

        return result;
    }

    private sealed class Layout
    {
        public bool Valid;
        public bool HasMegaIds;
        public bool HasAdmin;
        public bool HasIds;
        public bool IdsIsDefault;
        public bool AdminHasProperties;
        public bool RootHasProperties;
        public int RootStart;
        public int RootEnd;
        public int AdminStart;
        public int AdminEnd;
        public int IdsPropertyStart;
        public int IdsStart;
        public int IdsEnd;
        public int FirstRootProperty = -1;
        public int FirstAdminProperty = -1;

        public readonly List<long> IdsValues = new();
    }

    /// <summary>
    /// Один проход по JSON: позиции корня, объекта admin и массива ids. Комментарии и
    /// trailing comma разрешены — файл должен читаться теми же правилами, что и конфиг.
    /// </summary>
    private static Layout Scan(ReadOnlySpan<byte> json)
    {
        var layout = new Layout();
        var depth = 0;
        string? rootProperty = null;
        var inAdmin = false;
        var expectingIds = false;
        var idsArrayDepth = -1;

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            while (reader.Read())
            {
                var at = (int)reader.TokenStartIndex;
                switch (reader.TokenType)
                {
                    case JsonTokenType.StartObject:
                        depth++;
                        if (depth == 1)
                        {
                            layout.RootStart = at;
                        }
                        else if (depth == 2 && rootProperty == "admin")
                        {
                            inAdmin = true;
                            layout.HasAdmin = true;
                            layout.AdminStart = at;
                        }

                        break;

                    case JsonTokenType.EndObject:
                        if (depth == 2 && inAdmin)
                        {
                            layout.AdminEnd = at + 1;
                            inAdmin = false;
                        }
                        else if (depth == 1)
                        {
                            layout.RootEnd = at + 1;
                        }

                        depth--;
                        break;

                    case JsonTokenType.PropertyName:
                        if (depth == 1)
                        {
                            layout.RootHasProperties = true;
                            if (layout.FirstRootProperty < 0)
                                layout.FirstRootProperty = at;
                            rootProperty = reader.GetString();
                        }
                        else if (depth == 2 && inAdmin)
                        {
                            layout.AdminHasProperties = true;
                            if (layout.FirstAdminProperty < 0)
                                layout.FirstAdminProperty = at;

                            var name = reader.GetString();
                            if (name == "ids")
                            {
                                expectingIds = true;
                                layout.IdsPropertyStart = at;
                            }
                            else if (name == "megaIds")
                            {
                                layout.HasMegaIds = true;
                            }
                        }

                        break;

                    case JsonTokenType.StartArray:
                        depth++;
                        if (expectingIds && depth == 3 && inAdmin)
                        {
                            expectingIds = false;
                            layout.HasIds = true;
                            layout.IdsStart = at;
                            idsArrayDepth = depth;
                        }

                        break;

                    case JsonTokenType.EndArray:
                        if (idsArrayDepth == depth)
                        {
                            layout.IdsEnd = at + 1;
                            idsArrayDepth = -1;
                            layout.IdsIsDefault = layout.IdsValues.Count == 0
                                || (layout.IdsValues.Count == 1
                                    && layout.IdsValues[0] == LegacyOwnerId);
                        }

                        depth--;
                        break;

                    case JsonTokenType.Number:
                        if (idsArrayDepth == depth && reader.TryGetInt64(out var number))
                            layout.IdsValues.Add(number);
                        else if (idsArrayDepth == depth)
                            layout.IdsValues.Add(long.MinValue);
                        break;
                }
            }

            layout.Valid = layout.RootEnd > layout.RootStart;
            if (!layout.HasIds)
                layout.IdsIsDefault = false;
        }
        catch (JsonException)
        {
            layout.Valid = false;
        }

        return layout;
    }

    /// <summary>Позиция последнего непробельного байта перед закрывающей скобкой.</summary>
    private static int LastContentByte(ReadOnlySpan<byte> span, int rootEnd)
    {
        var at = Math.Min(rootEnd - 2, span.Length - 1);
        while (at >= 0 && span[at] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')
            at--;
        return at + 1;
    }

    /// <summary>Отступ строки, на которой начинается токен (пробелы/табы слева).</summary>
    private static string IndentAt(ReadOnlySpan<byte> span, int tokenStart)
    {
        if (tokenStart <= 0 || tokenStart > span.Length)
            return string.Empty;

        var lineStart = tokenStart;
        while (lineStart > 0 && span[lineStart - 1] != (byte)'\n')
            lineStart--;

        var end = lineStart;
        while (end < tokenStart && span[end] is (byte)' ' or (byte)'\t')
            end++;

        return end == tokenStart
            ? Encoding.UTF8.GetString(span[lineStart..end])
            : string.Empty;
    }

    private static string DetectNewline(ReadOnlySpan<byte> span)
    {
        var index = span.IndexOf((byte)'\n');
        return index > 0 && span[index - 1] == (byte)'\r' ? "\r\n" : "\n";
    }

    private static void WriteAtomic(string path, byte[] content)
    {
        var temp = path + ".mega.tmp";
        File.WriteAllBytes(temp, content);
        try
        {
            File.SetUnixFileMode(temp, File.GetUnixFileMode(path));
        }
        catch
        {
            // Windows/FAT: режим не скопировать — не повод не сохранить конфиг.
        }

        File.Move(temp, path, overwrite: true);
    }
}
