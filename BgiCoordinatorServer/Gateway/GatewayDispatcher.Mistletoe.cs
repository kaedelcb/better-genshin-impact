using System.Text.Json.Nodes;

namespace BgiCoordinatorServer.Gateway;

public sealed partial class GatewayDispatcher
{
    partial void RegisterMistletoe()
    {
        _commands["mistletoe.request"] = async (env, ctx) => await _ops.RequestMistletoeAsync(ctx,
            GetString(env, "roomCode"), GetString(env, "targetUid"), GetString(env, "targetInstanceId"),
            GetString(env, "requestId"), env.Payload?["request"] as JsonObject);
        _commands["mistletoe.respond"] = async (env, ctx) => await _ops.RespondMistletoeAsync(ctx,
            GetString(env, "roomCode"), GetString(env, "requestId"), env.Payload?["reply"] as JsonObject);
    }
}
