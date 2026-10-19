using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;
using Envelope = BgiCoordinatorServer.Gateway.GatewayEnvelope;
using Protocol = BgiCoordinatorServer.Gateway.GatewayProtocol;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

/// <summary>四条真实本地网关连接；不启动游戏、不接触运行中的助手或真实 User。</summary>
public sealed class FourMemberConnectionRecoveryGatewayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalBatchSurvivesMemberReconnect_AndTerminalCannotBeRevived(bool stopAfterReconnect)
    {
        await using var server = await Server.StartAsync();
        var room = "recovery-" + Guid.NewGuid().ToString("N");
        var uids = Enumerable.Range(0, 4).Select(i => "isolated-member-" + i).ToArray();
        var instances = uids.Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var tokens = uids.Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var clients = new SignalRClient[4];
        const string group = "隔离四人锄地";
        try
        {
            for (var i = 0; i < 4; i++)
            {
                clients[i] = new SignalRClient();
                await ConnectAsync(i);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = uids[i], BgiStatus = "running",
                    ConfigGroups = [group], OnlineHoeingGroupNames = [group],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4
                });
            }
            var request = Guid.NewGuid().ToString("N");
            var batch = await clients[0].BeginCoordinatedBatchAsync(request, uids,
                DateTimeOffset.UtcNow.AddMinutes(1));
            foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
                for (var i = 0; i < 4; i++) batch = await UpdateAsync(i, stage);
            Assert.Equal("running", batch.Phase);
            Assert.Equal(0, batch.Attempt);

            // 真实断开并以同执行实例、原 token 重新入房，不能变成第二个批次。
            await clients[0].DisposeAsync();
            clients[0] = new SignalRClient();
            await ConnectAsync(0);
            var restored = await UpdateAsync(0, "takeoverConfirmed");
            Assert.Equal(batch.BatchId, restored.BatchId);
            Assert.Equal("running", restored.Phase);
            Assert.Equal(0, restored.Index);
            Assert.Equal(0, restored.Attempt);
            Assert.Equal(4, server.Manager.GetControlRoomPlayers("CTRL_" + room).Count);
            var duplicate = await clients[0].BeginCoordinatedBatchAsync(request, uids,
                DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Equal(batch.BatchId, duplicate.BatchId);
            Assert.Equal(batch.Generation, duplicate.Generation);

            if (stopAfterReconnect)
            {
                await clients[3].SendRemoteCommandAsync(new RemoteCommand
                {
                    Cmd = "stop", Target = ["*"], CommandId = Guid.NewGuid().ToString("N"),
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20),
                    Params = new() { ["stopScope"] = "all" }
                });
                await WaitUntilAsync(async () => (await clients[0].GetCurrentCoordinatedBatchAsync())?.Phase == "aborted");
            }
            else
            {
                for (var i = 0; i < 4; i++) batch = await UpdateAsync(i, "takeoverConfirmed", "succeeded");
                Assert.Equal("completed", batch.Phase);
            }

            // 停止/完成后，重连和迟到的成功回执均只能读取原终态。
            var terminal = (await clients[0].GetCurrentCoordinatedBatchAsync())!;
            await clients[0].DisposeAsync();
            clients[0] = new SignalRClient();
            await ConnectAsync(0);
            for (var i = 0; i < 4; i++)
            {
                var late = await UpdateAsync(i, "takeoverConfirmed", "succeeded");
                Assert.Equal(terminal.BatchId, late.BatchId);
                Assert.Equal(terminal.Phase, late.Phase);
                Assert.Equal(terminal.Index, late.Index);
                Assert.Equal(terminal.Attempt, late.Attempt);
            }
            Assert.Equal(stopAfterReconnect ? "user_stop_all" : "", terminal.Reason);
            Assert.Equal(terminal.BatchId, (await clients[0].BeginCoordinatedBatchAsync(request,
                uids, DateTimeOffset.UtcNow.AddMinutes(1))).BatchId);

            Task ConnectAsync(int i) => clients[i].ConnectAsync(server.Url, room, "isolated-only",
                uids[i], uids[i], uids.ToList(), clientInstanceId: instances[i], bypassSystemProxy: true)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Task<MultiplayerHoeingAssistant.Services.CoordinatedBatchSnapshot> UpdateAsync(
                int i, string stage, string result = "")
                => clients[i].UpdateCoordinatedBatchAsync(batch.Generation, tokens[i], batch.BatchId,
                    "group", 0, 0, result, CancellationToken.None, stage)
                    .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var client in clients)
                if (client is not null) await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task FourGameConnectionsRequireAllArrivals_AndGraceReconnectKeepsMemberIdentity()
    {
        await using var server = await Server.StartAsync();
        var connections = new HubConnection[4];
        var events = Enumerable.Range(0, 4).Select(_ => new ConcurrentQueue<string>()).ToArray();
        var arrivals = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        const string syncId = "isolated-route_strict_2";
        string? roomCode = null;
        try
        {
            for (var i = 0; i < 4; i++) connections[i] = await NewGameConnectionAsync(i);
            var created = await SendAsync(connections[0], Protocol.Names.RoomCreate,
                new { playerName = "member-0", playerUid = "member-0", expectedPlayerCount = 4, reportedVersion = "0.1.0+isolated" });
            roomCode = created.Payload!["roomCode"]!.GetValue<string>();
            for (var i = 1; i < 4; i++)
                Assert.True((await SendAsync(connections[i], Protocol.Names.RoomJoin,
                    new { playerName = "member-" + i, playerUid = "member-" + i, reportedVersion = "0.1.0+isolated" }, roomCode))
                    .Payload!["success"]!.GetValue<bool>());
            await SendAsync(connections[0], Protocol.Names.RoomMarkStarted, null, roomCode);

            for (var i = 0; i < 3; i++)
                await SendAsync(connections[i], Protocol.Names.SyncWaitForAllPlayers,
                    new { syncId, syncProgress = 1L }, roomCode);
            Assert.All(arrivals, arrived => Assert.False(arrived.Task.IsCompleted));
            await SendAsync(connections[3], Protocol.Names.SyncWaitForAllPlayers,
                new { syncId, syncProgress = 1L }, roomCode);
            await Task.WhenAll(arrivals.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(events, seen => Assert.Contains(syncId, seen));

            var oldConnection = connections[1].ConnectionId!;
            await connections[1].DisposeAsync();
            await WaitUntilAsync(() =>
            {
                var room = server.Manager.GetRoom(roomCode)!;
                lock (room) return Task.FromResult(room.GracePendingMembers.ContainsKey(oldConnection));
            });
            // 心跳已老，但宽限仍有效：走真实清退入口也不能提前踢人。
            var beforeReconnect = server.Manager.GetRoom(roomCode)!;
            lock (beforeReconnect)
                beforeReconnect.Players.Single(p => p.ConnectionId == oldConnection).LastHeartbeat = DateTime.UtcNow.AddSeconds(-35);
            typeof(RoomManager).GetMethod("RemoveDeadPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server.Manager, [TimeSpan.FromSeconds(30), DateTime.UtcNow]);
            Assert.Equal(4, beforeReconnect.Players.Count);

            connections[1] = await NewGameConnectionAsync(1);
            Assert.True((await SendAsync(connections[1], Protocol.Names.RoomJoin,
                new { playerName = "member-1", playerUid = "member-1", reportedVersion = "0.1.0+isolated" }, roomCode))
                .Payload!["success"]!.GetValue<bool>());
            var restored = server.Manager.GetRoom(roomCode)!;
            lock (restored)
            {
                Assert.Equal(4, restored.Players.Count);
                Assert.DoesNotContain(restored.Players, p => p.ConnectionId == oldConnection);
                Assert.Contains(restored.Players, p => p.ConnectionId == connections[1].ConnectionId);
                Assert.False(restored.GracePendingMembers.ContainsKey(oldConnection));
            }
            await SendAsync(connections[1], Protocol.Names.SessionHeartbeat, null, roomCode);
            Assert.Equal(roomCode, server.Manager.GetRoomByConnectionId(connections[1].ConnectionId!).RoomCode);
        }
        finally
        {
            // 先结束成员，最后结束房主，避免测试收尾误报房主先退。
            foreach (var i in new[] { 1, 2, 3, 0 })
                if (connections[i] is not null) await connections[i].DisposeAsync();
        }

        async Task<HubConnection> NewGameConnectionAsync(int index)
        {
            var connection = new HubConnectionBuilder().WithUrl(server.Url + "/gateway").Build();
            connection.On<Envelope>("evt", envelope =>
            {
                if (envelope.Name != "sync.allArrived") return;
                // Requests use syncId; the AllArrived event carries syncPointId.
                var id = envelope.Payload?["syncPointId"]?.GetValue<string>();
                if (id is null) return;
                events[index].Enqueue(id);
                if (id == syncId) arrivals[index].TrySetResult();
            });
            await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await SendAsync(connection, Protocol.Names.SessionHello,
                new { clientKind = "bgi", protocolVersion = Protocol.ProtocolVersion, capabilities = Array.Empty<string>() }, query: true);
            return connection;
        }
    }

    private static async Task<Envelope> SendAsync(HubConnection connection, string name, object? payload,
        string? room = null, bool query = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await connection.InvokeAsync<Envelope>(query ? "Query" : "Dispatch", new Envelope
        {
            Type = query ? "query" : "command", Name = name, RequestId = Guid.NewGuid().ToString("N"),
            RoomCode = room, Payload = Envelope.ToPayload(payload), SentAtUtc = DateTime.UtcNow
        }, timeout.Token);
        Assert.Null(response.Payload?["error"]);
        return response;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "隔离网关状态未在期限内收敛。");
            await Task.Delay(20);
        }
    }

    private sealed class Server(WebApplication application) : IAsyncDisposable
    {
        public string Url => application.Urls.Single();
        public RoomManager Manager => application.Services.GetRequiredService<RoomManager>();

        public static async Task<Server> StartAsync()
        {
            Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离 NEXUSBGI_DATA_ROOT。");
            var root = Path.Combine(AssistantDataDirectory.Root, "four-member-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_ => new RoomManager(50));
            builder.Services.AddSingleton<GatewaySessionTracker>();
            builder.Services.AddSingleton<GatewayBroadcaster>();
            builder.Services.AddSingleton<RoomPhaseObserver>();
            builder.Services.AddSingleton<IClientCapabilityLookup, GatewayCapabilityLookup>();
            builder.Services.AddSingleton<RoomOperations>();
            builder.Services.AddSingleton<GatewayDispatcher>();
            builder.Services.AddSignalR();
            var application = builder.Build();
            application.MapHub<GatewayHub>("/gateway");
            application.MapHub<CoordinatorHub>("/hub");
            await application.StartAsync();
            return new Server(application);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
