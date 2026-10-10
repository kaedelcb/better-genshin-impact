namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：战斗族（fight.reportParticipant/reportDone）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterFight()
    {
        _commands[GatewayProtocol.Names.FightReportParticipant] = async (env, ctx) =>
        {
            await _ops.ReportFightParticipantAsync(ctx, GetString(env, "syncKey"));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.FightReportDone] = async (env, ctx) =>
        {
            var terminal = await _ops.ReportFightDoneAsync(ctx, GetString(env, "syncPointId"));
            return new { ack = true, terminal };
        };

        _queries[GatewayProtocol.Names.FightGetStatus] = async (env, ctx) =>
            await _ops.GetFightDoneStatusAsync(ctx, GetString(env, "syncKey"));
    }
}
