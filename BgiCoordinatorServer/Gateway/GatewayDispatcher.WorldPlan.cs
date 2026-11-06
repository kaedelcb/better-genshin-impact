namespace BgiCoordinatorServer.Gateway;

public sealed partial class GatewayDispatcher
{
    partial void RegisterWorldPlan()
    {
        _commands[GatewayProtocol.Names.WorldPlanBegin] = async (env, ctx)
            => await _ops.BeginWorldPlanAsync(ctx, GetBool(env, "forceLegacy"));
        _queries[GatewayProtocol.Names.WorldPlanAcquire] = async (env, ctx)
            => await _ops.AcquireWorldPlanAsync(ctx);
        _queries[GatewayProtocol.Names.WorldPlanGet] = async (env, ctx)
            => new { snapshot = await _ops.GetWorldPlanAsync(GetString(env, "planId"), GetString(env, "ticket")) };
        _commands[GatewayProtocol.Names.WorldPlanReport] = async (env, ctx)
            => new { snapshot = await _ops.ReportWorldPlanAsync(GetString(env, "planId"), GetString(env, "ticket"), GetBool(env, "known"), GetInt(env, "remainingCount")) };
        _commands[GatewayProtocol.Names.WorldPlanAck] = async (env, ctx)
            => new { snapshot = await _ops.AckWorldPlanAsync(GetString(env, "planId"), GetString(env, "ticket")) };
        _commands[GatewayProtocol.Names.WorldPlanCreateTarget] = async (env, ctx)
            => new { snapshot = await _ops.CreateWorldPlanTargetAsync(ctx, GetString(env, "planId"), GetString(env, "ticket")) };
        _commands[GatewayProtocol.Names.WorldPlanLeaveSource] = async (env, ctx)
            => new { snapshot = await _ops.LeaveWorldPlanSourceAsync(ctx, GetString(env, "planId"), GetString(env, "ticket")) };
        _commands[GatewayProtocol.Names.WorldPlanAbort] = async (env, ctx)
            => new { snapshot = await _ops.AbortWorldPlanAsync(GetString(env, "planId"), GetString(env, "ticket")) };
        _queries[GatewayProtocol.Names.WorldPlanValidateTarget] = async (env, ctx)
            => new { snapshot = await _ops.ValidateWorldPlanTargetAsync(ctx, GetString(env, "planId"), GetString(env, "ticket")) };
    }
}
