namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：经验上限族（exp.reportFightResult，payload.kind 区分 5 个旧语义）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterExp()
    {
        // payload.kind 区分 5 个旧语义（见 GatewayProtocol.Names.ExpReportFightResult 注释）：
        // capReached/capCleared/armed/twoNoExp/twoNoExpCleared，行为与旧方法一一对应
        _commands[GatewayProtocol.Names.ExpReportFightResult] = async (env, ctx) =>
        {
            var kind = GetString(env, "kind");
            switch (kind)
            {
                case "capReached":
                    await _ops.ReportExpCapReachedAsync(ctx);
                    break;
                case "capCleared":
                    await _ops.ReportExpCapClearedAsync(ctx);
                    break;
                case "armed":
                    await _ops.ReportExpArmedAsync(ctx);
                    break;
                case "twoNoExp":
                    await _ops.ReportTwoConsecutiveNoExpAsync(ctx);
                    break;
                case "twoNoExpCleared":
                    await _ops.ReportTwoConsecutiveNoExpClearedAsync(ctx);
                    break;
                default:
                    return (object)new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = $"未知 kind: {kind}" } };
            }
            return new { ack = true };
        };
    }
}
