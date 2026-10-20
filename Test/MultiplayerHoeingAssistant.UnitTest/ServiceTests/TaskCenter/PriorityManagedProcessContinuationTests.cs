using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class PriorityManagedProcessContinuationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "priority-managed-process-" + Guid.NewGuid().ToString("N"));
    private readonly List<TaskCenterHost> _hosts = [];
    private readonly List<OnlineHoeingBatch> _batches = [];
    private readonly List<BgiExternalClient> _clients = [];

    private sealed class OwnedProcess : IDisposable
    {
        private readonly Process _process;
        internal BgiEpoch Epoch { get; }
        private OwnedProcess(Process process)
        {
            _process = process;
            Epoch = new() { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
        }
        internal static async Task<OwnedProcess> StartAsync()
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,
                "WindowsPowerShell", "v1.0", "powershell.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            start.ArgumentList.Add("-NoLogo"); start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-Command");
            start.ArgumentList.Add("[Console]::WriteLine('READY'); [Console]::Out.Flush(); Start-Sleep -Seconds 120");
            var owned = new OwnedProcess(Process.Start(start) ?? throw new InvalidOperationException("owned process did not start"));
            try
            {
                Assert.Equal("READY", await owned._process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
                return owned;
            }
            catch { owned.Dispose(); throw; }
        }
        internal void Exit()
        {
            if (!_process.HasExited)
            {
                Assert.Equal(Epoch.ProcessId, _process.Id);
                Assert.Equal(Epoch.StartTicksUtc, _process.StartTime.ToUniversalTime().Ticks);
                try { _process.Kill(); }
                catch (InvalidOperationException) when (_process.HasExited) { }
            }
            Assert.True(_process.WaitForExit(5000));
        }
        public void Dispose() { Exit(); _process.Dispose(); }
    }

    // This port supplies executor facts. Authority validation, freezing, send and exit observation
    // all run through the production BgiWorkflowExecutionBoundary (RequiresStopAuthority = true).
    private sealed class Port(BgiEpoch epoch) : IBgiExecutionPort
    {
        private readonly ConcurrentDictionary<string, BgiJobInfo> _jobs = new();
        internal readonly ConcurrentQueue<JsonElement> Starts = new();
        internal int FenceQueries;
        private int _bAttempts;
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch ServerEpoch => epoch;
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (operation == WorkflowStopAuthority.Operation)
            {
                Interlocked.Increment(ref FenceQueries);
                return Task.FromResult(new BgiExternalResponse { Success = true, Data = JsonSerializer.Serialize(new
                {
                    bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                    stopVersion = 0, monotonicFrequency = Stopwatch.Frequency,
                }) });
            }
            Assert.Equal(BgiExternalClient.ExternalOperations.TaskStart, operation);
            var json = JsonSerializer.Serialize(payload);
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            Starts.Enqueue(data);
            Assert.Equal(0, data.GetProperty("expectedStopVersion").GetInt64());
            Assert.Equal(epoch.ProcessId, data.GetProperty("bgiEpoch").GetProperty("processId").GetInt32());
            Assert.Equal(epoch.StartTicksUtc, data.GetProperty("bgiEpoch").GetProperty("startTicksUtc").GetInt64());
            var node = data.GetProperty("nodeId").GetString();
            var failed = node == "B" && Interlocked.Increment(ref _bAttempts) == 1;
            var jobId = Guid.NewGuid().ToString("N");
            _jobs[jobId] = new()
            {
                JobId = jobId, Epoch = epoch, IdempotencyKey = data.GetProperty("idempotencyKey").GetString(),
                WorkflowRunId = data.GetProperty("workflowRunId").GetString(), NodeId = node,
                Iteration = data.GetProperty("iteration").GetInt32(), Occurrence = data.GetProperty("occurrence").GetInt32(),
                Attempt = data.GetProperty("attempt").GetInt32(), State = failed ? "failed" : "succeeded",
                ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
                RequestFingerprintVersion = 1, RequestFingerprint = BgiOriginalRequestFingerprint.Compute(operation, json),
                RequestOperation = operation, ConfigRevision = data.GetProperty("expectedConfigRevision").GetString(),
                TaskId = data.GetProperty("taskId").ValueKind == JsonValueKind.String ? data.GetProperty("taskId").GetString() : null,
            };
            return Task.FromResult(new BgiExternalResponse { Success = true,
                Data = JsonSerializer.Serialize(new { status = "queued", taskHandle = jobId }) });
        }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
            => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = epoch, Jobs = _jobs.Values.ToArray() });
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult(_jobs.TryGetValue(jobId, out var job)
                ? ((string?)job.State, (BgiJobInfo?)job) : ((string?)"not_found", (BgiJobInfo?)null));
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("recovery must not cancel an unrelated replacement job");
    }
    private sealed class Prerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }
    private sealed class Terminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed("unused"));
    }

    private (TaskCenterHost Host, OnlineHoeingBatch Batch, WorkflowRunRecord Run, Port Port) Arrange(
        BgiEpoch previous, bool admissionWired = false, Action? beforeCreateRunner = null)
    {
        using var current = Process.GetCurrentProcess();
        var replacement = new BgiEpoch { ProcessId = current.Id, StartTicksUtc = current.StartTime.ToUniversalTime().Ticks };
        var port = new Port(replacement);
        var client = new BgiExternalClient(); // Never started: production adapter assembly only; executor facts use the existing Port seam.
        _clients.Add(client);
        Func<BgiExternalClient?, WorkflowStore, RunStore, WorkflowRunner>? runnerFactory = admissionWired ? null
            : (_, workflows, runs) =>
            {
                beforeCreateRunner?.Invoke();
                return new WorkflowRunner(workflows, runs, new BgiWorkflowExecutionBoundary(port, runs),
                    new Prerequisite(), new Terminal());
            };
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => client, null, runnerFactory, () => (true, null),
            localExecutionCapability: () => true,
            statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true,
                TaskStatusBgiEpoch = WorkflowStopAuthority.Epoch(replacement),
                TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
            admissionWired: admissionWired, successorAdmissionWired: admissionWired,
            admissionSeams: admissionWired ? new TaskCenterAdmissionSeams
            {
                Epoch = WorkflowStopAuthority.Epoch(replacement), F11Active = false, Occupied = false, FactsUnknown = false,
                ProductionBoundaryFactory = (_, runs) => new BgiWorkflowExecutionBoundary(port, runs),
            } : null,
            onlineEpochProvider: () => replacement);
        _hosts.Add(host);
        if (admissionWired) host.EnsureRecovered(); // Bind the real RunStore owner before recording the original accepted arrangement.
        var workflow = new WorkflowDocument
        {
            Name = "原B重试后继续C", Activation = new() { Status = "active" },
            Nodes = new[] { "B", "C" }.Select(id => new WorkflowNode
                { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id, ConfigKey = id + "#k", Revision = "rev-1" } }).ToList(),
        };
        host.Workflows.Save(workflow, null);
        var run = host.Runs.CreateRun(workflow.WorkflowId!, host.Workflows.LoadSnapshot(workflow.WorkflowId!).Revision,
            handoff: admissionWired ? new HandoffIdentity { IntentKey = "original-handoff", ExecutionId = "original-execution",
                StepId = "original-step", Mode = StartupHandoffModes.Start } : null,
            admissionSourceScope: admissionWired ? "bgi:local:" + WorkflowStopAuthority.Epoch(previous) : null,
            stopAuthority: new(WorkflowStopAuthority.Epoch(previous)!, 7, "original-intent", 10, Stopwatch.Frequency));
        run.State = WorkflowRunState.OnlineHoeingParking;
        run.Cursor = new() { NodeId = "B", Attempt = 1 };
        run.TriggerConsumed = true;
        run.CurrentSubmission = new()
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1), NodeId = "B", Attempt = 1,
            Epoch = WorkflowStopAuthority.Epoch(previous), WireRunId = run.WireRunId,
            JobId = Guid.NewGuid().ToString("D"), Intent = SubmitIntentState.Accepted, SendAttempted = true,
            Fingerprint = "original-frozen", ExpiresAtUtc = "2030-01-01T00:00:00Z",
            ObservedTerminal = "cancelled", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "cancelled",
        };
        run.OnlineHoeingHandoff = new()
        {
            StateContinuation = true, PriorityRecovery = true, Generation = 7, Ticket = "original-ticket",
            Epoch = WorkflowStopAuthority.Epoch(previous)!, NodeId = "B", Attempt = 1, Phase = "parked",
            OriginalJobId = run.CurrentSubmission.JobId, OriginalSubmissionKey = run.CurrentSubmission.Key,
        };
        host.Runs.Update(run);
        var batch = new OnlineHoeingBatch(new("managed-process", 7, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] },
        }, "self", new() { Policy = TaskConflictPolicy.Resume })
        {
            ManagedRunId = run.RunId, ExecutionEpoch = previous, StateContinuationEnabled = true,
            StateContinuationStopVersion = 7, TakeoverTicket = "original-ticket",
        };
        batch.EnsureRecoveryStopSignal(); batch.MarkLocalSuccess(previous);
        _batches.Add(batch);
        return (host, batch, run, port);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActualOwnedExecutorExitContinuesOriginalRunnerBRetryThenCWithOriginalStopIntent(bool admissionWired)
    {
        using var previous = await OwnedProcess.StartAsync();
        var prepared = Arrange(previous.Epoch, admissionWired);
        previous.Exit();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Assert.True(await prepared.Host.ContinuePriorityPlanOnReplacementAsync(prepared.Batch, previous.Epoch,
            prepared.Port.ServerEpoch, budget.Token));
        WorkflowRunRecord? finished = null;
        while (!(finished = prepared.Host.Runs.Load(prepared.Run.RunId))!.IsTerminal)
            await Task.Delay(10, budget.Token);
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(prepared.Run.RunId, finished.RunId);
        Assert.Equal(prepared.Run.WireRunId, finished.WireRunId);
        Assert.Equal(prepared.Run.StopAuthority, finished.StopAuthority);
        var continuation = Assert.Single(finished.PriorityProcessContinuations);
        Assert.Equal(WorkflowStopAuthority.Epoch(previous.Epoch), continuation.PreviousEpoch);
        Assert.Equal(prepared.Run.StopAuthority!.IntentId, continuation.Authority.IntentId);
        Assert.Equal(prepared.Run.StopAuthority.IntentTimestamp, continuation.Authority.IntentTimestamp);
        Assert.Equal(WorkflowStopAuthority.Epoch(prepared.Port.ServerEpoch), continuation.Authority.Epoch);
        var starts = prepared.Port.Starts.ToArray();
        Assert.Equal(new[] { "B", "B", "C" }, starts.Select(s => s.GetProperty("nodeId").GetString()));
        Assert.Equal(new[] { 2, 3, 1 }, starts.Select(s => s.GetProperty("attempt").GetInt32()));
        Assert.Equal(3, starts.Select(s => s.GetProperty("idempotencyKey").GetString()).Distinct().Count());
        Assert.All(starts, s => Assert.Equal(prepared.Run.WireRunId, s.GetProperty("workflowRunId").GetString()));
        Assert.True(prepared.Port.FenceQueries > 0);
        Assert.Single(prepared.Host.Runs.List());
        if (admissionWired)
        {
            Assert.True(prepared.Host.SuccessorAdmissionWiredForTest);
            Assert.Equal(prepared.Run.AdmissionParentSource, finished.AdmissionParentSource);
            Assert.Equal("bgi:local:" + WorkflowStopAuthority.Epoch(previous.Epoch), finished.AdmissionSourceScope);
            var lease = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
            Assert.Equal(ArbitrationLeaseStatus.Valid, lease.Status);
            var nodeOperations = lease.File!.Handoff!.Operations
                .Concat(lease.File.Handoff.ArchivedOperations.Select(a => a.Operation))
                .Where(op => op.OperationType == OperationType.NodeExecution && op.RunBinding == finished.RunId).ToArray();
            Assert.Equal(3, nodeOperations.Length);
            Assert.All(nodeOperations, op =>
            {
                Assert.Equal(WorkflowStopAuthority.Epoch(prepared.Port.ServerEpoch), op.TargetEpoch);
                Assert.Equal(finished.AdmissionSourceScope, op.Candidate!.Scope);
                Assert.Equal(finished.AdmissionParentSource, op.ParentSource);
                Assert.Equal(1, op.LastSendSeq);
            });
        }
    }

    [Theory]
    [InlineData("living_executor")]
    [InlineData("shared_stop")]
    [InlineData("record_stop")]
    public async Task LivingOriginalOrExplicitStopCannotStartReplacement(string condition)
    {
        using var previous = await OwnedProcess.StartAsync();
        var prepared = Arrange(previous.Epoch);
        if (condition != "living_executor") previous.Exit();
        if (condition == "shared_stop") prepared.Batch.RecoveryStopSignal!.Stop();
        if (condition == "record_stop") prepared.Host.Runs.UpdateMergingIf(prepared.Run.RunId,
            run => { run.StopRequested = true; return true; }, out _);
        Assert.False(await prepared.Host.ContinuePriorityPlanOnReplacementAsync(prepared.Batch, previous.Epoch,
            prepared.Port.ServerEpoch, CancellationToken.None));
        Assert.Empty(prepared.Port.Starts);
        Assert.Empty(prepared.Host.Runs.Load(prepared.Run.RunId)!.PriorityProcessContinuations);
    }

    [Fact]
    public async Task TemporaryRunnerConstructionFailureReleasesReservationAndRetriesSameRunOnce()
    {
        using var previous = await OwnedProcess.StartAsync();
        var factoryCalls = 0;
        var prepared = Arrange(previous.Epoch, beforeCreateRunner: () =>
        {
            if (Interlocked.Increment(ref factoryCalls) == 1) throw new IOException("temporary runner construction failure");
        });
        previous.Exit();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<IOException>(() => prepared.Host.ContinuePriorityPlanOnReplacementAsync(
            prepared.Batch, previous.Epoch, prepared.Port.ServerEpoch, budget.Token));
        Assert.Empty(prepared.Port.Starts);
        Assert.True(await prepared.Host.ContinuePriorityPlanOnReplacementAsync(
            prepared.Batch, previous.Epoch, prepared.Port.ServerEpoch, budget.Token));
        WorkflowRunRecord? finished = null;
        while (!(finished = prepared.Host.Runs.Load(prepared.Run.RunId))!.IsTerminal)
            await Task.Delay(10, budget.Token);
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(2, factoryCalls);
        Assert.Equal(prepared.Run.RunId, finished.RunId);
        Assert.Equal(prepared.Run.WireRunId, finished.WireRunId);
        Assert.Equal(prepared.Run.StopAuthority, finished.StopAuthority);
        Assert.Single(finished.PriorityProcessContinuations);
        var starts = prepared.Port.Starts.ToArray();
        Assert.Equal(new[] { "B", "B", "C" }, starts.Select(s => s.GetProperty("nodeId").GetString()));
        Assert.Equal(new[] { 2, 3, 1 }, starts.Select(s => s.GetProperty("attempt").GetInt32()));
        Assert.Equal(3, starts.Select(s => s.GetProperty("idempotencyKey").GetString()).Distinct().Count());
        Assert.Single(prepared.Host.Runs.List());
    }

    public void Dispose()
    {
        foreach (var host in _hosts) host.ShutdownAsync().GetAwaiter().GetResult();
        foreach (var batch in _batches) batch.RecoveryStopSignal?.Dispose();
        foreach (var client in _clients) client.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
