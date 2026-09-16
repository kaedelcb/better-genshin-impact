namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：路线边界锚点族（route-anchor）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterRouteAnchor()
    {
        // 加入/创建锚点：客户端完成 route N 后声明自己到达边界 N
        _commands[GatewayProtocol.Names.RouteAnchorEnroll] = async (env, ctx) =>
        {
            return await _ops.EnrollRouteAnchorAsync(ctx,
                GetString(env, "sessionId"),
                GetInt(env, "worldEpoch"),
                GetString(env, "planId"),
                GetInt(env, "completedRouteIndex", -1),
                GetInt(env, "nextRouteIndex", -1));
        };

        // 提交边界结果（completed / skipped / recovered / rerun-completed）
        _commands[GatewayProtocol.Names.RouteAnchorReport] = async (env, ctx) =>
        {
            return await _ops.ReportRouteAnchorAsync(ctx,
                GetString(env, "anchorId"),
                GetString(env, "sessionId"),
                GetInt(env, "worldEpoch"),
                GetString(env, "planId"),
                GetInt(env, "completedRouteIndex", -1),
                GetString(env, "outcome"));
        };

        // 已到达边界且可以继续（Ready）
        _commands[GatewayProtocol.Names.RouteAnchorArrived] = async (env, ctx) =>
        {
            return await _ops.ArriveRouteAnchorAsync(ctx,
                GetString(env, "anchorId"),
                GetString(env, "sessionId"),
                GetInt(env, "worldEpoch"),
                GetString(env, "planId"));
        };

        // Pull 执行确认（成功/失败同形，success 区分；commandId 必须匹配）
        Func<GatewayEnvelope, GatewayHandlerContext, Task<object?>> pullAck = async (env, ctx) =>
        {
            return await _ops.ReportRouteAnchorPullAppliedAsync(ctx,
                GetString(env, "anchorId"),
                GetString(env, "commandId"),
                GetBool(env, "success", true),
                GetString(env, "reason"));
        };
        _commands[GatewayProtocol.Names.RouteAnchorPullApplied] = pullAck;
        _commands[GatewayProtocol.Names.RouteAnchorPullFailed] = pullAck;

        // 取消当前锚点（成员主动停止/任务取消）：置 Stopped 并广播，避免各端各自等超时
        _commands[GatewayProtocol.Names.RouteAnchorCancel] = async (env, ctx) =>
        {
            return await _ops.CancelRouteAnchorAsync(ctx,
                GetString(env, "anchorId"),
                GetString(env, "reason"));
        };

        // 权威状态查询（事件只负责通知，查询才是事实来源）
        _queries[GatewayProtocol.Names.RouteAnchorStateQuery] = async (env, ctx) =>
        {
            return await _ops.QueryRouteAnchorStateAsync(ctx);
        };
    }
}
