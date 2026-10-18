using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.Services;

/// <summary>旧 /hub 和新 /gateway 共用可信日志上下文，包含网关捕获的异常。</summary>
public sealed class RoomLogHubFilter(RoomLogContext context, ILogger<RoomLogHubFilter> logger) : IHubFilter
{
    public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        using var scope = context.BeginConnectionScope(logger, invocationContext.Context.ConnectionId);
        return await next(invocationContext);
    }

    public async Task OnDisconnectedAsync(HubLifetimeContext lifetimeContext, Exception? exception,
        Func<HubLifetimeContext, Exception?, Task> next)
    {
        using var scope = context.BeginSnapshotScope(logger, context.SnapshotConnection(lifetimeContext.Context.ConnectionId));
        await next(lifetimeContext, exception);
    }
}
