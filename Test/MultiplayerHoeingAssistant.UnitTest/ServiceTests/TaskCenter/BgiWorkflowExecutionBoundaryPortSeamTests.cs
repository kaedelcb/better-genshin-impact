using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R5.2 B2-γ 实施前置：生产执行边界的「端口接缝 + 提交两段拆分」组件夹具。
/// 动机（设计稿 §12）：原边界直接持有 sealed 的 BgiExternalClient，导致「后继节点经仲裁面真实提交 +
/// 断言发送次数」这条验收在不起真实 IPC 的前提下**不可满足**。端口化后本夹具可断言发送次数与三态映射。
/// 场景施工方内置，owner 0 点击。
/// </summary>
public class BgiWorkflowExecutionBoundaryPortSeamTests : IDisposable
{
    private readonly string _dir;
    private readonly RunStore _runs;

    public BgiWorkflowExecutionBoundaryPortSeamTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "tcbound-" + Guid.NewGuid().ToString("N")[..8]);
        _runs = new RunStore(Path.Combine(_dir, "runs"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>可控执行端口：记录发送次数与载荷，按脚本返回响应。</summary>
    private sealed class FakePort : IBgiExecutionPort
    {
        public bool Ready { get; set; } = true;
        public string? TakeoverTicket { get; set; }
        public bool IsReady => Ready;
        public BgiEpoch? ServerEpoch { get; set; } = new() { ProcessId = 4321, StartTicksUtc = 638999999999999999 };
        public List<(string Operation, string PayloadJson)> Sends { get; } = [];
        public List<BgiExternalResponse> ScriptedResponses { get; } = [];

        /// <summary>发送前注入（夹具用于在「准备完成、对账之前」制造身份被改／被替换等交错）。</summary>
        public Action? BeforeSend { get; set; }

        /// <summary>发送抛出（触发异常对账路径；OCE 触发取消对账路径）。</summary>
        public Exception? SendThrows { get; set; }

        /// <summary>对账查询返回的作业列表快照（null＝查不到，对账判 Unknown）。</summary>
        public BgiJobListSnapshot? JobList { get; set; }
        // Explicit simulated server admission: calculate only from captured original wire bytes.
        public bool ProjectOriginalPayloadEvidence { get; set; }

        /// <summary>对账查询次数（会诊要求：断言取消分支确实执行了对账，而非直接跳过）。</summary>
        public int JobListQueries { get; private set; }

        /// <summary>远端取消请求次数。</summary>
        public int CancelCalls { get; private set; }

        /// <summary>取消被请求时，持久化记录里**已**带有该 jobId（证明「先落盘、后取消」顺序）。</summary>
        public bool CancelObservedPersistedJob { get; private set; }

        /// <summary>取消观测回调（夹具注入：读取持久化记录判断顺序）。</summary>
        public Func<bool>? OnCancelObserve { get; set; }

        public bool HasCapability(string name) => Ready;

        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            Sends.Add((operation, payload is null ? "" : System.Text.Json.JsonSerializer.Serialize(payload)));
            BeforeSend?.Invoke();
            if (SendThrows is not null) throw SendThrows;
            return Task.FromResult(ScriptedResponses.Count > 0
                ? ScriptedResponses[Sends.Count - 1 < ScriptedResponses.Count ? Sends.Count - 1 : ScriptedResponses.Count - 1]
                : new BgiExternalResponse { Success = true, Data = "{\"status\":\"accepted\",\"taskHandle\":\"job-1\"}" });
        }

        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
        {
            JobListQueries++;
            if (ProjectOriginalPayloadEvidence && JobList is { } snapshot && Sends.Count == 1)
            {
                var original = Sends[0];
                var payload = System.Text.Json.Nodes.JsonNode.Parse(original.PayloadJson)!;
                return Task.FromResult<BgiJobListSnapshot?>(new BgiJobListSnapshot
                {
                    Epoch = snapshot.Epoch,
                    Jobs = snapshot.Jobs.Select(job =>
                    {
                        var fields = System.Text.Json.Nodes.JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(job))!.AsObject();
                        fields["RequestFingerprint"] = BgiOriginalRequestFingerprint.Compute(original.Operation, original.PayloadJson);
                        fields["RequestFingerprintVersion"] = 1;
                        fields["RequestOperation"] = original.Operation;
                        fields["TaskId"] = payload["taskId"]?.DeepClone();
                        fields["ConfigRevision"] = payload["expectedConfigRevision"]?.DeepClone();
                        return System.Text.Json.JsonSerializer.Deserialize<BgiJobInfo>(fields.ToJsonString())!;
                    }).ToList(),
                });
            }
            return Task.FromResult(JobList);
        }

        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult<(string?, BgiJobInfo?)>((null, null));

        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct)
        {
            CancelCalls++;
            CancelObservedPersistedJob = OnCancelObserve?.Invoke() ?? false;
            return Task.CompletedTask;
        }
    }

    /// <summary>种子运行：CurrentSubmission 已按引擎纪律落盘意图（Intent=IntentRecorded）。</summary>
    private (WorkflowRunRecord Run, WorkflowNode Node, WorkflowNodeOccurrence Occurrence) Seed(
        string nodeKind = "resource.oneDragonConfig", string? config = "配置A", string? revision = "rev-1")
    {
        var run = _runs.CreateRun("wf-boundary", "r-1", note: "边界夹具种子",
            stopAuthority: new WorkflowStopAuthorityRecord("4321:638999999999999999", 0, "fixture-intent", 1, System.Diagnostics.Stopwatch.Frequency));
        var occurrence = new WorkflowNodeOccurrence("n-1", 0, 0, 0);
        // 与生产 DriveAsync 真实形态一致：节点驱动循环在提交前经 Relocate/ApplyRelocation 把权威游标指向当前出现。
        run.Cursor = new WorkflowNodeCursor { NodeId = "n-1", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "n-1", 0, 0, 1),
            NodeId = "n-1",
            Occurrence = 0,
            LoopIteration = 0,
            Attempt = 1,
            Intent = SubmitIntentState.IntentRecorded,
        };
        _runs.Update(run);
        var node = new WorkflowNode
        {
            NodeId = "n-1",
            Kind = nodeKind,
            Ref = new WorkflowResourceRef { Config = config, Revision = revision },
        };
        return (run, node, occurrence);
    }

    [Fact]
    public async Task ReconcileThroughWorkflowInterface_UsesRealBoundaryQuery()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var concrete = new BgiWorkflowExecutionBoundary(port, _runs);
        Assert.Null(concrete.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true)).Rejection);
        IWorkflowExecutionBoundary boundary = concrete;
        var result = await boundary.ReconcileSubmissionAsync(run, run.CurrentSubmission!, default);
        Assert.True(result.Uncertain);
        Assert.Equal(1, port.JobListQueries);
        Assert.Empty(port.Sends);
    }

    // ── 1. 正常路径：冻结身份 → 恰好发送一次 → 受理带 jobId；载荷携带完整出现身份 ──

    [Fact]
    public async Task DeliveryFence_ProductionObservationDoesNotRefreshFrozenVersion()
    {
        var (run, _, _) = Seed();
        var port = new FakePort();
        port.ScriptedResponses.Add(new BgiExternalResponse { Success = true, Data = System.Text.Json.JsonSerializer.Serialize(new
        {
            bgiEpoch = new { processId = 4321, startTicksUtc = 638999999999999999L }, stopVersion = 1,
            lastManualStopTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), monotonicFrequency = System.Diagnostics.Stopwatch.Frequency,
        }) });
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        Assert.False(await boundary.InspectStopAuthorityAsync(run.StopAuthority!, default));
        Assert.Equal(0, _runs.Load(run.RunId)!.StopAuthority!.Version);
        Assert.Equal(WorkflowStopAuthority.Operation, Assert.Single(port.Sends).Operation);
    }

    [Fact]
    public async Task DeliveryFence_StopAfterPrepareDoesNotCallNetworkOrClearPossibleSendFact()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        Assert.Null(prepared.Rejection);
        Assert.True(_runs.UpdateMergingIf(run.RunId, latest => { latest.StopRequested = true; return true; }, out _));
        var result = await boundary.SendPreparedAsync(prepared, default);
        Assert.False(result.Uncertain);
        Assert.False(result.Accepted);
        Assert.Empty(port.Sends);
        var restored = _runs.Load(run.RunId)!;
        Assert.True(restored.CurrentSubmission!.SendAttempted);
        Assert.Null(restored.CurrentSubmission.ObservedTerminal);
        Assert.False(restored.CurrentSubmission.ExecutionExitConfirmed);
        Assert.Null(restored.CurrentSubmission.ServerRejectionEvidence);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(restored.CurrentSubmission));
        Assert.Equal(System.Text.Json.JsonValueKind.Object, json.RootElement.GetProperty("localNoSendProof").ValueKind);
        Assert.False(RunStore.HasUnresolvedTerminalResponsibility(restored));
        restored.State = WorkflowRunState.Cancelled;
        _runs.Update(restored);
        _runs.RecoverOnStart();
        Assert.Equal(WorkflowRunState.Cancelled, _runs.Load(run.RunId)!.State);
        Assert.True((await boundary.SendPreparedAsync(prepared, default)).Uncertain);
        Assert.Empty(port.Sends);
    }

    private sealed class StopAtPreparedBoundary(BgiWorkflowExecutionBoundary real) : IWorkflowExecutionBoundary
    {
        public Action<string>? Stop;
        public int Lookups;
        public bool SingleNativeSupported => true;
        public bool RequiresStopAuthority => true;
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string id, long timestamp, CancellationToken ct)
            => Task.FromResult<WorkflowStopAuthorityRecord?>(new("4321:638999999999999999", 0, id, timestamp, System.Diagnostics.Stopwatch.Frequency));
        public Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
            => Task.FromResult<bool?>(true);
        public async Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            var prepared = real.PrepareSubmit(request);
            Assert.Null(prepared.Rejection);
            Stop!(request.Run.RunId);
            return await real.SendPreparedAsync(prepared, ct);
        }
        public Task<BoundarySubmitResult> ReconcileSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission sub, CancellationToken ct)
        { Lookups++; return Task.FromResult(BoundarySubmitResult.UnknownWith("unexpected lookup")); }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string id, CancellationToken ct)
            => throw new InvalidOperationException("zero-call submission has no remote job");
    }

    private sealed class NoPrerequisites : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => throw new InvalidOperationException("fixture declares no prerequisites");
    }
    private sealed class NoCompletion : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => throw new InvalidOperationException("stopped fixture cannot invoke completion");
    }

    [Fact]
    public async Task PreparedStop_RealRunnerStopsWithoutLookupSuccessorOrFakeRawTerminal()
    {
        var flows = new WorkflowStore(Path.Combine(_dir, "flows"));
        var flow = new WorkflowDocument { Name = "prepared-stop", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "first", Kind = "resource.oneDragonConfig", Ref = new() { Config = "config", Revision = "revision" } },
                new() { NodeId = "next", Kind = "resource.oneDragonConfig", Ref = new() { Config = "config", Revision = "revision" } }] };
        flows.Save(flow, null);
        var port = new FakePort();
        var boundary = new StopAtPreparedBoundary(new BgiWorkflowExecutionBoundary(port, _runs));
        var runner = new WorkflowRunner(flows, _runs, boundary, new NoPrerequisites(), new NoCompletion());
        boundary.Stop = id => runner.RequestAction(id, WorkflowRunAction.Stop);
        var result = await runner.StartAsync(flow.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowRunState.Cancelled, result.State);
        Assert.True(result.StopRequested);
        Assert.Empty(port.Sends);
        Assert.Equal(0, boundary.Lookups);
        Assert.Equal("first", result.CurrentSubmission!.NodeId);
        Assert.NotNull(result.CurrentSubmission.LocalNoSendProof);
        Assert.Null(result.CurrentSubmission.ObservedTerminal);
        Assert.False(result.CurrentSubmission.ExecutionExitConfirmed);
        _runs.RecoverOnStart();
        Assert.Equal(WorkflowRunState.Cancelled, _runs.Load(result.RunId)!.State);
    }

    [Theory]
    [InlineData("delete")]
    [InlineData("replace")]
    [InlineData("job")]
    [InlineData("raw")]
    [InlineData("wire")]
    [InlineData("key")]
    public async Task PreparedStop_ProofCannotBeRemovedRewrittenOrContradicted(string change)
    {
        var (run, node, occurrence) = Seed();
        var boundary = new BgiWorkflowExecutionBoundary(new FakePort(), _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        Assert.True(_runs.UpdateMergingIf(run.RunId, latest => { latest.StopRequested = true; return true; }, out _));
        Assert.False((await boundary.SendPreparedAsync(prepared, default)).Uncertain);
        var current = _runs.Load(run.RunId)!;
        var proof = current.CurrentSubmission!.LocalNoSendProof!;
        if (change == "delete") current.CurrentSubmission.LocalNoSendProof = null;
        if (change == "replace") current.CurrentSubmission.LocalNoSendProof = proof with { ConsumptionId = Guid.NewGuid().ToString("N") };
        if (change == "job") current.CurrentSubmission.JobId = "late-job";
        if (change == "raw") current.CurrentSubmission.ObservedTerminal = "cancelled";
        if (change == "wire") current.WireRunId = "replacement-wire";
        if (change == "key") current.CurrentSubmission.Key = "replacement-key";
        Assert.Throws<RunRecordConflictException>(() => _runs.Update(current));
        var restored = _runs.Load(run.RunId)!;
        Assert.Equal(proof, restored.CurrentSubmission!.LocalNoSendProof);
        Assert.False(RunStore.HasUnresolvedTerminalResponsibility(restored));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("authority")]
    [InlineData("submission")]
    public async Task PreparedStop_MutatedPreparedInputsCannotDischargeOriginal(string change)
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        Assert.True(_runs.UpdateMergingIf(run.RunId, latest => { latest.StopRequested = true; return true; }, out _));
        if (change == "payload")
            prepared = BgiWorkflowExecutionBoundary.PreparedSubmit.Ok(run, prepared.Submission!, new { changedPayload = true });
        if (change == "authority")
            run.StopAuthority = run.StopAuthority! with { Version = 123 };
        if (change == "submission")
            prepared.Submission!.Key = "mutated-memory-key";
        // The immutable original proof can still discharge a mutated caller view; a rewrapped changed payload cannot.
        var result = await boundary.SendPreparedAsync(prepared, default);
        Assert.Equal(change == "payload", result.Uncertain);
        Assert.Empty(port.Sends);
        Assert.Equal(change == "payload", RunStore.HasUnresolvedTerminalResponsibility(_runs.Load(run.RunId)!));
    }

    [Theory]
    [InlineData("job")]
    [InlineData("accepted")]
    [InlineData("terminal")]
    [InlineData("exit")]
    public async Task PreparedStop_ConflictingDurableFactsRemainUnknown(string fact)
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        Assert.True(_runs.UpdateMergingIf(run.RunId, latest =>
        {
            latest.StopRequested = true;
            var sub = latest.CurrentSubmission!;
            if (fact == "job") sub.JobId = "accepted-job";
            if (fact == "accepted") sub.AcceptedSendIdentity = "accepted-send";
            if (fact == "terminal") sub.ObservedTerminal = "succeeded";
            if (fact == "exit") sub.ExecutionExitConfirmed = true;
            return true;
        }, out _));
        Assert.True((await boundary.SendPreparedAsync(prepared, default)).Uncertain);
        Assert.Empty(port.Sends);
        Assert.True(RunStore.HasUnresolvedTerminalResponsibility(_runs.Load(run.RunId)!));
    }

    [Fact]
    public async Task PreparedStop_PublishFailurePreservesUnresolvedFactAndZeroCalls()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        Assert.True(_runs.UpdateMergingIf(run.RunId, latest => { latest.StopRequested = true; return true; }, out _));
        _runs.PublishFaultForTest = _ => new IOException("prepared proof publication failed");
        Assert.True((await boundary.SendPreparedAsync(prepared, default)).Uncertain);
        Assert.Empty(port.Sends);
        Assert.True(RunStore.HasUnresolvedTerminalResponsibility(_runs.Load(run.RunId)!));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeliveryRejection_NoJobRequiresSameEpochAndEntireFrozenRequest(bool wrongEpoch)
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        port.BeforeSend = () =>
        {
            var echo = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(port.Sends.Last().PayloadJson);
            port.ScriptedResponses.Add(new BgiExternalResponse
            {
                Success = false, ErrorCode = "invalid_request",
                Data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    executionDisposition = "server_rejected_before_acceptance",
                    accepted = false,
                    operation = BgiExternalClient.ExternalOperations.TaskStart,
                    bgiEpoch = new { processId = wrongEpoch ? 9999 : 4321, startTicksUtc = 638999999999999999L },
                    request = echo,
                }),
            });
        };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, true), default);
        var persisted = _runs.Load(run.RunId)!.CurrentSubmission!;
        Assert.Single(port.Sends);
        Assert.True(persisted.SendAttempted); // 已进入线路，不能改写成未发送
        Assert.Null(persisted.JobId);
        if (wrongEpoch)
        {
            Assert.True(result.Uncertain);
            Assert.False(persisted.ExecutionExitConfirmed);
            Assert.Null(persisted.ObservedTerminal);
        }
        else
        {
            Assert.False(result.Accepted);
            Assert.False(result.Uncertain);
            Assert.True(persisted.ExecutionExitConfirmed);
            Assert.Equal("rejected", persisted.ObservedTerminal);
            Assert.Equal(SubmitIntentState.Rejected, persisted.Intent);
        }
    }

    [Fact]
    public async Task DeliveryRejection_FingerprintChangedAfterPrepare_CannotClearReplacementFact()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        port.BeforeSend = () =>
        {
            var echo = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(port.Sends.Last().PayloadJson);
            run.CurrentSubmission!.Fingerprint = "replacement-fingerprint";
            Assert.Throws<RunRecordConflictException>(() => _runs.Update(run));
            // Simulate an external corrupt writer in this private fixture directory; the real store rejects it.
            File.WriteAllText(Path.Combine(_dir, "runs", run.RunId + ".run.json"), System.Text.Json.JsonSerializer.Serialize(run));
            port.ScriptedResponses.Add(new BgiExternalResponse
            {
                Success = false, ErrorCode = "invalid_request",
                Data = System.Text.Json.JsonSerializer.Serialize(new
                {
                    executionDisposition = "server_rejected_before_acceptance", accepted = false,
                    operation = BgiExternalClient.ExternalOperations.TaskStart,
                    bgiEpoch = new { processId = 4321, startTicksUtc = 638999999999999999L }, request = echo,
                }),
            });
        };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, true), default);
        Assert.True(result.Uncertain);
        var saved = _runs.Load(run.RunId)!.CurrentSubmission!;
        Assert.Equal("replacement-fingerprint", saved.Fingerprint);
        Assert.False(saved.ExecutionExitConfirmed);
        Assert.Null(saved.ObservedTerminal);
    }

    [Fact]
    public async Task DeliveryRejection_PreparedRequestFreezesTakeoverTicketBeforeSdkSend()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort { TakeoverTicket = "ticket-original" };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        using var payload = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(prepared.Payload));
        Assert.True(payload.RootElement.TryGetProperty("takeoverTicket", out var ticket));
        Assert.Equal("ticket-original", ticket.GetString());
        port.TakeoverTicket = "ticket-later";
        await boundary.SendPreparedAsync(prepared, default);
        using var sent = System.Text.Json.JsonDocument.Parse(Assert.Single(port.Sends).PayloadJson);
        Assert.Equal("ticket-original", sent.RootElement.GetProperty("takeoverTicket").GetString());
    }

    [Fact]
    public async Task Submit_HappyPath_SendsExactlyOnce_WithFrozenOccurrenceIdentity()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.True(result.Accepted);
        Assert.Equal("job-1", result.JobId);
        var send = Assert.Single(port.Sends); // 发送次数可断言（端口化的直接收益）
        Assert.Equal(BgiExternalClient.ExternalOperations.TaskStart, send.Operation);
        // 冻结身份：幂等键 + 运行（线身份）+ 节点出现身份三段（nodeId/iteration/occurrence）+ attempt
        Assert.Contains("\"idempotencyKey\":\"" + run.CurrentSubmission!.Key + "\"", send.PayloadJson);
        Assert.Contains("\"workflowRunId\":\"" + run.WireRunId + "\"", send.PayloadJson);
        Assert.Contains("\"nodeId\":\"n-1\"", send.PayloadJson);
        Assert.Contains("\"iteration\":0", send.PayloadJson);
        Assert.Contains("\"occurrence\":0", send.PayloadJson);
        Assert.Contains("\"attempt\":1", send.PayloadJson);
        // 纪元按原样编码（进程号+起始 tick 两字段，不做字符串化改写）
        Assert.Contains("\"processId\":4321", send.PayloadJson);
        Assert.Contains("\"startTicksUtc\":638999999999999999", send.PayloadJson);
        // 冻结事实落盘（此后缺 jobId ≠ 未发送）
        var persisted = _runs.Load(run.RunId)!;
        Assert.True(persisted.CurrentSubmission!.SendAttempted);
        Assert.Equal(SubmitIntentState.Submitted, persisted.CurrentSubmission.Intent);
    }

    // ── 2. 本地校验失败=可证实未受理：一次都不发送 ──


    /// <summary>
    /// **[G4-residual①·红反例] 冻结 CAS 的权威游标检查**：盘上记录的权威游标已在「宿主检查视角之后」
    /// 被并发推进到下一节点（提交键/attempt 未变，意图身份复核不拦截）⇒ `PrepareSubmit` 必须**拒绝**，
    /// 且**零冻结事实、零发送**（可证实未发送），不得凭旧视角进入冻结。这是冻结 CAS 游标检查本身的
    /// 判别夹具（区别于宿主侧 successor_cursor_changed 检查——本夹具绕过宿主直接打边界层）。
    /// </summary>
    [Fact]
    public void PrepareSubmit_CursorAdvancedBeforeFreeze_RejectsWithZeroSendAndNoFreezeFacts()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        // 并发写入者把权威游标推进到下一节点（提交键/attempt 未变——模拟宿主检查视角已过期的真实窗口）。
        Assert.True(_runs.UpdateMergingIf(run.RunId, latest =>
        {
            latest.Cursor = new WorkflowNodeCursor { NodeId = "n-2", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
            return true;
        }, out _));

        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));

        Assert.NotNull(prepared.Rejection);   // 必须拒绝（可证实未发送）
        Assert.Empty(port.Sends);             // 零发送
        var persisted = _runs.Load(run.RunId)!;
        Assert.False(persisted.CurrentSubmission!.SendAttempted);  // 零冻结事实
        Assert.Equal(SubmitIntentState.IntentRecorded, persisted.CurrentSubmission.Intent);  // 意图未被推进
        Assert.Null(persisted.CurrentSubmission.Fingerprint);      // 未发布冻结指纹
    }
    [Fact]
    public async Task Submit_LocalValidationFails_RejectsWithoutSending()
    {
        var (run, node, occurrence) = Seed(revision: null); // 缺 expectedConfigRevision → 严格合同拒绝
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.False(result.Accepted);
        Assert.False(result.Uncertain);
        Assert.Empty(port.Sends); // 副作用前拒绝必须零发送
    }

    // ── 3. 纪元未知=可证实未发送（严格合同要求 bgiEpoch）──

    [Fact]
    public async Task Submit_EpochMissing_RejectsWithoutSending()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort { ServerEpoch = null };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.False(result.Accepted);
        Assert.False(result.Uncertain);
        Assert.Empty(port.Sends);
    }

    // ── 4. 两段拆分可独立调用：PrepareSubmit 不发送、SendPreparedAsync 才发送 ──
    // 注意（会诊定稿）：接线态次序是「门面锁内占位 → Sender 内准备 → 发送」，**不是**在准备与发送之间插仲裁。

    [Fact]
    public async Task PrepareThenSend_Split_SendsOnlyInSecondSegment()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var request = new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true);

        var prepared = boundary.PrepareSubmit(request);

        Assert.Null(prepared.Rejection); // 第 1 段通过
        Assert.Empty(port.Sends); // 第 1 段无远端副作用
        Assert.True(run.CurrentSubmission!.SendAttempted); // 冻结事实已落盘（此后缺 jobId ≠ 未发送）

        var result = await boundary.SendPreparedAsync(prepared, default);

        Assert.True(result.Accepted);
        Assert.Single(port.Sends); // 发送只发生在第 2 段
    }

    // ── 5. 白名单外失败码=Unknown（不猜未受理，待对账不重发）──

    [Fact]
    public async Task Submit_NonWhitelistedFailure_MapsToUncertain()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        port.ScriptedResponses.Add(new BgiExternalResponse { Success = false, ErrorCode = "task_start_failed" });
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.True(result.Uncertain);
        Assert.Single(port.Sends); // 已尝试发送一次，但不重发
    }

    // ── 6. 白名单内失败码=确定未受理拒绝 ──

    [Fact]
    public async Task Submit_WhitelistedFailureWithoutTypedEvidence_RemainsUnknown()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        port.ScriptedResponses.Add(new BgiExternalResponse { Success = false, ErrorCode = "stale_epoch" });
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.False(result.Accepted);
        Assert.True(result.Uncertain);
        Assert.Null(run.CurrentSubmission!.ServerRejectionEvidence);
        Assert.True(run.CurrentSubmission.SendAttempted);
        Assert.Single(port.Sends);
    }

    // ── 7. 会诊阻断项处置：拒绝对象送进发送段 → 永不发送 ──

    [Fact]
    public async Task SendPrepared_RejectionObject_ReturnsRejectionWithoutSending()
    {
        var (run, node, occurrence) = Seed(revision: null); // 第 1 段必然拒绝
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true));
        Assert.NotNull(prepared.Rejection); // 前置：这是拒绝对象

        var result = await boundary.SendPreparedAsync(prepared, default);

        Assert.False(result.Accepted);
        Assert.False(result.Uncertain);
        Assert.Empty(port.Sends); // 拒绝对象绝不能被送去发送
    }

    // ── 8. 会诊阻断项处置：同一冻结载荷重复消费 → 不增发 ──

    [Fact]
    public async Task SendPrepared_SecondConsumption_DoesNotSendAgain()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true));

        var first = await boundary.SendPreparedAsync(prepared, default);
        var second = await boundary.SendPreparedAsync(prepared, default);

        Assert.True(first.Accepted);
        Assert.True(second.Uncertain); // 内部违例：响亮未知，不重发
        Assert.Single(port.Sends); // 全链只发送一次
    }

    // ── 9. 会诊阻断项处置：授权纪元与本机当前纪元不一致 → 可证实未发送地拒绝 ──

    [Fact]
    public void PrepareSubmit_AuthorizedEpochMismatch_RejectedWithoutSending()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort(); // 当前纪元 4321:638999999999999999
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var prepared = boundary.PrepareSubmit(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true),
            authorizedEpoch: "9999:111111111111111111"); // 门面授权的是另一个纪元

        Assert.NotNull(prepared.Rejection);
        Assert.False(prepared.Rejection!.Accepted);
        Assert.Empty(port.Sends); // 绝不把新纪元写成旧授权对应的发送身份
    }

    // ── 10. 授权纪元一致=正常放行（护栏不得误杀正常路径）──

    [Fact]
    public void PrepareSubmit_AuthorizedEpochMatches_Accepted()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var prepared = boundary.PrepareSubmit(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true),
            authorizedEpoch: "4321:638999999999999999");

        Assert.Null(prepared.Rejection);
        Assert.Empty(port.Sends);
    }

    // ── 11. 会诊复核反例处置：同一冻结载荷**并发**消费 → 全链仍只发送一次 ──

    [Fact]
    public async Task SendPrepared_ConcurrentConsumption_SendsExactlyOnce()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true));

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => boundary.SendPreparedAsync(prepared, default))));

        Assert.Single(port.Sends); // 并发下仍恰好一次
        Assert.Single(results.Where(r => r.Accepted)); // 恰一胜者
        Assert.Equal(7, results.Count(r => r.Uncertain)); // 其余响亮未知，不重发
    }

    // ── 12. 会诊复核要求：纪元不符时必须**在冻结之前**拒绝（内存与持久化冻结字段均不变）──

    [Fact]
    public void PrepareSubmit_AuthorizedEpochMismatch_DoesNotFreezeOrPersist()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var prepared = boundary.PrepareSubmit(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true),
            authorizedEpoch: "9999:111111111111111111");

        Assert.NotNull(prepared.Rejection);
        Assert.Null(prepared.Run);                       // 未产出可发送载荷
        Assert.Null(run.CurrentSubmission!.Epoch);       // 内存未冻结
        Assert.False(run.CurrentSubmission.SendAttempted);
        Assert.Equal(SubmitIntentState.IntentRecorded, run.CurrentSubmission.Intent);
        var persisted = _runs.Load(run.RunId)!;          // 持久化亦未冻结
        Assert.Null(persisted.CurrentSubmission!.Epoch);
        Assert.False(persisted.CurrentSubmission.SendAttempted);
    }

    // ── 13. 会诊复核要求：纪元一致时，实际发送载荷必须携带**授权纪元**（同组标量一致）──

    [Fact]
    public async Task SendPrepared_AuthorizedEpochMatch_PayloadCarriesAuthorizedEpoch()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true),
            authorizedEpoch: "4321:638999999999999999");

        var result = await boundary.SendPreparedAsync(prepared, default);

        Assert.True(result.Accepted);
        var send = Assert.Single(port.Sends);
        Assert.Contains("\"processId\":4321", send.PayloadJson);
        Assert.Contains("\"startTicksUtc\":638999999999999999", send.PayloadJson);
        Assert.Equal("4321:638999999999999999", _runs.Load(run.RunId)!.CurrentSubmission!.Epoch); // 持久化同一组值
    }

    // ── 14b. 会诊回溯复核反例：拒绝槽位不得承载非拒绝结果（No(Accepted)/No(Unknown) 必须被工厂拒绝）──
    // 反例形态：No(AcceptedWith("x")) 会让 SendPreparedAsync 零发送却报 Accepted（第一道护栏只看 Rejection 非空）。

    [Fact]
    public void PrepareSubmit_NoFactory_RejectsNonRejectedResults()
    {
        Assert.Throws<ArgumentException>(() =>
            BgiWorkflowExecutionBoundary.PreparedSubmit.No(BoundarySubmitResult.AcceptedWith("fabricated-job")));
        Assert.Throws<ArgumentException>(() =>
            BgiWorkflowExecutionBoundary.PreparedSubmit.No(BoundarySubmitResult.UnknownWith("fabricated-unknown")));
        Assert.Throws<ArgumentNullException>(() =>
            BgiWorkflowExecutionBoundary.PreparedSubmit.No(null!));
        // 合法拒绝仍可构造（不误伤正常路径）
        var ok = BgiWorkflowExecutionBoundary.PreparedSubmit.No(BoundarySubmitResult.Rejected("precheck_rejected"));
        Assert.NotNull(ok.Rejection);
    }

    // ── 15. 对账路径（会诊要求）：正常命中用冻结身份落盘受理事实 ──

    private static BgiJobListSnapshot SnapshotFor(string key, string wireRunId, string nodeId, int iteration, string jobId)
        => new()
        {
            Epoch = new BgiEpoch { ProcessId = 4321, StartTicksUtc = 638999999999999999 },
            Jobs = [new BgiJobInfo { IdempotencyKey = key, WorkflowRunId = wireRunId, NodeId = nodeId, Iteration = iteration, Occurrence = 0, Attempt = 1, JobId = jobId, State = "queued" }],
        };

    [Fact]
    public async Task Reconcile_UncertainSendHitWithFrozenIdentity_PersistsAccepted()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
            ProjectOriginalPayloadEvidence = true,
            JobList = SnapshotFor(run.CurrentSubmission!.Key, run.WireRunId, "n-1", 0, "job-reconciled"),
        };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.True(result.Accepted);
        Assert.Equal("job-reconciled", result.JobId);
        Assert.Single(port.Sends); // 只发送一次（对账命中不重发）
        var persisted = _runs.Load(run.RunId)!;
        Assert.Equal(SubmitIntentState.Accepted, persisted.CurrentSubmission!.Intent);
        Assert.Equal("job-reconciled", persisted.CurrentSubmission.JobId);
    }

    // ── 16. 对账路径（会诊反例 1）：同一实例身份被原地改写 → 不得落盘受理事实 ──

    private static BgiJobListSnapshot OriginalSnapshot(BgiWorkflowExecutionBoundary.PreparedSubmit prepared)
    {
        var identity = prepared.Reconcile!;
        var json = System.Text.Json.JsonSerializer.Serialize(prepared.Payload);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        return new BgiJobListSnapshot
        {
            Epoch = new BgiEpoch { ProcessId = 4321, StartTicksUtc = 638999999999999999 },
            Jobs = [new BgiJobInfo
            {
                IdempotencyKey = identity.Key, WorkflowRunId = identity.WireRunId, NodeId = identity.NodeId,
                Iteration = identity.LoopIteration, Occurrence = identity.Occurrence, Attempt = identity.Attempt,
                JobId = "job-original", State = "queued", TaskId = payload["taskId"]?.GetValue<string>(),
                ConfigRevision = payload["expectedConfigRevision"]?.GetValue<string>(),
                RequestFingerprintVersion = 1, RequestOperation = prepared.Operation,
                RequestFingerprint = BgiOriginalRequestFingerprint.Compute(prepared.Operation, json),
            }],
        };
    }

    [Fact]
    public async Task Reconcile_ReopenedOriginalFrozenPayloadMatchesWithoutResending()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var prepared = new BgiWorkflowExecutionBoundary(port, _runs)
            .PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        port.JobList = OriginalSnapshot(prepared);
        var reopenedStore = new RunStore(Path.Combine(_dir, "runs"));
        var reopened = reopenedStore.Load(run.RunId)!;
        var evidence = Assert.IsType<FrozenOriginalRequestEvidence>(reopened.CurrentSubmission!.OriginalRequestEvidence);
        Assert.Equal(port.JobList.Jobs.Single().RequestFingerprint, evidence.Fingerprint);
        Assert.Equal("rev-1", evidence.ConfigRevision);
        IWorkflowExecutionBoundary boundary = new BgiWorkflowExecutionBoundary(port, reopenedStore);
        var result = await boundary.ReconcileSubmissionAsync(reopened, reopened.CurrentSubmission, default);
        Assert.True(result.Accepted);
        Assert.Equal("job-original", new RunStore(Path.Combine(_dir, "runs")).Load(run.RunId)!.CurrentSubmission!.JobId);
        Assert.Empty(port.Sends);
        Assert.Equal(1, port.JobListQueries);
    }

    [Theory]
    [InlineData("remove")]
    [InlineData("fingerprint")]
    [InlineData("task")]
    [InlineData("config")]
    public void OrdinaryWriter_CannotRewriteOriginalFrozenRequestEvidence(string drift)
    {
        var (run, node, occurrence) = Seed();
        new BgiWorkflowExecutionBoundary(new FakePort(), _runs)
            .PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        var original = _runs.Load(run.RunId)!.CurrentSubmission!.OriginalRequestEvidence!;
        var changed = _runs.Load(run.RunId)!;
        changed.CurrentSubmission!.OriginalRequestEvidence = drift switch
        {
            "remove" => null, "fingerprint" => original with { Fingerprint = new string('A', 64) },
            "task" => original with { TaskId = "wrong" }, _ => original with { ConfigRevision = "wrong" },
        };
        Assert.Throws<RunRecordConflictException>(() => _runs.Update(changed));
        Assert.Equal(original, _runs.Load(run.RunId)!.CurrentSubmission!.OriginalRequestEvidence);
    }

    [Fact]
    public async Task ReconcileHistorical_MissingLocalOriginalEvidenceCannotUseServerHit()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        port.JobList = OriginalSnapshot(prepared);
        var history = System.Text.Json.JsonSerializer.Deserialize<WorkflowSubmission>(
            System.Text.Json.JsonSerializer.Serialize(run.CurrentSubmission))!;
        history.OriginalRequestEvidence = null; // A legacy original, not an authorized enhancement of the stored record.
        var before = File.ReadAllBytes(Path.Combine(_dir, "runs", run.RunId + ".run.json"));
        Assert.Null(await boundary.ReconcileHistoricalSubmissionAsync(run, history, default));
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_dir, "runs", run.RunId + ".run.json")));
        Assert.Empty(port.Sends);
    }

    [Fact]
    public void LegacyPossiblySentWithoutLocalFingerprint_CannotAcquireInventedOriginalEvidence()
    {
        var (run, _, _) = Seed();
        run.CurrentSubmission!.SendAttempted = true;
        run.CurrentSubmission.Intent = SubmitIntentState.Submitted;
        _runs.Update(run);
        var changed = _runs.Load(run.RunId)!;
        changed.CurrentSubmission!.OriginalRequestEvidence = new(1, new string('A', 64), "ext.task.start", null, "rev-1");
        Assert.Throws<RunRecordConflictException>(() => _runs.Update(changed));
        Assert.Null(_runs.Load(run.RunId)!.CurrentSubmission!.OriginalRequestEvidence);
    }

    [Theory]
    [InlineData("TaskId")]
    [InlineData("ConfigRevision")]
    [InlineData("RequestFingerprint")]
    [InlineData("RequestFingerprintVersion")]
    [InlineData("RequestOperation")]
    [InlineData("missing")]
    public async Task Reconcile_WrongOrMissingOriginalServerPayloadRemainsUnknown(string drift)
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort { SendThrows = new IOException("response lost after send") };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var prepared = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, true));
        Assert.Null(prepared.Rejection);
        var json = System.Text.Json.JsonSerializer.Serialize(prepared.Payload);
        var payload = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        var projection = new System.Text.Json.Nodes.JsonObject
        {
            ["IdempotencyKey"] = run.CurrentSubmission!.Key, ["WorkflowRunId"] = run.WireRunId,
            ["NodeId"] = "n-1", ["Iteration"] = 0, ["Occurrence"] = 0, ["Attempt"] = 1,
            ["JobId"] = "job-original", ["State"] = "queued",
            ["TaskId"] = payload["taskId"]?.DeepClone(),
            ["ConfigRevision"] = payload["expectedConfigRevision"]?.DeepClone(),
            ["RequestFingerprint"] = BgiOriginalRequestFingerprint.Compute(prepared.Operation, json),
            ["RequestFingerprintVersion"] = 1, ["RequestOperation"] = prepared.Operation,
        };
        if (drift == "missing")
        {
            projection.Remove("RequestFingerprint");
            projection.Remove("RequestFingerprintVersion");
            projection.Remove("RequestOperation");
        }
        else if (drift == "RequestFingerprintVersion") projection[drift] = 2;
        else projection[drift] = "wrong-original-evidence";
        port.JobList = new BgiJobListSnapshot
        {
            Epoch = port.ServerEpoch,
            Jobs = [System.Text.Json.JsonSerializer.Deserialize<BgiJobInfo>(projection.ToJsonString())!],
        };
        var result = await boundary.SendPreparedAsync(prepared, default);
        Assert.True(result.Uncertain);
        Assert.Single(port.Sends);
        var disk = _runs.Load(run.RunId)!;
        Assert.Null(disk.CurrentSubmission!.JobId);
        Assert.Equal(SubmitIntentState.Submitted, disk.CurrentSubmission.Intent);
    }

    [Fact]
    public async Task Reconcile_SubmissionIdentityMutatedInPlace_DoesNotPersistAccepted()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
            ProjectOriginalPayloadEvidence = true,
            JobList = SnapshotFor(run.CurrentSubmission!.Key, run.WireRunId, "n-1", 0, "job-reconciled"),
        };
        port.BeforeSend = () => run.CurrentSubmission!.Key = "mutated-key"; // 准备之后、对账之前改写身份
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.True(result.Uncertain); // 不臆断落盘：保守 Unknown
        Assert.Single(port.Sends);     // 零重发
        Assert.NotEqual(SubmitIntentState.Accepted, run.CurrentSubmission!.Intent);
        Assert.Null(run.CurrentSubmission.JobId);
        // 磁盘断言（会诊要求）：盘上仍是「准备阶段」的事实——Submitted、SendAttempted、空 jobId。
        var diskMutated = _runs.Load(run.RunId)!;
        Assert.Equal(SubmitIntentState.Submitted, diskMutated.CurrentSubmission!.Intent);
        Assert.True(diskMutated.CurrentSubmission.SendAttempted);
        Assert.Null(diskMutated.CurrentSubmission.JobId);
    }

    // ── 17. 对账路径（会诊反例 2）：提交被替换成另一实例 → 不得落盘受理事实 ──

    [Fact]
    public async Task Reconcile_SubmissionReplaced_DoesNotPersistAccepted()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
            ProjectOriginalPayloadEvidence = true,
            JobList = SnapshotFor(run.CurrentSubmission!.Key, run.WireRunId, "n-1", 0, "job-reconciled"),
        };
        // 会诊要求：只换对象引用、**完整保留冻结身份字段**——否则单删 ReferenceEquals 比较也测不出来。
        var original = run.CurrentSubmission!;
        port.BeforeSend = () => run.CurrentSubmission = new WorkflowSubmission
        {
            Key = original.Key, NodeId = original.NodeId, Occurrence = original.Occurrence,
            LoopIteration = original.LoopIteration, Attempt = original.Attempt,
            Epoch = original.Epoch, Intent = original.Intent,
        };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.True(result.Uncertain);
        Assert.Single(port.Sends);
        Assert.Null(run.CurrentSubmission!.JobId);
        Assert.NotEqual(SubmitIntentState.Accepted, original.Intent); // 旧实例也不得被错误写成 Accepted
        // 磁盘断言（会诊要求）：盘上不得出现受理事实。
        var diskReplaced = _runs.Load(run.RunId)!;
        Assert.Null(diskReplaced.CurrentSubmission!.JobId);
        Assert.NotEqual(SubmitIntentState.Accepted, diskReplaced.CurrentSubmission.Intent);
    }

    // ── 18. 对账路径（会诊要求）：取消分支保持「对账后重抛 OCE」纪律，且不重发 ──

    [Fact]
    public async Task Reconcile_OperationCanceledToSend_ReThrowsAfterReconcile()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new OperationCanceledException(),
            ProjectOriginalPayloadEvidence = true,
            JobList = SnapshotFor(run.CurrentSubmission!.Key, run.WireRunId, "n-1", 0, "job-reconciled"),
        };
        // 取消发生时，盘上是否已带 jobId（证明「先落盘、后取消」）
        port.OnCancelObserve = () => _runs.Load(run.RunId)?.CurrentSubmission?.JobId == "job-reconciled";
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default));

        Assert.Single(port.Sends); // 取消也不重发
        // 会诊要求：断言对账确实发生、受理事实已落盘、且「先落盘、后取消」的顺序。
        Assert.True(port.JobListQueries > 0, "取消分支必须执行按幂等键的对账查询");
        var diskAfterOce = _runs.Load(run.RunId)!;
        Assert.Equal("job-reconciled", diskAfterOce.CurrentSubmission!.JobId);
        Assert.True(port.CancelCalls > 0, "对账命中后必须请求远端取消");
        Assert.True(port.CancelObservedPersistedJob, "顺序必须是「先落盘 jobId、后请求取消」");
    }

    // ── 19. 并发写入边界（[P7／§12.2 第 3 项「字段合并」更新语义]）：并发推进 ⇒ **合并不丢事实**；
    //        若并发推进改变了**本次发送身份**，则该命中不属于本笔 ⇒ 不落盘、保守 Unknown（零重发）。 ──

    [Fact]
    public async Task Reconcile_ConcurrentRecordAdvance_WriteBackRejected_ConservativeUnknown()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
            ProjectOriginalPayloadEvidence = true,
            JobList = SnapshotFor(run.CurrentSubmission!.Key, run.WireRunId, "n-1", 0, "job-reconciled"),
        };
        // 情形①：另一写入者只改了**非自有字段**（Note）⇒ 合并写回：受理事实落盘 **且** 并发改动保留。
        port.BeforeSend = () =>
        {
            var fresh = _runs.Load(run.RunId)!;
            fresh.Note = (fresh.Note ?? "") + "；并发推进";
            _runs.Update(fresh);
        };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.True(result.Accepted, "并发只改动非自有字段时，受理事实应经**字段合并**落盘（P7）");
        Assert.Single(port.Sends);     // 零重发（对账命中不重发）
        var disk = _runs.Load(run.RunId)!;
        Assert.Equal("job-reconciled", disk.CurrentSubmission!.JobId);          // 受理事实已在盘上
        Assert.Contains("并发推进", disk.Note);                                  // 并发写入者的改动**未被覆盖**
        Assert.Equal(SubmitIntentState.Accepted, run.CurrentSubmission!.Intent); // 内存视图与盘上一致
        Assert.Equal("job-reconciled", run.CurrentSubmission.JobId);

        // 情形②：另一写入者推进了**本次发送身份**（attempt 前进）⇒ 命中不属于本笔：不落盘、保守 Unknown。
        var (run2, node2, occurrence2) = Seed();
        var port2 = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
            ProjectOriginalPayloadEvidence = true,
            JobList = SnapshotFor(run2.CurrentSubmission!.Key, run2.WireRunId, "n-1", 0, "job-reconciled"),
        };
        port2.BeforeSend = () =>
        {
            var fresh = _runs.Load(run2.RunId)!;
            fresh.CurrentSubmission!.Attempt += 1;   // 发送身份被并发推进（attempt 改变）
            Assert.Throws<RunRecordConflictException>(() => _runs.Update(fresh));
            // Retain the downstream conflict counterexample using explicit private-file corruption.
            File.WriteAllText(Path.Combine(_dir, "runs", fresh.RunId + ".run.json"), System.Text.Json.JsonSerializer.Serialize(fresh));
        };
        var boundary2 = new BgiWorkflowExecutionBoundary(port2, _runs);
        var result2 = await boundary2.SubmitAsync(
            new WorkflowSubmitRequest(run2, occurrence2, node2, SuppressConfigCompletionAction: true), default);

        Assert.True(result2.Uncertain, "并发推进改变发送身份时不得绑定受理事实（保守 Unknown）");
        Assert.Single(port2.Sends);                  // 零重发
        var disk2 = _runs.Load(run2.RunId)!;
        Assert.Null(disk2.CurrentSubmission!.JobId); // 盘上不得出现该笔的受理事实
    }

    // ── 14. 会诊复核反例：把同一冻结载荷**重新包装**成新实例 → 仍不得二次发送 ──
    // 消费状态绑定在冻结凭据（WorkflowSubmission 实例）上，而不是包装实例上——因此重新包装不重置护栏。

    [Fact]
    public async Task SendPrepared_RewrappedSamePayload_StillSendsOnlyOnce()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);
        var first = boundary.PrepareSubmit(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true));
        // 会诊反例的构造方式（现在仍可表达）：同凭据、同载荷，但换一个新的包装实例。
        var rewrapped = BgiWorkflowExecutionBoundary.PreparedSubmit.Ok(first.Run!, first.Submission!, first.Payload!);

        var r1 = await boundary.SendPreparedAsync(first, default);
        var r2 = await boundary.SendPreparedAsync(rewrapped, default);

        Assert.True(r1.Accepted);
        Assert.True(r2.Uncertain); // 凭据已消费：响亮未知，不重发
        Assert.Single(port.Sends);
    }

    // ── 15. [P8／§24.62] 「可证实未发送 vs 已进入线路后失败」判据接缝（本批核心） ─────────────────────

    /// <summary>
    /// **[P8／§24.62]** 判据**只认证据载体**（`BgiNotSentException`）：证据码三值 ⇒ true；
    /// **一切其它异常**（含同样派生自 `InvalidOperationException` 的普通异常、`IOException`、`TimeoutException`、
    /// `OperationCanceledException`）⇒ false（必须保留未决责任）——防「按消息文本/类型泛化推断」把可能已发送
    /// 误判为未发送。
    /// </summary>
    [Theory]
    [InlineData("not_sent_channel", true)]
    [InlineData("not_sent_pipe", true)]
    [InlineData("not_sent_local", true)]
    [InlineData("plain_invalid_operation", false)]
    [InlineData("io", false)]
    [InlineData("timeout", false)]
    [InlineData("canceled", false)]
    public void IsProvenNotSent_EvidenceCarrierOnly(string kind, bool expected)
    {
        Exception ex = kind switch
        {
            "not_sent_channel" => new BgiNotSentException(BgiNotSentException.ChannelNotReady, "ext 通道未就绪"),
            "not_sent_pipe" => new BgiNotSentException(BgiNotSentException.PipeNotConnected, "ext 通道未就绪"),
            "not_sent_local" => new BgiNotSentException(BgiNotSentException.LocalRequestRejected, "重复的 ext 请求 ID"),
            // 同一消息文本、同一父类型，但**不是**证据载体 ⇒ 不得判为未发送
            "plain_invalid_operation" => new InvalidOperationException("ext 通道未就绪"),
            "io" => new IOException("broken pipe"),
            "timeout" => new TimeoutException("no response"),
            _ => new OperationCanceledException(),
        };

        Assert.Equal(expected, BgiWorkflowExecutionBoundary.IsProvenNotSent(ex));
        Assert.Equal(expected, BgiWorkflowExecutionBoundary.NotSentEvidence(ex) is not null);
    }

    /// <summary>
    /// **[P8／§24.62]** 证据码**枚举全表**＋兼容性：①白名单恰三值且与 `AllEvidenceCodes` 一致
    /// （新增证据码必须同时登记，防「悄悄加码」）；②证据载体仍**派生自 `InvalidOperationException`**
    /// ⇒ 既有 `catch (InvalidOperationException)` 降级路径**逐字不变**。
    /// </summary>
    [Fact]
    public void BgiNotSentEvidence_WhitelistEnumerated_AndStaysCatchCompatible()
    {
        Assert.Equal(3, BgiNotSentException.AllEvidenceCodes.Count);
        Assert.All(BgiNotSentException.AllEvidenceCodes,
            code => Assert.True(BgiNotSentException.IsKnownEvidenceCode(code), code));
        Assert.False(BgiNotSentException.IsKnownEvidenceCode("unknown_code"));
        Assert.False(BgiNotSentException.IsKnownEvidenceCode(null));

        var ex = new BgiNotSentException(BgiNotSentException.ChannelNotReady, "ext 通道未就绪");
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("ext 通道未就绪", ex.Message);   // 既有文案逐字保留（外部降级判据不得失真）

        // **[会诊重要项处置]** 未登记证据码**构造即拒**（fail-fast）＋判据侧再校验（双层防线）：
        // 否则 `new BgiNotSentException("write_failed", …)` 这类误用会自动打开重试窗口，使枚举边界失效。
        Assert.Throws<ArgumentException>(() => new BgiNotSentException("write_failed", "写后失败（未登记码）"));
        Assert.Throws<ArgumentException>(() => new BgiNotSentException("", "空码"));
        Assert.Throws<ArgumentException>(() => new BgiNotSentException(null!, "空码"));
        Assert.Throws<ArgumentException>(() => new BgiNotSentException("CHANNEL_NOT_READY", "大小写变形"));  // 大小写敏感

        // **[会诊重要项处置·第二层防线独立取证]** 绕过构造期校验（仅测试接缝）后，判据**仍**不得把未登记码
        // 当成「可证实未发送」——否则「只看异常类型」的退化实现会让本断言转红。
        Assert.False(BgiWorkflowExecutionBoundary.IsProvenNotSent(
            BgiNotSentException.CreateBypassingWhitelistForTest("write_failed")));
        Assert.False(BgiWorkflowExecutionBoundary.IsProvenNotSent(
            BgiNotSentException.CreateBypassingWhitelistForTest(null)));
        Assert.False(BgiWorkflowExecutionBoundary.IsProvenNotSent(
            BgiNotSentException.CreateBypassingWhitelistForTest("CHANNEL_NOT_READY")));
        // **[第 3 轮会诊重要项处置]** 证据码读取与判据**同口径**：未登记码一律**不得**返回非空证据
        // （否则后续调用方可能把非空码误当证明）。
        Assert.Null(BgiWorkflowExecutionBoundary.NotSentEvidence(
            BgiNotSentException.CreateBypassingWhitelistForTest("write_failed")));
        Assert.Null(BgiWorkflowExecutionBoundary.NotSentEvidence(
            BgiNotSentException.CreateBypassingWhitelistForTest(null)));
        Assert.Null(BgiWorkflowExecutionBoundary.NotSentEvidence(
            BgiNotSentException.CreateBypassingWhitelistForTest("CHANNEL_NOT_READY")));   // 大小写变形 ⇒ 无证据
        // 正向对照：同一接缝用已登记码 ⇒ 判据成立（证明上面三支不是因为对象构造失败而被判 false）
        Assert.True(BgiWorkflowExecutionBoundary.IsProvenNotSent(
            BgiNotSentException.CreateBypassingWhitelistForTest(BgiNotSentException.PipeNotConnected)));
        Assert.Equal(BgiNotSentException.PipeNotConnected, BgiWorkflowExecutionBoundary.NotSentEvidence(
            BgiNotSentException.CreateBypassingWhitelistForTest(BgiNotSentException.PipeNotConnected)));
    }

    /// <summary>
    /// **[P8／§24.62·会诊重要项处置]** `MapAdmissionResultToBoundary` 对 `RetryableRejected` **单独分流**：
    /// 门面已按 §3.3 结清（责任 `Settled`、`Submission` 关闭、重试窗口派生）⇒ 回映射到 Runner 的结果
    /// **必须保留可重试性**；其余确定拒绝（`TerminalRejected`／`NotSelected`／`F11Blocked`／`NeedPreemptConfirm`）
    /// 保持**终局**（`Retryable=false`）。
    /// </summary>
    [Fact]
    public void MapAdmissionResultToBoundary_PreservesRetryWindowForRetryableRejected()
    {
        var retryable = TaskCenterHost.MapAdmissionResultToBoundary(new AdmissionResult
        {
            Kind = AdmissionResultKind.RetryableRejected,
            ReasonCode = "channel_not_ready",
            Detail = "可证实未发送 ⇒ 窗口内可经 RetryAsync 再入场。",
        });
        Assert.False(retryable.Accepted);
        Assert.False(retryable.Uncertain);
        Assert.True(retryable.Retryable);

        foreach (var kind in new[]
                 {
                     AdmissionResultKind.TerminalRejected, AdmissionResultKind.NotSelected,
                     AdmissionResultKind.F11Blocked, AdmissionResultKind.NeedPreemptConfirm,
                 })
        {
            var terminal = TaskCenterHost.MapAdmissionResultToBoundary(new AdmissionResult
            {
                Kind = kind, ReasonCode = "x", Detail = "终局确定拒绝",
            });
            Assert.False(terminal.Uncertain);
            Assert.False(terminal.Retryable);   // 终局：不开重试窗口
        }
    }

    /// <summary>
    /// **[P8／§24.62]** 发送层抛**证据载体** ⇒ `Rejected` ＋ **可重试窗口**（§3.2a 无损拒绝类），
    /// **不是** `Uncertain`；且对账**仍先执行**（一旦命中即证明证据有误、按命中回执，绝不硬判未发送）；
    /// 发送恰一次（不重发）。
    /// </summary>
    [Theory]
    [InlineData(BgiNotSentException.ChannelNotReady, "ext 通道未就绪")]
    [InlineData(BgiNotSentException.PipeNotConnected, "ext 通道未就绪（管道未连接）")]
    [InlineData(BgiNotSentException.LocalRequestRejected, "重复的 ext 请求 ID")]
    public async Task Send_ProvenNotSent_RejectedWithRetryWindow_ReconcileStillAttempted(string evidenceCode, string message)
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort { SendThrows = new BgiNotSentException(evidenceCode, message) };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.False(result.Accepted);
        Assert.False(result.Uncertain);          // **不得**按不可考停驻
        Assert.True(result.Retryable);           // 无损拒绝类 ⇒ 开重试窗口
        Assert.Contains(evidenceCode, result.RejectReason!);
        Assert.Single(port.Sends);               // 一次尝试、不重发
        Assert.True(port.JobListQueries > 0, "对账必须先执行：一旦命中即证明证据有误，不得硬判未发送");
    }

    /// <summary>
    /// **[P8／§24.62 反例]** 「已进入（或可能已进入）线路后失败」= 写入后无响应/部分写入等 ⇒ 必须**保留未决责任**
    /// （`Uncertain` ＋ `Retryable=false`）：不得因「异常看起来弱」升级为未发送（双跑风险）。
    /// </summary>
    [Theory]
    [InlineData("io")]
    [InlineData("timeout")]
    public async Task Send_SentThenFailed_StaysUncertain_NoRetryWindow(string kind)
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = kind == "io" ? new IOException("broken pipe") : new TimeoutException("no response"),
        };
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.False(result.Accepted);
        Assert.True(result.Uncertain);
        Assert.False(result.Retryable);
        Assert.Single(port.Sends);
        Assert.True(port.JobListQueries > 0);
    }

    /// <summary>
    /// **[P8／§24.62 真实传输]** 生产客户端**未连接**时抛**证据载体**且证据码＝`channel_not_ready`
    /// （沿用既有文案与父类型，既有降级 catch 不受影响）。
    /// **[会诊重要项处置]** **不动全局静态接缝**：`BgiExternalClient` 仅在 `Start/StartAsync` 后才会连接，
    /// 本夹具**不启动**客户端 ⇒ `State=Down`、`_pipe=null` 恒成立，**与并行测试无相互污染**（原先改写静态
    /// 管道名会把随机假名泄漏给并行构造的客户端）。
    /// </summary>
    [Fact]
    public async Task RealClient_ChannelNotReady_ThrowsNotSentEvidence()
    {
        using var client = new BgiExternalClient();   // 未 Start：State=Down、无管道（不写任何全局接缝）
        var ex = await Assert.ThrowsAsync<BgiNotSentException>(() =>
            client.SendCommandAsync(BgiExternalClient.ExternalOperations.TaskStart, new { }, TimeSpan.FromSeconds(1), default));

        Assert.Equal(BgiNotSentException.ChannelNotReady, ex.EvidenceCode);
        Assert.IsAssignableFrom<InvalidOperationException>(ex);
        Assert.Equal("ext 通道未就绪", ex.Message);
        Assert.True(BgiWorkflowExecutionBoundary.IsProvenNotSent(ex));
    }
}
