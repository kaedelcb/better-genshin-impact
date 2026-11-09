using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BgiCoordinatorServer.Services;

public static class ControlRoomAuth
{
    private static readonly ConcurrentDictionary<string, string> RoomPasswordHashes = new();

    /// <summary>
    /// 校验密码 + UID 白名单。
    /// 首次设置：房间尚无密码哈希且 allowCreate 时，记录 SHA256 并允许加入（即建房）。
    /// 房间尚无密码哈希且不允许建房时拒绝（网页端只能加入助手已建好的房间）。
    /// 已有哈希时：对比 SHA256 是否匹配。
    /// UID 白名单校验：allowedUids 为空列表时不校验（网页端），否则必须包含 playerUid。
    /// </summary>
    public static ControlRoomAuthResult Authenticate(string roomCode, string password, string playerUid,
        List<string> allowedUids, bool allowCreate = true)
    {
        // UID 白名单校验
        if (allowedUids.Count > 0 && !allowedUids.Contains(playerUid))
            return ControlRoomAuthResult.Rejected;

        var hash = ComputeHash(roomCode, password);

        if (RoomPasswordHashes.TryGetValue(roomCode, out var storedHash))
            return hash == storedHash ? ControlRoomAuthResult.Joined : ControlRoomAuthResult.Rejected;
        if (!allowCreate)
            return ControlRoomAuthResult.RoomNotFound;

        // 首次设置；并发首建时以先写入者为准
        storedHash = RoomPasswordHashes.GetOrAdd(roomCode, hash);
        return hash == storedHash ? ControlRoomAuthResult.Joined : ControlRoomAuthResult.Rejected;
    }

    private static string ComputeHash(string roomCode, string password)
    {
        var input = $"{roomCode}:{password}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}

public enum ControlRoomAuthResult
{
    Joined,
    Rejected,
    RoomNotFound,
}
