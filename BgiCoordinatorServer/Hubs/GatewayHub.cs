using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.Hubs;

/// <summary>
/// 网关 Hub（《通信方案》§4.1-4.4）：65 个公开方法收敛为 Dispatch/Query 两个入口。
/// 与旧 CoordinatorHub 双轨并存：旧客户端（/hub）零感知，新客户端（/gateway）
/// 连接后第一条消息必须是 session.hello（握手完成前拒绝其它消息）。
/// 业务逻辑不在本类——经 GatewayDispatcher 路由到 RoomOperations 共享路径。
/// </summary>
public class GatewayHub : Hub
{
    private readonly GatewayDispatcher _dispatcher;
    private readonly GatewaySessionTracker _tracker;
    private readonly RoomOperations _ops;
    private readonly ILogger<GatewayHub> _logger;

    public GatewayHub(GatewayDispatcher dispatcher, GatewaySessionTracker tracker, RoomOperations ops, ILogger<GatewayHub> logger)
    {
        _dispatcher = dispatcher;
        _tracker = tracker;
        _ops = ops;
        _logger = logger;
    }

    /// <summary>客户端 → 服务端：命令与事件上报（统一入口，返回 ACK 而非业务终态）。</summary>
    public Task<GatewayEnvelope> Dispatch(GatewayEnvelope envelope)
        => _dispatcher.DispatchAsync(GatewayHandlerContext.V3(Context.ConnectionId), envelope);

    /// <summary>客户端 → 服务端：只读查询（同步返回）。</summary>
    public Task<GatewayEnvelope> Query(GatewayEnvelope envelope)
        => _dispatcher.QueryAsync(GatewayHandlerContext.V3(Context.ConnectionId), envelope);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _tracker.Remove(Context.ConnectionId);
        _logger.LogInformation("[Gateway] 连接 {ConnId} 断开（{Error}）",
            Context.ConnectionId, exception?.Message ?? "正常");
        // 房间域断线清理与旧 Hub 共用同一实现（宽限期/房主关房/同步点重评估/万叶顶替等）
        await _ops.HandleDisconnectAsync(GatewayHandlerContext.V3(Context.ConnectionId), exception);
        await base.OnDisconnectedAsync(exception);
    }
}
