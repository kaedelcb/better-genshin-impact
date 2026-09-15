using System.Text.Json;
using BetterGenshinImpact.Shared.CooperativeRerun;
using BgiCoordinatorServer.Services;

namespace BgiCoordinatorServer.Gateway;

public sealed partial class GatewayDispatcher
{
    partial void RegisterRerun()
    {
        _commands[GatewayProtocol.Names.RerunUpdate] = (env, ctx) => Task.FromResult(HandleRerun(env, ctx, false));
        _queries[GatewayProtocol.Names.RerunState] = (env, ctx) => Task.FromResult(HandleRerun(env, ctx, true));
    }

    private object? HandleRerun(GatewayEnvelope env, GatewayHandlerContext ctx, bool pollOnly)
    {
        try
        {
            var request = env.Payload?.Deserialize<RerunRequest>(GatewayJson.Options);
            if (request == null || (pollOnly && request.Operation != RerunProtocol.Poll))
                return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "rerun.state requires a Poll request; request payload is required." } };
            return _ops.ApplyRerun(ctx, request, connectionId =>
                _tracker.TryGet(connectionId, out var hello) &&
                hello?.Capabilities?.Contains(RerunProtocol.Capability, StringComparer.Ordinal) == true);
        }
        catch (RerunProtocolException ex)
        {
            return new { error = new { code = ex.Code, message = ex.Message } };
        }
        catch (JsonException)
        {
            return new { error = new { code = GatewayProtocol.ErrorCodes.BadRequest, message = "Invalid rerun request payload." } };
        }
    }
}
