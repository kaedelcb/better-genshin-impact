using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：路线验证族（route.reportList/reportVariantSchema/reportVerificationDone）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterRouteVerification()
    {
        _commands[GatewayProtocol.Names.RouteReportList] = async (env, ctx) =>
        {
            // Get 返回 null 时按原兼容路径传 null（旧方法体内有 ?./??= 兼容），! 仅为编译期可空注解
            await _ops.ReportRouteListAsync(ctx, Get<List<RouteHash>>(env, "routes")!);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.RouteReportVariantSchema] = async (env, ctx) =>
        {
            await _ops.ReportRouteVariantSchemaAsync(ctx, Get<List<RouteVariantSchemaItem>>(env, "items")!);
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.RouteReportVerificationDone] = async (env, ctx) =>
        {
            await _ops.ReportRouteVerificationDoneAsync(ctx);
            return new { ack = true };
        };
    }
}
