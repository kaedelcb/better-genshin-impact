using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace BgiCoordinatorServer.Services;

public static class ControlRoomAuth
{
    private static readonly ConcurrentDictionary<string, string> RoomPasswordHashes = new();

    /// <summary>
    /// 校验密码 + UID 白名单。
    /// 首次设置：房间尚无密码哈希时，记录 SHA256 并允许加入。
    /// 已有哈希时：对比 SHA256 是否匹配。
    /// UID 白名单校验：allowedUids 为空列表时不校验（网页端），否则必须包含 playerUid。
    /// </summary>
    public static bool Authenticate(string roomCode, string password, string playerUid, List<string> allowedUids)
    {
        // UID 白名单校验
        if (allowedUids.Count > 0 && !allowedUids.Contains(playerUid))
            return false;

        var hash = ComputeHash(roomCode, password);

        if (RoomPasswordHashes.TryGetValue(roomCode, out var storedHash))
        {
            return hash == storedHash;
        }
        else
        {
            // 首次设置
            RoomPasswordHashes[roomCode] = hash;
            return true;
        }
    }

    private static string ComputeHash(string roomCode, string password)
    {
        var input = $"{roomCode}:{password}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}