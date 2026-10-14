using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal sealed class RemoteCenterOperationConflictException(string message) : InvalidOperationException(message);

/// <summary>执行端操作回执台账。重启前未终结的受理只恢复为未知，绝不假成功。</summary>
internal sealed class RemoteCenterOperationStore
{
    private sealed record Entry(RemoteCenterReply Reply, DateTimeOffset UpdatedAtUtc,
        string? RequestFingerprint = null);
    private static readonly object Gate = new();
    private readonly string _path;

    internal RemoteCenterOperationStore(string? path = null)
        => _path = path ?? Path.Combine(AssistantDataDirectory.Root,
            "remote-center-operation-results.json");

    internal void Begin(string id, RemoteCenterReply accepted, string requestFingerprint)
    {
        lock (Gate)
        {
            var map = Read();
            if (map.ContainsKey(id))
                throw new InvalidOperationException("执行端已有相同远程操作身份，拒绝重复执行");
            map[id] = new Entry(accepted, DateTimeOffset.UtcNow, requestFingerprint);
            Write(map);
        }
    }

    internal void Complete(string id, RemoteCenterReply result)
    {
        lock (Gate)
        {
            var map = Read();
            if (!map.TryGetValue(id, out var accepted))
                throw new InvalidOperationException("执行端缺少原受理记录，终态不能无依据写入");
            map[id] = new Entry(result, DateTimeOffset.UtcNow, accepted.RequestFingerprint);
            Write(map);
        }
    }

    internal RemoteCenterReply? Get(string id)
    {
        lock (Gate)
        {
            if (!Read().TryGetValue(id, out var entry)) return null;
            return RecoveredReply(id, entry);
        }
    }

    internal RemoteCenterReply? GetForRequest(string id, string requestFingerprint)
    {
        lock (Gate)
        {
            if (!Read().TryGetValue(id, out var entry)) return null;
            if (entry.RequestFingerprint != requestFingerprint)
                throw new RemoteCenterOperationConflictException(
                    "执行端已有相同操作身份，但原请求内容不一致或旧记录无法核对；本次未执行，原操作责任保持");
            return RecoveredReply(id, entry);
        }
    }

    private static RemoteCenterReply RecoveredReply(string id, Entry entry)
        => entry.Reply.Status == "accepted"
            ? RemoteCenterReply.Unknown(id,
                "执行端重启前已受理此操作，但未留下终态；请核对执行端运行记录")
            : entry.Reply;

    private Dictionary<string, Entry> Read()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(_path))
                ?? throw new InvalidDataException("执行端操作台账为空");
            if (map.Any(item => string.IsNullOrWhiteSpace(item.Key)
                || item.Value is null
                || item.Value.Reply is null
                || string.IsNullOrWhiteSpace(item.Value.Reply.Status)))
                throw new InvalidDataException("执行端操作台账身份不完整");
            return new(map, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException("执行端远程操作台账无法读取；已保留原文件，禁止新操作", ex);
        }
    }

    private void Write(Dictionary<string, Entry> map)
    {
        if (map.Count > 2048)
        {
            foreach (var completed in map.Where(item => item.Value.Reply.Status is "ok" or "rejected")
                .OrderBy(item => item.Value.UpdatedAtUtc).Take(map.Count - 1536).ToArray())
                map.Remove(completed.Key);
        }
        if (map.Count > 4096)
            throw new IOException("执行端未决操作过多，拒绝继续受理以保护旧回执");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) File.Copy(_path, _path + ".backup", overwrite: true);
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        if (!LocalPeerSyncService.WriteFileAtomic(_path, json)
            || !string.Equals(File.ReadAllText(_path), json, StringComparison.Ordinal))
            throw new IOException("执行端操作回执台账写入未确认；操作没有新受理");
    }
}
