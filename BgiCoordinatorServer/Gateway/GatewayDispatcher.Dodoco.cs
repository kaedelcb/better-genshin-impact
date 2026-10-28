using System.Text.Json.Nodes;

namespace BgiCoordinatorServer.Gateway;

public sealed partial class GatewayDispatcher
{
    partial void RegisterDodoco()
    {
        _commands["dodoco.request"] = async (env, ctx) => await _ops.RequestDodocoAsync(ctx,
            GetString(env, "roomCode"), GetString(env, "targetUid"), GetString(env, "requestId"),
            GetString(env, "operation"), env.Payload?["args"] as JsonObject);
        _commands["dodoco.respond"] = async (env, ctx) => await _ops.RespondDodocoAsync(ctx,
            GetString(env, "roomCode"), GetString(env, "requestId"), GetString(env, "status"),
            GetString(env, "errorCode"), GetString(env, "message"), env.Payload?["data"] as JsonObject);
    }
}
