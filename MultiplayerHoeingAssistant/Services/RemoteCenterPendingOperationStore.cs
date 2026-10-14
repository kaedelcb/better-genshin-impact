using System.IO;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

internal sealed record RemoteCenterPendingOperation(string Id, string Operation, DateTimeOffset SentAtUtc);

/// <summary>监控端发送前落盘的远程操作身份；重启后仍先核对原请求，不猜测重发。</summary>
internal sealed class RemoteCenterPendingOperationStore
{
    private static readonly object Gate = new();
    private readonly string _path;

    internal RemoteCenterPendingOperationStore(string? path = null)
        => _path = path ?? Path.Combine(AssistantDataDirectory.Root,
            "remote-center-pending-operations.json");

    internal RemoteCenterPendingOperation? Get(string targetKey)
    {
        lock (Gate) return Read().GetValueOrDefault(targetKey);
    }

    internal void Remember(string targetKey, RemoteCenterPendingOperation operation)
    {
        lock (Gate)
        {
            var map = Read();
            if (map.ContainsKey(targetKey))
                throw new InvalidOperationException("该执行端仍有未确认操作，不能生成新的命令");
            map[targetKey] = operation;
            Write(map);
        }
    }

    internal void Clear(string targetKey, string operationId)
    {
        lock (Gate)
        {
            var map = Read();
            if (map.TryGetValue(targetKey, out var current) && current.Id == operationId)
            {
                map.Remove(targetKey);
                Write(map);
            }
        }
    }

    private Dictionary<string, RemoteCenterPendingOperation> Read()
    {
        if (!File.Exists(_path)) return new(StringComparer.Ordinal);
        try
        {
            var map = JsonSerializer.Deserialize<Dictionary<string, RemoteCenterPendingOperation>>(
                File.ReadAllText(_path)) ?? throw new InvalidDataException("操作台账为空");
            if (map.Any(entry => string.IsNullOrWhiteSpace(entry.Key)
                || entry.Value is null
                || string.IsNullOrWhiteSpace(entry.Value.Id)
                || string.IsNullOrWhiteSpace(entry.Value.Operation)))
                throw new InvalidDataException("操作台账身份不完整");
            return new(map, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException)
        {
            throw new InvalidDataException("监控端远程操作台账无法读取；已保留原文件，禁止新操作", ex);
        }
    }

    private void Write(Dictionary<string, RemoteCenterPendingOperation> map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (File.Exists(_path)) File.Copy(_path, _path + ".backup", overwrite: true);
        var json = JsonSerializer.Serialize(map, new JsonSerializerOptions { WriteIndented = true });
        if (!LocalPeerSyncService.WriteFileAtomic(_path, json)
            || !string.Equals(File.ReadAllText(_path), json, StringComparison.Ordinal))
            throw new IOException("监控端远程操作台账未确认写入；禁止发送新命令");
    }
}
