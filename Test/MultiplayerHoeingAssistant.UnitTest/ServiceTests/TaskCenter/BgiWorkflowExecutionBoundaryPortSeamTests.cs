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

        public bool HasCapability(string name) => Ready;

        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            Sends.Add((operation, payload is null ? "" : System.Text.Json.JsonSerializer.Serialize(payload)));
            return Task.FromResult(ScriptedResponses.Count > 0
                ? ScriptedResponses[Sends.Count - 1 < ScriptedResponses.Count ? Sends.Count - 1 : ScriptedResponses.Count - 1]
                : new BgiExternalResponse { Success = true, Data = "{\"status\":\"accepted\",\"taskHandle\":\"job-1\"}" });
        }

        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct) => Task.FromResult<BgiJobListSnapshot?>(null);

        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult<(string?, BgiJobInfo?)>((null, null));

        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
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

    // ── 4. 两段拆分可独立调用：PrepareSubmit 不发送、SendPreparedAsync 才发送（接线态仲裁面必须插在两者之间）──

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
}
