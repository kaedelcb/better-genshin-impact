namespace BgiCoordinatorServer.Gateway;

public sealed partial class GatewayDispatcher
{
    partial void RegisterDesktopControl()
    {
        _commands["desktop.control.request"] = async (env, ctx) => await _ops.RequestDesktopControlAsync(ctx,
            GetString(env, "roomCode"), Get<Models.DesktopControlCommand>(env, "command"));
        _commands["desktop.control.respond"] = async (env, ctx) => await _ops.RespondDesktopControlAsync(ctx,
            GetString(env, "roomCode"), Get<Models.DesktopControlResult>(env, "result"));
        _commands["desktop.control.cancel"] = async (env, ctx) => await _ops.CancelDesktopControlAsync(ctx,
            GetString(env, "roomCode"), GetString(env, "sessionId"), GetString(env, "targetUid"), GetString(env, "controlId"), GetString(env, "requestId"));
        _commands["desktop.control.presence"] = async (env, ctx) => await _ops.RequestControlPresenceAsync(ctx,
            GetString(env, "roomCode"), Get<Models.DesktopControlPresenceRequest>(env, "presence"));
        _commands["desktop.control.presenceResult"] = async (env, ctx) => await _ops.RespondControlPresenceAsync(ctx,
            GetString(env, "roomCode"), Get<Models.DesktopControlPresenceResult>(env, "result"));
    }
}
