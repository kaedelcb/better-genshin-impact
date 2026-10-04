using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

// R5.2 B2-gamma step2: delegation contract fixtures for the successor-submission decorator.
// Pins two things: (1) only SubmitAsync is rerouted to the admission face;
// (2) capabilities / terminal observation / remote cancel delegate verbatim to inner.
public class ArbitrationWorkflowExecutionBoundaryTests
{
    [Fact]
    public async Task OriginalRound_ThreeParameterRunnerReconcileUsesAdmissionSettlement()
    {
        var inner = new StubBoundary();
        var request = DummyRequest();
        var sub = new WorkflowSubmission { Key = "original" };
        var calls = 0;
        using var cancellation = new CancellationTokenSource();
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner,
            (_, _) => Task.FromResult(BoundarySubmitResult.UnknownWith("unused")),
            (run, submission, token) =>
            {
                Assert.Same(request.Run, run);
                Assert.Same(sub, submission);
                Assert.Equal(cancellation.Token, token);
                calls++;
                return Task.FromResult(BoundarySubmitResult.UnknownWith("original settlement failed"));
            });
        IWorkflowExecutionBoundary runnerBoundary = decorator;
        var result = await runnerBoundary.ReconcileSubmissionAsync(request.Run, sub, cancellation.Token);
        Assert.True(result.Uncertain);
        Assert.Equal("original settlement failed", result.RejectReason);
        Assert.Equal(1, calls);
    }

    private sealed class StubBoundary : IWorkflowExecutionBoundary
    {
        public int SubmitCalls;
        public int AwaitCalls;
        public int CancelCalls;
        public bool SingleNativeSupported { get; set; } = true;
        public bool SuppressConfigCompletionSupported { get; set; }

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            SubmitCalls++;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("inner-job"));
        }

        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            AwaitCalls++;
            return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        }

        public Task RequestCancelAsync(string jobId, CancellationToken ct)
        {
            CancelCalls++;
            return Task.CompletedTask;
        }
    }

    private static WorkflowSubmitRequest DummyRequest()
    {
        var run = new WorkflowRunRecord { RunId = "run-x", WorkflowId = "wf-x" };
        var node = new WorkflowNode { NodeId = "n-1", Kind = "resource.oneDragonConfig" };
        return new WorkflowSubmitRequest(run, new WorkflowNodeOccurrence("n-1", 0, 0, 0), node, true);
    }

    [Fact]
    public async Task SubmitAsync_RoutesToAdmission_NotToInner()
    {
        var inner = new StubBoundary();
        var admitted = 0;
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (req, ct) =>
        {
            admitted++;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("admitted-job"));
        });

        var result = await decorator.SubmitAsync(DummyRequest(), default);

        Assert.Equal(1, admitted);
        Assert.Equal(0, inner.SubmitCalls);
        Assert.Equal("admitted-job", result.JobId);
    }

    [Fact]
    public void Capabilities_MirrorInner()
    {
        var inner = new StubBoundary { SingleNativeSupported = false, SuppressConfigCompletionSupported = true };
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.False(decorator.SingleNativeSupported);
        Assert.True(decorator.SuppressConfigCompletionSupported);
    }

    [Fact]
    public async Task AwaitAndCancel_DelegateToInner()
    {
        var inner = new StubBoundary();
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => throw new InvalidOperationException("must not be called"));

        var terminal = await decorator.AwaitTerminalAsync("job-1", default);
        await decorator.RequestCancelAsync("job-1", default);

        Assert.Equal("succeeded", terminal.Terminal);
        Assert.Equal(1, inner.AwaitCalls);
        Assert.Equal(1, inner.CancelCalls);
    }
    /// <summary>
    /// **§12.2「第 3 步其他强制项·夹具盲区」①：参数与令牌原样透传**——装饰器必须把**同一个**
    /// `WorkflowSubmitRequest` 实例与**同一个** `CancellationToken` 交给准入委托（不得重建请求、不得换令牌）。
    /// </summary>
    [Fact]
    public async Task SubmitAsync_PassesRequestInstanceAndTokenVerbatim()
    {
        var inner = new StubBoundary();
        var request = DummyRequest();
        using var cts = new CancellationTokenSource();
        WorkflowSubmitRequest? seenRequest = null;
        CancellationToken seenToken = default;
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (req, ct) =>
        {
            seenRequest = req;
            seenToken = ct;
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("admitted-job"));
        });

        var result = await decorator.SubmitAsync(request, cts.Token);

        Assert.Same(request, seenRequest);            // 同一实例（请求快照不得被重建/复制）
        Assert.Equal(cts.Token, seenToken);           // 同一令牌（取消语义原样透传）
        Assert.Equal("admitted-job", result.JobId);
        Assert.Equal(0, inner.SubmitCalls);
    }

    /// <summary>
    /// **夹具盲区②：故障与取消的透明传播**——同步抛错、故障 Task、取消 Task 三种形态**不得**被本层
    /// 吞掉/改写（否则上层会把「不可考」误当「确定未受理」）。
    /// </summary>
    [Fact]
    public async Task SubmitAsync_PropagatesSyncThrowFaultedTaskAndCancelledTask()
    {
        var inner = new StubBoundary();
        // 预先构造**故障/取消任务实体**：装饰器必须返回**同一任务实例**（不得 wrap 成新 Task、不得改写故障形态）。
        var faultedTask = Task.FromException<BoundarySubmitResult>(new IOException("faulted-boom"));
        var cancelledToken = new CancellationToken(canceled: true);
        var cancelledTask = Task.FromCanceled<BoundarySubmitResult>(cancelledToken);
        var syncThrow = new ArbitrationWorkflowExecutionBoundary(inner,
            (_, _) => throw new InvalidOperationException("sync-boom"));
        var faulted = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => faultedTask);
        var cancelled = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => cancelledTask);

        // ① 同步抛错必须**同步**逸出（不得被包成 faulted Task 后才抛出）。
        var syncEx = Record.Exception(() => { _ = syncThrow.SubmitAsync(DummyRequest(), default); });
        Assert.IsType<InvalidOperationException>(syncEx);   // 同步抛出的**原异常**（而非包裹后的异步故障）
        // ② 故障 Task 必须**原样返回同一实例**（形态与实体都不被改写）。
        Assert.Same(faultedTask, faulted.SubmitAsync(DummyRequest(), default));
        await Assert.ThrowsAsync<IOException>(() => faultedTask);
        // ③ 取消 Task 同样原样返回同一实例，且保持取消态（不得改写成普通异常或普通故障）。
        Assert.Same(cancelledTask, cancelled.SubmitAsync(DummyRequest(), default));
        Assert.True(cancelledTask.IsCanceled);
        Assert.Equal(0, inner.SubmitCalls);   // 三条异常路径都不得回落到 inner
    }

    /// <summary>
    /// **夹具盲区③：能力值变化后必须复读**——能力属性每次读取都取 inner 实况（本层不得在构造时缓存常量）。
    /// </summary>
    [Fact]
    public void Capabilities_AreReReadPerAccess_NotCachedAtConstruction()
    {
        var inner = new StubBoundary { SingleNativeSupported = false, SuppressConfigCompletionSupported = false };
        var decorator = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) => throw new InvalidOperationException("must not be called"));

        Assert.False(decorator.SingleNativeSupported);            // 构造期取值
        Assert.False(decorator.SuppressConfigCompletionSupported);
        inner.SingleNativeSupported = true;                       // 运行期实况变化
        inner.SuppressConfigCompletionSupported = true;
        Assert.True(decorator.SingleNativeSupported);             // 必须复读（不得缓存）
        Assert.True(decorator.SuppressConfigCompletionSupported);
    }
}
