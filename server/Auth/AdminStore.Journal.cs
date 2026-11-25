using System.Globalization;
using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Auth;

/// <summary>Кто и откуда сделал действие — для журнала и страницы безопасности.</summary>
public sealed record AdminJournalContext(
    int? ActorId,
    string ActorNick = "",
    string ActorIp = "",
    string TargetType = "",
    int? TargetId = null,
    string TargetNick = "",
    string Result = "ok",
    bool Reversible = false,
    string? Note = null,
    string? UndoOf = null);

/// <summary>Одна запись журнала. Старые строки читаются с пустыми новыми полями.</summary>
public sealed record AdminJournalEntry(
    string Id,
    DateTime TsUtc,
    string Category,
    string Action,
    int? ActorId,
    string ActorNick,
    string ActorIp,
    string TargetType,
    int? TargetId,
    string TargetNick,
    string Result,
    bool Reversible,
    string? UndoOf,
    string? Note,
    JsonElement? Before,
    JsonElement? After)
{
    /// <summary>Есть ли более поздняя запись отката, ссылающаяся на эту.</summary>
    public bool Reverted { get; init; }

    /// <summary>Id записи отката, если она попала в прочитанное окно.</summary>
    public string? RevertedBy { get; init; }
}

/// <summary>Фильтр журнала. Все поля необязательные, кроме лимита.</summary>
public sealed class AdminJournalQuery
{
    public int Limit { get; init; } = 50;
    public string? BeforeId { get; init; }
    public DateTime? BeforeTsUtc { get; init; }
    public string? Category { get; init; }
    public string? Action { get; init; }
    public int? ActorId { get; init; }
    public int? TargetId { get; init; }
    public string? Search { get; init; }
    public DateTime? FromUtc { get; init; }
    public DateTime? ToUtc { get; init; }
    public bool ReversibleOnly { get; init; }
}

/// <summary>Страница журнала: новые сверху, курсор — последняя отданная запись.</summary>
public sealed record AdminJournalPage(
    IReadOnlyList<AdminJournalEntry> Entries,
    string? NextId,
    DateTime? NextTsUtc,
    bool HasMore);

/// <summary>
/// Чтение и ротация <c>auth/journal.jsonl</c>. Запись остаётся в основном файле
/// <see cref="AdminStore"/>: здесь только разбор, фильтры и безопасная ротация.
/// </summary>
public sealed partial class AdminStore
{
    /// <summary>После этого размера журнал уезжает в архив, чтобы не рос бесконечно.</summary>
    public const long JournalRotateBytes = 20L * 1024 * 1024;

    /// <summary>Сколько архивов храним рядом. Старые удаляются только при ротации.</summary>
    public const int JournalArchivesKept = 24;

    /// <summary>Сколько строк максимум просматриваем на один запрос — защита от гигантского файла.</summary>
    private const int JournalScanLimit = 50_000;

    private readonly object _journalGate = new();

    /// <summary>Прочитать страницу журнала. Никогда не бросает: битые строки пропускаются.</summary>
    public AdminJournalPage ReadJournal(AdminJournalQuery query)
    {
        query ??= new AdminJournalQuery();
        var limit = Math.Clamp(query.Limit, 1, 200);
        var result = new List<AdminJournalEntry>(limit);
        var undos = new Dictionary<string, string>(StringComparer.Ordinal);
        var scanned = 0;
        var cursorSeen = string.IsNullOrWhiteSpace(query.BeforeId);
        var hasMore = false;

        lock (_journalGate)
        {
            foreach (var line in ReadLinesNewestFirstLocked())
            {
                if (scanned++ >= JournalScanLimit)
                    break;

                var entry = ParseEntry(line);
                if (entry is null)
                    continue;

                if (entry.UndoOf is { Length: > 0 } original)
                    undos[original] = entry.Id;

                if (!cursorSeen)
                {
                    if (query.BeforeTsUtc is { } beforeTs)
                    {
                        // Запасной курсор по времени: id мог уехать в архив ротации.
                        if (entry.TsUtc >= beforeTs)
                            continue;
                        cursorSeen = true;
                    }
                    else if (string.Equals(entry.Id, query.BeforeId, StringComparison.Ordinal))
                    {
                        cursorSeen = true;
                        continue;
                    }
                    else
                    {
                        continue;
                    }
                }

                if (!Matches(entry, query))
                    continue;

                if (result.Count >= limit)
                {
                    hasMore = true;
                    break;
                }

                result.Add(entry);
            }
        }

        // Отметить отменённые записи, если запись отката попала в это же окно.
        if (undos.Count > 0)
        {
            for (var i = 0; i < result.Count; i++)
            {
                if (undos.TryGetValue(result[i].Id, out var undoId))
                    result[i] = result[i] with { Reverted = true, RevertedBy = undoId };
            }
        }

        var last = result.Count > 0 ? result[^1] : null;
        return new AdminJournalPage(
            result,
            last?.Id,
            last?.TsUtc,
            hasMore || (result.Count == limit && scanned < JournalScanLimit));
    }

    /// <summary>Найти одну запись по id. <c>null</c> — нет такой или журнал не читается.</summary>
    public AdminJournalEntry? ReadJournalEntry(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return null;

        AdminJournalEntry? found = null;
        var undos = new Dictionary<string, string>(StringComparer.Ordinal);
        var scanned = 0;

        lock (_journalGate)
        {
            foreach (var line in ReadLinesNewestFirstLocked())
            {
                if (scanned++ >= JournalScanLimit)
                    break;

                var entry = ParseEntry(line);
                if (entry is null)
                    continue;

                if (entry.UndoOf is { Length: > 0 } original)
                    undos[original] = entry.Id;
                if (found is null && string.Equals(entry.Id, id, StringComparison.Ordinal))
                    found = entry;
            }
        }

        if (found is null)
            return null;
        if (undos.TryGetValue(found.Id, out var undoId))
            found = found with { Reverted = true, RevertedBy = undoId };
        return found;
    }

    /// <summary>Записать запись и вернуть её id; <c>null</c> — журнал недоступен.</summary>
    internal string? AppendJournal(object entry, DateTime nowUtc, AdminJournalContext? context = null)
    {
        lock (_journalGate)
        {
            try
            {
                EnsureDirectory(_directory, DirectoryMode);
                var payload = new Dictionary<string, object?>
                {
                    ["id"] = "J-" + Guid.NewGuid().ToString("N")[..16].ToUpperInvariant(),
                    ["ts"] = nowUtc.ToString("O", CultureInfo.InvariantCulture),
                };

                if (context is not null)
                {
                    payload["actorId"] = context.ActorId;
                    payload["actorNick"] = context.ActorNick;
                    payload["actorIp"] = context.ActorIp;
                    payload["targetType"] = context.TargetType;
                    payload["targetId"] = context.TargetId;
                    payload["targetNick"] = context.TargetNick;
                    payload["result"] = context.Result;
                    payload["reversible"] = context.Reversible;
                    if (!string.IsNullOrWhiteSpace(context.Note))
                        payload["note"] = context.Note;
                    if (!string.IsNullOrWhiteSpace(context.UndoOf))
                        payload["undoOf"] = context.UndoOf;
                }

                // JsonElement из анонимного объекта: дописываем поля в тот же объект.
                // Значения оставляем узлами: списки прав должны лежать массивами, а не
                // строкой — по журналу потом строится откат.
                var node = JsonSerializer.SerializeToNode(entry, JournalJsonOptions)?.AsObject();
                if (node is not null)
                {
                    foreach (var property in node)
                        payload[property.Key] = property.Value;
                }

                var line = JsonSerializer.Serialize(payload, JournalJsonOptions) + "\n";
                var path = Path.Combine(_directory, JournalFileName);
                using (var stream = new FileStream(
                           path, FileMode.Append, FileAccess.Write, FileShare.Read))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(line);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: false);
                }

                TrySetMode(path, SecretFileMode);
                RotateJournalLocked(path);
                return payload["id"] as string;
            }
            catch (Exception ex)
            {
                Log($"[auth] журнал не записан: {ex.Message}");
                return null;
            }
        }
    }

    private void RotateJournalLocked(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;
            var info = new FileInfo(path);
            if (info.Length < JournalRotateBytes)
                return;

            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            var archive = Path.Combine(_directory, $"journal-{stamp}.jsonl");
            File.Move(path, archive, overwrite: false);
            TrySetMode(archive, SecretFileMode);

            var archives = System.IO.Directory
                .EnumerateFiles(_directory, "journal-*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(JournalArchivesKept)
                .ToList();
            foreach (var old in archives)
            {
                try { File.Delete(old); }
                catch { /* архив не удалился — не повод ломать запись */ }
            }
        }
        catch (Exception ex)
        {
            Log($"[auth] ротация журнала: {ex.Message}");
        }
    }

    /// <summary>Строки журнала от новых к старым: текущий файл, затем архивы.</summary>
    private IEnumerable<string> ReadLinesNewestFirstLocked()
    {
        var current = Path.Combine(_directory, JournalFileName);
        if (File.Exists(current))
        {
            foreach (var line in ReadLinesReverse(current))
                yield return line;
        }

        IEnumerable<string> archives;
        try
        {
            archives = System.IO.Directory
                .EnumerateFiles(_directory, "journal-*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path => path, StringComparer.Ordinal)
                .ToList();
        }
        catch
        {
            yield break;
        }

        foreach (var archive in archives)
        {
            foreach (var line in ReadLinesReverse(archive))
                yield return line;
        }
    }

    /// <summary>Читать текстовый файл с конца, не загружая его целиком в память.</summary>
    private static IEnumerable<string> ReadLinesReverse(string path)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 16 * 1024, FileOptions.SequentialScan);
        }
        catch
        {
            yield break;
        }

        using (stream)
        {
            var position = stream.Length;
            var chunk = new byte[16 * 1024];
            var line = new List<byte>(256);

            while (position > 0)
            {
                var read = (int)Math.Min(chunk.Length, position);
                position -= read;
                stream.Position = position;
                var got = stream.Read(chunk, 0, read);
                if (got <= 0)
                    break;

                for (var i = got - 1; i >= 0; i--)
                {
                    var b = chunk[i];
                    if (b == (byte)'\n')
                    {
                        if (line.Count > 0)
                        {
                            line.Reverse();
                            var text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                            line.Clear();
                            if (text.Length > 0)
                                yield return text;
                        }
                        continue;
                    }

                    line.Add(b);
                }
            }

            if (line.Count > 0)
            {
                line.Reverse();
                var text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
                if (text.Length > 0)
                    yield return text;
            }
        }
    }

    private static AdminJournalEntry? ParseEntry(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var id = Text(root, "id");
            if (id.Length == 0)
                id = "J-OLD-" + Convert.ToHexString(
                    System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(line)))[..12];

            var ts = DateTime.UtcNow;
            var rawTs = Text(root, "ts");
            if (rawTs.Length > 0)
            {
                if (!DateTime.TryParse(
                        rawTs, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out ts))
                    ts = DateTime.UtcNow;
            }

            JsonElement? before = null;
            JsonElement? after = null;
            if (root.TryGetProperty("before", out var beforeNode)
                && beforeNode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                before = beforeNode.Clone();
            if (root.TryGetProperty("after", out var afterNode)
                && afterNode.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                after = afterNode.Clone();

            return new AdminJournalEntry(
                id,
                ts,
                Text(root, "category"),
                Text(root, "action"),
                Int(root, "actorId"),
                Text(root, "actorNick"),
                Text(root, "actorIp"),
                Text(root, "targetType"),
                Int(root, "targetId"),
                Text(root, "targetNick"),
                Text(root, "result") is { Length: > 0 } result ? result : "ok",
                Bool(root, "reversible"),
                Text(root, "undoOf") is { Length: > 0 } undo ? undo : null,
                Text(root, "note") is { Length: > 0 } note ? note : null,
                before,
                after);
        }
        catch
        {
            return null;
        }
    }

    private static bool Matches(AdminJournalEntry entry, AdminJournalQuery query)
    {
        if (query.Category is { Length: > 0 } category
            && !string.Equals(entry.Category, category, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.Action is { Length: > 0 } action
            && !string.Equals(entry.Action, action, StringComparison.OrdinalIgnoreCase))
            return false;
        if (query.ActorId is { } actor && entry.ActorId != actor)
            return false;
        if (query.TargetId is { } target && entry.TargetId != target)
            return false;
        if (query.ReversibleOnly && !entry.Reversible)
            return false;
        if (query.FromUtc is { } from && entry.TsUtc < from)
            return false;
        if (query.ToUtc is { } to && entry.TsUtc > to)
            return false;
        if (query.Search is { Length: > 0 } search)
        {
            var needle = search.Trim();
            if (needle.Length > 0
                && !Contains(entry.ActorNick, needle)
                && !Contains(entry.TargetNick, needle)
                && !Contains(entry.ActorIp, needle)
                && !Contains(entry.Note, needle)
                && !Contains(entry.Id, needle)
                && entry.ActorId?.ToString(CultureInfo.InvariantCulture) != needle
                && entry.TargetId?.ToString(CultureInfo.InvariantCulture) != needle)
                return false;
        }

        return true;
    }

    private static bool Contains(string? text, string needle) =>
        text is { Length: > 0 }
        && text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String
            ? node.GetString() ?? ""
            : "";

    private static int? Int(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var node))
            return null;
        if (node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var number))
            return number;
        if (node.ValueKind == JsonValueKind.String
            && int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number))
            return number;
        return null;
    }

    private static bool Bool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var node)
        && node.ValueKind == JsonValueKind.True;
}
