using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

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
internal static partial class ConfigMigration
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

    /// <summary>
    /// Убедиться, что в файле есть корневая секция <c>test</c> с двумя выключенными
    /// флагами. Существующую секцию не трогает: даже если в ней только один ключ,
    /// второй возьмёт значение по умолчанию. Перед записью делает копию в
    /// <c>auth/backups</c>, после — проверяет, что JSON остался валидным.
    /// </summary>
    internal static bool EnsureTestSection(string path, out string message)
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
        if (!layout.Valid || layout.HasTest)
            return false;

        var newline = DetectNewline(span);
        var rootIndent = layout.FirstRootProperty >= 0
            ? IndentAt(span, layout.FirstRootProperty)
            : "  ";
        if (rootIndent.Length == 0)
            rootIndent = "  ";

        var insertAt = LastContentByte(span, layout.RootEnd);
        if (insertAt < 0)
            return false;

        var block = new StringBuilder();
        if (layout.RootHasProperties && span[insertAt - 1] != (byte)',')
            block.Append(',');
        block.Append(newline).Append(newline);
        block.Append(rootIndent).Append("// ===== ТЕСТОВЫЙ РЕЖИМ =====").Append(newline);
        block.Append(rootIndent).Append("// !!! ЗАПРЕЩЕНО ВКЛЮЧАТЬ НА БОЕВОМ СЕРВЕРЕ !!!").Append(newline);
        block.Append(rootIndent).Append("// Только отдельный тестовый сервер. Включишь на боевом —").Append(newline);
        block.Append(rootIndent).Append("// любой игрок получит мега-админку и снесёт конфиг, баны и админов.").Append(newline);
        block.Append(rootIndent).Append("// Оба флага по умолчанию false. Панель их не редактирует.").Append(newline);
        block.Append(rootIndent).Append("\"test\": {").Append(newline);
        block.Append(rootIndent).Append("  // вход в панель и команды без личного пароля;").Append(newline);
        block.Append(rootIndent).Append("  // НИКОГДА не включать на боевом сервере.").Append(newline);
        block.Append(rootIndent).Append("  \"noAdminPassword\": false,").Append(newline);
        block.Append(newline);
        block.Append(rootIndent).Append("  // аккаунту без админки выдаётся мега-админка;").Append(newline);
        block.Append(rootIndent).Append("  // НИКОГДА не включать на боевом сервере.").Append(newline);
        block.Append(rootIndent).Append("  \"autoMegaAdmin\": false").Append(newline);
        block.Append(rootIndent).Append('}');

        var updated = Apply(span, new List<Edit>
        {
            new(insertAt, insertAt, Encoding.UTF8.GetBytes(block.ToString())),
        });
        var result = new byte[bom + updated.Length];
        if (bom > 0)
            Buffer.BlockCopy(bytes, 0, result, 0, bom);
        Buffer.BlockCopy(updated, 0, result, bom, updated.Length);

        // Миграция не имеет права оставить владельца с битым конфигом: проверяем
        // результат до записи теми же правилами, что и загрузчик.
        try
        {
            using var doc = JsonDocument.Parse(result.AsMemory(bom), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("test", out var test)
                || test.ValueKind != JsonValueKind.Object)
                return false;
        }
        catch (JsonException)
        {
            return false;
        }

        BackupFile(path, out var backup);
        try
        {
            WriteAtomic(path, result);
        }
        catch
        {
            return false;
        }

        message = "добавлена секция test: noAdminPassword=false, autoMegaAdmin=false"
                  + (backup.Length > 0 ? $" (копия: {backup})" : "");
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
    /// <summary>
    /// Заменить <c>admin.ids</c> целиком, сохранив комментарии вокруг массива.
    /// Нужно панели: добавление/удаление обычного админа — это правка конфига,
    /// а не запись в обход него. Пустой список разрешён: сервер может держать
    /// только мега-админов, они и так попадают в рабочий список. За то, что без
    /// мега-админов список не останется пустым, отвечает вызывающий.
    /// </summary>
    internal static bool TrySetAdminIds(
        string path, IReadOnlyList<int> ids, out string message)
    {
        message = string.Empty;
        if (ids is null)
        {
            message = "список админов не передан";
            return false;
        }

        var clean = ids.Where(id => id > 0).Distinct().OrderBy(id => id).ToList();
        if (clean.Count != ids.Distinct().Count())
        {
            message = "в списке админов есть некорректные id";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            message = $"конфиг не прочитан: {ex.Message}";
            return false;
        }

        var bom = bytes.Length >= 3
                  && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        var span = bytes.AsSpan(bom);
        var layout = Scan(span);
        if (!layout.Valid || !layout.HasAdmin || !layout.HasIds
            || layout.IdsStart < 0 || layout.IdsEnd <= layout.IdsStart)
        {
            message = "в config.json нет admin.ids в ожидаемом виде — правь файл руками";
            return false;
        }

        var newline = DetectNewline(span);
        var indent = IndentAt(span, layout.IdsPropertyStart);
        var multiline = span[layout.IdsStart..layout.IdsEnd].IndexOf((byte)'\n') >= 0;
        var text = multiline
            ? "[" + newline
              + string.Join(newline,
                  clean.Select(id => indent + "  " + id.ToString(CultureInfo.InvariantCulture)
                                     + ","))
                  .TrimEnd(',') // последняя запятая не нужна
              + newline + indent + "]"
            : "[ " + string.Join(", ", clean) + " ]";

        var updated = Apply(span, new List<Edit>
        {
            new(layout.IdsStart, layout.IdsEnd, Encoding.UTF8.GetBytes(text)),
        });
        var result = new byte[bom + updated.Length];
        if (bom > 0)
            Buffer.BlockCopy(bytes, 0, result, 0, bom);
        Buffer.BlockCopy(updated, 0, result, bom, updated.Length);

        try
        {
            BackupFile(path, out var backup);
            WriteAtomic(path, result);
            message = backup.Length == 0
                ? $"admin.ids → [ {string.Join(", ", clean)} ]"
                : $"admin.ids → [ {string.Join(", ", clean)} ]; копия: {backup}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"конфиг не записан: {ex.Message}";
            return false;
        }
    }

    /// <summary>Тип значения, которое панель разрешает менять в config.json.</summary>
    internal enum ScalarKind
    {
        String,
        Bool,
        Int,
    }

    /// <summary>
    /// Заменить скалярное значение по пути (<c>admin.ban.enabled</c>), сохранив
    /// комментарии и форматирование. Меняются только объектные ключи известной формы;
    /// тип обязан совпадать с текущим, иначе конфиг пишется «наугад» и сервер его
    /// молча заменит дефолтом. После записи файл ещё раз разбирается: битый JSON
    /// на диск не попадает.
    /// </summary>
    internal static bool TrySetScalar(
        string path,
        IReadOnlyList<string> keyPath,
        ScalarKind kind,
        string rawValue,
        out string message)
    {
        message = string.Empty;
        if (keyPath is null || keyPath.Count == 0)
        {
            message = "не указан ключ конфига";
            return false;
        }

        var literal = kind switch
        {
            ScalarKind.String => JsonSerializer.Serialize(
                rawValue ?? "", new JsonSerializerOptions
                {
                    Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
                }),
            ScalarKind.Bool => bool.TryParse((rawValue ?? "").Trim(), out var flag)
                ? (flag ? "true" : "false")
                : null,
            ScalarKind.Int => int.TryParse(
                (rawValue ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var number) ? number.ToString(CultureInfo.InvariantCulture) : null,
            _ => null,
        };
        if (literal is null)
        {
            message = "значение не того типа";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            message = $"конфиг не прочитан: {ex.Message}";
            return false;
        }

        var bom = bytes.Length >= 3
                  && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
            ? 3
            : 0;
        var span = bytes.AsSpan(bom);
        if (!TryFindScalar(span, keyPath, out var start, out var end, out var current))
        {
            message = $"в config.json нет {string.Join('.', keyPath)} в ожидаемом виде — правь файл руками";
            return false;
        }

        var kindMatches = kind switch
        {
            ScalarKind.String => current == JsonTokenType.String,
            ScalarKind.Bool => current is JsonTokenType.True or JsonTokenType.False,
            ScalarKind.Int => current == JsonTokenType.Number,
            _ => false,
        };
        if (!kindMatches)
        {
            message = $"{string.Join('.', keyPath)} в конфиге другого типа — правь файл руками";
            return false;
        }

        var updatedSpan = Apply(span, new List<Edit>
        {
            new(start, end, Encoding.UTF8.GetBytes(literal)),
        });

        // Проверка результата: путь обязан найтись, значение — совпасть.
        if (!TryFindScalar(updatedSpan, keyPath, out _, out _, out var updatedKind)
            || !KindMatches(updatedKind, kind)
            || !JsonParses(updatedSpan))
        {
            message = "новый конфиг не прошёл проверку — файл не изменён";
            return false;
        }

        var result = new byte[bom + updatedSpan.Length];
        if (bom > 0)
            Buffer.BlockCopy(bytes, 0, result, 0, bom);
        Buffer.BlockCopy(updatedSpan, 0, result, bom, updatedSpan.Length);

        try
        {
            BackupFile(path, out _);
            WriteAtomic(path, result);
            message = $"{string.Join('.', keyPath)} → {literal}";
            return true;
        }
        catch (Exception ex)
        {
            message = $"конфиг не записан: {ex.Message}";
            return false;
        }
    }

    /// <summary>Найти байтовые границы скалярного значения по объектному пути.</summary>
    private static bool TryFindScalar(
        ReadOnlySpan<byte> json,
        IReadOnlyList<string> keyPath,
        out int start,
        out int end,
        out JsonTokenType tokenType)
    {
        start = -1;
        end = -1;
        tokenType = JsonTokenType.None;
        var stack = new List<string>();
        string? pending = null;

        try
        {
            var reader = new Utf8JsonReader(json, new JsonReaderOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

            while (reader.Read())
            {
                switch (reader.TokenType)
                {
                    case JsonTokenType.PropertyName:
                        pending = reader.GetString() ?? "";
                        break;

                    case JsonTokenType.StartObject:
                    case JsonTokenType.StartArray:
                        if (pending is not null)
                            stack.Add(pending);
                        pending = null;
                        break;

                    case JsonTokenType.EndObject:
                    case JsonTokenType.EndArray:
                        if (stack.Count > 0)
                            stack.RemoveAt(stack.Count - 1);
                        pending = null;
                        break;

                    default:
                        if (pending is not null
                            && stack.Count == keyPath.Count - 1
                            && NamesMatch(stack, keyPath)
                            && string.Equals(pending, keyPath[^1], StringComparison.Ordinal))
                        {
                            // BytesConsumed — весь документ с начала чтения, а не длина
                            // токена: с ним end вылезал за границы, если после ключа
                            // в файле есть ещё данные. ValueSpan — сырые байты значения
                            // без кавычек, поэтому строке возвращаем их обратно.
                            start = (int)reader.TokenStartIndex;
                            end = start + reader.ValueSpan.Length
                                  + (reader.TokenType == JsonTokenType.String ? 2 : 0);
                            tokenType = reader.TokenType;
                            return true;
                        }

                        pending = null;
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return false;
    }

    private static bool NamesMatch(List<string> stack, IReadOnlyList<string> path)
    {
        if (stack.Count != path.Count - 1)
            return false;
        for (var i = 0; i < stack.Count; i++)
        {
            if (!string.Equals(stack[i], path[i], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool KindMatches(JsonTokenType tokenType, ScalarKind kind) =>
        kind switch
        {
            ScalarKind.String => tokenType == JsonTokenType.String,
            ScalarKind.Bool => tokenType is JsonTokenType.True or JsonTokenType.False,
            ScalarKind.Int => tokenType == JsonTokenType.Number,
            _ => false,
        };

    private static bool JsonParses(ReadOnlySpan<byte> json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Копия конфига перед правкой: <c>auth/backups/config-…json</c>. Каталог auth
    /// закрыт (700), поэтому копия не светит конфиг посторонним. Храним последние 30.
    /// </summary>
    internal static void BackupFile(string path, out string backupPath)
    {
        backupPath = string.Empty;
        try
        {
            var bytes = File.ReadAllBytes(path);
            var configDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            var dir = Path.Combine(configDir, "auth", "backups");
            Directory.CreateDirectory(dir);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, (UnixFileMode)0b111_000_000);

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var name = $"config-{stamp}.json";
            backupPath = Path.Combine(dir, name);
            File.WriteAllBytes(backupPath, bytes);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(backupPath, (UnixFileMode)0b110_000_000);

            foreach (var old in Directory.EnumerateFiles(dir, "config-*.json")
                         .OrderByDescending(File.GetLastWriteTimeUtc)
                         .Skip(30))
            {
                try { File.Delete(old); }
                catch { /* старая копия не удалилась — не повод ломать запись */ }
            }
        }
        catch
        {
            // Без копии правку не блокируем: конфиг важнее, а резервная копия —
            // удобство. Ошибку видно по пустому backupPath в сообщении.
        }
    }

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
        public bool HasTest;
        public bool HasPublicAccess;
        public bool HasAdmin;
        public bool HasIds;
        public bool IdsIsDefault;
        public bool AdminHasProperties;
        public bool PublicAccessHasProperties;
        public bool RootHasProperties;
        public int RootStart;
        public int RootEnd;
        public int AdminStart;
        public int AdminEnd;
        public int PublicAccessStart = -1;
        public int PublicAccessEnd = -1;
        public int IdsPropertyStart;
        public int IdsStart;
        public int IdsEnd;
        public int FirstRootProperty = -1;
        public int FirstAdminProperty = -1;
        public int FirstPublicAccessProperty = -1;

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
        var inPublicAccess = false;
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
                        else if (depth == 2
                                 && string.Equals(rootProperty, "publicAccess",
                                     StringComparison.OrdinalIgnoreCase))
                        {
                            inPublicAccess = true;
                            layout.HasPublicAccess = true;
                            layout.PublicAccessStart = at;
                        }

                        break;

                    case JsonTokenType.EndObject:
                        if (depth == 2 && inAdmin)
                        {
                            layout.AdminEnd = at + 1;
                            inAdmin = false;
                        }
                        else if (depth == 2 && inPublicAccess)
                        {
                            layout.PublicAccessEnd = at + 1;
                            inPublicAccess = false;
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
                            if (string.Equals(rootProperty, "test", StringComparison.OrdinalIgnoreCase))
                                layout.HasTest = true;
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
                        else if (depth == 2 && inPublicAccess)
                        {
                            layout.PublicAccessHasProperties = true;
                            if (layout.FirstPublicAccessProperty < 0)
                                layout.FirstPublicAccessProperty = at;
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
