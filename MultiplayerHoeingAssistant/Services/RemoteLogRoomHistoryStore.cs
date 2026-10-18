using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>按服务器与房间保存协助连接记录。密码只以当前 Windows 账户的 DPAPI 密文落盘。</summary>
public sealed class RemoteLogRoomHistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly string _path;

    public RemoteLogRoomHistoryStore(string? path = null) =>
        _path = path ?? Path.Combine(AssistantDataDirectory.Root, "remote-log-rooms.json");

    public IReadOnlyList<RemoteLogRoomHistoryEntry> GetRooms(string serverUrl)
    {
        var server = NormalizeServer(serverUrl);
        return Read().Where(r => r.ServerUrl == server).OrderByDescending(r => r.LastUsedUtc)
            .Select(r => new RemoteLogRoomHistoryEntry(r.RoomCode, r.LastUsedUtc)).ToList();
    }

    public string? GetPassword(string serverUrl, string roomCode)
    {
        var server = NormalizeServer(serverUrl);
        roomCode = roomCode.Trim();
        var record = Read().FirstOrDefault(r => r.ServerUrl == server && r.RoomCode == roomCode);
        if (record == null) return null;
        if (string.IsNullOrEmpty(record.ProtectedPassword))
            throw new InvalidOperationException("迁移的房间书签未包含密码，请重新输入密码授权连接");
        var bytes = ProtectedData.Unprotect(Convert.FromBase64String(record.ProtectedPassword),
            Entropy(server, roomCode), DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    /// <summary>仅在连接成功后调用；相同服务器的同一房间更新密码和使用时间，其余记录保留。</summary>
    public void Remember(string serverUrl, string roomCode, string password)
    {
        var server = NormalizeServer(serverUrl);
        roomCode = roomCode.Trim();
        if (string.IsNullOrEmpty(roomCode) || string.IsNullOrEmpty(password))
            throw new ArgumentException("房间号和密码不能为空");
        using var writing = ConfigFileWriteGate.Acquire(_path);
        var records = Read(); // 损坏的历史不能被静默覆盖。
        var bytes = Encoding.UTF8.GetBytes(password);
        string protectedPassword;
        try
        {
            protectedPassword = Convert.ToBase64String(ProtectedData.Protect(bytes,
                Entropy(server, roomCode), DataProtectionScope.CurrentUser));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
        records.RemoveAll(r => r.ServerUrl == server && r.RoomCode == roomCode);
        records.Add(new StoredRoom(server, roomCode, protectedPassword, DateTimeOffset.UtcNow));
        Write(records);
    }

    public void Forget(string serverUrl, string roomCode)
    {
        var server = NormalizeServer(serverUrl);
        using var writing = ConfigFileWriteGate.Acquire(_path);
        var records = Read();
        if (records.RemoveAll(r => r.ServerUrl == server && r.RoomCode == roomCode.Trim()) > 0) Write(records);
    }

    private List<StoredRoom> Read() => File.Exists(_path)
        ? JsonSerializer.Deserialize<List<StoredRoom>>(File.ReadAllText(_path), JsonOptions)
            ?? throw new InvalidDataException("房间记录格式无效")
        : [];

    private void Write(List<StoredRoom> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(records.OrderByDescending(r => r.LastUsedUtc), JsonOptions));
            File.Move(temporary, _path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string NormalizeServer(string serverUrl)
    {
        var value = MhaGatewayClient.NormalizeBaseUrl(serverUrl, out _);
        var uri = new Uri(value, UriKind.Absolute);
        return uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant() + uri.AbsolutePath.TrimEnd('/');
    }

    private static byte[] Entropy(string server, string roomCode) =>
        Encoding.UTF8.GetBytes("NexusBGI.RemoteLogRoom.v1\n" + server + "\n" + roomCode);

    private sealed record StoredRoom(string ServerUrl, string RoomCode, string ProtectedPassword, DateTimeOffset LastUsedUtc);
}

public sealed record RemoteLogRoomHistoryEntry(string RoomCode, DateTimeOffset LastUsedUtc);
