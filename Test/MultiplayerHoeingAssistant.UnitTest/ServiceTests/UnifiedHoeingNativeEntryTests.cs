using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 真实本地服务器 / SignalR / 私有命名管道 / 生产 MainViewModel 事件入口。
/// BGI 端仅为协议替身，本测试不把替身作业当成游戏线路实际执行证据。
/// 单独运行此类，外部须预先设置唯一的 NEXUSBGI_DATA_ROOT；测试不启动用户 BGI。
/// </summary>
[Collection("TaskCenterPipeTransport")]
public sealed class UnifiedHoeingNativeEntryTests
{
    private static readonly ConcurrentQueue<string> Progress = new();

    private static void RecordProgress(string message)
    {
        var line = $"{DateTimeOffset.Now:O} [thread {Environment.CurrentManagedThreadId}] {message}";
        Progress.Enqueue(line);
        File.AppendAllText(Path.Combine(AssistantDataDirectory.Root, "native-entry-progress.log"), line + Environment.NewLine);
    }

    private sealed class TracedResource(string name, Func<ValueTask> dispose) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            RecordProgress("disposing " + name);
            await dispose();
            RecordProgress(name + " disposed");
        }
    }

    [Fact]
    public async Task ProductionEntry_RejectsConcurrentBegin_StopsOnlyExactBatch_AndStartsAgainAfterCleanup()
    {
        Assert.True(AssistantDataDirectory.IsExplicit,
            "必须先设置隔离 NEXUSBGI_DATA_ROOT，禁止在真实 User 上运行入口验证。");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownedServers = new ConcurrentBag<IsolatedCoordinator>();
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current); // 此真实入口用独立 testhost，不借用其他测试的 Dispatcher。
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try { await RunEntryAsync(ownedServers.Add); finished.TrySetResult(); }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "UnifiedHoeingNativeEntry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await finished.Task.WaitAsync(TimeSpan.FromMinutes(3)); }
        catch (TimeoutException error)
        {
            // The STA can be stuck in disposal: do not leave our child server alive after the testhost exits.
            foreach (var server in ownedServers) server.StopOwnedProcess();
            throw new TimeoutException("原生整链未完成；最后阶段：\n" + string.Join("\n", Progress), error);
        }
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离 WPF Dispatcher 没有退出。");
    }

    private static async Task RunEntryAsync(Action<IsolatedCoordinator> ownServer)
    {
        Directory.CreateDirectory(AssistantDataDirectory.Root);
        const string uid = "100000001";
        const string groupName = "隔离开锄入口";
        const string quickGroupName = "快捷指令专用配置组";
        const string quickKey = "一键传奇";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        RecordProgress("starting coordinator");
        var server = await IsolatedCoordinator.StartAsync();
        ownServer(server);
        await using var serverScope = new TracedResource("coordinator", server.DisposeAsync);
        RecordProgress("coordinator ready");
        var pipe = new BgiInstancePipeDouble();
        await using var pipeScope = new TracedResource("pipe", pipe.DisposeAsync);
        var script = new OwnedBatchPeer(groupName, quickGroupName);
        await using var scriptScope = new TracedResource("script", () => { script.Dispose(); return ValueTask.CompletedTask; });
        pipe.BodyForTest = script.Reply;
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        var external = new BgiExternalClient(Path.GetDirectoryName(Environment.ProcessPath));
        await using var externalScope = new TracedResource("external client", () => { external.Dispose(); return ValueTask.CompletedTask; });
        var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        await using var monitorScope = new TracedResource("process monitor", () => { monitor.Dispose(); return ValueTask.CompletedTask; });
        SignalRClient? client = null;
        MainViewModel? vm = null;
        try
        {
            RecordProgress("connecting private BGI pipe");
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            RecordProgress("view model created");
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uid, PlayerName = "隔离入口执行端",
                TeamUids = [uid], ControlRoomPassword = "isolated-entry-only",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [groupName], OnlineHoeingGroupTypes = ["group"],
                QuickCommands = new() { [quickKey] = "GROUP:" + quickGroupName },
                ExpectedHoeingPlayers = 1, DisclaimerAccepted = true, BypassSystemProxy = true,
            };
            var executor = new CommandExecutor(monitor, config.BgiPath, () => external,
                logger: vm.AddLog, isBatchInFlight: () => Active(vm)?.IsAlive == true);
            executor.BgiRunningProbeOverride = () => true; // The private pipe peer is the fixture's running BGI.
            executor.TaskStartBgiPresenceProbeOverride = () => true; // Only the private pipe peer exists in this test.
            executor.TaskStartBgiLaunchOverride = () => throw new InvalidOperationException("隔离验收禁止启动真实或额外进程");
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("隔离验收禁止停止真实进程");
            Set(vm, "_config", config);
            Set(vm, "_externalClient", external);
            Set(vm, "_processMonitor", monitor);
            Set(vm, "_commandExecutor", executor);
            vm.RoomCode = "NATIVE-" + Guid.NewGuid().ToString("N")[..8];
            client = (SignalRClient)Call(vm, "CreateWiredSignalRClient")!;
            RecordProgress("connecting production client");
            using (var connectionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                await (Task)Call(vm, "ConnectSignalRWithConfigAsync", client, connectionBudget.Token,
                    "隔离正式开始生产建连入口", false)!;
            RecordProgress("production client connected");
            Assert.True(client.SupportsCoordinatedStart);
            Assert.True(client.SupportsCompletionChoice);
            RecordProgress("reporting initial control status");
            await client.ReportControlStatusAsync(new ControlStatus
            {
                PlayerUid = uid, PlayerName = config.PlayerName, BgiStatus = "running", BgiVersion = CoordinatorEntryServerData.BgiVersion,
                ConfigGroups = [groupName, quickGroupName], OnlineHoeingGroupNames = [groupName],
                QuickCommands = new() { [quickKey] = "GROUP:" + quickGroupName },
                OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 1,
                HoeingCompletionAction = new("resume", "group", "", 0),
            });

            RecordProgress("beginning first batch");
            var first = await BeginAsync(client, uid);
            RecordProgress("first batch accepted; waiting for owned task");
            await WaitAsync(() => script.StartCount == 1 && Active(vm)?.BatchId == first.BatchId,
                () => Diagnostics(vm, pipe, server));
            var firstActive = Active(vm)!;
            Assert.Equal(1, script.SoloTakeovers);
            var soloSuspend = Assert.Single(pipe.Received.Where(r => r.Operation == "task.suspend")).Data!.Value;
            Assert.Equal(script.SoloRoot.ToString("N"), soloSuspend.GetProperty("expectedExecutionInstanceId").GetString());
            Assert.Equal(1, soloSuspend.GetProperty("expectedStateRevision").GetInt64());
            Assert.Equal(TaskConflictPolicy.Stop, firstActive.Policy!.Policy);
            Assert.True(firstActive.Prepared);
            Assert.True(firstActive.TakenOver);
            Assert.Equal("running", firstActive.Phase);
            var firstStart = Assert.Single(pipe.Received.Where(r => r.Operation == "ext.task.start")).Data!.Value;
            Assert.True(firstActive.PriorityRecoveryEnabled);
            Assert.Equal($"{first.BatchId}/0/0", firstStart.GetProperty("hoeingBatchId").GetString());
            Assert.Equal(uid, Assert.Single(firstStart.GetProperty("hoeingParticipantUids").EnumerateArray()).GetString());
            Assert.False(firstStart.TryGetProperty("preempt", out var preempt) && preempt.ValueKind == JsonValueKind.True);
            Assert.Equal(1, pipe.CountOf("task.suspend"));

            var refusal = await Assert.ThrowsAsync<GatewayErrorException>(() => BeginAsync(client, uid));
            Assert.Contains("previous_batch_active", refusal.Message);
            Assert.Equal(first.BatchId, (await client.GetCurrentCoordinatedBatchAsync())!.BatchId);
            Assert.Same(firstActive, Active(vm));
            Assert.False(firstActive.IsCancellationRequested);
            Assert.Equal(1, script.StartCount);
            Assert.Equal(0, script.CancelCount);
            Assert.Equal(1, pipe.CountOf("task.suspend"));

            await StopAsync(client, uid, first.BatchId);
            await WaitAsync(() => Active(vm) == null, () => Diagnostics(vm, pipe, server));
            var stopped = await client.GetCurrentCoordinatedBatchAsync();
            Assert.Equal(first.BatchId, stopped!.BatchId);
            Assert.Equal("aborted", stopped.Phase);
            Assert.True(stopped.CleanupComplete);
            Assert.Equal(1, script.CancelCount);
            Assert.Null(executor.TakeoverTicketSnapshot);
            Assert.Null(external.TakeoverTicket);

            var self = vm.Members.Single(m => m.PlayerUid == uid);
            self.IsSelected = true;
            Assert.True(client.SupportsQuickCommandCompletion);
            await vm.ExecuteBoundQuickCommandAsync(quickKey, [self], useCompletionActions: true);
            var second = (await client.GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal(quickKey, second.QuickCommandKey);
            Assert.Equal(quickGroupName, Assert.Single(second.Bindings[uid]).Name);
            Assert.True(second.UseCompletionActions);
            Assert.NotEqual(first.BatchId, second.BatchId);
            await WaitAsync(() => script.StartCount == 2 && Active(vm)?.BatchId == second.BatchId,
                () => Diagnostics(vm, pipe, server));
            var secondActive = Active(vm)!;
            Assert.Equal(TaskConflictPolicy.Resume, secondActive.Policy!.Policy);
            var quickStart = pipe.Received.Where(r => r.Operation == "ext.task.start").Last().Data!.Value;
            Assert.Equal(quickGroupName, quickStart.GetProperty("groupName").GetString());
            Assert.Equal("resume", vm.Members.Single(m => m.PlayerUid == uid).HoeingCompletionAction!.Policy);
            var stopsBeforeDelayedOldMessage = script.CancelCount;
            await StopAsync(client, uid, first.BatchId);
            // 让实际 SignalR 转发和 Dispatcher 处理完成，验证旧 stop 不能落到后继作业。
            await Task.Delay(1300);
            Assert.Same(secondActive, Active(vm));
            Assert.False(secondActive.IsCancellationRequested);
            Assert.Equal(stopsBeforeDelayedOldMessage, script.CancelCount);
            Assert.Equal(2, script.StartCount);
            Assert.Equal(second.BatchId, (await client.GetCurrentCoordinatedBatchAsync())!.BatchId);

            await StopAsync(client, uid, second.BatchId);
            await WaitAsync(() => Active(vm) == null, () => Diagnostics(vm, pipe, server));
            Assert.True((await client.GetCurrentCoordinatedBatchAsync())!.CleanupComplete);
            Assert.Equal(2, script.CancelCount);
            Assert.Equal(2, pipe.CountOf("task.suspend"));
            Assert.Equal(2, pipe.CountOf("task.resume"));
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
            Assert.Null(executor.TakeoverTicketSnapshot);

            // 上线时也复现“单机锄地仍在跑”，不能只覆盖空闲上线。
            script.StartAnotherSolo();
            var onlineSoloRoot = script.SoloRoot;
            // 定时上线与一键开始必须进入同一生产批次入口，并携带本机刚武装的精确意图。
            Assert.True(await (Task<bool>)Call(vm, "MarkOnlineAsync", "scheduled")!);
            await WaitAsync(() => script.StartCount == 3 && Active(vm)?.Start?.Source == "online",
                () => Diagnostics(vm, pipe, server));
            var onlineActive = Active(vm)!;
            var onlineStart = onlineActive.Start!;
            var online = await client.GetCurrentCoordinatedBatchAsync();
            Assert.Equal("online", online!.Source);
            Assert.Equal(onlineActive.BatchId, online.BatchId);
            Assert.NotEqual(second.BatchId, online.BatchId);
            Assert.True(online.IntentGenerations[uid] > 0);
            Assert.Equal(online.IntentGenerations[uid], onlineStart.IntentGenerations[uid]);
            Assert.True(onlineActive.Prepared);
            Assert.True(onlineActive.TakenOver);
            Assert.Equal(2, script.SoloTakeovers);
            var onlineSuspend = pipe.Received.Last(r => r.Operation == "task.suspend").Data!.Value;
            Assert.Equal(onlineSoloRoot.ToString("N"), onlineSuspend.GetProperty("expectedExecutionInstanceId").GetString());

            // 只在这一小段注入已接收旧帧：复用生产反序列化/分发器验证退休批次事件不会重入。
            // 其余开始、推进、停止、清理都经过前面的实际 SignalR 网络连接。
            ReplayEvent(client, GatewayProtocol.Events.ControlBatchStart, first);
            ReplayEvent(client, GatewayProtocol.Events.ControlBatchState, stopped!);
            Assert.True(await (Task<bool>)Call(vm, "MarkOnlineAsync", "command")!);
            await Task.Delay(1300);
            Assert.Same(onlineActive, Active(vm));
            Assert.False(onlineActive.IsCancellationRequested);
            Assert.Equal(3, script.StartCount);
            Assert.Equal(2, script.CancelCount);
            Assert.Equal(online.BatchId, (await client.GetCurrentCoordinatedBatchAsync())!.BatchId);

            await StopAsync(client, uid, online.BatchId);
            await WaitAsync(() => Active(vm) == null, () => Diagnostics(vm, pipe, server));
            Assert.True((await client.GetCurrentCoordinatedBatchAsync())!.CleanupComplete);
            Assert.Equal(3, script.CancelCount);
            Assert.Equal(3, pipe.CountOf("task.suspend"));
            Assert.Equal(3, pipe.CountOf("task.resume"));
            Assert.Null(executor.TakeoverTicketSnapshot);

            ReplayEvent(client, GatewayProtocol.Events.ControlBatchStart, onlineStart);
            await Task.Delay(400);
            Assert.Null(Active(vm));
            Assert.Equal(3, script.StartCount);
            Assert.Equal(3, script.CancelCount);

            // 用户全停止走真实房间路由，即使观察到的是上一批 ID 也必须停止当前批次。
            var fourth = await BeginAsync(client, uid, useActions: true);
            await WaitAsync(() => script.StartCount == 4 && Active(vm)?.BatchId == fourth.BatchId,
                () => Diagnostics(vm, pipe, server));
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("隔离验收禁止触碰真实 BGI 进程");
            await client.SendRemoteCommandAsync(new RemoteCommand
            {
                Cmd = "stop", Target = ["*"], CommandId = Guid.NewGuid().ToString("N"),
                Params = new() { ["stopScope"] = "all", ["hoeingBatchId"] = first.BatchId }
            });
            await WaitAsync(() => pipe.CountOf("ext.task.stop") == 1 && Active(vm) == null,
                () => Diagnostics(vm, pipe, server));
            Assert.Equal(4, script.CancelCount);
            Assert.True((await client.GetCurrentCoordinatedBatchAsync())!.CleanupComplete);
            Assert.Null(executor.TakeoverTicketSnapshot);
            Assert.Equal(4, script.StartCount);
            ReplayEvent(client, GatewayProtocol.Events.ControlBatchStart, fourth);
            await Task.Delay(400);
            Assert.Null(Active(vm));
            Assert.Equal(4, script.StartCount);

            // DC54A4 recovery chain through the real server and production reset handler:
            // this endpoint retained an older local batch while the server moved on.
            var orphan = new OnlineHoeingBatch(first with
            {
                BatchId = "local-lagging-" + first.BatchId, Generation = fourth.Generation - 1,
            }, uid, new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Stop });
            orphan.RunTask = Task.CompletedTask;
            Set(vm, "_activeBatch", orphan);
            var enteredReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var allowReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var physicalResets = 0;
            executor.SeedTakeoverStateForTest("orphan-ticket", new BgiEpoch { ProcessId = 10, StartTicksUtc = 100 });
            executor.ResetProcessOverrideForTest = async () =>
            {
                Interlocked.Increment(ref physicalResets);
                enteredReset.TrySetResult();
                await allowReset.Task;
                return true;
            };
            executor.ResetStatusQueryOverrideForTest = _ => Task.FromResult<IpcResponse?>(new()
            {
                Success = true, Data = JsonSerializer.Serialize(new
                {
                    executionIdle = true, running = false, slotOccupied = false, queueDepth = 0,
                    takeoverActive = false, hasSuspendedTaskContext = false, currentTaskHandle = (string?)null,
                    bgiEpoch = new { processId = Environment.ProcessId,
                        startTicksUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks },
                })
            });
            executor.ResetReadyTimeoutForTest = TimeSpan.FromSeconds(2);
            var reset = await client.ResetCoordinatedBatchAsync("native-repair", [uid],
                expectedGeneration: fourth.Generation, expectedBatchId: fourth.BatchId);
            var resetCommand = new RemoteCommand
            {
                Cmd = "reset_state", CommandId = reset.ResetId, Target = [uid],
                Params = new() { ["resetId"] = reset.ResetId, ["generation"] = reset.Generation, ["batchId"] = reset.BatchId },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            };
            try
            {
                await client.SendRemoteCommandAsync(resetCommand);
                await enteredReset.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var pendingReset = await Assert.ThrowsAsync<GatewayErrorException>(() => BeginAsync(client, uid));
                Assert.Contains("previous_batch_active", pendingReset.Message);
            }
            finally { allowReset.TrySetResult(); }
            await WaitAsync(() => Active(vm) == null, () => Diagnostics(vm, pipe, server));
            Assert.False(orphan.IsAlive);
            Assert.True(orphan.CleanupConfirmed);
            Assert.True((await client.GetCurrentCoordinatedBatchAsync())!.CleanupComplete);
            Assert.Equal(1, physicalResets);
            var fifth = await BeginAsync(client, uid);
            await WaitAsync(() => script.StartCount == 5 && Active(vm)?.BatchId == fifth.BatchId,
                () => Diagnostics(vm, pipe, server));
            var successor = Active(vm)!;
            await client.SendRemoteCommandAsync(resetCommand); // Old authorization cannot reach the successor.
            await Task.Delay(400);
            Assert.Same(successor, Active(vm));
            Assert.False(successor.IsCancellationRequested);
            Assert.Equal(1, physicalResets);
            await StopAsync(client, uid, fifth.BatchId);
            await WaitAsync(() => Active(vm) == null, () => Diagnostics(vm, pipe, server));
            Assert.True((await client.GetCurrentCoordinatedBatchAsync())!.CleanupComplete);
            Assert.Equal(5, script.StartCount);
            RecordProgress("all five batch assertions passed");
        }
        catch (Exception error)
        {
            RecordProgress("entry failed before cleanup: " + error);
            throw;
        }
        finally
        {
            RecordProgress("cleaning view model");
            // 仅向本夹具的私有管道作业收口；不调用 MainVM 通用强制重置或真实进程结束。
            if (vm != null)
            {
                Active(vm)?.Cancel();
                if (Active(vm)?.RunTask is { } pending)
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(45)); } catch { }
                var host = (TaskCenterHost?)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
                Set(vm, "_disposing", true);
                Call(vm, "StopSignalRRecovery");
                foreach (var field in new[] { "_statusTimer", "_retryTimer", "_onlineTimer", "_resumeTimeoutTimer" })
                    (typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(vm) as IDisposable)?.Dispose();
                RecordProgress("shutting down task center");
                if (host != null) await host.ShutdownAsync();
            }
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExt;
            RecordProgress("disposing control client");
            if (client != null) await client.DisposeAsync();
            RecordProgress("control client disposed; disposing pipe and coordinator");
        }
    }

    private static Task<CoordinatedBatchSnapshot> BeginAsync(SignalRClient client, string uid, bool useActions = false) =>
        client.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), [uid], DateTimeOffset.UtcNow.AddMinutes(1),
            useCompletionActions: useActions);

    private static Task StopAsync(SignalRClient client, string uid, string batchId) => client.SendRemoteCommandAsync(new RemoteCommand
    {
        Cmd = "stop", Target = [uid], CommandId = Guid.NewGuid().ToString("N"),
        Params = new Dictionary<string, object> { ["hoeingBatchId"] = batchId },
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20),
    });

    private static OnlineHoeingBatch? Active(MainViewModel vm) =>
        (OnlineHoeingBatch?)typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
    private static void Set(MainViewModel vm, string field, object value) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
    private static object? Call(MainViewModel vm, string method, params object[] args) =>
        typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);

    private static void ReplayEvent(SignalRClient client, string eventName, CoordinatedBatchSnapshot snapshot) =>
        typeof(SignalRClient).GetMethod("DispatchEvt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client,
            [new GatewayEnvelope { Type = "event", Name = eventName, Payload = GatewayEnvelope.ToPayload(snapshot) }]);

    private static async Task WaitAsync(Func<bool> predicate, Func<string> diagnostics)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(80);
        Assert.True(predicate(), diagnostics());
    }

    private static string Diagnostics(MainViewModel vm, BgiInstancePipeDouble pipe, IsolatedCoordinator server) =>
        $"active={Active(vm)?.BatchId}/{Active(vm)?.Phase}/{Active(vm)?.Message}\n"
        + string.Join("\n", vm.CommandLogs.Take(30)) + "\noperations="
        + string.Join(",", pipe.Received.Select(r => r.Operation)) + "\nserver=" + server.Output;

    /// <summary>真实帧传输的保守协议替身：作业一直 running，只有自己的 ownedOnly cancel 才进入终态。</summary>
    private sealed class OwnedBatchPeer(string groupName, string quickGroupName) : IDisposable
    {
        private sealed record Job(string Id, string Key, string Name, string BatchId)
        {
            public bool Cancelled;
        }
        private readonly object _gate = new();
        private readonly List<Job> _jobs = [];
        // 复现 9 点故障：首批开始时正在运行原生单机锄地，而不是空闲。
        public Guid SoloRoot { get; private set; } = Guid.NewGuid();
        private Guid _soloJob = Guid.NewGuid();
        private bool _soloRunning = true;
        public int SoloTakeovers { get; private set; }
        private string? _ticket;
        private readonly List<Mistletoe.Shared.CoordinatedStopSignal> _stopSignals = [];
        private long _manualStopVersion;
        private long? _lastManualStopTimestamp;
        private int _cancelCount;
        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var signal in _stopSignals) signal.Dispose();
                _stopSignals.Clear();
            }
        }
        private readonly int _pid = Environment.ProcessId;
        private readonly long _ticks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private object Epoch => new { processId = _pid, startTicksUtc = _ticks };
        public int StartCount { get { lock (_gate) return _jobs.Count; } }
        public int CancelCount { get { lock (_gate) return _cancelCount; } }
        public void StartAnotherSolo()
        {
            lock (_gate)
            {
                Assert.Null(_ticket);
                Assert.DoesNotContain(_jobs, j => !j.Cancelled);
                SoloRoot = Guid.NewGuid();
                _soloJob = Guid.NewGuid();
                _soloRunning = true;
            }
        }

        private static string? Value(JsonElement? data, string name) => data is { } d && d.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public (bool, string?, string?, object?)? Reply(string operation, JsonElement? data)
        {
            lock (_gate)
            {
                var active = _jobs.LastOrDefault(j => !j.Cancelled);
                switch (operation)
                {
                    case "ext.hello":
                        return (true, null, null, new
                        {
                            bgiVersion = CoordinatorEntryServerData.BgiVersion, sessionId = "private-pipe-only",
                            windowsSessionId = Process.GetCurrentProcess().SessionId, processId = _pid, bgiEpoch = Epoch,
                            capabilities = new Dictionary<string, bool>
                            {
                                ["event.push"] = true, ["task.queue"] = true, ["task.takeover"] = true,
                                ["hoeing.batchOutcome.v1"] = true, ["hoeing.coordinatedStart.v1"] = true,
                                ["hoeing.finishEvidence.v1"] = true,
                                ["execution.targetedSuspend.v1"] = true, ["config.applied"] = true,
                                ["task.coordinatedRecoveryStop.v1"] = true,
                                ["task.stateContinuation.v1"] = true,
                                ["execution.manualStopFence.v1"] = true,
                            }
                        });
                    case "ext.execution.manualStopFence":
                        return (true, null, null, new { bgiEpoch = Epoch, stopVersion = _manualStopVersion,
                            lastManualStopTimestamp = _lastManualStopTimestamp, monotonicFrequency = Stopwatch.Frequency });
                    case "task.status":
                    case "ext.task.status":
                        return (true, null, null, new
                        {
                            running = active != null || _soloRunning, taskRunning = active != null || _soloRunning,
                            autoHoeingRunning = active != null || _soloRunning,
                            autoHoeingMultiplayer = _soloRunning ? false : active != null ? (bool?)true : null,
                            slotOccupied = active != null || _soloRunning, executionSlotOccupied = active != null || _soloRunning,
                            executionIdentityAvailable = active != null || _soloRunning,
                            executionInstanceId = _soloRunning ? SoloRoot.ToString("N") : active?.Id,
                            executionJobId = _soloRunning ? _soloJob.ToString("N") : active?.Id,
                            executionStateRevision = 1, stateRevision = 1, executionIdle = active == null && !_soloRunning,
                            executionExitConfirmed = !_soloRunning && Value(data, "executionInstanceId") == SoloRoot.ToString("N"),
                            executionExitQueryInstanceId = Value(data, "executionInstanceId"),
                            executionExitAtUtc = DateTime.UtcNow, executionExitOrder = 1L,
                            executionExitReason = !_soloRunning && Value(data, "executionInstanceId") == SoloRoot.ToString("N")
                                ? "confirmed" : active == null && !_soloRunning ? "no_active_root" : "not_exited",
                            bgiEpoch = Epoch, takeoverActive = _ticket != null, hasSuspendedTaskContext = false,
                            queueDepth = 0, currentTaskHandle = active?.Id,
                        });
                    case "ext.config.list":
                        return (true, null, null, new { configGroups = new[] { groupName, quickGroupName }, oneClickConfigs = Array.Empty<string>() });
                    case "ext.config.describe":
                        return (true, null, null, new { bgiEpoch = Epoch, configRevision = "native-entry-r1",
                            tasks = new[] { new { name = "隔离首线边界协议任务", enabled = true } } });
                    case "ext.job.list":
                        return (true, null, null, new { bgiEpoch = Epoch, jobs = _jobs.Select(j => new
                        {
                            jobId = j.Id, idempotencyKey = j.Key, name = j.Name, kind = "group",
                            state = j.Cancelled ? "cancelled" : "running", wasCancelled = j.Cancelled,
                            exitConfirmed = j.Cancelled, exitDisposition = j.Cancelled ? "terminal" : null,
                        }).ToArray() });
                    case "task.suspend":
                        if (active != null || Value(data, "takeoverTicket") is not { } ticket)
                            return (false, "not_current", "替身拒绝非空闲接管", null);
                        if (Value(data, "recoveryStopSignal") is { } signalName)
                        {
                            var signal = Mistletoe.Shared.CoordinatedStopSignal.Open(signalName);
                            if (signal is null || signal.IsStopped)
                            {
                                signal?.Dispose();
                                return (false, "manual_stop_fence", "停止信号不可用", null);
                            }
                            _stopSignals.Add(signal);
                        }
                        if (_soloRunning)
                        {
                            if (Value(data, "expectedExecutionInstanceId") != SoloRoot.ToString("N")
                                || data?.GetProperty("expectedStateRevision").GetInt64() != 1)
                                return (false, "not_current", "单机执行身份或修订不匹配", null);
                            _soloRunning = false;
                            SoloTakeovers++;
                            _ticket = ticket;
                            return (true, null, null, new { takeoverTicket = ticket, quiesceConfirmed = true,
                                liveTask = true, expectedExecutionInstanceId = SoloRoot.ToString("N"), expectedStateRevision = 1L });
                        }
                        _ticket = ticket;
                        return (true, null, null, new { takeoverTicket = ticket, quiesceConfirmed = true,
                            liveTask = false, expectedExecutionInstanceId = (string?)null, expectedStateRevision = (long?)null });
                    case "ext.task.start":
                        if (Value(data, "idempotencyKey") is { } retainedKey
                            && _jobs.SingleOrDefault(j => j.Key == retainedKey) is { } adopted)
                            return (true, null, null, new { status = "adopted", taskHandle = adopted.Id, queuePosition = 0 });
                        if (_ticket == null || Value(data, "takeoverTicket") != _ticket || active != null)
                            return (false, "task_busy", "替身票据或空闲条件不满足", null);
                        var job = new Job(Guid.NewGuid().ToString("N"), Value(data, "idempotencyKey")!,
                            Value(data, "groupName")!, Value(data, "hoeingBatchId")!);
                        _jobs.Add(job);
                        return (true, null, null, new { status = "queued", taskHandle = job.Id, queuePosition = 0 });
                    case "ext.task.queueStatus":
                        var queryKey = Value(data, "idempotencyKey");
                        var queried = _jobs.SingleOrDefault(j => queryKey != null ? j.Key == queryKey : j.Id == Value(data, "taskHandle"));
                        if (queried == null)
                            return (true, null, null, new { status = "not_found", taskHandle = (string?)null,
                                idempotencyKey = queryKey, bgiEpoch = Epoch, executionExitConfirmed = false });
                        return (true, null, null, new { status = queried.Cancelled ? "completed" : "running",
                            taskHandle = queried.Id, idempotencyKey = queried.Key, bgiEpoch = Epoch,
                            executionExitConfirmed = queried.Cancelled,
                            cancelled = queried.Cancelled, errorCode = queried.Cancelled ? "user_cancelled" : null,
                            hoeingEvidence = new { plannedNativeCount = 1, started = 1, completed = 0, failed = 0,
                                cancelled = queried.Cancelled ? 1 : 0, active = queried.Cancelled ? 0 : 1,
                                @sealed = queried.Cancelled, jobResult = queried.Cancelled ? "Cancelled" : null,
                                revision = queried.Cancelled ? 2L : 1L } });
                    case "ext.task.acknowledge":
                        var acknowledged = _jobs.SingleOrDefault(j => j.Key == Value(data, "submissionKey")
                            && j.Id == Value(data, "taskHandle"));
                        if (acknowledged is not { Cancelled: true })
                            return (false, "result_not_terminal", "结果未终结，不能确认", null);
                        return (true, null, null, new { status = "acknowledged" });
                    case "ext.task.cancel":
                        var owned = _jobs.SingleOrDefault(j => j.Id == Value(data, "taskHandle"));
                        if (owned == null || Value(data, "ownedOnly") != "v1")
                            return (false, "invalid_request", "只能取消本句柄", null);
                        if (!owned.Cancelled) { owned.Cancelled = true; _cancelCount++; }
                        return (true, null, null, new { status = "cancel_requested" });
                    case "ext.task.stop":
                        if (Value(data, "stopScope") != "all")
                            return (false, "invalid_request", "此入口要求全停止语义", null);
                        foreach (var pending in _jobs.Where(j => !j.Cancelled))
                        {
                            pending.Cancelled = true;
                            _cancelCount++;
                        }
                        _soloRunning = false;
                        _ticket = null;
                        _manualStopVersion++;
                        _lastManualStopTimestamp = Stopwatch.GetTimestamp();
                        foreach (var signal in _stopSignals) signal.Stop();
                        return (true, null, null, new { status = "stop_requested" });
                    case "task.resume":
                        if (active != null || Value(data, "takeoverTicket") != _ticket || _ticket == null)
                            return (false, "stale_ticket", "替身执行权尚未匹配收口", null);
                        _ticket = null;
                        return (true, null, null, new { status = "cleared_not_resumed" });
                    default: return null; // ping / ext.event.subscribe 使用同帧、同进程身份的既有传输夹具。
                }
            }
        }
    }

    private sealed class IsolatedCoordinator : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output = new();
        public string Url { get; }
        public string Output => string.Join("\n", _output.TakeLast(35));
        private IsolatedCoordinator(Process process, string url) { _process = process; Url = url; }

        public static async Task<IsolatedCoordinator> StartAsync()
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "BgiCoordinatorServer", "BgiCoordinatorServer.csproj")))
                repo = repo.Parent;
            Assert.NotNull(repo);
            var serverDll = Environment.GetEnvironmentVariable("NEXUSBGI_TEST_COORDINATOR_DLL")
                ?? Path.Combine(repo!.FullName, "BgiCoordinatorServer", "bin", "Debug", "net8.0", "BgiCoordinatorServer.dll");
            Assert.True(File.Exists(serverDll), "先使用 DeployToBgiTools=false 构建本次源码的 Debug server。");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            var url = "http://127.0.0.1:" + port;
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.Combine(repo.FullName, "BgiCoordinatorServer")
            };
            start.ArgumentList.Add(serverDll);
            start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
            CoordinatorEntryServerData.Configure(start, "native-entry");
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var owned = new IsolatedCoordinator(process, url);
            process.OutputDataReceived += (_, e) => { if (e.Data != null) owned._output.Enqueue(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) owned._output.Enqueue(e.Data); };
            Assert.True(process.Start());
            process.BeginOutputReadLine(); process.BeginErrorReadLine();
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (DateTime.UtcNow < deadline)
                {
                    Assert.False(process.HasExited, owned.Output);
                    try { using var response = await http.GetAsync(url + "/health"); if (response.IsSuccessStatusCode) return owned; }
                    catch (HttpRequestException) { }
                    catch (TaskCanceledException) { }
                    await Task.Delay(150);
                }
                throw new TimeoutException("隔离服务器未就绪：" + owned.Output);
            }
            catch { await owned.DisposeAsync(); throw; }
        }

        private bool _disposed;
        public void StopOwnedProcess()
        {
            // Only this Process.Start instance; callable outside the STA if its cleanup gets stuck.
            lock (_process)
                if (!_disposed && !_process.HasExited) _process.Kill(entireProcessTree: false);
        }

        public async ValueTask DisposeAsync()
        {
            StopOwnedProcess();
            await _process.WaitForExitAsync();
            await File.WriteAllTextAsync(Path.Combine(AssistantDataDirectory.Root, "native-entry-coordinator.log"), string.Join("\n", _output));
            lock (_process) { _disposed = true; _process.Dispose(); }
        }
    }
}
