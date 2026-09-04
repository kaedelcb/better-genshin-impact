namespace BgiCoordinatorServer.Gateway;

/// <summary>路由表注册：万叶族（kazuha.declareCapability/setPlayer/notifyCollectStarted）。</summary>
public sealed partial class GatewayDispatcher
{
    partial void RegisterKazuha()
    {
        _commands[GatewayProtocol.Names.KazuhaDeclareCapability] = async (env, ctx) =>
        {
            await _ops.DeclareKazuhaCapabilityAsync(ctx);
            return new { ack = true };
        };

        // SetKazuhaPlayer 已废弃空实现（只记 deprecated 警告），保留独立消息名作 no-op 路由；index 缺省 0
        _commands[GatewayProtocol.Names.KazuhaSetPlayer] = async (env, ctx) =>
        {
            await _ops.SetKazuhaPlayerAsync(ctx, GetInt(env, "index", 0));
            return new { ack = true };
        };

        _commands[GatewayProtocol.Names.KazuhaNotifyCollectStarted] = async (env, ctx) =>
        {
            await _ops.NotifyKazuhaCollectStartedAsync(ctx,
                GetString(env, "syncKey"),
                GetDouble(env, "collectX"),
                GetDouble(env, "collectY"));
            return new { ack = true };
        };
    }
}
