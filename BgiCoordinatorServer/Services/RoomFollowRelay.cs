using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BgiCoordinatorServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.Services;

public sealed record RoomFollowBindingResult(
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("demand")] bool Demand);

/// <summary>仅内存、仅元数据的能力中继。不读取房间登记、成员身份、登录密码或控制授权。</summary>
public sealed class RoomFollowRelay(IHubContext<RoomFollowHub> hub)
{
    public const int MaxChannels = 256;
    public const int MaxSubscribersPerChannel = 8;
    public const int MaxStateBytes = 64 * 1024;
    // Hub 协议再次对元数据 JSON 字符串转义；单独限制外层消息和解码后的正文。
    public const int MaxInvocationBytes = MaxStateBytes * 6 + 4096;
    internal const string SupersededMessage = "Follow publisher superseded.";

    private readonly object _registryGate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private long _version;

    // 只由服务端 HubCallerContext 创建，不接受客户端自报的 connectionId 或角色。
    internal sealed class Connection(string id, CancellationToken aborted = default, Action? abort = null)
    {
        internal string Id { get; } = id;
        internal string? ChannelId { get; set; }
        internal bool IsPublisher { get; set; }
        internal volatile bool Attached;
        internal volatile bool Superseded;
        private volatile bool _closed;
        internal bool IsClosed => _closed || Aborted.IsCancellationRequested;
        internal CancellationToken Aborted { get; } = aborted;
        internal void Close() => _closed = true;
        internal void Abort()
        {
            Close();
            try { abort?.Invoke(); }
            catch { /* 连接关闭竞态，不记录私有参数。 */ }
        }
    }

    private sealed record ExecutorIdentity(string Uid, string InstanceId, string MachineId);

    private sealed class Channel(string id, long version)
    {
        internal string Id { get; } = id;
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int InUse; // _registryGate 保护；含等待通道锁的操作。
        internal Connection? Publisher;
        internal readonly HashSet<Connection> Subscribers = [];
        internal ExecutorIdentity? Identity;
        internal string? State;
        internal long Version = version;
        internal bool Demand => Subscribers.Count != 0;
    }

    internal Task<RoomFollowBindingResult> BindPublisherAsync(Connection connection, string? writeSecret, string? readHash)
    {
        if (!IsHex(writeSecret) || !IsHex(readHash)) throw InvalidCapability();
        return BindAsync(connection, ChannelId(Hash(writeSecret!), readHash!), publisher: true);
    }

    internal Task<RoomFollowBindingResult> BindSubscriberAsync(Connection connection, string? readSecret, string? writeHash)
    {
        if (!IsHex(readSecret) || !IsHex(writeHash)) throw InvalidCapability();
        return BindAsync(connection, ChannelId(writeHash!, Hash(readSecret!)), publisher: false);
    }

    private async Task<RoomFollowBindingResult> BindAsync(Connection connection, string id, bool publisher)
    {
        var channel = AcquireForBinding(connection, id, publisher);
        await channel.Gate.WaitAsync();
        try
        {
            CheckConnection(connection);
            await PruneClosedLockedAsync(channel);
            CheckConnection(connection);
            if (connection.Attached)
            {
                if (publisher ? !ReferenceEquals(channel.Publisher, connection) : !channel.Subscribers.Contains(connection))
                    throw InvalidBinding();
            }
            else if (publisher)
            {
                var previous = channel.Publisher;
                channel.Publisher = connection;
                connection.Attached = true;
                if (previous is not null)
                {
                    previous.Superseded = true;
                    await SetStateLockedAsync(channel, null, force: true);
                    // 停止旧连接发送；同一旧连接即便再次 bind 或迟到 publish 也不能抢回角色。
                    // 不主动断开成功收到通知的旧连接，避免有效能力的两个实例因自动重连反复互踢。
                    if (!await TrySendAsync(previous, "FollowDemand", [false])) previous.Abort();
                }
            }
            else
            {
                if (channel.Subscribers.Count >= MaxSubscribersPerChannel)
                    throw new HubException("Follow subscriber capacity reached.");
                var wasDemanded = channel.Demand;
                channel.Subscribers.Add(connection);
                connection.Attached = true;
                if (!wasDemanded) await NotifyDemandLockedAsync(channel);
            }

            // 发送通知期间可能断线；不能向新绑定返回确认已离线的发布端旧状态。
            await PruneClosedLockedAsync(channel);
            CheckConnection(connection);
            if (publisher && !ReferenceEquals(channel.Publisher, connection)) throw InvalidBinding();
            return new(channel.Version, publisher ? null : channel.State, channel.Demand);
        }
        finally { channel.Gate.Release(); Release(channel); }
    }

    internal async Task PublishAsync(Connection connection, string? state)
    {
        var channel = AcquireForPublish(connection);
        await channel.Gate.WaitAsync();
        try
        {
            CheckConnection(connection);
            if (!ReferenceEquals(channel.Publisher, connection)) throw InvalidBinding();
            await PruneClosedLockedAsync(channel);
            CheckConnection(connection);
            if (!ReferenceEquals(channel.Publisher, connection)) throw InvalidBinding();

            ExecutorIdentity? identity;
            try
            {
                identity = state is null ? null : ValidateState(state);
                if (identity is not null && channel.Identity is not null && identity != channel.Identity)
                    throw new HubException("Follow executor identity changed.");
            }
            catch (HubException)
            {
                // 已绑定发布端提供无效状态时撤回旧视图，而不是继续让它冒充当前状态。
                await SetStateLockedAsync(channel, null);
                throw;
            }
            if (!channel.Demand) return;
            CheckConnection(connection);
            channel.Identity ??= identity;
            await SetStateLockedAsync(channel, state);
        }
        finally { channel.Gate.Release(); Release(channel); }
    }

    internal async Task DisconnectAsync(Connection connection)
    {
        Channel channel;
        lock (_registryGate)
        {
            connection.Close();
            if (connection.ChannelId is not { } id || !_channels.TryGetValue(id, out channel!)) return;
            channel.InUse++;
        }
        await channel.Gate.WaitAsync();
        try
        {
            // 已被替换的旧连接晚到的 Disconnect 不清除新发布端的状态。
            if (ReferenceEquals(channel.Publisher, connection))
            {
                channel.Publisher = null;
                await SetStateLockedAsync(channel, null, force: true);
            }
            else if (channel.Subscribers.Remove(connection) && !channel.Demand)
                await NoDemandLockedAsync(channel);
        }
        finally { channel.Gate.Release(); Release(channel); }
    }

    private Channel AcquireForBinding(Connection connection, string id, bool publisher)
    {
        lock (_registryGate)
        {
            CheckConnection(connection);
            if (connection.ChannelId is { } bound && (bound != id || connection.IsPublisher != publisher))
                throw InvalidBinding();
            if (!_channels.TryGetValue(id, out var channel))
            {
                if (connection.Attached) throw InvalidBinding();
                if (_channels.Count >= MaxChannels) throw new HubException("Follow channel capacity reached.");
                channel = new Channel(id, NextVersion());
                _channels.Add(id, channel);
            }
            connection.ChannelId = id;
            connection.IsPublisher = publisher;
            channel.InUse++;
            return channel;
        }
    }

    private Channel AcquireForPublish(Connection connection)
    {
        lock (_registryGate)
        {
            CheckConnection(connection);
            if (!connection.IsPublisher || !connection.Attached || connection.ChannelId is not { } id
                || !_channels.TryGetValue(id, out var channel)) throw InvalidBinding();
            channel.InUse++;
            return channel;
        }
    }

    private void Release(Channel channel)
    {
        lock (_registryGate)
        {
            if (--channel.InUse == 0 && channel.Publisher is null && !channel.Demand)
                _channels.Remove(channel.Id);
        }
    }

    private static void CheckConnection(Connection connection)
    {
        if (connection.Superseded) throw new HubException(SupersededMessage);
        if (connection.IsClosed) throw InvalidBinding();
    }

    private long NextVersion() => Interlocked.Increment(ref _version);

    private async Task PruneClosedLockedAsync(Channel channel)
    {
        if (channel.Subscribers.RemoveWhere(s => s.IsClosed) != 0 && !channel.Demand)
            await NoDemandLockedAsync(channel);
        if (channel.Publisher is { IsClosed: true })
        {
            channel.Publisher = null;
            await SetStateLockedAsync(channel, null, force: true);
        }
    }

    private async Task NoDemandLockedAsync(Channel channel)
    {
        // 不为离线跟随者保留元数据；新订阅需求必须重新取得发布端最新快照。
        if (channel.State is not null)
        {
            channel.State = null;
            channel.Version = NextVersion();
        }
        await NotifyDemandLockedAsync(channel);
    }

    private async Task SetStateLockedAsync(Channel channel, string? state, bool force = false)
    {
        if (!force && string.Equals(channel.State, state, StringComparison.Ordinal)) return;
        channel.State = state;
        channel.Version = NextVersion();
        if (!channel.Demand) return;
        var subscribers = channel.Subscribers.ToArray();
        var args = new object?[] { channel.Version, channel.State };
        var sent = await Task.WhenAll(subscribers.Select(s => TrySendAsync(s, "FollowState", args)));
        for (var i = 0; i < subscribers.Length; i++)
        {
            if (sent[i]) continue;
            channel.Subscribers.Remove(subscribers[i]);
            subscribers[i].Abort(); // 不让无法投递的连接一直显示旧视图。
        }
        if (!channel.Demand) await NoDemandLockedAsync(channel);
    }

    private async Task NotifyDemandLockedAsync(Channel channel)
    {
        if (channel.Publisher is not { } publisher) return;
        if (await TrySendAsync(publisher, "FollowDemand", [channel.Demand])) return;
        channel.Publisher = null;
        publisher.Abort();
        await SetStateLockedAsync(channel, null, force: true);
    }

    private async Task<bool> TrySendAsync(Connection connection, string method, object?[] args)
    {
        if (connection.IsClosed) return false;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(connection.Aborted);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            await hub.Clients.Client(connection.Id).SendCoreAsync(method, args, budget.Token).WaitAsync(budget.Token);
            return !connection.IsClosed;
        }
        catch { return false; } // 不记录负载、能力或网络异常中可能包含的私有内容。
    }

    private static bool IsHex(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string ChannelId(string writeHash, string readHash)
    {
        if (writeHash == readHash) throw InvalidCapability();
        return Hash(writeHash + ":" + readHash);
    }

    private static HubException InvalidCapability() => new("Invalid follow capability.");
    private static HubException InvalidBinding() => new("Invalid follow binding.");
    private static HubException InvalidState() => new("Invalid follow metadata.");
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // 与助手 RoomFollowSnapshotCodec 使用相同白名单。未知/缺失/重复字段均拒绝，不反序列化房间配置。
    private static ExecutorIdentity ValidateState(string state)
    {
        try
        {
            if (state.Length > MaxStateBytes || StrictUtf8.GetByteCount(state) > MaxStateBytes)
                throw new HubException("Follow metadata exceeds the byte limit.");
            using var document = JsonDocument.Parse(state, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            RequireFields(root, "version", "uid", "instanceId", "machineId", "confirmed", "profiles", "activeId", "viewedId", "showOrchestration");
            if (Number(root.GetProperty("version")) != 1) throw InvalidState();
            var identity = new ExecutorIdentity(Identifier(root.GetProperty("uid")), Identifier(root.GetProperty("instanceId")),
                Identifier(root.GetProperty("machineId")));
            var confirmed = Boolean(root.GetProperty("confirmed"));
            _ = Boolean(root.GetProperty("showOrchestration"));
            var profiles = root.GetProperty("profiles");
            if (profiles.ValueKind != JsonValueKind.Array || profiles.GetArrayLength() > 5) throw InvalidState();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var profile in profiles.EnumerateArray())
            {
                RequireFields(profile, "id", "name", "note", "serverUrl", "roomCode", "teamUids", "expectedHoeingPlayers");
                var id = Identifier(profile.GetProperty("id"));
                if (id == "__all_rooms__" || !ids.Add(id)) throw InvalidState();
                _ = Text(profile.GetProperty("name"), 256);
                _ = Text(profile.GetProperty("note"), 8192, multiline: true);
                var server = Text(profile.GetProperty("serverUrl"), 2048);
                if (server.Length != 0 && (!Uri.TryCreate(server, UriKind.Absolute, out var uri)
                    || !uri.IsWellFormedOriginalString() || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0
                    || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)) throw InvalidState();
                _ = Text(profile.GetProperty("roomCode"), 256);
                var members = profile.GetProperty("teamUids");
                if (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() > 64) throw InvalidState();
                foreach (var uid in members.EnumerateArray()) _ = Identifier(uid);
                if (Number(profile.GetProperty("expectedHoeingPlayers")) is < 1 or > 4) throw InvalidState();
            }
            var active = Text(root.GetProperty("activeId"), 512);
            var viewed = Text(root.GetProperty("viewedId"), 512);
            if (active.Length != 0 && !ids.Contains(active) || confirmed && active.Length == 0
                || viewed.Length != 0 && viewed != "__all_rooms__" && !ids.Contains(viewed)) throw InvalidState();
            return identity;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        { throw InvalidState(); }
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw InvalidState();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw InvalidState();
        if (seen.Count != names.Length) throw InvalidState();
    }

    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n)
        ? n : throw InvalidState();

    private static bool Boolean(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw InvalidState()
    };

    private static string Text(JsonElement value, int max, bool multiline = false)
    {
        if (value.ValueKind != JsonValueKind.String) throw InvalidState();
        var text = value.GetString()!;
        if (text.Length > max || text.Any(c => char.IsControl(c) && !(multiline && c is '\t' or '\r' or '\n')))
            throw InvalidState();
        _ = StrictUtf8.GetByteCount(text);
        return text;
    }

    private static string Identifier(JsonElement value)
    {
        var text = Text(value, 512);
        if (string.IsNullOrWhiteSpace(text) || text != text.Trim()) throw InvalidState();
        return text;
    }
}
