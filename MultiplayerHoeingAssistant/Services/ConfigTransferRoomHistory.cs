using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Portable room bookmarks. No plaintext or DPAPI-encrypted password leaves the source machine.</summary>
public static class ConfigTransferRoomHistory
{
    public const string EntryName = "remote-log-room-bookmarks.json";
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    public sealed record Bookmark(string ServerUrl, string RoomCode, DateTimeOffset LastUsedUtc);

    public static byte[] Export(byte[] localHistory)
    {
        var records = Read(localHistory);
        var bookmarks = records.Select(r => new Bookmark(String(r, "serverUrl"), String(r, "roomCode"), Time(r))).ToArray();
        Validate(bookmarks);
        return JsonSerializer.SerializeToUtf8Bytes(bookmarks, Options);
    }

    /// <summary>Preserves every local password; new entries are bookmarks requiring authorization.</summary>
    public static byte[] PrepareImport(byte[] portableBookmarks, byte[]? localHistory)
    {
        // Reject unexpected fields, especially passwords accidentally supplied as a portable bookmark.
        var portable = Read(portableBookmarks);
        foreach (var record in portable)
            if (record.Any(p => p.Key is not ("serverUrl" or "roomCode" or "lastUsedUtc")))
                throw new InvalidDataException("协助房间书签包含非便携字段，未导入");
        var bookmarks = portable.Select(r => new Bookmark(String(r, "serverUrl"), String(r, "roomCode"), Time(r))).ToArray();
        Validate(bookmarks);
        var local = localHistory == null ? new JsonArray() : JsonNode.Parse(localHistory) as JsonArray
            ?? throw new InvalidDataException("本机房间记录无效，已保留原文件");
        foreach (var bookmark in bookmarks)
        {
            if (local.OfType<JsonObject>().Any(r => String(r, "serverUrl") == bookmark.ServerUrl && String(r, "roomCode") == bookmark.RoomCode)) continue;
            local.Add(new JsonObject
            {
                ["serverUrl"] = bookmark.ServerUrl, ["roomCode"] = bookmark.RoomCode,
                ["protectedPassword"] = "", ["lastUsedUtc"] = JsonValue.Create(bookmark.LastUsedUtc)
            });
        }
        return JsonSerializer.SerializeToUtf8Bytes(local, Options);
    }
    private static JsonObject[] Read(byte[] bytes)
    {
        var array = JsonNode.Parse(bytes) as JsonArray ?? throw new InvalidDataException("房间书签应为数组");
        return array.Select(r => r as JsonObject ?? throw new InvalidDataException("房间书签记录无效")).ToArray();
    }
    private static string String(JsonObject record, string key)
        => record.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>() ?? "";
    private static DateTimeOffset Time(JsonObject record)
        => record.FirstOrDefault(p => p.Key.Equals("lastUsedUtc", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<DateTimeOffset>() ?? DateTimeOffset.MinValue;
    private static void Validate(IEnumerable<Bookmark> bookmarks)
    {
        foreach (var bookmark in bookmarks)
            if (!Uri.TryCreate(bookmark.ServerUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")
                || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                || string.IsNullOrWhiteSpace(bookmark.RoomCode) || bookmark.RoomCode.Length > 256 || bookmark.RoomCode.Any(char.IsControl))
                throw new InvalidDataException("房间书签的服务器或房间号无效");
    }
}
