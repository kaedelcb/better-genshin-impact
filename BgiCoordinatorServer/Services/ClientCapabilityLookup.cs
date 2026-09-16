using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 客户端能力查询（route-anchor 等新协议域的"禁止半启用"门控依赖它）。
///
/// 抽成接口的原因：<see cref="RoomOperations"/> 不应直接依赖传输层的会话表，
/// 否则测试无法构造"部分客户端不支持"的场景。
/// </summary>
public interface IClientCapabilityLookup
{
    /// <summary>该连接是否在 hello 中宣告了指定能力（未知连接/未握手 → false）。</summary>
    bool Supports(string connectionId, string capability);
}

/// <summary>基于网关会话表的实现（生产路径）。</summary>
public sealed class GatewayCapabilityLookup : IClientCapabilityLookup
{
    private readonly GatewaySessionTracker _sessions;

    public GatewayCapabilityLookup(GatewaySessionTracker sessions)
    {
        _sessions = sessions;
    }

    public bool Supports(string connectionId, string capability)
        => _sessions.TryGet(connectionId, out var hello)
           && hello != null
           && Array.IndexOf(hello.Capabilities, capability) >= 0;
}
