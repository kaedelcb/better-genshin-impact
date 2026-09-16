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

        // 集体跳段执行确认（collective-skip-applied-ack）：客户端按 skipId 回报本地跳段结果。
        // 纯新增命令——旧客户端从不发送，既有命令语义零变化。
        _commands[GatewayProtocol.Names.SyncReportCollectiveSkipApplied] = async (env, ctx) =>
        {
            await _ops.ReportCollectiveSkipAppliedAsync(ctx,
                GetString(env, "skipId"),
                GetLong(env, "actualProgress", -1),
                GetBool(env, "success", true),
                GetString(env, "reason"));
            return new { ack = true };
        };
    }
}
