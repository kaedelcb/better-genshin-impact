using System;
using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R5.2 B2-γ 第 2 步：把 Runner 的**后继节点提交**接进仲裁面的装饰器（设计稿 §5.2/§12）。
///
/// 除 <see cref="SubmitAsync"/> 外的成员一律逐字委托 inner——终态观察/远端取消与仲裁无关，不得被本层改写。
/// <see cref="SubmitAsync"/> 交给注入的准入委托：由宿主构造后继候选（节点出现身份+轮次+attempt、同 run 继承
/// runId 段、授权来自运行台账合法游标）后经门面提交；真正的线协议发送发生在门面 Sender 回调内，
/// 从而满足 §0 的统一链路次序（锁内占位 → 锁外发送 → 三态对账）。
///
/// **本类不做冻结、不直接发送、不接触租约**：冻结与发送归执行边界的发送段（§12 架构决定），
/// 本层只负责「换一条进入门面的路」。
/// </summary>
internal sealed class ArbitrationWorkflowExecutionBoundary : IWorkflowExecutionBoundary
{
    private readonly IWorkflowExecutionBoundary _inner;
    private readonly Func<WorkflowSubmitRequest, CancellationToken, Task<BoundarySubmitResult>> _submitViaAdmission;

    public ArbitrationWorkflowExecutionBoundary(
        IWorkflowExecutionBoundary inner,
        Func<WorkflowSubmitRequest, CancellationToken, Task<BoundarySubmitResult>> submitViaAdmission)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _submitViaAdmission = submitViaAdmission ?? throw new ArgumentNullException(nameof(submitViaAdmission));
    }

    /// <summary>能力实况=inner 实况（本层不缓存、不改写能力判定）。</summary>
    public bool SingleNativeSupported => _inner.SingleNativeSupported;

    public bool SuppressConfigCompletionSupported => _inner.SuppressConfigCompletionSupported;

    /// <summary>后继提交唯一的分流点：经仲裁面（其余路径一律与 inner 等价）。</summary>
    public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        => _submitViaAdmission(request, ct);

    public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        => _inner.AwaitTerminalAsync(jobId, ct);

    public Task RequestCancelAsync(string jobId, CancellationToken ct)
        => _inner.RequestCancelAsync(jobId, ct);
}
