namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：同步点族（sync.reportArrival/waitForAllPlayers）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterSyncPoint()
    {
        // ReportArrival / ReportArrivalWithExpectedCount 聚合：expectedCount 缺省 0，
        // ==0 走 ReportArrival 等价路径，>0 走 WithExpectedCount 等价路径（两方法日志文案不同，保留两条 ops 方法）
        _commands[GatewayProtocol.Names.SyncReportArrival] = async (env, ctx) =>
        {
            var syncPointId = GetString(env, "syncPointId");
            var expectedCount = GetInt(env, "expectedCount", 0);
            if (expectedCount > 0)
                await _ops.ReportArrivalWithExpectedCountAsync(ctx, syncPointId, expectedCount);
            else
                await _ops.ReportArrivalAsync(ctx, syncPointId);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.SyncWaitForAllPlayers] = async (env, ctx) =>
        {
            await _ops.WaitForAllPlayersAsync(ctx,
                GetString(env, "syncId"),
                GetLong(env, "syncProgress", -1));
            return new { ack = true };
        };
    }
}
