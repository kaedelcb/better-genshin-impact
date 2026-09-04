namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：会话族（session.heartbeat）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterSession()
    {
        // Heartbeat / HeartbeatWithProgress 聚合：payload 含 routeIndex 时走 WithProgress 等价路径，
        // 缺省走 Heartbeat 等价路径（两方法调用的 RoomManager API 不同，保留两条 ops 方法）
        _commands[GatewayProtocol.Names.SessionHeartbeat] = async (env, ctx) =>
        {
            if (env.Payload != null && env.Payload.TryGetPropertyValue("routeIndex", out var n) && n != null)
            {
                await _ops.HeartbeatWithProgressAsync(ctx,
                    GetInt(env, "routeIndex"),
                    GetDateTime(env, "routeStartTime"),
                    GetDouble(env, "routeEstimatedSeconds"));
            }
            else
            {
                await _ops.HeartbeatAsync(ctx);
            }
            return new { ack = true };
        };
    }
}
