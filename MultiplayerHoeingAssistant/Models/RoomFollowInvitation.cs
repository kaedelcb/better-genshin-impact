using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>仅授予一个指定执行端的元数据读取能力，不授予房间登录、配置或控制命令权限。</summary>
public sealed class RoomFollowInvitation
{
    public const string Prefix = "BGIFOLLOW1.";

    [JsonPropertyName("version"), JsonRequired] public int Version { get; init; } = RoomFollowFormat.Version;
    [JsonPropertyName("serverUrl"), JsonRequired] public string ServerUrl { get; init; } = "";
    [JsonPropertyName("local"), JsonRequired] public bool Local { get; init; }
    [JsonPropertyName("uid"), JsonRequired] public string Uid { get; init; } = "";
    [JsonPropertyName("instanceId"), JsonRequired] public string InstanceId { get; init; } = "";
    [JsonPropertyName("machineId"), JsonRequired] public string MachineId { get; init; } = "";
    [JsonPropertyName("channelId"), JsonRequired] public string ChannelId { get; init; } = "";
    [JsonPropertyName("readSecret"), JsonRequired] public string ReadSecret { get; init; } = "";
    [JsonPropertyName("writeHash"), JsonRequired] public string WriteHash { get; init; } = "";

    public string Encode()
    {
        Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, RoomFollowFormat.Json);
        try
        {
            var encoded = Prefix + Base64Url(bytes);
            return encoded.Length <= RoomFollowFormat.MaxInvitationLength ? encoded : throw InvalidInvitation();
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public static RoomFollowInvitation Parse(string value)
    {
        if (value is null || value.Length > RoomFollowFormat.MaxInvitationLength) throw InvalidInvitation();
        value = value.Trim();
        if (!value.StartsWith(Prefix, StringComparison.Ordinal)) throw InvalidInvitation();
        var encoded = value[Prefix.Length..];
        if (encoded.Length == 0 || encoded.Length % 4 == 1 || encoded.Any(c => c is not (>= 'A' and <= 'Z'
            or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))) throw InvalidInvitation();
        try
        {
            var base64 = encoded.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '='));
            try
            {
                if (Base64Url(bytes) != encoded) throw InvalidInvitation();
                using var document = JsonDocument.Parse(bytes, RoomFollowFormat.Document);
                return Read(document.RootElement);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { throw InvalidInvitation(); } // 不回显包含读取能力的原始输入。
    }

    internal static RoomFollowInvitation Read(JsonElement element)
    {
        if (!RoomFollowFormat.HasFields(element, ["version", "serverUrl", "local", "uid", "instanceId", "machineId",
                "channelId", "readSecret", "writeHash"])) throw InvalidInvitation();
        var invitation = element.Deserialize<RoomFollowInvitation>(RoomFollowFormat.Json) ?? throw InvalidInvitation();
        invitation.Validate();
        return invitation;
    }

    internal void Validate()
    {
        if (Version != RoomFollowFormat.Version || !RoomFollowFormat.IsIdentity(Uid)
            || !RoomFollowFormat.IsIdentity(InstanceId) || !RoomFollowFormat.IsIdentity(MachineId)
            || !RoomFollowFormat.IsHex(ReadSecret) || !RoomFollowFormat.IsHex(WriteHash)
            || !RoomFollowFormat.IsHex(ChannelId)) throw InvalidInvitation();
        var readHash = RoomFollowFormat.Hash(ReadSecret);
        if (readHash == WriteHash || ChannelId != RoomFollowFormat.Channel(WriteHash, readHash)
            || (Local ? ServerUrl != "" : RoomFollowFormat.NormalizeRelayUrl(ServerUrl) != ServerUrl))
            throw InvalidInvitation();
    }

    private static FormatException InvalidInvitation() => new("房间跟随邀请格式无效。");
    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>每个跟随者单独发放的授权。发布能力只在执行端保存；BrowserId 为关联标识，不是登录凭据。</summary>
public sealed class RoomFollowGrant
{
    [JsonPropertyName("invitation"), JsonRequired] public RoomFollowInvitation Invitation { get; init; } = new();
    [JsonPropertyName("writeSecret"), JsonRequired] public string WriteSecret { get; init; } = "";
    [JsonPropertyName("readHash"), JsonRequired] public string ReadHash { get; init; } = "";
    [JsonPropertyName("browserId")] public string? BrowserId { get; init; }

    public static RoomFollowGrant Create(string serverUrl, bool local, string uid, string instanceId, string machineId,
        string? browserId = null)
    {
        var writeSecret = RoomFollowFormat.NewSecret();
        var writeHash = RoomFollowFormat.Hash(writeSecret);
        string readSecret, readHash;
        do
        {
            readSecret = RoomFollowFormat.NewSecret();
            readHash = RoomFollowFormat.Hash(readSecret);
        } while (readHash == writeHash);
        var grant = new RoomFollowGrant
        {
            WriteSecret = writeSecret, ReadHash = readHash, BrowserId = browserId,
            Invitation = new RoomFollowInvitation
            {
                // 本机邀请不携带未使用的网络地址。远程地址创建后固定，不跟随房间切换。
                ServerUrl = local ? "" : RoomFollowFormat.NormalizeRelayUrl(serverUrl),
                Local = local, Uid = uid, InstanceId = instanceId, MachineId = machineId,
                ChannelId = RoomFollowFormat.Channel(writeHash, readHash), ReadSecret = readSecret, WriteHash = writeHash
            }
        };
        grant.Validate();
        return grant;
    }

    internal void Validate()
    {
        if (Invitation is null || !RoomFollowFormat.IsHex(WriteSecret) || !RoomFollowFormat.IsHex(ReadHash)
            || (BrowserId is not null && !RoomFollowFormat.IsIdentity(BrowserId)))
            throw new FormatException("房间跟随授权格式无效。");
        Invitation.Validate();
        if (RoomFollowFormat.Hash(WriteSecret) != Invitation.WriteHash || RoomFollowFormat.Hash(Invitation.ReadSecret) != ReadHash)
            throw new FormatException("房间跟随发布和订阅能力不匹配。");
    }
}

internal static class RoomFollowFormat
{
    internal const int Version = 1;
    internal const int MaxStateBytes = 64 * 1024;
    internal const int MaxInvitationLength = 24 * 1024;
    internal const int MaxGrants = 256;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };
    internal static readonly JsonDocumentOptions Document = new() { MaxDepth = 8 };

    internal static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static string Channel(string writeHash, string readHash) => Hash(writeHash + ":" + readHash);
    internal static bool IsHex(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsIdentity(string? value) => IsText(value, 512) && !string.IsNullOrWhiteSpace(value) && value == value.Trim();

    internal static bool IsText(string? value, int max, bool multiline = false)
    {
        if (value is null || value.Length > max || value.Any(c => char.IsControl(c) && !(multiline && c is '\t' or '\r' or '\n')))
            return false;
        try { _ = Utf8.GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }

    internal static bool HasFields(JsonElement value, string[] required, string? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if ((!required.Contains(property.Name, StringComparer.Ordinal) && property.Name != optional) || !seen.Add(property.Name))
                return false;
        return required.All(seen.Contains);
    }

    private static bool TryServerUri(string? value, out Uri? uri)
    {
        uri = null;
        return IsText(value, 2048) && !string.IsNullOrEmpty(value) && Uri.TryCreate(value, UriKind.Absolute, out uri)
            && uri.IsWellFormedOriginalString() && uri.Scheme is "https" or "http" && uri.Host.Length != 0
            && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    }

    // Room URL 只是显示/匹配元数据，不用于传输跟随能力。禁止在地址中夹带用户信息或查询凭据。
    internal static bool IsMetadataServerUrl(string? value) => value == "" || TryServerUri(value, out _);

    internal static string NormalizeRelayUrl(string value)
    {
        if (!TryServerUri(value?.Trim(), out var uri) || uri is null || uri.Scheme != Uri.UriSchemeHttps)
            throw new FormatException("远程房间跟随需要不含凭据的 HTTPS 服务地址。");
        var result = uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        foreach (var suffix in new[] { "/hub", "/gateway", "/room-follow" })
            if (result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                result = result[..^suffix.Length];
                break;
            }
        return result;
    }
}
