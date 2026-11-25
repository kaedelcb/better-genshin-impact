using System.Security.Cryptography;
using System.Text.Json;

namespace BgiCoordinatorServer.Services;

/// <summary>只保存控制房间登记，不保存连接或批次。未提供目录时供独立测试使用内存登记。</summary>
public sealed class ControlRoomAuth
{
    public const int DefaultMaxRoomsPerUid = 5;
    private const string CodeCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int PasswordIterations = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string? _path;
    private readonly int _maxRoomsPerUid;
    private readonly object _gate = new();
    private Dictionary<string, Registration>? _rooms;

    public ControlRoomAuth(string? root = null, int maxRoomsPerUid = DefaultMaxRoomsPerUid)
    {
        _path = root is null ? null : Path.Combine(Path.GetFullPath(root), "registry.json");
        _maxRoomsPerUid = maxRoomsPerUid > 0 ? maxRoomsPerUid : throw new ArgumentOutOfRangeException(nameof(maxRoomsPerUid));
    }

    private sealed record Registration(string? CreatorUid, string[] AllowedUids, string Salt, string PasswordHash);

    internal (string RoomCode, string Reason) Create(string password, string playerUid, IReadOnlyList<string> allowedUids)
    {
        if (!IsMemberUid(playerUid) || !IsPassword(password) || !IsRoster(allowedUids, playerUid))
            return ("", "房间密码或成员 UID 无效");
        lock (_gate)
        {
            var rooms = Load();
            if (rooms.Values.Count(room => room.CreatorUid == playerUid) >= _maxRoomsPerUid)
                return ("", $"该 UID 已达到创建房间上限（{_maxRoomsPerUid}）");
            var code = new char[6];
            string roomCode;
            do
            {
                for (var i = 0; i < code.Length; i++) code[i] = CodeCharacters[RandomNumberGenerator.GetInt32(CodeCharacters.Length)];
                roomCode = new string(code);
                // 原助手房号是 6 位哈希前缀（十六进制），升级前未登记的旧房也不会被新号占用。
            } while (roomCode.All(char.IsAsciiHexDigit) || rooms.ContainsKey(roomCode));
            SaveRegistration(rooms, roomCode, NewRegistration(playerUid, allowedUids, password));
            return (roomCode, "");
        }
    }

    internal ControlRoomAuthResult Authenticate(string roomCode, string password, string playerUid,
        IReadOnlyList<string> allowedUids, bool allowLegacyCreate = false, bool allowLegacyMigration = false)
    {
        if (!IsRoomCode(roomCode) || !IsUid(playerUid) || !IsPassword(password) || !IsRoster(allowedUids, playerUid))
            return ControlRoomAuthResult.Rejected;

        Registration? registration;
        lock (_gate)
        {
            var rooms = Load();
            if (!rooms.TryGetValue(roomCode, out registration))
            {
                var confirmedLegacyCode = roomCode.Length == 6 && roomCode.All(char.IsAsciiHexDigit)
                    && allowedUids.Count > 0
                    && RoomLogContext.ComputeControlCode(allowedUids.OrderBy(uid => uid).ToArray()) == roomCode;
                if (!IsMemberUid(playerUid) || (!allowLegacyCreate && !(allowLegacyMigration && confirmedLegacyCode)))
                    return ControlRoomAuthResult.RoomNotFound;
                SaveRegistration(rooms, roomCode, NewRegistration(null, confirmedLegacyCode ? allowedUids : [], password));
                return ControlRoomAuthResult.Joined;
            }
        }

        // 网页控制端继续用密码 + 原房间昵称校验，日志协助旁观端凭房号和密码只读查看，都不是可执行成员；其他连接不能绕过登记的名单。
        if (IsMemberUid(playerUid) && registration.AllowedUids.Length > 0
            && !registration.AllowedUids.Contains(playerUid, StringComparer.Ordinal)) return ControlRoomAuthResult.Rejected;
        var actual = DerivePassword(password, Convert.FromHexString(registration.Salt));
        return CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(registration.PasswordHash))
            ? ControlRoomAuthResult.Joined : ControlRoomAuthResult.Rejected;
    }

    private Dictionary<string, Registration> Load()
    {
        if (_rooms is not null) return _rooms;
        if (_path is null) return _rooms = new(StringComparer.Ordinal);
        try
        {
            using var stream = File.OpenRead(_path);
            var rooms = JsonSerializer.Deserialize<Dictionary<string, Registration>>(stream, JsonOptions);
            if (rooms is null || rooms.Any(pair => !IsRoomCode(pair.Key) || !IsValidRegistration(pair.Value)))
                throw new JsonException("控制房间登记文件格式无效");
            return _rooms = rooms;
        }
        catch (FileNotFoundException) { return _rooms = new(StringComparer.Ordinal); }
        catch (DirectoryNotFoundException) { return _rooms = new(StringComparer.Ordinal); }
    }

    private void SaveRegistration(Dictionary<string, Registration> rooms, string roomCode, Registration registration)
    {
        if (_path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = File.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(stream, new Dictionary<string, Registration>(rooms, StringComparer.Ordinal)
                    {
                        [roomCode] = registration
                    }, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        rooms.Add(roomCode, registration);
    }

    private static Registration NewRegistration(string? creator, IReadOnlyList<string> roster, string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return new(creator, roster.Distinct(StringComparer.Ordinal).ToArray(), Convert.ToHexString(salt),
            Convert.ToHexString(DerivePassword(password, salt)));
    }

    private static byte[] DerivePassword(string password, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, PasswordIterations, HashAlgorithmName.SHA256, 32);

    private static bool IsRoomCode(string value) => value.Length is > 0 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    private static bool IsUid(string? value) => value is { Length: > 0 and <= 128 }
        && value == value.Trim() && !value.Any(char.IsControl);
    internal static bool IsWebUid(string? value) =>
        IsUid(value) && value!.StartsWith("web_", StringComparison.Ordinal);
    internal static bool IsLogViewerUid(string? value) =>
        IsUid(value) && value!.StartsWith("log_viewer_", StringComparison.Ordinal);
    internal static bool IsMemberUid(string? value) =>
        IsUid(value) && !IsWebUid(value) && !IsLogViewerUid(value);
    private static bool IsPassword(string value) => value.Length is > 0 and <= 128;
    private static bool IsRoster(IReadOnlyList<string> roster, string playerUid) => roster.Count <= 64 &&
        roster.All(IsUid) && (roster.Count == 0 || roster.Contains(playerUid, StringComparer.Ordinal));
    private static bool IsValidRegistration(Registration? room) => room is not null
        && (room.CreatorUid is null || IsMemberUid(room.CreatorUid))
        && room.AllowedUids is { Length: <= 64 } && room.AllowedUids.All(IsUid)
        && room.Salt is { Length: 32 } && room.Salt.All(char.IsAsciiHexDigit)
        && room.PasswordHash is { Length: 64 } && room.PasswordHash.All(char.IsAsciiHexDigit);
}

public enum ControlRoomAuthResult
{
    Joined,
    Rejected,
    RoomNotFound,
}
