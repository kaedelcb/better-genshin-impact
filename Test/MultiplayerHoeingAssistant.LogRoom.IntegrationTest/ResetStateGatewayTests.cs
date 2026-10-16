using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

/// <summary>隔离真实网关，验证监控端的正式复位等待能收到各执行端确认，不启动 BGI 或 WPF Application。</summary>
public sealed class ResetStateGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ObserverReset_UsesGatewayReplies_AndReportsOnlyFailedMembers(bool peerFails)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须设置隔离 NEXUSBGI_DATA_ROOT。");
        var suffix = Guid.NewGuid().ToString("N");
        var root = Path.Combine(AssistantDataDirectory.Root, "reset-gateway-" + suffix);
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new RoomManager(50));
        builder.Services.AddSingleton<GatewaySessionTracker>();
        builder.Services.AddSingleton<GatewayBroadcaster>();
        builder.Services.AddSingleton<RoomPhaseObserver>();
        builder.Services.AddSingleton<RoomOperations>();
        builder.Services.AddSingleton<GatewayDispatcher>();
        builder.Services.AddSignalR();
        await using var server = builder.Build();
        server.MapHub<GatewayHub>("/gateway");
        server.MapHub<CoordinatorHub>("/hub");
        await server.StartAsync();
        var url = server.Urls.Single();
        var room = "reset-" + suffix;
        var resetId = "reset_" + suffix;
        const string password = "isolated-reset-test";
        await using var observer = new SignalRClient();
        await using var ownExecutor = new SignalRClient();
        await using var peerExecutor = new SignalRClient();
        var vm = new MainViewModel();
        Field("_config").SetValue(vm, new AssistConfig
        {
            ServerUrl = url, PlayerUid = "owner", PlayerName = "监控发起端", ObserverMode = true,
            ControlRoomPassword = password, TeamUids = ["owner", "peer"], DisclaimerAccepted = true,
            BgiPath = Path.Combine(root, "unused", "BetterGI.exe")
        });
        Field("_signalRClient").SetValue(vm, observer);
        var replies = new ConcurrentQueue<RemoteCommand>();
        var allReplies = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observerResetCommands = 0;
        observer.OnRemoteCommand += command =>
        {
            if (command.Cmd == "reset_state") Interlocked.Increment(ref observerResetCommands);
            if (command.Cmd != "ack") return;
            try
            {
                Method("CompleteResetAck").Invoke(vm, [command]);
                replies.Enqueue(command);
                if (replies.Count == 4) allReplies.TrySetResult();
            }
            catch (Exception ex) { allReplies.TrySetException(ex); }
        };
        var own = new ExecutorProbe(ownExecutor, "owner", resetId, "success");
        var peer = new ExecutorProbe(peerExecutor, "peer", resetId, peerFails ? "failed" : "success");

        try
        {
            await ownExecutor.ConnectAsync(url, room, password, "owner", "自身执行端", [],
                clientInstanceId: "own-" + suffix).WaitAsync(TimeSpan.FromSeconds(10));
            await peerExecutor.ConnectAsync(url, room, password, "peer", "第二执行端", [],
                clientInstanceId: "peer-" + suffix).WaitAsync(TimeSpan.FromSeconds(10));
            await observer.ConnectAsync(url, room, password, "owner", "监控发起端", [],
                isRemote: true).WaitAsync(TimeSpan.FromSeconds(10));

            // 从正式 MainViewModel 等待入口发送，经 SDK -> Kestrel /gateway -> SDK 完成确认。
            var wait = (Task<IReadOnlyList<string>>)Method("SendAndWaitResetAcksAsync")
                .Invoke(vm, [resetId, new[] { "owner", "peer" }, 1, null])!;
            var missing = await wait.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(own.Replied.Task, peer.Replied.Task).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(peerFails ? new[] { "peer" } : Array.Empty<string>(), missing);
            Assert.Empty((IDictionary)Field("_pendingResetAcks").GetValue(vm)!);

            // 等待已经完成后再重复回执，迟到 ACK 不能变成新的 reset_state 或留下等待项。
            await Task.WhenAll(own.RepeatAckAsync(), peer.RepeatAckAsync());
            await allReplies.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, own.ResetCount);
            Assert.Equal(1, peer.ResetCount);
            Assert.Equal(0, observerResetCommands);
            Assert.Empty((IDictionary)Field("_pendingResetAcks").GetValue(vm)!);
            Assert.All(replies, reply => Assert.Equal(resetId, reply.CommandId));
            Assert.Equal(2, replies.Count(reply => reply.SenderUid == "owner"));
            Assert.Equal(2, replies.Count(reply => reply.SenderUid == "peer"));
            Assert.All(replies.Where(reply => reply.SenderUid == "peer"), reply =>
                Assert.Equal(peerFails ? "failed" : "success", reply.Params!["status"].ToString()));
            var manager = server.Services.GetRequiredService<RoomManager>();
            Assert.Equal(2, manager.GetControlRoomPlayers("CTRL_" + room).Count);
            Assert.Empty(manager.GetAndClearPendingCommands("owner"));
            Assert.Empty(manager.GetAndClearPendingCommands("peer"));
        }
        finally { await server.StopAsync(); }
    }

    private static FieldInfo Field(string name)
        => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static MethodInfo Method(string name)
        => typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class ExecutorProbe
    {
        private readonly SignalRClient _client;
        private readonly string _uid;
        private readonly string _resetId;
        private readonly string _status;
        private RemoteCommand? _reply;
        private int _resetCount;
        public int ResetCount => Volatile.Read(ref _resetCount);
        public TaskCompletionSource Replied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ExecutorProbe(SignalRClient client, string uid, string resetId, string status)
        {
            _client = client;
            _uid = uid;
            _resetId = resetId;
            _status = status;
            client.OnRemoteCommand += command =>
            {
                if (command.Cmd != "reset_state") return;
                Interlocked.Increment(ref _resetCount);
                _ = ReplyAsync(command);
            };
        }

        private async Task ReplyAsync(RemoteCommand command)
        {
            try
            {
                Assert.Equal(_resetId, command.CommandId);
                Assert.Equal(_resetId, command.Params!["resetId"].ToString());
                _reply = new RemoteCommand
                {
                    Cmd = "ack", Sender = _uid, SenderUid = _uid, Target = [command.SenderUid],
                    CommandId = command.CommandId,
                    Params = new Dictionary<string, object> { ["status"] = _status, ["message"] = "隔离执行端复位结果" }
                };
                await _client.SendRemoteCommandAsync(_reply);
                Replied.TrySetResult();
            }
            catch (Exception ex) { Replied.TrySetException(ex); }
        }

        public Task RepeatAckAsync() => _client.SendRemoteCommandAsync(_reply!);
    }
}
