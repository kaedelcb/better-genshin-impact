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
    [Fact]
    public async Task ProductionEntry_RejectsConcurrentBegin_StopsOnlyExactBatch_AndStartsAgainAfterCleanup()
    {
        Assert.True(AssistantDataDirectory.IsExplicit,
            "必须先设置隔离 NEXUSBGI_DATA_ROOT，禁止在真实 User 上运行入口验证。");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current); // 此真实入口用独立 testhost，不借用其他测试的 Dispatcher。
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try { await RunEntryAsync(); finished.TrySetResult(); }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "UnifiedHoeingNativeEntry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离 WPF Dispatcher 没有退出。");
    }

    private static async Task RunEntryAsync()
    {
        const string uid = "100000001";
        const string groupName = "隔离开锄入口";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        await using var server = await IsolatedCoordinator.StartAsync();
        await using var pipe = new BgiInstancePipeDouble();
        var script = new IdleOwnedBatchPeer(groupName);
        pipe.BodyForTest = script.Reply;
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var external = new BgiExternalClient(Path.GetDirectoryName(Environment.ProcessPath));
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        await using var client = new SignalRClient();
        MainViewModel? vm = null;
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uid, PlayerName = "隔离入口执行端",
                TeamUids = [uid], ControlRoomPassword = "isolated-entry-only",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [groupName], OnlineHoeingGroupTypes = ["group"],
                ExpectedHoeingPlayers = 1, DisclaimerAccepted = true,
            };
            var executor = new CommandExecutor(monitor, config.BgiPath, () => external,
                logger: vm.AddLog, isBatchInFlight: () => Active(vm)?.IsAlive == true);
            Set(vm, "_config", config);
            Set(vm, "_externalClient", external);
            Set(vm, "_processMonitor", monitor);
            Set(vm, "_commandExecutor", executor);
            Set(vm, "_signalRClient", client);
            Call(vm, "WireSignalRClient", client);
            await client.ConnectAsync(server.Url, "NATIVE-" + Guid.NewGuid().ToString("N")[..8],
                config.ControlRoomPassword, uid, config.PlayerName, [uid],
                clientInstanceId: config.ClientInstanceId, bypassSystemProxy: true);
            Assert.True(client.SupportsCoordinatedStart);
            Assert.True(client.SupportsCompletionChoice);
            await client.ReportControlStatusAsync(new ControlStatus
            {
                PlayerUid = uid, PlayerName = config.PlayerName, BgiStatus = "running",
                ConfigGroups = [groupName], OnlineHoeingGroupNames = [groupName],
                OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 1,
                HoeingCompletionAction = new("resume", "group", "", 0),
            });

            var first = await BeginAsync(client, uid);
            await WaitAsync(() => script.StartCount == 1 && Active(vm)?.BatchId == first.BatchId,
                () => Diagnostics(vm, pipe, server));
            var firstActive = Active(vm)!;
            Assert.Equal(TaskConflictPolicy.Stop, firstActive.Policy!.Policy);
            Assert.True(firstActive.Prepared);
            Assert.True(firstActive.TakenOver);
            Assert.Equal("running", firstActive.Phase);
            var firstStart = Assert.Single(pipe.Received.Where(r => r.Operation == "ext.task.start")).Data!.Value;
            Assert.Equal(first.BatchId, firstStart.GetProperty("hoeingBatchId").GetString());
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

            var second = await BeginAsync(client, uid, useActions: true);
            Assert.True(second.UseCompletionActions);
            Assert.NotEqual(first.BatchId, second.BatchId);
            await WaitAsync(() => script.StartCount == 2 && Active(vm)?.BatchId == second.BatchId,
                () => Diagnostics(vm, pipe, server));
            var secondActive = Active(vm)!;
            Assert.Equal(TaskConflictPolicy.Resume, secondActive.Policy!.Policy);
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

            // 上线按钮与一键开始必须进入同一生产批次入口，并携带本机刚武装的精确意图。
            Assert.True(await (Task<bool>)Call(vm, "MarkOnlineAsync", "command")!);
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
        }
        finally
        {
            // 仅向本夹具的私有管道作业收口；不调用 MainVM 通用强制重置或真实进程结束。
            if (vm != null)
            {
                Active(vm)?.Cancel();
                if (Active(vm)?.RunTask is { } pending)
                    try { await pending.WaitAsync(TimeSpan.FromSeconds(45)); } catch { }
                var host = (TaskCenterHost?)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
                Set(vm, "_disposing", true);
                if (host != null) await host.ShutdownAsync();
            }
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExt;
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
    private sealed class IdleOwnedBatchPeer(string groupName)
    {
        private sealed record Job(string Id, string Key, string Name, string BatchId)
        {
            public bool Cancelled;
        }
        private readonly object _gate = new();
        private readonly List<Job> _jobs = [];
        private string? _ticket;
        private int _cancelCount;
        private readonly int _pid = Environment.ProcessId;
        private readonly long _ticks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private object Epoch => new { processId = _pid, startTicksUtc = _ticks };
        public int StartCount { get { lock (_gate) return _jobs.Count; } }
        public int CancelCount { get { lock (_gate) return _cancelCount; } }
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
                            bgiVersion = "isolated-native-entry", sessionId = "private-pipe-only",
                            windowsSessionId = Process.GetCurrentProcess().SessionId, processId = _pid, bgiEpoch = Epoch,
                            capabilities = new Dictionary<string, bool>
                            {
                                ["event.push"] = true, ["task.queue"] = true, ["task.takeover"] = true,
                                ["hoeing.batchOutcome.v1"] = true, ["hoeing.coordinatedStart.v1"] = true,
                                ["execution.targetedSuspend.v1"] = true, ["config.applied"] = true,
                            }
                        });
                    case "task.status":
                    case "ext.task.status":
                        return (true, null, null, new
                        {
                            running = active != null, taskRunning = active != null, autoHoeingRunning = active != null,
                            slotOccupied = active != null, executionSlotOccupied = active != null,
                            executionIdentityAvailable = active != null, executionInstanceId = active?.Id,
                            executionStateRevision = 1, stateRevision = 1, executionIdle = active == null,
                            executionExitConfirmed = false, executionExitReason = active == null ? "no_active_root" : "not_exited",
                            bgiEpoch = Epoch, takeoverActive = _ticket != null, hasSuspendedTaskContext = false,
                        });
                    case "ext.config.list":
                        return (true, null, null, new { configGroups = new[] { groupName }, oneClickConfigs = Array.Empty<string>() });
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
                        _ticket = ticket;
                        return (true, null, null, new { takeoverTicket = ticket, quiesceConfirmed = true,
                            liveTask = false, expectedExecutionInstanceId = (string?)null, expectedStateRevision = (long?)null });
                    case "ext.task.start":
                        if (_ticket == null || Value(data, "takeoverTicket") != _ticket || active != null)
                            return (false, "task_busy", "替身票据或空闲条件不满足", null);
                        var job = new Job(Guid.NewGuid().ToString("N"), Value(data, "idempotencyKey")!,
                            Value(data, "groupName")!, Value(data, "hoeingBatchId")!);
                        _jobs.Add(job);
                        return (true, null, null, new { status = "queued", taskHandle = job.Id, queuePosition = 0 });
                    case "ext.task.queueStatus":
                        var queried = _jobs.SingleOrDefault(j => j.Id == Value(data, "taskHandle"));
                        return queried == null ? (false, "not_found", "未知句柄", null)
                            : (true, null, null, new { status = queried.Cancelled ? "completed" : "running",
                                cancelled = queried.Cancelled, errorCode = queried.Cancelled ? "user_cancelled" : null });
                    case "ext.task.cancel":
                        var owned = _jobs.SingleOrDefault(j => j.Id == Value(data, "taskHandle"));
                        if (owned == null || Value(data, "ownedOnly") != "v1")
                            return (false, "invalid_request", "只能取消本句柄", null);
                        if (!owned.Cancelled) { owned.Cancelled = true; _cancelCount++; }
                        return (true, null, null, new { status = "cancel_requested" });
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
            var serverDll = Path.Combine(repo!.FullName, "BgiCoordinatorServer", "bin", "Debug", "net8.0", "BgiCoordinatorServer.dll");
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
            start.Environment["ROOM_LOG_ROOT"] = Path.Combine(Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT")!, "native-entry-room-logs");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
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

        public async ValueTask DisposeAsync()
        {
            // 只停止本测试 Process.Start 得到的实例；不查找或结束已有服务器/助手/BGI。
            if (!_process.HasExited) { _process.Kill(entireProcessTree: false); await _process.WaitForExitAsync(); }
            var root = Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT")!;
            await File.WriteAllTextAsync(Path.Combine(root, "native-entry-coordinator.log"), string.Join("\n", _output));
            _process.Dispose();
        }
    }
}
