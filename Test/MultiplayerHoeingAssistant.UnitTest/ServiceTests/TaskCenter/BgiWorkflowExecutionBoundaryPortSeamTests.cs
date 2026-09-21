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
        var run = _runs.CreateRun("wf-boundary", "r-1", note: "边界夹具种子");
        var occurrence = new WorkflowNodeOccurrence("n-1", 0, 0, 0);
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

    // ── 1. 正常路径：冻结身份 → 恰好发送一次 → 受理带 jobId；载荷携带完整出现身份 ──

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
    public async Task Submit_WhitelistedFailure_MapsToRejected()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort();
        port.ScriptedResponses.Add(new BgiExternalResponse { Success = false, ErrorCode = "stale_epoch" });
        var boundary = new BgiWorkflowExecutionBoundary(port, _runs);

        var result = await boundary.SubmitAsync(new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), default);

        Assert.False(result.Accepted);
        Assert.False(result.Uncertain);
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
            Jobs = [new BgiJobInfo { IdempotencyKey = key, WorkflowRunId = wireRunId, NodeId = nodeId, Iteration = iteration, JobId = jobId, State = "queued" }],
        };

    [Fact]
    public async Task Reconcile_UncertainSendHitWithFrozenIdentity_PersistsAccepted()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
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

    [Fact]
    public async Task Reconcile_SubmissionIdentityMutatedInPlace_DoesNotPersistAccepted()
    {
        var (run, node, occurrence) = Seed();
        var port = new FakePort
        {
            SendThrows = new InvalidOperationException("transport down"),
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
            JobList = SnapshotFor(run2.CurrentSubmission!.Key, run2.WireRunId, "n-1", 0, "job-reconciled"),
        };
        port2.BeforeSend = () =>
        {
            var fresh = _runs.Load(run2.RunId)!;
            fresh.CurrentSubmission!.Attempt += 1;   // 发送身份被并发推进（attempt 改变）
            _runs.Update(fresh);
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
}
