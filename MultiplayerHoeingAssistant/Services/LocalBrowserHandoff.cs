using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>同机监控端只领取同 UID 执行端的一次性链接；不在监控端创建替代会话。</summary>
internal sealed class LocalBrowserHandoff : IDisposable
{
    internal sealed record Identity(string Uid, string Name, string RoomCode, string InstanceId,
        string Secret, bool IsExecutor, int SessionId);
    internal sealed record Target(LocalConfigEditChannel.Endpoint Endpoint, string Uid, string Name,
        string RoomCode, string InstanceId, int SessionId)
    {
        public string Label => $"{Name} · {Uid} · Windows 会话 {SessionId} · {InstanceId[..Math.Min(8, InstanceId.Length)]}";
    }

    private const string Probe = "local_browser.probe", Open = "local_browser.open";
    private readonly Func<Identity> _identity;
    private readonly Func<Identity, string> _createPairingUrl;
    private readonly LocalConfigEditChannel _channel;
    internal LocalConfigEditChannel.Endpoint Address => _channel.Address;

    internal LocalBrowserHandoff(Func<Identity> identity, Func<Identity, string> createPairingUrl, Action<string> report)
    {
        _identity = identity;
        _createPairingUrl = createPairingUrl;
        _channel = new LocalConfigEditChannel(() => _identity().Secret, HandleAsync, report, channel: "browser");
    }

    private Task<RemoteCommand?> HandleAsync(RemoteCommand command)
    {
        var own = _identity();
        if (!own.IsExecutor || string.IsNullOrWhiteSpace(own.Uid) || string.IsNullOrEmpty(own.InstanceId)
            || command.Cmd is not (Probe or Open) || command.RoomCode != own.RoomCode
            || command.SenderUid != own.Uid || command.Target is not { Count: 1 }
            || command.Target[0] != own.Uid || string.IsNullOrWhiteSpace(command.CommandId)
            || command.ExpiresAtUtc is null || command.ExpiresAtUtc <= DateTimeOffset.UtcNow
            || (command.Cmd == Open && command.TargetInstanceId != own.InstanceId))
            return Task.FromResult<RemoteCommand?>(null);

        // 探测不创建配对；只有明确选定实例的打开请求才生成一次性令牌。
        var values = new Dictionary<string, object>
        {
            ["instanceId"] = own.InstanceId, ["name"] = own.Name, ["sessionId"] = own.SessionId
        };
        if (command.Cmd == Open) values["url"] = _createPairingUrl(own);
        var reply = new RemoteCommand
        {
            Cmd = command.Cmd + "_result", CommandId = command.CommandId,
            RoomCode = own.RoomCode, SenderUid = own.Uid, Sender = own.Name,
            Target = [command.SenderUid], TargetInstanceId = own.InstanceId, Params = values
        };
        values["proof"] = Convert.ToHexString(Proof(own.Secret, reply));
        return Task.FromResult<RemoteCommand?>(reply);
    }

    internal static async Task<IReadOnlyList<Target>> FindExecutorsAsync(Identity caller)
    {
        RequireCaller(caller);
        var endpoints = await LocalConfigEditChannel.FindAllAsync(Request(caller, Probe), caller.Secret,
            Probe + "_result", "browser");
        var found = new List<Target>();
        foreach (var endpoint in endpoints)
        {
            try
            {
                var reply = await LocalConfigEditChannel.SendAsync(endpoint, Request(caller, Probe), caller.Secret,
                    TimeSpan.FromSeconds(2));
                if (!ValidReply(reply, caller, Probe, null)) continue;
                var instance = Value(reply!, "instanceId");
                if (string.IsNullOrWhiteSpace(instance) || instance != reply!.TargetInstanceId
                    || !int.TryParse(Value(reply, "sessionId"), out var sessionId) || sessionId < 0) continue;
                found.Add(new Target(endpoint, caller.Uid, Value(reply, "name"), caller.RoomCode, instance, sessionId));
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
            { /* 执行端可在扫描期间退出；不把消失的实例作为可用目标。 */ }
        }
        return found;
    }

    internal static async Task<string> GetPairingUrlAsync(Identity caller, Target target)
    {
        RequireCaller(caller);
        if (target.Uid != caller.Uid || target.RoomCode != caller.RoomCode || string.IsNullOrEmpty(target.InstanceId))
            throw new InvalidOperationException("执行端身份不匹配，请重新扫描。");
        RemoteCommand? reply;
        try
        {
            reply = await LocalConfigEditChannel.SendAsync(target.Endpoint,
                Request(caller, Open, target.InstanceId), caller.Secret);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("执行端已退出或暂时无法连接，请确认执行端助手正在运行后重试。", ex);
        }
        if (!ValidReply(reply, caller, Open, target.InstanceId))
            throw new InvalidOperationException("执行端已退出、切换模式或身份已变化，请重新打开。");
        var url = Value(reply!, "url");
        if (!IsPairingUrl(url)) throw new InvalidDataException("执行端返回的本机网页链接无效。");
        return url;
    }

    internal static bool IsPairingUrl(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "http" && uri.Host == "127.0.0.1" && uri.Port > 0 && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.StartsWith("#token=", StringComparison.Ordinal)
        && uri.Fragment.Length == 71 && uri.Fragment[7..].All(char.IsAsciiHexDigit);

    private static void RequireCaller(Identity caller)
    {
        if (string.IsNullOrWhiteSpace(caller.Uid) || string.IsNullOrWhiteSpace(caller.RoomCode)
            || string.IsNullOrEmpty(caller.Secret))
            throw new InvalidOperationException("请先配置与执行端一致的 UID、控制房间和房间密码，再打开本机网页。");
    }

    private static RemoteCommand Request(Identity caller, string operation, string? instance = null) => new()
    {
        Cmd = operation, CommandId = Guid.NewGuid().ToString("N"), RoomCode = caller.RoomCode,
        SenderUid = caller.Uid, Sender = caller.Name, Target = [caller.Uid], TargetInstanceId = instance,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(15)
    };

    private static bool ValidReply(RemoteCommand? reply, Identity caller, string operation, string? instance)
        => reply is not null && reply.Cmd == operation + "_result" && reply.RoomCode == caller.RoomCode
            && reply.SenderUid == caller.Uid && reply.Target is { Count: 1 } && reply.Target[0] == caller.Uid
            && (instance is null || reply.TargetInstanceId == instance) && ValidProof(caller.Secret, reply);
    // 发现阶段也验证执行端持有房间密码，避免仅凭可枚举的 PID/UID 接受伪造网页地址。
    private static byte[] Proof(string secret, RemoteCommand reply) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
        JsonSerializer.SerializeToUtf8Bytes(new[] { reply.CommandId, reply.Cmd, reply.RoomCode, reply.SenderUid,
            reply.TargetInstanceId ?? "", Value(reply, "instanceId"), Value(reply, "name"), Value(reply, "sessionId"), Value(reply, "url") }));
    private static bool ValidProof(string secret, RemoteCommand reply)
    {
        try { return CryptographicOperations.FixedTimeEquals(Proof(secret, reply), Convert.FromHexString(Value(reply, "proof"))); }
        catch (FormatException) { return false; }
    }
    private static string Value(RemoteCommand reply, string key) => reply.Params?.GetValueOrDefault(key)?.ToString() ?? "";
    public void Dispose() => _channel.Dispose();
}
