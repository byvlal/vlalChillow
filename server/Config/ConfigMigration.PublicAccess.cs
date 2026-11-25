using System.Text;
using System.Text.Json;

namespace StandChillow.LanServer.Config;

internal static partial class ConfigMigration
{
    /// <summary>
    /// Убедиться, что в файле есть корневая секция <c>publicAccess</c>, и перенести
    /// в неё устаревший <c>network.advertiseIp</c>. Существующую секцию не трогает,
    /// кроме пустого <c>address</c>: туда переезжает старое значение. Повторный
    /// запуск ничего не меняет.
    /// </summary>
    internal static bool EnsurePublicAccessSection(string path, out string message)
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
        if (!layout.Valid)
            return false;

        var newline = DetectNewline(span);
        var rootIndent = layout.FirstRootProperty >= 0
            ? IndentAt(span, layout.FirstRootProperty)
            : "  ";
        if (rootIndent.Length == 0)
            rootIndent = "  ";

        // Старый ключ: null/отсутствует — переносить нечего.
        var hasLegacy = TryFindScalar(
            span, new[] { "network", "advertiseIp" },
            out var legacyStart, out var legacyEnd, out var legacyKind);
        string? legacy = null;
        if (hasLegacy && legacyKind == JsonTokenType.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(
                    span[legacyStart..legacyEnd].ToArray());
                legacy = doc.RootElement.GetString()?.Trim();
                if (legacy is { Length: 0 })
                    legacy = null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        var hasAddress = TryFindScalar(
            span, new[] { "publicAccess", "address" },
            out var addressStart, out var addressEnd, out var addressKind);
        string? currentAddress = null;
        if (hasAddress && addressKind == JsonTokenType.String)
        {
            try
            {
                using var doc = JsonDocument.Parse(
                    span[addressStart..addressEnd].ToArray());
                currentAddress = doc.RootElement.GetString()?.Trim();
                if (currentAddress is { Length: 0 })
                    currentAddress = null;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        var edits = new List<Edit>();

        if (!layout.HasPublicAccess)
        {
            var insertAt = LastContentByte(span, layout.RootEnd);
            if (insertAt < 0)
                return false;

            var block = new StringBuilder();
            if (layout.RootHasProperties && span[insertAt - 1] != (byte)',')
                block.Append(',');
            block.Append(newline).Append(newline);
            block.Append(rootIndent).Append("// ===== ДОСТУП ИЗВНЕ =====").Append(newline);
            block.Append(rootIndent).Append("// playit.gg, туннели, проброс портов. Сервер слушает свои порты как обычно;").Append(newline);
            block.Append(rootIndent).Append("// здесь — адрес и порты, которые видят игроки снаружи.").Append(newline);
            block.Append(rootIndent).Append("// Пусто/null/0 — не подменять (обычный LAN-режим).").Append(newline);
            block.Append(rootIndent).Append("\"publicAccess\": {").Append(newline);
            block.Append(rootIndent).Append("  // Внешний IPv4-адрес, который выдал сервис.").Append(newline);
            block.Append(rootIndent).Append("  \"address\": ")
                 .Append(legacy is null ? "null" : JsonSerializer.Serialize(legacy))
                 .Append(',').Append(newline);
            block.Append(newline);
            block.Append(rootIndent).Append("  // Внешний UDP-порт лобби: туннель ведёт с него на server.lobbyPort.").Append(newline);
            block.Append(rootIndent).Append("  \"lobbyPort\": 0,").Append(newline);
            block.Append(newline);
            block.Append(rootIndent).Append("  // Внешний UDP-порт матча: туннель ведёт с него на 7777.").Append(newline);
            block.Append(rootIndent).Append("  \"matchPort\": 0").Append(newline);
            block.Append(rootIndent).Append('}');

            edits.Add(new Edit(insertAt, insertAt, Encoding.UTF8.GetBytes(block.ToString())));

            if (hasLegacy && legacyKind != JsonTokenType.Null)
            {
                edits.Add(new Edit(
                    legacyStart, legacyEnd, Encoding.UTF8.GetBytes("null")));
            }

            message = legacy is null
                ? "добавлена секция publicAccess (выключена)"
                : $"добавлена секция publicAccess, network.advertiseIp → publicAccess.address ('{legacy}')";
        }
        else if (hasLegacy && legacyKind != JsonTokenType.Null)
        {
            // Секция уже есть: старый ключ либо переезжает в пустой address,
            // либо (если новый адрес уже задан) просто гасится.
            if (legacy is not null)
            {
                if (hasAddress)
                {
                    if (currentAddress is null)
                    {
                        edits.Add(new Edit(
                            addressStart, addressEnd,
                            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(legacy))));
                    }
                }
                else if (layout.PublicAccessStart >= 0)
                {
                    var indent = layout.FirstPublicAccessProperty >= 0
                        ? IndentAt(span, layout.FirstPublicAccessProperty)
                        : rootIndent + "  ";
                    if (indent.Length == 0)
                        indent = rootIndent + "  ";

                    var insert = new StringBuilder();
                    insert.Append(newline).Append(indent)
                        .Append("\"address\": ")
                        .Append(JsonSerializer.Serialize(legacy));
                    if (layout.PublicAccessHasProperties)
                        insert.Append(',');
                    edits.Add(new Edit(
                        layout.PublicAccessStart + 1,
                        layout.PublicAccessStart + 1,
                        Encoding.UTF8.GetBytes(insert.ToString())));
                }
                else
                {
                    // Не смогли положить значение — старый ключ не трогаем.
                    return false;
                }
            }

            // Старое значение уже сохранено в новом месте — гасим ключ.
            edits.Add(new Edit(
                legacyStart, legacyEnd, Encoding.UTF8.GetBytes("null")));

            message = currentAddress is null && legacy is not null
                ? $"network.advertiseIp → publicAccess.address ('{legacy}')"
                : "network.advertiseIp обнулён (адрес уже задан в publicAccess)";
        }

        if (edits.Count == 0)
            return false;

        var updated = Apply(span, edits);
        var result = new byte[bom + updated.Length];
        if (bom > 0)
            Buffer.BlockCopy(bytes, 0, result, 0, bom);
        Buffer.BlockCopy(updated, 0, result, bom, updated.Length);

        // Миграция не имеет права оставить битый конфиг.
        try
        {
            using var doc = JsonDocument.Parse(result.AsMemory(bom), new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("publicAccess", out var access)
                || access.ValueKind != JsonValueKind.Object)
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

        if (backup.Length > 0)
            message += $" (копия: {backup})";
        return true;
    }
}
