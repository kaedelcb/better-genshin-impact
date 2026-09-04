namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：世界族（world.reportJoined/resetJoined/resetForNewRound）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterWorld()
    {
        _commands[GatewayProtocol.Names.WorldReportJoined] = async (env, ctx) =>
        {
            await _ops.ReportWorldJoinedAsync(ctx);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.WorldResetJoined] = async (env, ctx) =>
        {
            await _ops.ResetWorldJoinedAsync(ctx);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.WorldResetForNewRound] = async (env, ctx) =>
        {
            await _ops.ResetForNewWorldRoundAsync(ctx, GetInt(env, "newRound"));
            return new { ack = true };
        };
    }
}
