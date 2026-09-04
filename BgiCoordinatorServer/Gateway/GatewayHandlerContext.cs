namespace BgiCoordinatorServer.Gateway;

/// <summary>调用方协议来源。</summary>
public enum GatewayCallerProtocol
{
    /// <summary>旧 CoordinatorHub 连接（/hub）。</summary>
    Legacy,

    /// <summary>新 GatewayHub 连接（/gateway），已完成 session.hello。</summary>
    V3,
}

/// <summary>
/// 一次操作调用的上下文：连接 ID + 协议来源。
/// 旧 Hub 转发器与新网关路由共用同一组强类型操作方法，仅靠本上下文区分
/// 组管理/定向发送应落到哪个 Hub 的连接上（§4.7 双轨）。
/// </summary>
public sealed record GatewayHandlerContext(string ConnectionId, GatewayCallerProtocol Protocol)
{
    public static GatewayHandlerContext Legacy(string connectionId) => new(connectionId, GatewayCallerProtocol.Legacy);

    public static GatewayHandlerContext V3(string connectionId) => new(connectionId, GatewayCallerProtocol.V3);

    public bool IsV3 => Protocol == GatewayCallerProtocol.V3;
}
