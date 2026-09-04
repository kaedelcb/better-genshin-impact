using System.Collections.Concurrent;

namespace BgiCoordinatorServer.Gateway;

/// <summary>session.hello 的客户端载荷（§4.4 ClientHello）。</summary>
public sealed record ClientHello
{
    public string ClientKind { get; init; } = "";   // "bgi" | "assistant" | "web"
    public string ClientVersion { get; init; } = "";
    public int ProtocolVersion { get; init; }
    public string[] Capabilities { get; init; } = [];
}

/// <summary>
/// 网关会话跟踪（§4.4 握手时序：hello 完成前拒绝其它消息）。
/// 只有 GatewayHub(/gateway) 上的连接会登记；旧 /hub 连接天然是 Legacy，不入表。
/// 同时承担"某连接是否 v3 协议"的判定，供定向双发（Clients.Client 场景）选择落点。
/// </summary>
public sealed class GatewaySessionTracker
{
    private readonly ConcurrentDictionary<string, ClientHello> _sessions = new(StringComparer.Ordinal);

    public void CompleteHello(string connectionId, ClientHello hello) => _sessions[connectionId] = hello;

    public bool IsV3(string connectionId) => _sessions.ContainsKey(connectionId);

    public bool TryGet(string connectionId, out ClientHello? hello) => _sessions.TryGetValue(connectionId, out hello);

    public void Remove(string connectionId) => _sessions.TryRemove(connectionId, out _);
}
