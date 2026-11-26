using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.Hubs;

/// <summary>单独的跟随通道，不加入控制房间，也不使用任何现有房间认证。</summary>
public sealed class RoomFollowHub(RoomFollowRelay relay) : Hub
{
    private static readonly object BindingKey = new();

    public Task<RoomFollowBindingResult> BindPublisher(string? writeSecret, string? readHash)
        => relay.BindPublisherAsync(GetConnection(), writeSecret, readHash);

    public Task<RoomFollowBindingResult> BindSubscriber(string? readSecret, string? writeHash)
        => relay.BindSubscriberAsync(GetConnection(), readSecret, writeHash);

    public Task Publish(string? state) => relay.PublishAsync(GetConnection(), state);

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            if (Context.Items.TryGetValue(BindingKey, out var value) && value is RoomFollowRelay.Connection connection)
                await relay.DisconnectAsync(connection);
        }
        finally { await base.OnDisconnectedAsync(exception); }
    }

    private RoomFollowRelay.Connection GetConnection()
    {
        // Hub 对象不是连接级单例；绑定保存在服务端实际连接的上下文中。
        lock (Context.Items)
        {
            if (Context.Items.TryGetValue(BindingKey, out var value) && value is RoomFollowRelay.Connection connection)
                return connection;
            var created = new RoomFollowRelay.Connection(Context.ConnectionId, Context.ConnectionAborted, Context.Abort);
            Context.Items[BindingKey] = created;
            return created;
        }
    }

    // 仅在既有受信代理的 ForwardedHeaders 处理之后运行。不能自行信任客户端的转发头，
    // 也不能重定向带能力的明文请求。本机跟随走当前 Windows 用户私有管道，不走 HTTP。
    internal static Task RequireHttpsAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments("/room-follow", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!context.Request.IsHttps)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return Task.CompletedTask;
            }
        }
        return next(context);
    }
}
