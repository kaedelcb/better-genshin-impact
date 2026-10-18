using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomLogContextTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BothHubRoutesCaptureUnstructuredCommandLogsOnlyInRegisteredRoom(bool gateway)
    {
        using var logs = new ContextLogHarness();
        var connection = "caller_" + Guid.NewGuid().ToString("N");
        logs.Manager.AddToControlRoom("CTRL_ABC123", connection, "123", "成员", "instance");
        var setup = new GatewayTestHarness();
        var broadcaster = new GatewayBroadcaster(setup.LegacyHub.Object, setup.GatewayHub.Object, setup.Tracker,
            logs.Factory.CreateLogger<GatewayBroadcaster>());
        var ops = new RoomOperations(logs.Manager, logs.Factory.CreateLogger<RoomOperations>(), broadcaster,
            new RoomPhaseObserver(logs.Factory.CreateLogger<RoomPhaseObserver>()), setup.Capabilities, logs.Context);
        var dispatcher = new GatewayDispatcher(setup.Tracker, logs.Factory.CreateLogger<GatewayDispatcher>(), ops);
        Hub hub = gateway
            ? new GatewayHub(dispatcher, setup.Tracker, ops, logs.Factory.CreateLogger<GatewayHub>())
            : new CoordinatorHub(ops);
        var caller = new Mock<HubCallerContext>();
        caller.SetupGet(context => context.ConnectionId).Returns(connection);
        var method = gateway ? typeof(GatewayHub).GetMethod(nameof(GatewayHub.Dispatch))!
            : typeof(CoordinatorHub).GetMethod(nameof(CoordinatorHub.SendRemoteCommand))!;
        var invocation = new HubInvocationContext(caller.Object, Mock.Of<IServiceProvider>(), hub, method, []);
        var filter = new RoomLogHubFilter(logs.Context, logs.Factory.CreateLogger<RoomLogHubFilter>());
        var command = new RemoteCommand { RoomCode = "ABC123", Cmd = "query_status", Sender = "123", Target = ["*"] };

        await filter.InvokeMethodAsync(invocation, async _ =>
        {
            await ops.SendRemoteCommandAsync(gateway ? GatewayHandlerContext.V3(connection) : GatewayHandlerContext.Legacy(connection), command);
            // 模拟恶意信封目标，业务拒绝日志应只归调用者已登记的控制房间。
            command.RoomCode = "DEF456";
            await ops.SendRemoteCommandAsync(gateway ? GatewayHandlerContext.V3(connection) : GatewayHandlerContext.Legacy(connection), command);
            logs.Factory.CreateLogger("GatewayFailure").LogError("处理消息失败");
            logs.Factory.CreateLogger("SpoofedField").LogWarning("拒绝目标房间 {RoomCode}", "DEF456");
            return null;
        });

        var entries = logs.Store.Read("ABC123");
        Assert.Contains(entries, entry => entry.Message.Contains("转发到"));
        Assert.Contains(entries, entry => entry.Message.Contains("拒绝发送命令"));
        Assert.Contains(entries, entry => entry.Message == "处理消息失败");
        Assert.Empty(logs.Store.Read("DEF456"));
    }

    [Fact]
    public void ScopeTracksRoomSwitchAndStopsAtUnbind()
    {
        using var logs = new ContextLogHarness();
        var connection = "game_" + Guid.NewGuid().ToString("N");
        var first = logs.Manager.CreateRoom(connection, "房主", playerUid: "123");
        var logger = logs.Factory.CreateLogger("Game");
        using (logs.Context.BeginConnectionScope(logger, connection))
        {
            logger.LogDebug("第一房间");
            logs.Manager.LeaveRoom(connection);
            var second = logs.Manager.CreateRoom(connection, "房主", playerUid: "123");
            logger.LogInformation("第二房间");
            logs.Manager.LeaveRoom(connection);
            logger.LogWarning("已解绑 {RoomCode}", first);
            Assert.Contains(logs.Store.Read(second), entry => entry.Message == "第二房间");
            Assert.DoesNotContain(logs.Store.Read(first), entry => entry.Message == "第二房间");
        }
        Assert.Contains(logs.Store.Read(first), entry => entry.Message == "第一房间");
        Assert.DoesNotContain(logs.Store.Read(first), entry => entry.Message.Contains("已解绑"));
    }

    [Fact]
    public void UnknownConnectionCannotInjectIntoRoomUsingStructuredField()
    {
        using var logs = new ContextLogHarness();
        var logger = logs.Factory.CreateLogger("Unregistered");
        using (logs.Context.BeginConnectionScope(logger, "unknown"))
            logger.LogError("房间 {RoomCode} 请求失败", "ABC123");
        Assert.Empty(logs.Store.Read("ABC123"));
    }

    [Fact]
    public void TeardownExceptionRetainsVerifiedInitialSessionWithoutAttributingOrdinaryUnboundLogs()
    {
        using var logs = new ContextLogHarness();
        var code = logs.Manager.CreateRoom("host", "房主", playerUid: "123");
        var sessionId = logs.Manager.GetRoom(code)!.SessionId;
        var logger = logs.Factory.CreateLogger("GatewayFailure");
        using (logs.Context.BeginConnectionScope(logger, "host"))
        {
            logs.Manager.DeleteRoom(code);
            logger.LogError(new IOException("移组失败"), "关闭房间后处理失败");
            logger.LogInformation("解绑后的普通消息");
        }
        var error = Assert.Single(logs.Store.Read(code).Where(entry => entry.Message.Contains("关闭房间后处理失败")));
        Assert.Equal(sessionId, error.GameSessionId);
        Assert.DoesNotContain(logs.Store.Read(code), entry => entry.Message == "解绑后的普通消息");
    }
}

internal sealed class ContextLogHarness : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "bgi-room-context-" + Guid.NewGuid().ToString("N"));
    public RoomManager Manager { get; } = new();
    public RoomLogArchive Archive { get; }
    public RoomLogStore Store { get; }
    public ILoggerFactory Factory { get; }
    public RoomLogContext Context { get; }

    public ContextLogHarness()
    {
        Archive = new RoomLogArchive(DirectoryPath);
        Store = new RoomLogStore(Archive);
        Factory = LoggerFactory.Create(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(new RoomLogProvider(Store, Manager)));
        Context = new RoomLogContext(Manager, Archive, Factory.CreateLogger<RoomLogContext>());
    }

    public void Dispose()
    {
        Factory.Dispose();
        if (Directory.Exists(DirectoryPath)) Directory.Delete(DirectoryPath, recursive: true);
    }
}
