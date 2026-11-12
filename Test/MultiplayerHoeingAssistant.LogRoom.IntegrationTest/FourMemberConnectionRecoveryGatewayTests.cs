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

    [Fact]
    public async Task V2WorldPlanSkipsAB_RequiresReconnectedDWorldArrival_AndRetiresStartupAbort()
    {
        await using var server = await Server.StartAsync();
        var connections = new HubConnection[4];
        var uids = new[] { "loopback-A", "loopback-B", "loopback-C", "loopback-D" };
        var tickets = new (string PlanId, string Ticket)[4];
        var arrived = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var closed = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        string? targetCode = null;
        const string version = "0.1.0+isolated-world-plan-v2";
        try
        {
            for (var i = 0; i < 4; i++) connections[i] = await ConnectV2Async(i);
            var source = await SendAsync(connections[0], Protocol.Names.RoomCreate,
                new { playerName = uids[0], playerUid = uids[0], expectedPlayerCount = 4, reportedVersion = version });
            var sourceCode = source.Payload!["roomCode"]!.GetValue<string>();
            await SendAsync(connections[0], Protocol.Names.RoomSetConfig,
                new { config = new { multiWorldEnabled = true, multiWorldCount = 4, partyTimeoutSeconds = 60 } },
                sourceCode);
            for (var i = 1; i < 4; i++)
                Assert.True((await SendAsync(connections[i], Protocol.Names.RoomJoin,
                    new { playerName = uids[i], playerUid = uids[i], reportedVersion = version }, sourceCode))
                    .Payload!["success"]!.GetValue<bool>());
            var begin = await SendAsync(connections[0], Protocol.Names.WorldPlanBegin, new { forceLegacy = false }, sourceCode);
            tickets[0] = ReadAdmission(begin);
            for (var i = 1; i < 4; i++)
            {
                tickets[i] = ReadAdmission(await SendAsync(connections[i], Protocol.Names.WorldPlanAcquire, null, sourceCode, query: true));
                Assert.Equal(tickets[0].PlanId, tickets[i].PlanId);
            }
            var counts = new[] { 0, 0, 2, 1 };
            for (var i = 0; i < 4; i++)
            {
                var report = await SendAsync(connections[i], Protocol.Names.WorldPlanReport,
                    new { planId = tickets[i].PlanId, ticket = tickets[i].Ticket, known = true, remainingCount = counts[i] });
                Assert.Equal(i == 3 ? "Prepared" : "Planning", Stage(report));
            }
            for (var i = 0; i < 4; i++)
            {
                var ack = await SendAsync(connections[i], Protocol.Names.WorldPlanAck, Credential(i));
                Assert.Equal(i == 3 ? "Committed" : "Prepared", Stage(ack));
                if (i == 3)
                {
                    var remaining = ack.Payload!["snapshot"]!["remainingHostUids"]!.AsArray()
                        .Select(n => n!.GetValue<string>()).ToArray();
                    Assert.Equal(new[] { uids[2], uids[3] }, remaining);
                    Assert.Equal(4, ack.Payload["snapshot"]!["participantUids"]!.AsArray().Count);
                }
            }
            for (var i = 0; i < 4; i++)
            {
                var released = await SendAsync(connections[i], Protocol.Names.WorldPlanLeaveSource, Credential(i));
                Assert.Equal(i == 3, released.Payload!["snapshot"]!["sourceReleased"]!.GetValue<bool>());
            }
            Assert.Null(server.Manager.GetRoom(sourceCode));
            var target = await SendAsync(connections[2], Protocol.Names.WorldPlanCreateTarget, Credential(2));
            targetCode = target.Payload!["snapshot"]!["targetRoomCode"]!.GetValue<string>();
            Assert.NotEqual(sourceCode, targetCode);
            var targetSession = target.Payload["snapshot"]!["targetRoomSessionId"]!.GetValue<string>();
            Assert.Equal(targetSession, server.Manager.GetRoom(targetCode)!.SessionId);
            for (var i = 0; i < 2; i++)
                Assert.True((await SendAsync(connections[i], Protocol.Names.RoomJoin,
                    new { playerName = uids[i], playerUid = uids[i], reportedVersion = version }, targetCode))
                    .Payload!["success"]!.GetValue<bool>());
            await SendAsync(connections[2], Protocol.Names.RoomReportHostReady, null, targetCode);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal("Committed", Stage(await SendAsync(connections[i], Protocol.Names.WorldPlanValidateTarget,
                    Credential(i), targetCode, query: true)));
                Assert.True((await SendAsync(connections[i], Protocol.Names.WorldReportJoined, null, targetCode))
                    .Payload!["ack"]!.GetValue<bool>());
            }
            var count = await SendAsync(connections[2], Protocol.Names.RoomGetState,
                new { section = "worldJoinedCount" }, targetCode, query: true);
            Assert.Equal(3, count.Payload!["value"]!.GetValue<int>());
            await ExpectErrorAsync(connections[2], Protocol.Names.RoomMarkStarted,
                new { requireWorldPlanRoster = true }, "world_plan_roster_not_ready", targetCode);
            await Task.Delay(150); // Let real local evt delivery drain before asserting that no all-worlds signal was sent.
            Assert.All(arrived, signal => Assert.False(signal.Task.IsCompleted));
            Assert.False(server.Manager.GetRoom(targetCode)!.IsStarted);

            // D loses the source connection after obtaining its private ticket. A new connection must recover this same plan.
            await connections[3].DisposeAsync();
            connections[3] = await ConnectV2Async(3);
            var recovered = await SendAsync(connections[3], Protocol.Names.WorldPlanGet, Credential(3), query: true);
            Assert.Equal("Committed", Stage(recovered));
            Assert.Equal(targetCode, recovered.Payload!["snapshot"]!["targetRoomCode"]!.GetValue<string>());
            Assert.True((await SendAsync(connections[3], Protocol.Names.RoomJoin,
                new { playerName = uids[3], playerUid = uids[3], reportedVersion = version }, targetCode))
                .Payload!["success"]!.GetValue<bool>());
            Assert.Equal("Committed", Stage(await SendAsync(connections[3], Protocol.Names.WorldPlanValidateTarget,
                Credential(3), targetCode, query: true)));
            await ExpectErrorAsync(connections[2], Protocol.Names.RoomMarkStarted,
                new { requireWorldPlanRoster = true }, "world_plan_roster_not_ready", targetCode);
            Assert.All(arrived, signal => Assert.False(signal.Task.IsCompleted));
            await SendAsync(connections[3], Protocol.Names.WorldReportJoined, null, targetCode);
            await Task.WhenAll(arrived.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            var marked = await SendAsync(connections[2], Protocol.Names.RoomMarkStarted,
                new { requireWorldPlanRoster = true }, targetCode);
            Assert.True(marked.Payload!["ack"]!.GetValue<bool>());
            Assert.True(server.Manager.GetRoom(targetCode)!.IsStarted);
            await ExpectErrorAsync(connections[0], Protocol.Names.WorldPlanAbort, Credential(0), "world_plan_startup_retired");
            Assert.NotNull(server.Manager.GetRoom(targetCode));
            Assert.All(closed, signal => Assert.False(signal.Task.IsCompleted));
            await SendAsync(connections[2], Protocol.Names.RoomClose, null, targetCode);
            await Task.WhenAll(closed.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(server.Manager.GetRoom(targetCode));
        }
        finally
        {
            foreach (var i in new[] { 0, 1, 3, 2 })
                if (connections[i] is not null) await connections[i].DisposeAsync();
        }

        object Credential(int i) => new { planId = tickets[i].PlanId, ticket = tickets[i].Ticket };
        static string Stage(Envelope response) => response.Payload!["snapshot"]!["stage"]!.GetValue<string>();
        static (string PlanId, string Ticket) ReadAdmission(Envelope response)
        {
            Assert.Equal("Planning", Stage(response));
            return (response.Payload!["planId"]!.GetValue<string>(), response.Payload["ticket"]!.GetValue<string>());
        }

        async Task<HubConnection> ConnectV2Async(int index)
        {
            var connection = new HubConnectionBuilder().WithUrl(server.Url + "/gateway").Build();
            connection.On<Envelope>("evt", envelope =>
            {
                if (targetCode == null || envelope.RoomCode != targetCode) return;
                if (envelope.Name == "world.allJoined") arrived[index].TrySetResult();
                if (envelope.Name == "room.closed") closed[index].TrySetResult();
            });
            try
            {
                await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var hello = await SendAsync(connection, Protocol.Names.SessionHello,
                    new { clientKind = "bgi", protocolVersion = Protocol.ProtocolVersion,
                        capabilities = new[] { Protocol.WorldPlanCapability } }, query: true);
                Assert.Contains(Protocol.WorldPlanCapability, hello.Payload!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }

        static async Task ExpectErrorAsync(HubConnection connection, string name, object payload, string message, string? room = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await connection.InvokeAsync<Envelope>("Dispatch", new Envelope
            {
                Type = "command", Name = name, RequestId = Guid.NewGuid().ToString("N"), RoomCode = room,
                Payload = Envelope.ToPayload(payload), SentAtUtc = DateTime.UtcNow,
            }, timeout.Token);
            Assert.Equal(Protocol.ErrorCodes.InternalError, response.Payload!["error"]!["code"]!.GetValue<string>());
            Assert.Equal(message, response.Payload["error"]!["message"]!.GetValue<string>());
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
