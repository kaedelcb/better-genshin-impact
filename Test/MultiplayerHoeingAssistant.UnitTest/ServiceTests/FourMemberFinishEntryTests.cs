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
/// Four real SignalR connections and the production local MainVM/CommandExecutor entry.
/// Only BGI is replaced by a private named-pipe protocol peer; no game/user process is operated.
/// Run alone in a fresh testhost with a unique explicit NEXUSBGI_DATA_ROOT.
/// </summary>
[Collection("TaskCenterPipeTransport")]
public sealed class FourMemberFinishEntryTests
{
    [Fact]
    public async Task CompletedLocalMemberCanStartManualTaskWhilePeerTailRuns_AndLateTeamFinishCannotStopIt()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "隔离入口必须显式设置 NEXUSBGI_DATA_ROOT。");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        // WPF permits one Application per testhost. Both independent four-member
                        // scenarios use this STA, each with its own server, room and private pipe.
                        await RunEntryAsync(peerFails: true, reconnectAfterLocalFinish: false);
                        await RunEntryAsync(peerFails: false, reconnectAfterLocalFinish: true);
                        finished.TrySetResult();
                    }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "FourMemberFinishEntry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(4));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离 Dispatcher 未退出。");
    }

    private static async Task RunEntryAsync(bool peerFails, bool reconnectAfterLocalFinish)
    {
        Directory.CreateDirectory(AssistantDataDirectory.Root);
        string[] uids = ["100000011", "100000012", "100000013", "100000014"];
        const string boundGroup = "隔离联机主体和后置";
        const string manualGroup = "隔离完成后的手动任务";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        var caseName = peerFails ? "peer-abort" : "reconnect-peer-completed";
        await using var server = await IsolatedCoordinator.StartAsync(caseName);
        await using var pipe = new BgiInstancePipeDouble();
        var bgi = new FinishBgiPeer(boundGroup, manualGroup);
        pipe.BodyForTest = bgi.Reply;
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var external = new BgiExternalClient(Path.GetDirectoryName(Environment.ProcessPath));
        using var monitor = new BgiProcessMonitor(Environment.ProcessPath!);
        await using var originalLocal = new SignalRClient();
        SignalRClient local = originalLocal;
        SignalRClient? reconnected = null;
        await using var second = new SignalRClient();
        await using var third = new SignalRClient();
        await using var tail = new SignalRClient();
        using var peersStop = new CancellationTokenSource();
        var peers = new[] { new ControlledPeer(second, uids[1], false),
            new ControlledPeer(third, uids[2], false), new ControlledPeer(tail, uids[3], true) };
        Task[] peerTasks = [];
        MainViewModel? vm = null;
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var config = new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "隔离本机完成端",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-finish-only",
                ClientInstanceId = Guid.NewGuid().ToString("N"), BgiPath = Environment.ProcessPath!,
                OnlineHoeingGroupNames = [boundGroup], OnlineHoeingGroupTypes = ["group"],
                ExpectedHoeingPlayers = 4, DisclaimerAccepted = true,
            };
            var executor = new CommandExecutor(monitor, config.BgiPath, () => external, logger: vm.AddLog,
                isBatchInFlight: () => vm.HasLocalCoordinatedExecutionResponsibility);
            executor.StopProcessOverrideForTest = _ => throw new InvalidOperationException("隔离验收禁止停止真实进程");
            Set(vm, "_config", config);
            Set(vm, "_externalClient", external);
            Set(vm, "_processMonitor", monitor);
            Set(vm, "_commandExecutor", executor);
            Set(vm, "_signalRClient", local);
            Call(vm, "WireSignalRClient", local);
            var room = "FINISH-" + Guid.NewGuid().ToString("N")[..8];
            SignalRClient[] clients = [local, second, third, tail];
            for (var i = 0; i < clients.Length; i++)
            {
                await clients[i].ConnectAsync(server.Url, room, config.ControlRoomPassword,
                    uids[i], "隔离成员" + i, uids.ToList(),
                    clientInstanceId: i == 0 ? config.ClientInstanceId : Guid.NewGuid().ToString("N"),
                    bypassSystemProxy: true);
                Assert.True(clients[i].SupportsCoordinatedStart);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = "隔离成员" + i, BgiStatus = "running",
                    ConfigGroups = [boundGroup, manualGroup], OnlineHoeingGroupNames = [boundGroup],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = new("stop", "group", "", 0),
                });
            }
            peerTasks = peers.Select(p => p.RunAsync(peersStop.Token)).ToArray();
            var started = await local.BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1), useCompletionActions: false);
            await WaitAsync(() => bgi.CooperativeStarts == 1 && Active(vm)?.Phase == "running"
                && peers.All(p => p.Last?.Phase == "running"), () => Diagnostics(vm, bgi, peers, server));
            var oldBatch = Active(vm)!;
            var cooperativeJob = bgi.CooperativeJobId!;
            Assert.True(oldBatch.TakenOver);

            bgi.CompleteCooperative();
            await WaitAsync(() => oldBatch.LocalExecutionReleased && executor.TakeoverTicketSnapshot == null,
                () => Diagnostics(vm, bgi, peers, server));
            // Local release is not a server ACK. Wait for a real peer response containing our result.
            await WaitAsync(() => peers.Any(p => p.Last?.Members.Any(m => m.Uid == uids[0]
                    && m.Result == "succeeded") == true), () => Diagnostics(vm, bgi, peers, server));
            var waiting = await local.GetCurrentCoordinatedBatchAsync();
            Assert.Equal("running", waiting!.Phase);
            Assert.Equal("succeeded", waiting.Members.Single(m => m.Uid == uids[0]).Result);
            Assert.Equal("", waiting.Members.Single(m => m.Uid == uids[3]).Result);
            Assert.True(oldBatch.LocalSucceeded);
            Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            Assert.Null(external.TakeoverTicket);
            var resumesBeforeManual = pipe.CountOf("task.resume");
            Assert.Equal(1, resumesBeforeManual);

            // The real remote-command dispatcher, not a direct fake-start call, starts the new job.
            await second.SendRemoteCommandAsync(new RemoteCommand
            {
                Cmd = "start_group", Target = [uids[0]], CommandId = Guid.NewGuid().ToString("N"),
                Params = new() { ["groupName"] = manualGroup },
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
            });
            await WaitAsync(() => bgi.ManualStarts == 1, () => Diagnostics(vm, bgi, peers, server));
            var manualJob = bgi.ManualJobId!;
            Assert.NotEqual(cooperativeJob, manualJob);
            Assert.True(bgi.ManualRunning);
            Assert.True(oldBatch.LocalExecutionReleased);

            if (reconnectAfterLocalFinish)
            {
                // Force a real transport loss. The old production loop must finish on its own;
                // a replacement client retains the same executor identity and the same old batch.
                await local.DisposeAsync();
                await WaitAsync(() => oldBatch.RunTask?.IsCompleted == true,
                    () => Diagnostics(vm, bgi, peers, server));
                await Task.Delay(TimeSpan.FromSeconds(31));
                Assert.True(oldBatch.LocalSucceeded);
                Assert.True(oldBatch.LocalExecutionReleased);
                Assert.True(bgi.ManualRunning);
                reconnected = new SignalRClient();
                local = reconnected;
                Set(vm, "_signalRClient", local);
                Call(vm, "WireSignalRClient", local);
                await local.ConnectAsync(server.Url, room, config.ControlRoomPassword,
                    uids[0], config.PlayerName, uids.ToList(), clientInstanceId: config.ClientInstanceId,
                    bypassSystemProxy: true);
                await local.ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[0], PlayerName = config.PlayerName, BgiStatus = "running",
                    ConfigGroups = [boundGroup, manualGroup], OnlineHoeingGroupNames = [boundGroup],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = new("stop", "group", "", 0),
                });
                var restored = await local.GetCurrentCoordinatedBatchAsync();
                Assert.Equal(started.BatchId, restored!.BatchId);
                Assert.Equal("running", restored.Phase);
                Assert.Same(oldBatch, Active(vm));
                Assert.True(oldBatch.RunTask!.IsCompleted);
                Assert.False(vm.HasLocalCoordinatedExecutionResponsibility);
            }

            // The real server either aborts on the unfinished peer's error or completes normally.
            var ackGate = (SemaphoreSlim)typeof(MainViewModel).GetField("_coordinatedCleanupAckGate",
                BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
            if (reconnectAfterLocalFinish) await ackGate.WaitAsync();
            var terminalPhase = peerFails ? "aborted" : "completed";
            try
            {
                peers[2].ReportUnknown = peerFails;
                peers[2].CompleteTail = !peerFails;
                if (reconnectAfterLocalFinish)
                {
                    // The only completed notification arrives while a preceding cleanup owns the gate.
                    // It must remain queued, not be dropped with a zero-time semaphore wait.
                    await WaitAsync(() => peers[2].Last?.Phase == terminalPhase,
                        () => Diagnostics(vm, bgi, peers, server));
                    Assert.Same(oldBatch, Active(vm));
                }
            }
            finally { if (reconnectAfterLocalFinish) ackGate.Release(); }
            await WaitAsync(() => peers[2].Last?.Phase == terminalPhase && Active(vm) == null,
                () => Diagnostics(vm, bgi, peers, server));
            var terminal = (await local.GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal(started.BatchId, terminal.BatchId);
            Assert.Equal(terminalPhase, terminal.Phase);
            Assert.True(oldBatch.LocalSucceeded);
            Assert.Equal("succeeded", oldBatch.Outcome);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(0, bgi.CancelCount);

            // Cleanup ACK over the real connection, plus replay of already received old envelopes.
            await peers[2].SendDelayedCleanupAsync(terminal);
            Replay(local, GatewayProtocol.Events.ControlBatchState, terminal);
            Replay(local, GatewayProtocol.Events.ControlBatchState, terminal with { CleanupComplete = true });
            Replay(local, GatewayProtocol.Events.ControlBatchStart, started);
            await Task.Delay(1300);
            Assert.Null(Active(vm));
            Assert.Equal(manualJob, bgi.ManualJobId);
            Assert.True(bgi.ManualRunning);
            Assert.Equal(1, bgi.ManualStarts);
            Assert.Equal(1, bgi.CooperativeStarts);
            Assert.Equal(0, bgi.CancelCount);
            Assert.Equal(0, pipe.CountOf("ext.task.stop"));
            Assert.Equal(resumesBeforeManual, pipe.CountOf("task.resume"));

            // End only the private protocol job so its production waiter also finishes cleanly.
            bgi.CompleteManual();
            await Task.Delay(300);
        }
        finally
        {
            peersStop.Cancel();
            try { await Task.WhenAll(peerTasks).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
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
            if (reconnected != null) await reconnected.DisposeAsync();
        }
    }

    private sealed class ControlledPeer(SignalRClient client, string uid, bool hasLongTail)
    {
        private readonly string _token = Guid.NewGuid().ToString("N");
        public volatile bool ReportUnknown;
        public volatile bool CompleteTail;
        public CoordinatedBatchSnapshot? Last { get; private set; }
        public string? Error { get; private set; }
        public async Task RunAsync(CancellationToken ct)
        {
            string? batchId = null;
            var registered = false;
            var stage = "registered";
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var current = await client.GetCurrentCoordinatedBatchAsync(ct);
                    if (current != null)
                    {
                        if (batchId != current.BatchId) { batchId = current.BatchId; registered = false; stage = "registered"; }
                        var result = "";
                        if (registered)
                        {
                            if (current.Phase == "preparing") stage = "prepared";
                            if (current.Phase == "takeover") stage = "takeoverConfirmed";
                            if (current.Phase == "running") result = ReportUnknown ? "unknown"
                                : hasLongTail && !CompleteTail ? "" : "succeeded";
                            if (current.Phase is "completed" or "aborted") stage = "cleaned";
                        }
                        Last = await client.UpdateCoordinatedBatchAsync(current.Generation, _token, current.BatchId,
                            "group", current.Index, current.Attempt, result, ct, stage,
                            ReportUnknown ? "controlled_peer_after_body_failed" : "");
                        registered = true;
                    }
                    await Task.Delay(150, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception error) { Error = uid + ":" + error; throw; }
        }
        public Task<CoordinatedBatchSnapshot> SendDelayedCleanupAsync(CoordinatedBatchSnapshot snapshot)
            => client.UpdateCoordinatedBatchAsync(snapshot.Generation, _token, snapshot.BatchId,
                "group", snapshot.Index, snapshot.Attempt, "", CancellationToken.None, "cleaned");
    }

    private sealed class FinishBgiPeer(string boundGroup, string manualGroup)
    {
        private sealed record Job(string Id, string Key, string Name, bool Cooperative)
        { public bool Completed; public bool Cancelled; }
        private readonly object _gate = new();
        private readonly List<Job> _jobs = [];
        private string? _ticket;
        private int _cancelCount;
        private readonly int _pid = Environment.ProcessId;
        private readonly long _ticks = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
        private object Epoch => new { processId = _pid, startTicksUtc = _ticks };
        public int CooperativeStarts { get { lock (_gate) return _jobs.Count(j => j.Cooperative); } }
        public int ManualStarts { get { lock (_gate) return _jobs.Count(j => !j.Cooperative); } }
        public string? CooperativeJobId { get { lock (_gate) return _jobs.FirstOrDefault(j => j.Cooperative)?.Id; } }
        public string? ManualJobId { get { lock (_gate) return _jobs.FirstOrDefault(j => !j.Cooperative)?.Id; } }
        public bool ManualRunning { get { lock (_gate) return _jobs.Any(j => !j.Cooperative && !j.Completed && !j.Cancelled); } }
        public int CancelCount { get { lock (_gate) return _cancelCount; } }
        public void CompleteCooperative() { lock (_gate) _jobs.Single(j => j.Cooperative).Completed = true; }
        public void CompleteManual() { lock (_gate) _jobs.Single(j => !j.Cooperative).Completed = true; }
        private static string? Value(JsonElement? data, string name) => data is { } d && d.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        public (bool, string?, string?, object?)? Reply(string operation, JsonElement? data)
        {
            lock (_gate)
            {
                var active = _jobs.LastOrDefault(j => !j.Completed && !j.Cancelled);
                switch (operation)
                {
                    case "ext.hello":
                        return (true, null, null, new
                        {
                            bgiVersion = "isolated-four-member-finish", sessionId = "private-pipe-only",
                            windowsSessionId = Process.GetCurrentProcess().SessionId, processId = _pid, bgiEpoch = Epoch,
                            capabilities = new Dictionary<string, bool>
                            {
                                ["event.push"] = true, ["task.queue"] = true, ["task.takeover"] = true,
                                ["hoeing.batchOutcome.v1"] = true, ["hoeing.coordinatedStart.v1"] = true,
                                ["hoeing.finishEvidence.v1"] = true,
                                ["execution.targetedSuspend.v1"] = true, ["config.applied"] = true,
                            }
                        });
                    case "task.status":
                    case "ext.task.status":
                        var queriedRoot = Value(data, "executionInstanceId");
                        var exited = _jobs.Any(j => j.Id == queriedRoot && (j.Completed || j.Cancelled));
                        return (true, null, null, new
                        {
                            running = active != null, taskRunning = active != null,
                            autoHoeingRunning = active?.Cooperative == true,
                            autoHoeingMultiplayer = active?.Cooperative == true ? (bool?)true : null,
                            slotOccupied = active != null, executionSlotOccupied = active != null,
                            executionIdentityAvailable = active != null, executionInstanceId = active?.Id,
                            executionJobId = active?.Id, executionStateRevision = 1, stateRevision = 1,
                            executionIdle = active == null, executionExitConfirmed = exited,
                            executionExitQueryInstanceId = queriedRoot, executionExitAtUtc = DateTime.UtcNow,
                            executionExitOrder = 1L, executionExitReason = exited ? "confirmed" : active == null ? "no_active_root" : "not_exited",
                            bgiEpoch = Epoch, takeoverActive = _ticket != null, hasSuspendedTaskContext = false,
                            queueDepth = 0, currentTaskHandle = active?.Id,
                        });
                    case "ext.config.list":
                        return (true, null, null, new { configGroups = new[] { boundGroup, manualGroup }, oneClickConfigs = Array.Empty<string>() });
                    case "ext.config.describe":
                        return (true, null, null, new { bgiEpoch = Epoch, configRevision = "finish-entry-r1",
                            tasks = new[] { new { name = "隔离协议任务", enabled = true } } });
                    case "ext.job.list":
                        return (true, null, null, new { bgiEpoch = Epoch, jobs = _jobs.Select(j => new
                        {
                            jobId = j.Id, idempotencyKey = j.Key, name = j.Name, kind = "group",
                            state = j.Cancelled ? "cancelled" : j.Completed ? "succeeded" : "running", wasCancelled = j.Cancelled,
                            exitConfirmed = j.Completed || j.Cancelled, exitDisposition = j.Completed || j.Cancelled ? "terminal" : null,
                        }).ToArray() });
                    case "task.suspend":
                        if (active != null || Value(data, "takeoverTicket") is not { } ticket)
                            return (false, "not_current", "替身只接受空闲接管", null);
                        _ticket = ticket;
                        return (true, null, null, new { takeoverTicket = ticket, quiesceConfirmed = true,
                            liveTask = false, expectedExecutionInstanceId = (string?)null, expectedStateRevision = (long?)null });
                    case "ext.task.start":
                        var cooperative = Value(data, "coordinatedHoeing") == "v1";
                        if (active != null || cooperative && (_ticket == null || Value(data, "takeoverTicket") != _ticket)
                            || !cooperative && _ticket != null)
                            return (false, "task_busy", "新任务不能借用旧票据或覆盖活动任务", null);
                        var job = new Job(Guid.NewGuid().ToString("N"), Value(data, "idempotencyKey")!,
                            Value(data, "groupName")!, cooperative);
                        _jobs.Add(job);
                        return (true, null, null, new { status = "queued", taskHandle = job.Id, queuePosition = 0 });
                    case "ext.task.queueStatus":
                        var queried = _jobs.SingleOrDefault(j => j.Id == Value(data, "taskHandle"));
                        return queried == null ? (false, "not_found", "未知句柄", null) : (true, null, null, new
                        {
                            status = queried.Completed || queried.Cancelled ? "completed" : "running",
                            cancelled = queried.Cancelled, errorCode = queried.Cancelled ? "user_cancelled" : null,
                            hoeingEvidence = queried.Cooperative ? new
                            {
                                started = 1, completed = queried.Completed ? 1 : 0, failed = 0,
                                cancelled = queried.Cancelled ? 1 : 0, active = queried.Completed || queried.Cancelled ? 0 : 1,
                                @sealed = queried.Completed || queried.Cancelled,
                                jobResult = queried.Completed ? "Ran" : queried.Cancelled ? "Cancelled" : null, revision = 3,
                            } : null,
                        });
                    case "ext.task.cancel":
                        var owned = _jobs.SingleOrDefault(j => j.Id == Value(data, "taskHandle"));
                        if (owned == null || Value(data, "ownedOnly") != "v1") return (false, "invalid_request", "只允许本句柄取消", null);
                        if (!owned.Cancelled) { owned.Cancelled = true; _cancelCount++; }
                        return (true, null, null, new { status = "cancel_requested" });
                    case "task.resume":
                        if (active != null || _ticket == null || Value(data, "takeoverTicket") != _ticket)
                            return (false, "stale_ticket", "替身收尾条件不满足", null);
                        _ticket = null;
                        return (true, null, null, new { status = "cleared_not_resumed" });
                    case "ext.task.stop":
                        return (false, "invalid_request", "本验收禁止全停止副作用", null);
                    default: return null;
                }
            }
        }
    }

    private static OnlineHoeingBatch? Active(MainViewModel vm) =>
        (OnlineHoeingBatch?)typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
    private static void Set(MainViewModel vm, string field, object value) =>
        typeof(MainViewModel).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);
    private static object? Call(MainViewModel vm, string method, params object[] args) =>
        typeof(MainViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, args);
    private static void Replay(SignalRClient client, string name, CoordinatedBatchSnapshot snapshot) =>
        typeof(SignalRClient).GetMethod("DispatchEvt", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(client,
            [new GatewayEnvelope { Type = "event", Name = name, Payload = GatewayEnvelope.ToPayload(snapshot) }]);
    private static async Task WaitAsync(Func<bool> predicate, Func<string> diagnostics)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!predicate() && DateTime.UtcNow < deadline) await Task.Delay(80);
        Assert.True(predicate(), diagnostics());
    }
    private static string Diagnostics(MainViewModel vm, FinishBgiPeer bgi, ControlledPeer[] peers, IsolatedCoordinator server)
        => $"local={Active(vm)?.BatchId}/{Active(vm)?.Phase}/{Active(vm)?.Message}; jobs={bgi.CooperativeStarts}/{bgi.ManualStarts}; cancels={bgi.CancelCount}\n"
            + string.Join("\n", peers.Select(p => p.Last?.Phase + "/" + p.Error)) + "\n"
            + string.Join("\n", vm.CommandLogs.Take(30)) + "\nserver=" + server.Output;

    private sealed class IsolatedCoordinator : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly ConcurrentQueue<string> _output = new();
        private readonly string _caseName;
        public string Url { get; }
        public string Output => string.Join("\n", _output.TakeLast(35));
        private IsolatedCoordinator(Process process, string url, string caseName)
        { _process = process; Url = url; _caseName = caseName; }
        public static async Task<IsolatedCoordinator> StartAsync(string caseName)
        {
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo != null && !File.Exists(Path.Combine(repo.FullName, "BgiCoordinatorServer", "BgiCoordinatorServer.csproj"))) repo = repo.Parent;
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
            start.ArgumentList.Add(serverDll); start.ArgumentList.Add("--urls"); start.ArgumentList.Add(url);
            start.Environment["ROOM_LOG_ROOT"] = Path.Combine(Environment.GetEnvironmentVariable("NEXUSBGI_DATA_ROOT")!, "four-member-finish-" + caseName + "-room-logs");
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
            var process = new Process { StartInfo = start, EnableRaisingEvents = true };
            var owned = new IsolatedCoordinator(process, url, caseName);
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
            if (!_process.HasExited) { _process.Kill(entireProcessTree: false); await _process.WaitForExitAsync(); }
            try
            {
                Directory.CreateDirectory(AssistantDataDirectory.Root);
                await File.WriteAllTextAsync(Path.Combine(AssistantDataDirectory.Root, "four-member-finish-" + _caseName + "-coordinator.log"), string.Join("\n", _output));
            }
            catch (Exception error)
            {
                // Diagnostic persistence must never replace the original entry failure.
                Debug.WriteLine("隔离服务器日志保存失败：" + error);
            }
            _process.Dispose();
        }
    }
}
