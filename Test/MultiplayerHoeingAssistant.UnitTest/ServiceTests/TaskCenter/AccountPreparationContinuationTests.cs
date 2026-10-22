using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>真实引擎、前置适配器与磁盘台账验证账号准备未操作接续；不访问真实 IPC、游戏或 User。</summary>
public sealed class AccountPreparationContinuationTests : IDisposable
{
    private const string PreparationNotStarted = "account_preparation_not_started";
    private const string Account = "prerequisite.account";
    private const string Redeem = "prerequisite.redeemCode";
    private const string Uid = "123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "account-preparation-" + Guid.NewGuid().ToString("N"));
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public AccountPreparationContinuationTests()
    {
        _workflows = new WorkflowStore(Path.Combine(_dir, "flows"));
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task PreparationNotStarted_ResumesOriginalRunWithoutReplayingCompletedNodeOrSuccessfulPrerequisite()
    {
        var workflowId = Seed(Node("A"), Node("B", Strategy(Redeem), Strategy(Account)));
        var port = new Port();
        var boundary = new BodyBoundary();
        var first = await Runner(port, boundary).StartAsync(workflowId);

        Assert.Equal(WorkflowRunState.Interrupted, first.State);
        Assert.Equal("B", first.Cursor!.NodeId);
        Assert.Single(first.NodeOutcomes, o => o.NodeId == "A" && o.Result == "succeeded");
        Assert.DoesNotContain(first.NodeOutcomes, o => o.NodeId == "B");
        Assert.Equal(new[] { "A" }, boundary.SubmittedNodes);
        var beforeResume = _runs.Load(first.RunId)!;
        Assert.Null(beforeResume.CurrentSubmission);
        Assert.True(AccountPreparationRecovery.CanResume(beforeResume));
        var completedA = Assert.Single(beforeResume.SubmissionHistory, s => s.NodeId == "A");
        Assert.Equal("succeeded", completedA.ObservedTerminal);
        Assert.Equal("succeeded", completedA.EffectState);
        Assert.True(completedA.ExecutionExitConfirmed);
        var firstAccount = Assert.Single(beforeResume.PrerequisiteActions, a => a.Kind == Account);
        Assert.Equal(PreparationNotStarted, firstAccount.ErrorCode);
        Assert.Equal("failed", firstAccount.ObservedTerminal);
        Assert.Equal("failed", firstAccount.EffectState);
        Assert.True(firstAccount.ExecutionExitConfirmed);
        var originalAccountJson = JsonSerializer.Serialize(firstAccount);

        var resumed = await Runner(port, boundary).ResumeAsync(first.RunId);

        Assert.Equal(first.RunId, resumed.RunId);
        Assert.Equal(WorkflowRunState.Succeeded, resumed.State);
        Assert.Equal(new[] { "A", "B" }, boundary.SubmittedNodes);
        Assert.Single(_runs.List());
        Assert.Single(resumed.NodeOutcomes, o => o.NodeId == "A" && o.Result == "succeeded");
        Assert.Single(resumed.NodeOutcomes, o => o.NodeId == "B" && o.Result == "succeeded");
        Assert.Single(port.Sends, s => s.Operation == BgiExternalClient.ExternalOperations.PrerequisiteRedeemCode);
        var accountSends = port.Sends.Where(s => s.Operation == BgiExternalClient.ExternalOperations.PrerequisiteAccount).ToList();
        Assert.Equal(new[] { 1, 2 }, accountSends.Select(s => s.Payload.GetProperty("attempt").GetInt32()));
        Assert.NotEqual(accountSends[0].Payload.GetProperty("idempotencyKey").GetString(),
            accountSends[1].Payload.GetProperty("idempotencyKey").GetString());
        Assert.All(accountSends, s => Assert.Equal(first.WireRunId, s.Payload.GetProperty("workflowRunId").GetString()));
        var persisted = _runs.Load(first.RunId)!;
        var accounts = persisted.PrerequisiteActions.Where(a => a.Kind == Account).OrderBy(a => a.Attempt).ToList();
        Assert.Equal(new[] { 1, 2 }, accounts.Select(a => a.Attempt));
        Assert.Equal(originalAccountJson, JsonSerializer.Serialize(accounts[0]));
        Assert.Equal(PrerequisiteActionState.Succeeded, accounts[1].State);
        Assert.Single(persisted.PrerequisiteActions, a => a.Kind == Redeem && a.State == PrerequisiteActionState.Succeeded);
    }

    [Fact]
    public async Task ReconcileOriginalAccountAsNotStarted_LeavesInterruptedWithoutSendingAnotherAttempt()
    {
        var workflowId = Seed(Node("B", Strategy(Account)));
        var run = _runs.CreateRun(workflowId, _workflows.LoadSnapshot(workflowId).Revision,
            stopAuthority: Authority());
        run.State = WorkflowRunState.Interrupted;
        run.TriggerConsumed = true;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        var original = new PrerequisiteActionRecord
        {
            WireRunId = run.WireRunId, Epoch = "42:99", NodeId = "B", Occurrence = 0, LoopIteration = 0,
            Attempt = 1, StrategyIndex = 0, Kind = Account, AccountKey = RunStore.DeriveAccountKey(Uid),
            IdempotencyKey = "original-key", Fingerprint = "original-fingerprint", ExpiresAtUtc = "2030-01-01T00:00:00Z",
            SendAttempted = true, JobId = "original-job", State = PrerequisiteActionState.Submitted,
        };
        run.PrerequisiteActions.Add(original);
        _runs.Update(run);
        var port = new Port();
        port.RestoreOriginalJob(original);
        var boundary = new BodyBoundary();

        var reconciled = await Runner(port, boundary).ResumeAsync(run.RunId);

        Assert.Equal(run.RunId, reconciled.RunId);
        Assert.Equal(WorkflowRunState.Interrupted, reconciled.State);
        Assert.Equal("B", reconciled.Cursor!.NodeId);
        Assert.Empty(reconciled.NodeOutcomes);
        Assert.Empty(port.Sends);
        Assert.True(port.Queries > 0);
        Assert.Empty(boundary.SubmittedNodes);
        var saved = _runs.Load(run.RunId)!;
        Assert.True(AccountPreparationRecovery.CanResume(saved));
        var action = Assert.Single(saved.PrerequisiteActions);
        Assert.Equal("original-job", action.JobId);
        Assert.Equal("original-key", action.IdempotencyKey);
        Assert.Equal(1, action.Attempt);
        Assert.Equal(PreparationNotStarted, action.ErrorCode);
        Assert.Equal("failed", action.ObservedTerminal);
        Assert.True(action.ExecutionExitConfirmed);
    }

    [Theory]
    [InlineData("result_unknown", "prerequisite_outcome_unknown", WorkflowRunState.Unknown)]
    [InlineData("failed", "prerequisite_failed", WorkflowRunState.Failed)]
    public async Task OrdinaryUnknownOrFailed_DoesNotBecomePreparationRetry(string state, string error, WorkflowRunState expected)
    {
        var workflowId = Seed(Node("B", Strategy(Account)));
        var port = new Port { FirstAccountState = state, FirstAccountError = error };
        var boundary = new BodyBoundary();
        var runner = Runner(port, boundary);
        var run = await runner.StartAsync(workflowId);

        Assert.Equal(expected, run.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(port, boundary).ResumeAsync(run.RunId));
        Assert.Single(port.Sends);
        Assert.Single(_runs.Load(run.RunId)!.PrerequisiteActions);
        Assert.Empty(boundary.SubmittedNodes);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("cancel")]
    public async Task StopOrCancellationAtPreparationReply_DoesNotLeaveRetryableInterruptedRun(string action)
    {
        var workflowId = Seed(Node("B", Strategy(Account)));
        var port = new Port();
        var boundary = new BodyBoundary();
        using var cancellation = new CancellationTokenSource();
        var runner = Runner(port, boundary);
        port.OnQuery = () =>
        {
            if (action == "stop") runner.RequestAction(_runs.List().Single().RunId, WorkflowRunAction.Stop);
            else cancellation.Cancel();
        };

        var run = await runner.StartAsync(workflowId, cancellation.Token);

        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.True(_runs.Load(run.RunId)!.StopRequested);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Runner(port, boundary).ResumeAsync(run.RunId));
        Assert.Single(port.Sends);
        Assert.Single(_runs.Load(run.RunId)!.PrerequisiteActions);
        Assert.Empty(boundary.SubmittedNodes);
    }

    [Theory]
    [InlineData("missing-exit")]
    [InlineData("wrong-disposition")]
    [InlineData("wrong-kind")]
    [InlineData("missing-error")]
    [InlineData("wrong-error")]
    [InlineData("wrong-wire-identity")]
    [InlineData("unknown-effect")]
    [InlineData("unknown-terminal")]
    public async Task IncompleteOrUnrelatedOldPreparationRecord_CannotAuthorizeAnotherAttempt(string fault)
    {
        var kind = fault == "wrong-kind" ? Redeem : Account;
        var workflowId = Seed(Node("B", Strategy(kind)));
        var run = _runs.CreateRun(workflowId, _workflows.LoadSnapshot(workflowId).Revision,
            stopAuthority: Authority());
        run.State = WorkflowRunState.Interrupted;
        run.TriggerConsumed = true;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        var old = new PrerequisiteActionRecord
        {
            WireRunId = run.WireRunId, Epoch = "42:99", NodeId = "B", Occurrence = 0, LoopIteration = 0,
            Attempt = 1, StrategyIndex = 0, Kind = kind, AccountKey = RunStore.DeriveAccountKey(Uid),
            IdempotencyKey = "original-key", Fingerprint = "original-fingerprint", ExpiresAtUtc = "2030-01-01T00:00:00Z",
            SendAttempted = true, JobId = "original-job", State = PrerequisiteActionState.Failed,
            ObservedTerminal = "failed", ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
            EffectState = "failed", ErrorCode = PreparationNotStarted, Reason = "原账号准备未操作",
        };
        switch (fault)
        {
            case "missing-exit": old.ExecutionExitConfirmed = false; break;
            case "wrong-disposition": old.ExecutionExitDisposition = "unknown"; break;
            case "missing-error": old.ErrorCode = null; break;
            case "wrong-error": old.ErrorCode = "prerequisite_failed"; break;
            case "wrong-wire-identity": old.WireRunId = "other-run"; break;
            case "unknown-effect": old.EffectState = "unknown"; break;
            case "unknown-terminal": old.ObservedTerminal = "result_unknown"; break;
        }
        run.PrerequisiteActions.Add(old);
        _runs.Update(run);
        var before = JsonSerializer.Serialize(_runs.Load(run.RunId)!.PrerequisiteActions);
        var port = new Port();
        var boundary = new BodyBoundary();

        var resumed = await Runner(port, boundary).ResumeAsync(run.RunId);

        Assert.NotEqual(WorkflowRunState.Succeeded, resumed.State);
        Assert.Empty(port.Sends);
        Assert.Empty(boundary.SubmittedNodes);
        Assert.Equal(before, JsonSerializer.Serialize(_runs.Load(run.RunId)!.PrerequisiteActions));
    }

    private string Seed(params WorkflowNode[] nodes)
    {
        var doc = new WorkflowDocument { Name = "账号准备接续", Activation = new WorkflowActivation { Status = "active" }, Nodes = nodes.ToList() };
        _workflows.Save(doc, null);
        return doc.WorkflowId!;
    }

    private static WorkflowNode Node(string id, params WorkflowStrategy[] strategies) => new()
    {
        NodeId = id, Kind = "resource.oneDragonConfig", Strategies = strategies.ToList(),
        Ref = new WorkflowResourceRef { Config = id, ConfigKey = id + "#k", Revision = "rev-1" },
    };

    private static WorkflowStrategy Strategy(string kind) => new()
    { Kind = kind, Params = new() { ["uid"] = JsonSerializer.SerializeToElement(Uid) } };

    private WorkflowRunner Runner(Port port, BodyBoundary boundary) => new(_workflows, _runs, boundary,
        new BgiWorkflowPrerequisiteAdapter(port, _runs), new NoTerminal(),
        new WorkflowRunnerOptions { DelayAsync = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; } });

    private static WorkflowStopAuthorityRecord Authority(string intentId = "fixture-intent", long timestamp = 1)
        => new("42:99", 7, intentId, timestamp, Stopwatch.Frequency);

    private sealed class BodyBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => false;
        public bool RequiresStopAuthority => true;
        internal List<string> SubmittedNodes { get; } = [];
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string intentId, long timestamp, CancellationToken ct)
            => Task.FromResult<WorkflowStopAuthorityRecord?>(Authority(intentId, timestamp));
        public Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
            => Task.FromResult<bool?>(true);
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            SubmittedNodes.Add(request.Node.NodeId);
            TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("body-" + SubmittedNodes.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
    }

    private sealed class NoTerminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => throw new InvalidOperationException("本夹具不应触发收尾");
    }

    private sealed class Port : IBgiExecutionPort
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch ServerEpoch => new() { ProcessId = 42, StartTicksUtc = 99 };
        internal string FirstAccountState = "failed";
        internal string FirstAccountError = PreparationNotStarted;
        internal Action? OnQuery;
        internal int Queries;
        internal List<(string Operation, JsonElement Payload)> Sends { get; } = [];
        private readonly Dictionary<string, BgiJobInfo> _jobs = [];
        private int _accounts;

        internal void RestoreOriginalJob(PrerequisiteActionRecord original)
            => _jobs.Add(original.JobId!, new BgiJobInfo
            {
                JobId = original.JobId, Epoch = ServerEpoch, Kind = "prerequisite", Name = "prerequisite.account",
                IdempotencyKey = original.IdempotencyKey, WorkflowRunId = original.WireRunId, NodeId = original.NodeId,
                Iteration = original.LoopIteration, Occurrence = original.Occurrence, Attempt = original.Attempt,
                State = "failed", ErrorCode = PreparationNotStarted,
                ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
            });

        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var sent = JsonSerializer.SerializeToElement(payload);
            Sends.Add((operation, sent));
            var firstAccount = operation == BgiExternalClient.ExternalOperations.PrerequisiteAccount && ++_accounts == 1;
            var jobId = "prerequisite-" + Sends.Count;
            _jobs.Add(jobId, new BgiJobInfo
            {
                JobId = jobId, Epoch = ServerEpoch, Kind = "prerequisite", Name = operation[4..],
                IdempotencyKey = sent.GetProperty("idempotencyKey").GetString(),
                WorkflowRunId = sent.GetProperty("workflowRunId").GetString(), NodeId = sent.GetProperty("nodeId").GetString(),
                Iteration = sent.GetProperty("iteration").GetInt32(), Occurrence = sent.GetProperty("occurrence").GetInt32(),
                Attempt = sent.GetProperty("attempt").GetInt32(), State = firstAccount ? FirstAccountState : "succeeded",
                ErrorCode = firstAccount ? FirstAccountError : null, ErrorMessage = firstAccount ? "账号准备尚未发送游戏输入" : null,
                ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
            });
            return Task.FromResult(new BgiExternalResponse { Success = true, Data = JsonSerializer.Serialize(new { status = "queued", taskHandle = jobId }) });
        }

        public Task<(string?, BgiJobInfo?)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            Queries++;
            OnQuery?.Invoke();
            return Task.FromResult<(string?, BgiJobInfo?)>(("found", _jobs[jobId]));
        }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
            => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = ServerEpoch, Jobs = _jobs.Values.ToList() });
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }
}
