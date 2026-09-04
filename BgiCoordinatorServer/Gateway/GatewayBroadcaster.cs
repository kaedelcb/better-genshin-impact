using BgiCoordinatorServer.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.Gateway;

/// <summary>
/// 双发广播器（《通信方案》§4.7 迁移期双轨）。
/// 组广播：旧事件名经 IHubContext&lt;CoordinatorHub&gt; 发到 /hub 组内连接，
/// evt 信封经 IHubContext&lt;GatewayHub&gt; 发到 /gateway 组内连接。
/// 一个连接只属于一个 Hub，因此每个客户端恰好收到一份——
/// 旧客户端零感知（收旧名，与今天逐字节一致），新客户端收 evt。
/// 定向发送（Caller/Client）：按连接协议来源只发对应一份。
///
/// 纪律：evt 一侧的发送全部就地 try/catch 记日志，绝不冒泡——
/// 新协议通道的任何故障不得影响旧协议路径（行为零变化红线）。
/// 旧名一侧不包裹，异常表现与搬迁前完全一致。
/// </summary>
public sealed class GatewayBroadcaster
{
    private readonly IHubContext<CoordinatorHub> _legacyHub;
    private readonly IHubContext<GatewayHub> _gatewayHub;
    private readonly GatewaySessionTracker _sessions;
    private readonly ILogger<GatewayBroadcaster> _logger;

    public GatewayBroadcaster(
        IHubContext<CoordinatorHub> legacyHub,
        IHubContext<GatewayHub> gatewayHub,
        GatewaySessionTracker sessions,
        ILogger<GatewayBroadcaster> logger)
    {
        _legacyHub = legacyHub;
        _gatewayHub = gatewayHub;
        _sessions = sessions;
        _logger = logger;
    }

    /// <summary>旧事件名 → evt 新事件名；未收录时返回 null（映射完整性由单测守住）。</summary>
    public static string? MapEventName(string legacyEventName)
        => GatewayProtocol.LegacyEventMap.TryGetValue(legacyEventName, out var n) ? n : null;

    /// <summary>向房间/控制组双发：旧名 → /hub 组，evt → /gateway 组。</summary>
    public async Task BroadcastGroupAsync(string group, string legacyEventName, object? evtPayload, params object?[] legacyArgs)
    {
        await _legacyHub.Clients.Group(group).SendAsync(legacyEventName, legacyArgs);
        await SendEvtSafeAsync(_gatewayHub.Clients.Group(group), group, legacyEventName, evtPayload);
    }

    /// <summary>向调用方定向发送：按调用方协议来源只发一份（旧名或 evt）。</summary>
    public async Task SendToCallerAsync(GatewayHandlerContext ctx, string legacyEventName, object? evtPayload, params object?[] legacyArgs)
    {
        if (ctx.IsV3)
        {
            await SendEvtSafeAsync(_gatewayHub.Clients.Client(ctx.ConnectionId), null, legacyEventName, evtPayload);
            return;
        }
        await _legacyHub.Clients.Client(ctx.ConnectionId).SendAsync(legacyEventName, legacyArgs);
    }

    /// <summary>向指定连接定向发送：按该连接的协议登记只发一份（旧名或 evt）。</summary>
    public async Task SendToConnectionAsync(string connectionId, string legacyEventName, object? evtPayload, params object?[] legacyArgs)
    {
        if (_sessions.IsV3(connectionId))
        {
            var newName = MapEventName(legacyEventName);
            if (newName != null)
            {
                await SendEvtSafeAsync(_gatewayHub.Clients.Client(connectionId), null, legacyEventName, evtPayload);
            }
            return;
        }
        await _legacyHub.Clients.Client(connectionId).SendAsync(legacyEventName, legacyArgs);
    }

    /// <summary>组管理：按连接协议来源落到对应 Hub（与 Hub.Groups.AddToGroupAsync 等价）。</summary>
    public Task AddToGroupAsync(GatewayHandlerContext ctx, string group)
        => ctx.IsV3
            ? _gatewayHub.Groups.AddToGroupAsync(ctx.ConnectionId, group)
            : _legacyHub.Groups.AddToGroupAsync(ctx.ConnectionId, group);

    public Task RemoveFromGroupAsync(GatewayHandlerContext ctx, string group)
        => ctx.IsV3
            ? _gatewayHub.Groups.RemoveFromGroupAsync(ctx.ConnectionId, group)
            : _legacyHub.Groups.RemoveFromGroupAsync(ctx.ConnectionId, group);

    /// <summary>evt 一侧发送：就地捕获，记日志不冒泡（不破坏旧路径）。</summary>
    private async Task SendEvtSafeAsync(IClientProxy target, string? roomCode, string legacyEventName, object? evtPayload)
    {
        var newName = MapEventName(legacyEventName);
        if (newName == null)
        {
            _logger.LogWarning("[Gateway] 旧事件 {LegacyEvent} 无 evt 映射，跳过双发", legacyEventName);
            return;
        }
        try
        {
            await target.SendAsync(GatewayProtocol.Callbacks.Event,
                GatewayEnvelope.Event(newName, evtPayload, roomCode));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Gateway] evt 双发失败（已吞掉，不影响旧协议路径）：{NewName} ← {LegacyEvent}",
                newName, legacyEventName);
        }
    }
}
