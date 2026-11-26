using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>跟随能力单独保存为当前 Windows 用户的 DPAPI 密文，不写入房间配置或服务器。</summary>
public sealed class RoomFollowGrantStore
{
    private const int MaxPlaintextBytes = 1024 * 1024;
    private const int MaxProtectedBytes = MaxPlaintextBytes + 64 * 1024;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("NexusBGI.RoomFollowGrants.v1");
    private readonly string _path;

    public RoomFollowGrantStore(string? path = null) =>
        _path = Path.GetFullPath(path ?? Path.Combine(AssistantDataDirectory.Root, "room-follow-grants.protected"));

    /// <summary>仅不存在表示没有授权；损坏或无法解密不冒充空列表。</summary>
    public IReadOnlyList<RoomFollowGrant> Load()
    {
        byte[]? plaintext = null;
        try
        {
            ConfigTransferTransaction.CheckPath(_path);
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is <= 0 or > MaxProtectedBytes) throw InvalidStore();
            var ciphertext = new byte[(int)stream.Length];
            stream.ReadExactly(ciphertext);
            plaintext = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            if (plaintext.Length > MaxPlaintextBytes) throw InvalidStore();
            using var document = JsonDocument.Parse(plaintext, RoomFollowFormat.Document);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() > RoomFollowFormat.MaxGrants) throw InvalidStore();
            foreach (var item in root.EnumerateArray())
            {
                if (!RoomFollowFormat.HasFields(item, ["invitation", "writeSecret", "readHash"], optional: "browserId")) throw InvalidStore();
                _ = RoomFollowInvitation.Read(item.GetProperty("invitation"));
            }
            var grants = root.Deserialize<RoomFollowGrant[]>(RoomFollowFormat.Json) ?? throw InvalidStore();
            Validate(grants);
            return grants;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or CryptographicException or FormatException or ArgumentException)
        { throw InvalidStore(); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    /// <summary>显式替换授权列表；只将加密后数据原子写入，损坏的旧文件不自动覆盖。</summary>
    public void Save(IReadOnlyList<RoomFollowGrant> grants)
    {
        ArgumentNullException.ThrowIfNull(grants);
        if (grants.Count > RoomFollowFormat.MaxGrants) throw InvalidStore();
        var copy = grants.ToArray();
        Validate(copy);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(copy, RoomFollowFormat.Json);
        try
        {
            if (plaintext.Length > MaxPlaintextBytes) throw InvalidStore();
            var ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            if (ciphertext.Length > MaxProtectedBytes) throw InvalidStore();
            using var writing = ConfigFileWriteGate.Acquire(_path);
            _ = Load();
            var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(ciphertext);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, _path, overwrite: true);
            }
            finally { File.Delete(temporary); }
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static void Validate(IReadOnlyList<RoomFollowGrant> grants)
    {
        if (grants.Count > RoomFollowFormat.MaxGrants) throw InvalidStore();
        var channels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var grant in grants)
        {
            if (grant is null) throw InvalidStore();
            grant.Validate();
            if (!channels.Add(grant.Invitation.ChannelId)) throw InvalidStore();
        }
    }

    private static InvalidDataException InvalidStore() => new("房间跟随授权存储无效、超限或不属于当前 Windows 用户。");
}
