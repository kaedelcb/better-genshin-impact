namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：异常协调/重对齐族（anomaly.* 共 11 条，与旧方法一一对应）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterAnomaly()
    {
        _commands[GatewayProtocol.Names.AnomalyReportWaitPoint] = async (env, ctx) =>
        {
            await _ops.WaitPointReportAsync(ctx,
                GetString(env, "routeId"),
                GetString(env, "syncPointId"),
                GetInt(env, "worldRound"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyReportArrivalAtWaitPoint] = async (env, ctx) =>
        {
            await _ops.ReportArrivalAtWaitPointAsync(ctx, GetString(env, "syncPointId"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyClearStatus] = async (env, ctx) =>
        {
            await _ops.ClearAbnormalStatusAsync(ctx);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyNotify] = async (env, ctx) =>
        {
            await _ops.PlayerAnomalyNotifyAsync(ctx,
                GetString(env, "playerUid"),
                GetInt(env, "routeIndex"),
                GetBool(env, "passedSyncPoint"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyNotifyFightPoint] = async (env, ctx) =>
        {
            await _ops.PlayerAnomalyNotifyFightPointAsync(ctx,
                GetString(env, "playerUid"),
                GetInt(env, "routeIndex"),
                GetInt(env, "fightPointId"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyRecovered] = async (env, ctx) =>
        {
            await _ops.PlayerAnomalyRecoveredAsync(ctx, GetString(env, "playerUid"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyMemberStatusChanged] = async (env, ctx) =>
        {
            await _ops.MemberStatusChangedAsync(ctx,
                GetString(env, "playerUid"),
                GetString(env, "status"),
                GetLong(env, "targetProgress", -1));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyReportMemberProgress] = async (env, ctx) =>
        {
            await _ops.ReportMemberProgressAsync(ctx,
                GetString(env, "playerUid"),
                GetInt(env, "routeIndex"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyRouteSkipped] = async (env, ctx) =>
        {
            await _ops.RouteSkippedAsync(ctx,
                GetString(env, "playerUid"),
                GetInt(env, "routeIndex"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyWaitPointReached] = async (env, ctx) =>
        {
            await _ops.WaitPointReachedAsync(ctx,
                GetString(env, "playerUid"),
                GetString(env, "syncPointId"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.AnomalyFightingStatusChanged] = async (env, ctx) =>
        {
            await _ops.FightingStatusChangedAsync(ctx,
                GetString(env, "playerUid"),
                GetBool(env, "isFighting"));
            return new { ack = true };
        };
    }
}
