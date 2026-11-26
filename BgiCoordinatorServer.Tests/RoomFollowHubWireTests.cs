using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomFollowHubWireTests
{
    [Fact]
    public async Task LoopbackWebSocketBindsAndDeliversRealSignalRCallbacksWithLargeMetadata()
    {
        // 独立的内存中继与回环协议夹具，不读取用户配置。此测试验证 SignalR 线协议、路由与消息大小；
        // HTTPS 边界由 HttpsBoundaryAppliesOnlyToFollowEndpoint 单独验证。实际证书/TLS 由部署环境验证。
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = [], ContentRootPath = AppContext.BaseDirectory
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Services.AddSingleton<RoomFollowRelay>();
        builder.Services.AddSignalR().AddHubOptions<RoomFollowHub>(options => options.MaximumReceiveMessageSize = RoomFollowRelay.MaxInvocationBytes);
        await using var app = builder.Build();
        // 只在回环测试夹具中模拟已通过 HTTPS 检查的请求；生产入口不进行此转换。
        app.Use((context, next) =>
        {
            context.Request.Scheme = "https";
            return next(context);
        });
        app.Use(RoomFollowHub.RequireHttpsAsync);
        app.MapHub<RoomFollowHub>("/room-follow", options =>
        {
            options.Transports = HttpTransportType.WebSockets;
            options.ApplicationMaxBufferSize = RoomFollowRelay.MaxInvocationBytes;
            options.TransportMaxBufferSize = RoomFollowRelay.MaxInvocationBytes;
        });
        await app.StartAsync(token);
        var uri = new UriBuilder(app.Urls.Single() + "/room-follow") { Scheme = "ws" }.Uri;
        using var invoker = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false
        });
        using var reader = await Wire.ConnectAsync(uri, invoker, token);
        using var publisher = await Wire.ConnectAsync(uri, invoker, token);
        var grant = RoomFollowRelayTests.Grant.Create();
        await reader.InvokeAsync("1", "BindSubscriber", token, grant.ReadSecret, grant.WriteHash);
        var subscribe = await reader.NextAsync(token);
        Assert.Equal(3, subscribe.GetProperty("type").GetInt32());
        Assert.Equal(JsonValueKind.Null, subscribe.GetProperty("result").GetProperty("state").ValueKind);
        await publisher.InvokeAsync("2", "BindPublisher", token, grant.WriteSecret, grant.ReadHash);
        var publish = await publisher.NextAsync(token);
        Assert.Equal(3, publish.GetProperty("type").GetInt32());
        Assert.True(publish.GetProperty("result").GetProperty("demand").GetBoolean());

        var root = JsonNode.Parse(RoomFollowRelayTests.State(new string('a', 7500)))!.AsObject();
        var profiles = root["profiles"]!.AsArray();
        for (int i = 1; i < 5; i++)
        {
            var profile = profiles[0]!.DeepClone().AsObject();
            profile["id"] = "profile-" + i;
            profiles.Add(profile);
        }
        var state = root.ToJsonString();
        Assert.InRange(Encoding.UTF8.GetByteCount(state), 32769, RoomFollowRelay.MaxStateBytes);
        await publisher.InvokeAsync("3", "Publish", token, state);
        var changed = await reader.NextAsync(token);
        Assert.Equal("FollowState", changed.GetProperty("target").GetString());
        var args = changed.GetProperty("arguments");
        var version = args[0].GetInt64();
        Assert.Equal(state, args[1].GetString());
        Assert.Equal(3, (await publisher.NextAsync(token)).GetProperty("type").GetInt32());
        await publisher.CloseAsync(token);
        var cleared = await reader.NextAsync(token);
        Assert.Equal("FollowState", cleared.GetProperty("target").GetString());
        Assert.True(cleared.GetProperty("arguments")[0].GetInt64() > version);
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("arguments")[1].ValueKind);
        await reader.CloseAsync(token);
        await app.StopAsync(token);
    }

    private sealed class Wire : IDisposable
    {
        private readonly ClientWebSocket _socket = new();
        private readonly Queue<JsonElement> _pending = new();
        private readonly MemoryStream _partial = new();

        internal static async Task<Wire> ConnectAsync(Uri uri, HttpMessageInvoker http, CancellationToken token)
        {
            var wire = new Wire();
            try
            {
                await wire._socket.ConnectAsync(uri, http, token);
                await wire.SendAsync("{\"protocol\":\"json\",\"version\":1}", token);
                Assert.Empty((await wire.NextAsync(token)).EnumerateObject());
                return wire;
            }
            catch { wire.Dispose(); throw; }
        }

        internal Task InvokeAsync(string id, string target, CancellationToken token, params object?[] args)
            => SendAsync(JsonSerializer.Serialize(new { type = 1, invocationId = id, target, arguments = args }), token);

        private Task SendAsync(string json, CancellationToken token) => _socket.SendAsync(
            new ArraySegment<byte>(Encoding.UTF8.GetBytes(json + '\u001e')), WebSocketMessageType.Text, true, token);

        internal async Task<JsonElement> NextAsync(CancellationToken token)
        {
            var buffer = new byte[8192];
            while (true)
            {
                while (_pending.TryDequeue(out var message))
                    if (!message.TryGetProperty("type", out var type) || type.GetInt32() != 6) return message;
                var part = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
                Assert.Equal(WebSocketMessageType.Text, part.MessageType);
                for (var i = 0; i < part.Count; i++)
                {
                    if (buffer[i] != 0x1e) { _partial.WriteByte(buffer[i]); continue; }
                    using var document = JsonDocument.Parse(_partial.ToArray());
                    _pending.Enqueue(document.RootElement.Clone());
                    _partial.SetLength(0);
                }
            }
        }

        internal Task CloseAsync(CancellationToken token) => _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", token);
        public void Dispose() { _socket.Dispose(); _partial.Dispose(); }
    }
}
