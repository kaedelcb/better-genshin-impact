using System.Collections.Concurrent;
using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>节点执行提交请求（流程只持类型化引用；收尾抑制标记随调用传递，D10）。</summary>
public sealed record WorkflowSubmitRequest(
    WorkflowRunRecord Run,
    WorkflowNodeOccurrence Occurrence,
    WorkflowNode Node,
    bool SuppressConfigCompletionAction);

public enum BoundarySubmitKind
{
    Accepted,
    Rejected,
    Unknown,
    Wait,
    Hold,
}

/// <summary>提交边界的互斥判别结果；Wait/Hold 不等于拒绝，也不授予发送许可。</summary>
public sealed record BoundarySubmitResult
{
    private BoundarySubmitResult(BoundarySubmitKind kind, string? jobId, string? rejectReason,
        bool retryable, LocalWaitDecisionRecord? waitDecision)
    {
        Kind = kind;
        JobId = jobId;
        RejectReason = rejectReason;
        Retryable = retryable;
        WaitDecision = waitDecision;
    }

    public BoundarySubmitKind Kind { get; }
    public bool Accepted => Kind == BoundarySubmitKind.Accepted;
    public string? JobId { get; }
    public string? RejectReason { get; }
    public bool Uncertain => Kind == BoundarySubmitKind.Unknown;
    public bool Retryable { get; }
    public LocalWaitDecisionRecord? WaitDecision { get; }

    public static BoundarySubmitResult AcceptedWith(string jobId) => new(BoundarySubmitKind.Accepted, jobId, null, false, null);
    public static BoundarySubmitResult Rejected(string reason) => new(BoundarySubmitKind.Rejected, null, reason, false, null);
    /// <summary>
    /// **[P8／§24.62]** 确定拒绝·**开重试窗口**（§3.2a）：证据＝**可证实未发送**（`BgiNotSentException`）
    /// 或门面已结清的等值无损拒绝（`AdmissionResultKind.RetryableRejected`）。语义与普通「终局确定拒绝」
    /// 分开：窗口内可经**新许可**（`RetryAsync`）再入场。
    /// </summary>
    public static BoundarySubmitResult RejectedWithRetryWindow(string reason)
        => new(BoundarySubmitKind.Rejected, null, reason, true, null);
    public static BoundarySubmitResult UnknownWith(string reason) => new(BoundarySubmitKind.Unknown, null, reason, false, null);
    public static BoundarySubmitResult WaitWith(string reason, LocalWaitDecisionRecord? decision = null)
        => new(BoundarySubmitKind.Wait, null, reason, false, decision);
    public static BoundarySubmitResult HoldWith(string reason, LocalWaitDecisionRecord? decision = null)
        => new(BoundarySubmitKind.Hold, null, reason, false, decision);
}

/// <summary>边界观察终态（R4.8 一轮 B1/I3 结构化：远端原词与本地查询不可考分开）。
/// Terminal=远端终态原词（succeeded/failed/cancelled/skipped/rejected；null=未观察到）；
/// Uncertain=true 时调用方走 Unknown 停驻，不得经词汇映射落 failed；
/// Reason/ErrorCode 受控原因上 UI。</summary>
public sealed record BoundaryTerminalResult(string? Terminal, bool Uncertain, string? Reason, string? ErrorCode = null, bool ExecutionExitConfirmed = false, string? ExecutionExitDisposition = null)
{
    public static BoundaryTerminalResult Observed(string terminal, string? reason = null, string? errorCode = null, bool exitConfirmed = true, string? exitDisposition = "execution_exited")
        => new(terminal, false, reason, errorCode, exitConfirmed, exitConfirmed ? exitDisposition : null);
    public static BoundaryTerminalResult UncertainWith(string reason) => new(null, true, reason);
}

/// <summary>
/// 执行边界（R4.5 可测试接缝）：流程引擎 → BGI 单项/配置组/整龙能力的唯一出口。
/// 生产实现经 ext.task.start（严格合同）+ ext.job.status 终态查询；测试用假实现。
/// 等待不占槽位由引擎保证：等待期间不调用本接口、不提交任何作业。
/// </summary>
public interface IWorkflowExecutionBoundary
{
    /// <summary>执行端 task.single.native 能力实况（D4 预检输入）。</summary>
    bool SingleNativeSupported { get; }

    /// <summary>执行端 execution.suppressConfigCompletionAction 能力实况（B6/E4'；缺省 true=测试接缝免接线，生产按 capability 实况）。</summary>
    bool SuppressConfigCompletionSupported => true;
    // Defaults are component-only seams; both production boundary implementations require authority.
    bool RequiresStopAuthority => false;
    Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string intentId, long intentTimestamp, CancellationToken ct)
        => Task.FromResult<WorkflowStopAuthorityRecord?>(null);
    Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
        => Task.FromResult<bool?>(null);

    /// <summary>提交节点执行（调用前引擎已持久化提交意图，D11）。</summary>
    Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct);

    /// <summary>等待作业终态（R4.8 一轮 B3 纯观察：取消只终止等待，绝不再发远端取消——取消走 RequestCancelAsync）。</summary>
    Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct);

    /// <summary>观察同一冻结提交的业务终态及执行退出；生产实现核验epoch/出现身份/退出事实。</summary>
    Task<BoundaryTerminalResult> AwaitSubmissionExitAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
        => AwaitTerminalAsync(submission.JobId!, ct);

    /// <summary>请求远端取消在飞作业（R4.8 一轮 B3：best-effort，应答不代表清理完成，终态以 AwaitTerminalAsync 观察为准）。
    /// 默认 no-op（测试假实现免接线）；生产实现 = ext.task.cancel（ownedOnly=v1）。</summary>
    Task<BoundarySubmitResult> ReconcileSubmissionAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
        => Task.FromResult(BoundarySubmitResult.UnknownWith("边界不支持原键只读对账"));

    Task RequestSubmissionCancelAsync(WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
        => RequestCancelAsync(submission.JobId!, ct);
    Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>前置结果状态（R4.6 B3 结构化：Unknown 不按普通失败续跑，走保守路径）。</summary>
public enum PrerequisiteStatus
{
    /// <summary>放行资源执行。</summary>
    Proceed,
    /// <summary>真实失败（受控原因）。</summary>
    Failed,
    /// <summary>受理即拒（能力/队列/合同）。</summary>
    Rejected,
    /// <summary>取消（远端已确认——显式跳过确认链走完）。</summary>
    Cancelled,
    /// <summary>结果不确定（禁止续跑/重发，标 Unknown 待对账）。</summary>
    Unknown,
}

/// <summary>前置结果（R4.6 B3 结构化；JobId 受理即回报，供引擎落盘 submitted 事实）。</summary>
public sealed record PrerequisiteResult(PrerequisiteStatus Status, string? Reason, string? JobId = null)
{
    public static readonly PrerequisiteResult ProceedInstance = new(PrerequisiteStatus.Proceed, null);
    public bool Proceed => Status == PrerequisiteStatus.Proceed;
    public static PrerequisiteResult FailedWith(string reason) => new(PrerequisiteStatus.Failed, reason);
}

/// <summary>前置策略适配器（D8：账号/兑换等前置动作的受控执行出口；生产实现 = BgiWorkflowPrerequisiteAdapter）。</summary>
public interface IWorkflowPrerequisiteAdapter
{
    /// <summary>支持的前置策略类型（R4.6 I1 能力协商；缺省=全部——测试假实现免接线；生产按 ext capability 实况）。</summary>
    IReadOnlySet<string> SupportedKinds => WorkflowKindCatalog.StrategyKinds;

    /// <summary>执行前置策略；事实绑定 run/节点出现/attempt（意图记录由引擎先行落盘）。</summary>
    Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, CancellationToken ct);

    /// <summary>恢复对账（R4.6 B2：意图/在飞记录先查远端权威终态；查不到=Unknown，不盲目重发）。默认 Unknown（保守）。</summary>
    Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
        => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "适配器不支持对账", record.JobId));

    Task<PrerequisiteResult> ReconcileAsync(WorkflowRunRecord run, PrerequisiteActionRecord record, CancellationToken ct)
        => ReconcileAsync(record, ct);

    Task<PrerequisiteResult> ConfirmCancellationAsync(WorkflowRunRecord run, PrerequisiteActionRecord record, CancellationToken ct)
        => ConfirmCancellationAsync(record, ct);

    /// <summary>前置期显式跳过的远端取消确认（B4 同构；确认超时=Unknown）。默认直接确认（测试假实现无远端）。</summary>
    Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
        => Task.FromResult(new PrerequisiteResult(record.SendAttempted || !string.IsNullOrEmpty(record.JobId)
            ? PrerequisiteStatus.Unknown : PrerequisiteStatus.Cancelled, "缺少远端取消观察实现", record.JobId));
}

/// <summary>收尾执行结果（R4.6 E3'：executed / rejected / unknown / cancelled；发送 ≠ 完成）。</summary>
public sealed record TerminalExecutionResult(string State, string? JobId, string? Reason)
{
    public static TerminalExecutionResult Executed(string? jobId) => new("executed", jobId, null);
    public static TerminalExecutionResult RejectedWith(string? reason) => new("rejected", null, reason);
    public static TerminalExecutionResult UnknownWith(string? jobId, string? reason) => new("unknown", jobId, reason);
    public static TerminalExecutionResult CancelledWith(string? jobId, string? reason) => new("cancelled", jobId, reason);
}

/// <summary>终止动作执行器（D10：仅流程成功边界调用；生产实现 = BgiWorkflowTerminalExecutor）。</summary>
public interface IWorkflowTerminalExecutor
{
    /// <summary>支持的收尾类型（缺省=全部——测试假实现免接线；生产按 ext capability 实况）。</summary>
    IReadOnlySet<string> SupportedKinds => WorkflowKindCatalog.TerminalKinds;

    /// <summary>执行收尾动作；生产实现受理即持久化 submitted 事实（jobId），再等待 executed。</summary>
    Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct);

    Task<TerminalExecutionResult> ConfirmCancellationAsync(WorkflowRunRecord run, PendingCompletionRecord record, CancellationToken ct)
        => ConfirmCancellationAsync(record, ct);

    Task<TerminalExecutionResult> ConfirmCancellationAsync(PendingCompletionRecord record, CancellationToken ct)
        => Task.FromResult(TerminalExecutionResult.UnknownWith(record.JobId, "执行器缺少同身份收尾取消观察"));
}

/// <summary>显式运行动作（锚点 2：立即生效走显式动作，不硬切执行中叶子）。</summary>
public enum WorkflowRunAction
{
    /// <summary>停止流程（终态 Cancelled；不触发收尾，D10）。</summary>
    Stop,
    /// <summary>跳过当前节点（绑定请求时出现身份；远端取消确认后推进，B4）。</summary>
    SkipCurrent,
    /// <summary>重载流程定义（运行中改流：新修订在下一节点边界生效）。</summary>
    ReloadDefinition,
    /// <summary>暂停（≠停止；节点边界生效，保留等待记录；显式 ResumeAsync 恢复）。</summary>
    Pause,
}

/// <summary>引擎选项（失败策略/时钟/延时工厂/确认超时——测试可注入，全计时可取消）。</summary>
public sealed class WorkflowRunnerOptions
{
    /// <summary>节点失败/拒绝后是否继续后续节点（D12；默认 false=停止流程，保守防假成功续跑）。</summary>
    public bool ContinueOnNodeFailure { get; init; }

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>
    /// **[批次 20／Wave3／C11] 本地等待判定委托（可选；默认 null ⇒ 恒 false＝未接线）**。
    /// 接线批注入门面等待结论判定；测试注入停驻触发判定。**实例级**——随 Runner 生命周期，
    /// 无进程级静态污染（R43 重要-6）。
    /// </summary>
    public Func<WorkflowNodeOccurrence, bool>? ShouldRegisterLocalWait { get; init; }

    /// <summary>可取消延时（测试用手动时钟快进；生产 Task.Delay）。</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    /// <summary>显式跳过后确认远端终态的超时（B4：超时 = cancelUnconfirmed → Unknown，不猜成功）。</summary>
    public TimeSpan SkipConfirmTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>日志出口（留痕纪律；不含敏感账号字段）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// **[批次 20／C4①] 本地等待登记结果**（D1 登记点的结构化产物）。
/// <see cref="Park"/>＝运行应按「确定零发送」停驻（登记成功**或**登记被拒都停驻——门面已给出零发送
/// 结论，**不得**回落到提交路径把停驻改写成发送尝试）；<see cref="Reason"/> 区分两种停驻与不适用。
/// </summary>
public sealed class LocalWaitRegistrationOutcome
{
    /// <summary>
    /// true＝零发送停驻（已登记／登记被拒／**接缝命中而队列缺失＝接线缺陷**，三种来源都停驻）。
    /// **[Wave1 R15 重要-2／R17 重要-1]** 当前**不存在 Park=false 的产出路径**（登记接缝命中后
    /// 队列缺失也属接线缺陷，必须以零发送停驻收场——不得回落提交把门面零发送结论改写成真实发送）；
    /// <c>NotParked</c> 仅为接缝未命中场景预留的防御形态、当前无产出方。
    /// </summary>
    public bool Park { get; init; }

    /// <summary>停驻原因（审计用；不适用时为空串）。</summary>
    public string Reason { get; init; } = "";
    public LocalWaitDecisionRecord? Decision { get; init; }

    /// <summary>
    /// **[Wave1 R17 建议-2] 当前无产出方**（<see cref="WorkflowRunner.TryRegisterLocalWait"/> 全部出口
    /// 均为 Park=true）：保留为「登记接缝未命中」场景的防御形态，**不是**「未注入队列 ⇒ 走提交路径」的
    /// 合法出口（该组合＝接线缺陷，须停驻——[Wave1 R15 重要-2]）。
    /// </summary>
    public static LocalWaitRegistrationOutcome NotParked { get; } = new();
}

/// <summary>
/// 槲寄生 · 任务中心——WorkflowRunner / Reconciler（R4.5，R4 分解 D7/D11/D12 + ASTRA 二轮处置）。
/// 单运行单驱动循环（串行状态转换）：所有触发（节点终态/动作队列/修订检查/定时唤醒）
/// 统一进入驱动循环边界处理，不并发推进。
/// ASTRA 二轮处置落点：
/// - B1 修订寻址：Store 一致性快照（文档+修订同源）；重载后按稳定出现身份从最后完成节点
///   重算后继，旧序列坐标绝不直接寻址新定义；链尾也是节点边界（追加节点会被执行）；
/// - B2 一提交一身份：幂等键按 runId+出现身份+attempt 确定性派生，提交事实不跨节点残留；
/// - B3 崩溃窗口：观察终态+节点结果+游标推进单次原子落盘；游标 null 由 TailReached 消歧；
///   ResumeAsync 显式恢复入口；聚合只信 NodeOutcomes（含恢复后历史结果重建）；
/// - B4 显式跳过：请求绑定出现身份，叶子建立空窗不丢动作，远端取消确认（超时=Unknown）后推进；
/// - B5 收尾：成功边界先落盘收尾意图（Completing+PendingCompletion 记录）再执行，收尾失败记 Failed；
/// - B9 轮次等待：新一轮边界统一执行（成功/过滤/失败续跑同路径），skipAcrossDays 公式化；
/// - 等待不占槽位：纯本地可取消延时，不持有执行锁、不提交等待作业；暂停可打断等待。
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;
    private readonly IWorkflowExecutionBoundary _boundary;
    private readonly IWorkflowPrerequisiteAdapter _prerequisites;
    private readonly IWorkflowTerminalExecutor _terminal;
    private readonly WorkflowRunnerOptions _opt;
    /// <summary>
    /// **[批次 14／D1] 本地持久等待登记口（可选注入）。** 与 WorkflowRunner 的提交面**无耦合**：
    /// 只把「确定零发送的等待」落盘成可审计的等待项；`null`（未注入）⇒ 等待短路不生效（既有行为不变）。
    /// </summary>
    private readonly LocalWaitQueueStore? _localWaitQueue;
    private readonly WaitDecisionSource? _waitDecisionSource;

    /// <summary>
    /// **[批次 20／C4①] 等待登记前置引用来源（可选注入；生产未接线）。**
    /// D1 登记点载荷合同要求登记项携带持久化稳定前置引用；引用的**权威来源**属接线批事项（§24.111
    /// 「前置引用来源」残项），本注入点即其合同落点。`null` 或返回空白 ⇒ 登记点**拒绝登记**
    /// （不得登记结构性永不参选的等待项——那是 C4② 合同前存量的专属形态，不是新登记的合法产物）。
    /// </summary>
    private readonly Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?>? _localWaitPrerequisiteReferenceProvider;

    /// <summary>
    /// **[批次 20／Wave1 R9-F1] 等待登记准入 Scope 来源（可选注入；生产未接线）。**
    /// 登记点要求**权威 scope**；其权威来源按运行来源类别分流——移交来源运行＝运行台账
    /// <c>AdmissionSourceScope</c>（受理时捕获、只比较不重写）；面板来源运行＝租约侧
    /// <c>FlowRegistration</c> 反查（<c>ResolveAdmissionParent</c>，宿主职责，Runner 不可达）⇒
    /// 由本注入点供给（接线批接宿主反查）。解析次序（[Wave1 R23 重要-1／R24 重要-1 同步]）：**台账字段规范非空 ⇒ 恒取台账字段**；provider 仅在台账字段缺省（面板来源运行）时取用；
    /// 否则回落运行台账字段；两者皆缺 ⇒ 登记点**拒绝登记**（空段身份与提交面不同空间，
    /// 结构性永不可重入——R7 重要-2 合同不变；「面板来源＝无权威 scope」是错误等式，R9-F1 更正）。
    /// </summary>
    private readonly Func<WorkflowRunRecord, string?>? _localWaitAdmissionScopeProvider;

    private readonly ConcurrentDictionary<string, RunControl> _controls = new(StringComparer.Ordinal);

    /// <summary>
    /// 等待结果的受控原因码（`AdmissionResultKind.WaitLocally` 的**唯一**呈现词）。适配层与持久化层共用，
    /// 避免多处手写字面量漂移出「未知/失败」词表。
    /// </summary>
    public const string LocalWaitReasonCode = "local_wait";

    /// <summary>
    /// **[批次 20／C3] 等待停驻的结果词**（NodeOutcomes.Result 与提交产物用的**唯一**停驻词）。
    /// 它**不是完成词**：RecomputeSuccessor 的「最后完成身份」锚**必须**排除它（停驻项零发送、
    /// 未完成；把它当锚会把未发送节点静默跳过——批次 14 明文禁止「作业静默丢步」）。
    /// </summary>
    public const string LocalWaitResultWord = "waitLocally";

    /// <summary>跳过请求（B4：绑定请求时的出现身份；身份漂移则丢弃，不误伤后续节点）。</summary>
    private sealed record SkipRequest(string NodeId, int Occurrence, int LoopIteration)
    {
        public bool Matches(WorkflowNodeOccurrence occ)
            => NodeId == occ.NodeId && Occurrence == occ.Occurrence && LoopIteration == occ.LoopIteration;
    }

    private sealed class RunControl
    {
        public required CancellationTokenSource RunCts { get; init; }
        public ConcurrentQueue<WorkflowRunAction> Actions { get; } = new();

        /// <summary>LeafCts/PendingSkip 互斥（B4：Cancel/Dispose 竞争消除）。</summary>
        public object Sync { get; } = new();
        public CancellationTokenSource? LeafCts;
        public SkipRequest? PendingSkip;
        public bool PauseRequested;
        public TaskCompletionSource PauseSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public WorkflowRunner(WorkflowStore workflows, RunStore runs, IWorkflowExecutionBoundary boundary,
        IWorkflowPrerequisiteAdapter prerequisites, IWorkflowTerminalExecutor terminal,
        WorkflowRunnerOptions? options = null, LocalWaitQueueStore? localWaitQueue = null,
        Func<WorkflowRunRecord, WorkflowNodeOccurrence, string?>? localWaitPrerequisiteReferenceProvider = null,
        Func<WorkflowRunRecord, string?>? localWaitAdmissionScopeProvider = null,
        WaitDecisionSource? waitDecisionSource = null)
    {
        _workflows = workflows;
        _runs = runs;
        _boundary = boundary;
        _prerequisites = prerequisites;
        _terminal = terminal;
        _opt = options ?? new WorkflowRunnerOptions();
        _localWaitQueue = localWaitQueue;
        _waitDecisionSource = waitDecisionSource;
        _localWaitPrerequisiteReferenceProvider = localWaitPrerequisiteReferenceProvider;
        _localWaitAdmissionScopeProvider = localWaitAdmissionScopeProvider;
    }

    /// <summary>运行控制登记实况（R4.8 一轮 I5：宿主动作结构化反馈用——Paused/终态运行无登记，动作不得无声吞）。</summary>
    public bool HasActiveControl(string runId) => _controls.ContainsKey(runId);

    /// <summary>登记显式动作（线程安全；驱动循环在下一边界消费，Stop 同时取消运行令牌）。</summary>
    public void RequestAction(string runId, WorkflowRunAction action)
    {
        if (!_controls.TryGetValue(runId, out var control)) return;
        control.Actions.Enqueue(action);
        switch (action)
        {
            case WorkflowRunAction.Stop:
                try
                {
                    if (!_runs.UpdateMergingIf(runId, latest =>
                        {
                            if (!string.Equals(latest.RunId, runId, StringComparison.Ordinal)) return false;
                            latest.StopRequested = true;
                            return true;
                        }, out _))
                        throw new InvalidOperationException("停止意图未能耐久登记，未确认停止完成。");
                }
                finally { control.RunCts.Cancel(); }
                break;
            case WorkflowRunAction.Pause:
                control.PauseRequested = true;
                control.PauseSignal.TrySetResult();
                break;
            case WorkflowRunAction.SkipCurrent:
            {
                // B4：绑定请求时的当前游标身份；等待期间无执行中节点，不登记（等待可经 Stop 中断）
                var rec = _runs.Load(runId);
                if (rec?.State == WorkflowRunState.Waiting)
                {
                    Log(runId, "等待期间无执行中节点，跳过请求不登记。");
                    break;
                }
                control.PendingSkip = rec?.Cursor is { } cursor
                    ? new SkipRequest(cursor.NodeId, cursor.Occurrence, cursor.LoopIteration)
                    : new SkipRequest("", -1, -1); // 无游标：永不命中，仅留痕
                lock (control.Sync) control.LeafCts?.Cancel();
                break;
            }
        }
    }

    /// <summary>
    /// 启动流程运行（预检 → 建运行 → 驱动至终态/中断）。
    /// 预检失败抛 InvalidOperationException（响亮，不建运行）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartAsync(string workflowId, CancellationToken ct = default,
        long? explicitIntentTimestamp = null, string? explicitIntentId = null)
    {
        var intentTimestamp = explicitIntentTimestamp ?? System.Diagnostics.Stopwatch.GetTimestamp();
        var intentId = explicitIntentId ?? Guid.NewGuid().ToString("N");
        var snapshot = _workflows.LoadSnapshot(workflowId); // 隔离文件在此响亮抛出；文档+修订同源（B1）
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        var authority = await AcquireExplicitIntentStopAuthorityAsync(intentId, intentTimestamp, ct).ConfigureAwait(false);
        var run = _runs.CreateRun(workflowId, snapshot.Revision, stopAuthority: authority);
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + run.RunId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }

    /// <summary>
    /// 移交受理前预检（R4.9 + ASTRA 二轮 B3：权威计划预检先于受理落盘——不可执行流程响亮拒绝、不消耗 IntentKey、不留运行记录）。
    /// 无执行副作用；与 StartExistingRunAsync/ResumeAsync 内的预检同口径（驱动入口仍各自复验，防御纵深）。
    /// </summary>
    public void PreflightStartable(WorkflowSnapshot snapshot)
    {
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported);
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));
    }
    /// <summary>
    /// 驱动既有 Planned 运行（R4.9 移交受理路径：受理点=宿主 CreateRun 落盘（含移交身份），本入口不再建运行——
    /// 与 StartAsync 的「预检→建运行→驱动」不同，这里是「驱动已受理运行」。预检失败/隔离文件响亮抛出
    /// （运行记录保留——受理不撤回，由宿主侧收敛标注，R4.9 §3 步骤 4）。
    /// armTriggerLaunch=true（armTrigger 移交）：在本入口实际加载的定义快照上复验可挂载前提（七轮 重要3）——
    /// 受理→驱动重载窗口内前提失效时不激活执行（受理事实保留，运行收敛 Interrupted 可处置，绝不提交）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartExistingRunAsync(string runId, CancellationToken ct = default,
        bool armTriggerLaunch = false)
    {
        var run = _runs.Load(runId) ?? throw new FileNotFoundException("运行记录不存在：" + runId);
        if (run.State is not WorkflowRunState.Planned)
            throw new InvalidOperationException($"仅 Planned 新运行可经移交入口驱动（当前 {run.State}）。");

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId); // 隔离响亮抛出；文档+修订同源（B1）
        if (armTriggerLaunch && !HasMountableTrigger(snapshot.Document))
        {
            // 七轮 重要3：arm 前提必须在驱动实际使用的定义快照上复验——受理（快照 A 有 trigger.time）→驱动重载
            // （快照 B 触发器已被移除）窗口内前提失效时不得顺势进入执行：受理事实保留、运行收敛 Interrupted
            // （可处置/可显式恢复），绝不做任何提交
            run.State = WorkflowRunState.Interrupted;
            run.Note = AppendNote(run.Note,
                "armTrigger 挂载前提失效（受理后流程已无 trigger.time 触发器），未激活执行（受理事实保留）。");
            _runs.Update(run);
            return run;
        }
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        run.WorkflowRevision = snapshot.Revision; // 与 Resume 同口径：起步对账到当前修订（受理与驱动同窗口，正常相等）
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + runId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }
    /// <summary>
    /// 显式恢复运行（B3 恢复入口 + D7 生命周期触发的消费侧）。
    /// 仅接受 Interrupted/Paused；Unknown 拒绝自动恢复（结果不确定，需先按幂等键+job 查询对账）。
    /// 恢复点 = 游标身份在当前修订中重定位；已完成节点不重放；历史失败结果参与聚合。
    /// </summary>
    public async Task<WorkflowRunRecord> ResumeAsync(string runId, CancellationToken ct = default)
    {
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(runId, control))
        {
            control.RunCts.Dispose();
            throw new InvalidOperationException("运行登记冲突：" + runId);
        }
        try
        {
            // 先取得本 Runner 的运行控制，再读取与修改记录；控制冲突不得先写 Running/修订。
            var run = _runs.Load(runId) ?? throw new FileNotFoundException("运行记录不存在：" + runId);
            if (run.State == WorkflowRunState.Unknown)
                throw new InvalidOperationException("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复。");
            if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused
                or WorkflowRunState.LocalWaitParking))
                throw new InvalidOperationException($"仅 Interrupted/Paused/LocalWaitParking 可显式恢复（当前 {run.State}）。"
                    + "LocalWaitParking＝等待停驻（[批次 20／Wave3／C11=(a)]）：等待项就绪后显式重驱入口。");
            if (RunStore.HasUnresolvedExternalFact(run))
            {
                run.State = WorkflowRunState.Unknown;
                run.Note = AppendNote(run.Note, "显式恢复发现未决发送/收尾事实，标 Unknown，必须先对账，禁止重驱。");
                _runs.Update(run);
                throw new InvalidOperationException("运行记录含未决发送/收尾事实，已保留事实并标 Unknown；必须先对账，禁止自动恢复。");
            }

            var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
            var plan = new WorkflowPlan(snapshot.Document);
            var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
                _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
            if (!preflight.Executable)
                throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

            if (run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait or LocalWaitDecisionKind.Hold } priorDecision
                && run.CurrentSubmission?.Intent == SubmitIntentState.LocalWaitDeferred)
            {
                var sameRevision = string.Equals(run.WorkflowRevision, snapshot.Revision, StringComparison.Ordinal);
                WorkflowNodeOccurrence? waitOccurrence = null;
                var sameCursor = false;
                if (run.Cursor is { } savedCursor
                    && plan.TryLocate(savedCursor.NodeId, savedCursor.Occurrence, savedCursor.LoopIteration, out var located))
                {
                    waitOccurrence = located;
                    sameCursor = true;
                }
                if (sameRevision && sameCursor)
                {
                    var currentOccurrence = waitOccurrence!;
                    var resumeRequest = CreateWaitDecisionRequest(run, currentOccurrence, run.Cursor!.Attempt);
                    var decision = DecideLocalWait(resumeRequest, currentOccurrence)
                        ?? new LocalWaitDecisionRecord
                        {
                            Kind = LocalWaitDecisionKind.ContinueAdmission,
                            Context = ContextFromRequest(resumeRequest),
                            Reason = "测试接缝恢复按显式请求重驱",
                        };
                    if (decision.Kind is LocalWaitDecisionKind.Wait or LocalWaitDecisionKind.Hold)
                    {
                        if (decision.Kind == LocalWaitDecisionKind.Wait)
                        {
                            var prepared = TryRegisterLocalWait(run, currentOccurrence, run.Cursor!.Attempt, decision);
                            if (prepared.Decision?.Binding is { } refreshedBinding)
                            {
                                if (priorDecision.Binding is { } priorBinding
                                    && !SameWaitBindingPayload(priorBinding, refreshedBinding))
                                {
                                    CancelPersistedLocalWait(priorBinding, "恢复复核发现等待绑定身份/载荷漂移");
                        run.LocalWaitDecision = SanitizeWaitDecision(decision with
                        {
                            Kind = LocalWaitDecisionKind.Hold,
                            Binding = null,
                            Reason = "恢复复核发现来源、候选或队列载荷已漂移；旧绑定已取消，不以新快照替换。",
                            NoSendConfirmed = true,
                        });
                                }
                                else
                                {
                                    // 已存在的绑定不可被当前快照替换（包含其来源、身份、scope 与登记载荷）。
                                    run.LocalWaitDecision = SanitizeWaitDecision(prepared.Decision with
                                        { Binding = priorDecision.Binding ?? refreshedBinding });
                                }
                            }
                            else
                            {
                                CancelPersistedLocalWait(priorDecision.Binding, "恢复复核无法重建可信等待绑定");
                                run.LocalWaitDecision = SanitizeWaitDecision(decision with
                                {
                                    Kind = LocalWaitDecisionKind.Hold,
                                    Binding = null,
                                    Reason = prepared.Reason,
                                    NoSendConfirmed = true,
                                });
                            }
                        }
                        else
                        {
                            CancelPersistedLocalWait(priorDecision.Binding, "等待复核转为 Hold");
                            run.LocalWaitDecision = SanitizeWaitDecision(decision with { Binding = null });
                        }
                        run.State = WorkflowRunState.LocalWaitParking;
                        run.Note = AppendNote(run.Note, "显式恢复复核仍需本地等待/保持，未启动驱动、未发送。"
                            + Sanitize(run.LocalWaitDecision.Reason));
                        _runs.Update(run);
                        PublishPersistedLocalWait(run);
                        return run;
                    }
                }

                // 继续准入或身份/修订漂移：先墓碑化旧队列项，再清除活动绑定；若写入失败则维持原停驻。
                CancelPersistedLocalWait(priorDecision.Binding, sameRevision && sameCursor
                    ? "显式恢复重新进入完整准入" : "流程修订或游标身份已漂移");
                run.LocalWaitDecision = null;
            }

            // BO-6/7: explicit resume is a rescue boundary even when the saved cursor still exists.
            // A prior revision stamp or a current cursor must not hide an earlier live parked
            // obligation; recompute the plan-order successor from completion + all park history.
            // A persisted tail flag with live parks stays fail-closed through final aggregation.
            if (!run.TailReached && run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord))
            {
                var resumeSuccessor = RecomputeSuccessor(run, plan);
                ApplyRelocation(run, resumeSuccessor);
                Log(run, "显式恢复复核当前计划中的停驻义务，按锚/停驻全序重算恢复点。");
            }

            run.WorkflowRevision = snapshot.Revision; // 恢复即对账到当前修订（节点边界语义）
            run.State = WorkflowRunState.Running;
            run.Note = AppendNote(run.Note, "显式恢复运行（游标身份重定位，不重放已完成节点）。");
            _runs.Update(run);
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(runId, out _);
            control.RunCts.Dispose();
        }
    }

    private async Task<WorkflowRunRecord> DriveAsync(WorkflowRunRecord run, WorkflowPlan plan, RunControl control)
    {
        var ct = control.RunCts.Token;
        var flowFailure = false;
        // [BO-8 / R29 重要；继承缺陷] 推进段按**稳定出现身份**过滤已完成项：恢复点（显式恢复重算、
        // 修订热重载重算、或链尾重入的停驻）之后的线性推进不得二次提交已完成出现——修订重排把已
        // 完成节点挪到恢复点之后时，旧行为会重复触发外部副作用（不可撤销）。过滤只按
        // (NodeId, Occurrence, LoopIteration) 判定；停驻标记（LocalWaitResultWord）不算完成，
        // 停驻义务照常重驱。
        // [BO-9 / R34 F5 重要] 真实链尾仍存活的停驻义务按计划全序重入驱动（TryRelocateToLivePark）。
        // enteredAtTail：入口即为持久链尾的记录不由重入路径重开（BO-6 防御语义）。
        var enteredAtTail = run.TailReached;
        try
        {
            await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
            // 顶层触发器：入口等待（不占槽位；已消费则跳过——恢复不重等，B3）
            if (!run.TriggerConsumed)
            {
                await AwaitFlowTriggersAsync(run, plan, control, ct).ConfigureAwait(false);
                if (control.PauseRequested) return Pause(run);
                run.TriggerConsumed = true;
                run.State = WorkflowRunState.Running;
                _runs.Update(run);
            }

            var occurrence = Relocate(run, plan);
            if (occurrence is not null && run.Cursor is null)
            {
                // 首个待执行节点即落盘游标（B4：跳过动作绑定出现身份对首节点同样成立）
                ApplyRelocation(run, occurrence);
                _runs.Update(run);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
                if (control.PauseRequested) return Pause(run);

                // 边界①：显式动作 + 修订对账（新修订按稳定身份重算后继；链尾亦对账，B1）
                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);
                if (occurrence is null)
                {
                    // [BO-9 / R34 F5 重要；第 1 轮会诊 IMPORTANT-1 修复] 真实链尾：线性推进
                    // （plan.Next）到不了本链的更晚义务——无循环定义时既不回绕到更高轮次停驻
                    // （如 A@loop2），也到不了 RecomputeSuccessor 已选中但尚未履行的 rescue
                    // （如 [A,P] + A@0 完成 + P@1/P@2 停驻 ⇒ rescue A@1）。按计划全序重建并重入
                    // 最早的未履行恢复义务，避免「零发送义务/未执行出现被静默吞」后假成功。
                    // 终止性：每次重入都驱动该出现一次（完成或被过滤 ⇒ 离开义务集合；再次停驻或
                    // unknown ⇒ 驱动立即返回），义务集合严格收缩；重入后若经暂停/取消/修订边界返回，
                    // 驱动本身结束，不构成自旋。入口即链尾（持久 TailReached）不由本路径重开。
                    if (!enteredAtTail && TryRelocateToOutstandingObligation(run, plan, out var obligation))
                    {
                        Log(run, $"链尾仍存未履行的恢复义务 {obligation!.NodeId}#{obligation.Occurrence}"
                                 + $"（轮次 {obligation.LoopIteration}）：按计划全序重入重驱，不按链尾放行。");
                        occurrence = obligation;
                        ApplyRelocation(run, occurrence);
                        _runs.Update(run);
                        continue;
                    }
                    break; // 链尾（TailReached 已落盘）
                }

                // B3-③：提交终态已观察但结果未提交（仅遗留/手工记录可达；正常路径单次写已消除窗口）
                // ——按提交事实补记结果，绝不重跑该节点
                if (run.CurrentSubmission is { ObservedTerminal: { } observed } pendingSub
                    && pendingSub.NodeId == occurrence.NodeId
                    && pendingSub.Occurrence == occurrence.Occurrence
                    && pendingSub.LoopIteration == occurrence.LoopIteration)
                {
                    var mapped = MapTerminal(observed);
                    Log(run, $"提交 {pendingSub.Key} 终态已观察（{observed}）但结果未提交，按事实补记，不重跑。");
                    CommitOutcome(run, plan, occurrence, mapped.Result, mapped.Reason, rawTerminal: observed); // I2：ObservedTerminal 已是原始词
                    if (mapped.Result == "cancelled") throw new OperationCanceledException();
                    if (mapped.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan);
                    continue;
                }

                // B9：轮次起点等待统一在新一轮边界（成功/过滤跳过/失败续跑同路径；不占槽位）
                if (occurrence is { SequenceIndex: 0, LoopIteration: > 0 }
                    && occurrence.LoopIteration != run.LastScheduledRoundWait)
                {
                    if (!await AwaitLoopRoundStartAsync(run, plan, occurrence, control, ct).ConfigureAwait(false))
                    {
                        flowFailure = true;
                        break;
                    }
                    if (control.PauseRequested) return Pause(run);
                }

                // [BO-8] 恢复点或修订重排可能落在已完成出现之前。停驻义务照常重驱，但任何已完成
                // 稳定出现都不得二次提交。逐步推进（每步一个出现）保留定义边界与轮次起点处理。
                if (HasCompletedOutcome(run, occurrence))
                {
                    Log(run, $"推进过滤已完成出现 {occurrence.NodeId}#{occurrence.Occurrence}"
                             + $"（轮次 {occurrence.LoopIteration}）：按稳定身份跳过，不二次提交。");
                    occurrence = plan.Next(occurrence);
                    ApplyRelocation(run, occurrence);
                    _runs.Update(run);
                    continue;
                }

                var node = plan.NodeAt(occurrence);
                var gate = plan.EvaluateNode(occurrence, _opt.Clock(), _boundary.SingleNativeSupported);
                if (gate.Action == NodeGateAction.Skip)
                {
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 过滤跳过：{gate.Reason}");
                    CommitOutcome(run, plan, occurrence, "skippedFilter", gate.Reason);
                    occurrence = Relocate(run, plan);
                    continue;
                }
                if (gate.Action == NodeGateAction.Reject)
                {
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 响亮拒绝：{gate.Reason}");
                    CommitOutcome(run, plan, occurrence, "rejected", gate.Reason);
                    if (!_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan);
                    continue;
                }

                // 前置策略（D8 + R4.6 结构化结果：Proceed/Failed/Rejected/Cancelled/Unknown；
                // 前置段已建立叶子令牌，SkipCurrent 可取消在飞前置并经确认链对账）
                if (await ExecutePrerequisitesAsync(run, node, occurrence, control, ct).ConfigureAwait(false) is { } prereqOutcome)
                {
                    if (prereqOutcome.Result is "unknown" or "cancelUnconfirmed")
                    {
                        // B3/四轮阻断 5：结果不确定——先置状态再 CommitOutcome（单次原子落盘，游标不推进；不触发收尾、禁止自动重跑）
                        run.State = WorkflowRunState.Unknown;
                        run.Note = AppendNote(run.Note, "前置动作结果不确定，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                        CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                        return run;
                    }
                    CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                    if (prereqOutcome.Result == "cancelled") throw new OperationCanceledException();
                    if (prereqOutcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan); // skippedUser/skippedFilter/失败续跑：推进
                    continue;
                }

                // 提交（意图先行 → 提交 → 终态；观察终态+结果+游标单次落盘，B2/B3）
                var outcome = await SubmitAndAwaitAsync(run, plan, node, occurrence, control).ConfigureAwait(false);
                if (outcome.Result is LocalWaitResultWord)
                {
                    // **[批次 14／D1] 本地持久等待＝确定零发送的停驻**：与 unknown/cancelUnconfirmed 同族——
                    // **游标不推进**（`ApplyRelocation(run, occurrence)` 留在当前出现）、**不终态化**运行、
                    // **不触发收尾**、**不标 Unknown**（等待不是「结果不确定」，标 Unknown 会错误要求按幂等键+job 对账，
                    // 而本笔从未进入发送面 ⇒ 无 job 可查 ⇒ 永久无法收敛）。
                    // **[批次 20／Wave3／C11=(a)] 停驻态显式化**：置 **LocalWaitParking**（D-E4=(a) 不复用
                    // Running/Waiting）——运行无活动驱动、等待项就绪后可显式重驱（ResumeAsync 接受本状态）；
                    // 重启恢复扫描将其收敛为 Interrupted（可恢复）——「无重驱句柄的永久 Running」形态消除
                    // （Wave1 R11 F-A/R25 处置链的闭环）。同代际再次重驱同一出现 ⇒ 再停驻 ⇒ 状态保持 LocalWaitParking。
                    // [批次 21／BO-1] 停驻原因留痕到运行级 Note（与 unknown 分支同族的可观测性义务）：
                    // 登记未完成／被拒的原文经 Sanitize 追加，运行记录盘上可查（夹具钉死「登记未完成＋异常」子串）。
                    run.State = WorkflowRunState.LocalWaitParking;
                    run.Note = AppendNote(run.Note, "本地等待登记停驻（零发送，游标不推进）：" + Sanitize(outcome.Reason));
                    if (run.CurrentSubmission is { } deferred
                        && deferred.Intent == SubmitIntentState.IntentRecorded
                        && !deferred.SendAttempted
                        && string.IsNullOrEmpty(deferred.JobId)
                        && string.IsNullOrEmpty(deferred.AcceptedSendIdentity)
                        && deferred.ObservedTerminal is null)
                        deferred.Intent = SubmitIntentState.LocalWaitDeferred;
                    CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, rawTerminal: null);
                    PublishPersistedLocalWait(run);
                    return run;
                }
                if (outcome.Result is "cancelUnconfirmed" or "unknown")
                {
                    // B4/四轮阻断 5：远端终态未确认——先置状态再 CommitOutcome（单次原子落盘，游标不推进）；
                    // rawTerminal=null → ObservedTerminal 保持 null（未观察到原始词），恢复扫描按在飞标 Unknown
                    run.State = WorkflowRunState.Unknown;
                    run.Note = AppendNote(run.Note, "远端终态未确认，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                    CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, rawTerminal: null);
                    return run;
                }
                CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, outcome.RawTerminal);
                if (outcome.Result == "cancelled") throw new OperationCanceledException(); // BGI 取消事实 → 流程取消（D12）
                if (outcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                occurrence = Relocate(run, plan);
            }

            await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
            // 流程边界：聚合判定只信 NodeOutcomes（B3：含恢复后的历史结果重建，失败不被成功覆盖）
            var unresolvedParkedOutcome = run.NodeOutcomes.Any(o => o.Result == LocalWaitResultWord
                && plan.TryLocate(o.NodeId, o.Occurrence, o.LoopIteration, out var parked)
                && !HasCompletedOutcome(run, parked));
            var hadBadOutcome = run.NodeOutcomes.Any(o =>
                o.Result is "failed" or "rejected" or "cancelled" or "cancelUnconfirmed" or "unknown"); // R4.6 B3
            if (hadBadOutcome || unresolvedParkedOutcome || flowFailure)
            {
                run.State = WorkflowRunState.Failed;
                run.Note = AppendNote(run.Note, flowFailure
                    ? "循环/触发时刻计算失败，聚合结果 Failed。"
                    : unresolvedParkedOutcome
                        ? "存在仍可定位且未完成的本地停驻义务，未按链尾成功处理；聚合结果 Failed。"
                        : "存在失败/拒绝节点，聚合结果 Failed（失败不被后续成功覆盖）。");
                _runs.Update(run);
                return run;
            }

            // B5/E3'：收尾意图先行落盘（Completing + PendingCompletion 记录），再执行收尾动作；
            // Planner 预检保证每流程至多一个收尾动作（E3'：多收尾无逐动作水位，响亮拒绝）；
            // executed 才清偿意图；rejected/异常 → Failed 保留意图；unknown/cancelled → Failed + 意图标 unknown 禁止补发。
            // D15 定案：skippedUser/skippedFilter 不算坏结果，不阻断成功边界收尾（用户显式跳过视为认可完成）。
            var terminalActions = plan.Document.Terminal;
            if (terminalActions.Count > 0)
            {
                await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
                var action = terminalActions[0]; // E3'：单动作（多收尾已被 Planner 预检响亮拒绝）
                run.State = WorkflowRunState.Completing;
                run.PendingCompletion = new PendingCompletionRecord
                {
                    ActionId = "$flow#0", // B1：收尾身份 nodeId=$flow、iteration=动作序号（0 起）
                    Kind = action.Kind,
                    Action = action.GetString("action"),
                    State = "pending",
                };
                _runs.Update(run);
                TerminalExecutionResult terminalResult;
                try
                {
                    terminalResult = await _terminal.ExecuteAsync(action, run, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; } // B8：意图事实交外层按 pending/submitted 纪律处置
                catch (Exception ex)
                {
                    run.State = WorkflowRunState.Failed;
                    run.Note = AppendNote(run.Note,
                        $"收尾动作 {action.Kind} 执行失败：{Sanitize(ex.Message)}（流程主体成功，收尾失败不记成功；待执行收尾保留待人工处置）。");
                    _runs.Update(run);
                    return run;
                }
                switch (terminalResult.State)
                {
                    case "executed":
                        run.PendingCompletion!.State = "executed";
                        run.CompletionHistory.Add(run.PendingCompletion);
                        run.PendingCompletion = null; // Preserve raw result/exit/effect in durable history before discharge.
                        break;
                    case "rejected":
                        run.State = WorkflowRunState.Failed;
                        run.Note = AppendNote(run.Note,
                            $"收尾动作被拒绝：{Sanitize(terminalResult.Reason)}（动作未执行；意图保留待人工处置）。");
                        _runs.Update(run);
                        return run;
                    default: // unknown / cancelled：结果不可考——事实持久保留，禁止自动补发
                        run.PendingCompletion.State = "unknown";
                        run.PendingCompletion.JobId ??= terminalResult.JobId;
                        run.State = WorkflowRunState.Failed;
                        run.Note = AppendNote(run.Note,
                            $"收尾动作结果不确定（{terminalResult.State}）：{Sanitize(terminalResult.Reason)}（事实持久保留，禁止自动补发，需人工对账）。");
                        _runs.Update(run);
                        return run;
                }
            }

            run.State = WorkflowRunState.Succeeded;
            _runs.Update(run);
            return run;
        }
        catch (StopAuthorityUnknownException ex)
        {
            if (_runs.Load(run.RunId) is { } latest) RunStore.RebaseOnto(run, latest);
            run.State = WorkflowRunState.Unknown;
            run.Note = AppendNote(run.Note, ex.Message);
            _runs.Update(run);
            return run;
        }
        catch (OperationCanceledException)
        {
            // Stop意图先于令牌取消落盘；使用最新修订，不能以取消RPC回执清偿外部责任。
            if (_runs.Load(run.RunId) is { } latest) RunStore.RebaseOnto(run, latest);
            run.StopRequested = true;
            _runs.Update(run); // Stop intent is durable before any cancellation or cleanup observation.
            if (run.CurrentSubmission is { SendAttempted: true, JobId: null } uncertainSubmission
                && !LocalNoSendEvidence.IsDischarged(run, uncertainSubmission))
            {
                try
                {
                    using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _boundary.ReconcileSubmissionAsync(run, uncertainSubmission, lookup.Token).ConfigureAwait(false);
                }
                catch (Exception ex) { run.Note = AppendNote(run.Note, "主体原键对账未确认：" + ex.GetType().Name); }
            }
            if (run.CurrentSubmission is { JobId: { } jobId } submission
                && (!submission.ExecutionExitConfirmed || !BgiJobTerminalPolling.IsTerminal(submission.ObservedTerminal)))
            {
                try
                {
                    using var cancelBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _boundary.RequestSubmissionCancelAsync(run, submission, cancelBudget.Token).ConfigureAwait(false);
                }
                catch { /* 取消失败仍继续独立观察。 */ }
                try
                {
                    using var observeBudget = new CancellationTokenSource(_opt.SkipConfirmTimeout);
                    var observed = await _boundary.AwaitSubmissionExitAsync(run, submission, observeBudget.Token).ConfigureAwait(false);
                    if (!observed.Uncertain && observed.ExecutionExitConfirmed
                        && observed.Terminal is "succeeded" or "failed" or "cancelled" or "rejected" or "skipped")
                    {
                        submission.ObservedTerminal = observed.Terminal;
                        submission.ExecutionExitConfirmed = true;
                        submission.ExecutionExitDisposition = observed.ExecutionExitDisposition;
                        submission.EffectState = observed.Terminal;
                    }
                    else run.Note = AppendNote(run.Note, "停止后执行退出未确认：" + Sanitize(observed.Reason));
                }
                catch (Exception ex)
                {
                    run.Note = AppendNote(run.Note, "停止后执行退出未确认：" + Sanitize(ex.GetType().Name));
                }
            }
            _runs.Update(run); // Publish body outcome before a later adapter rebases this run.
            foreach (var prerequisite in run.PrerequisiteActions)
            {
                if (!(prerequisite.SendAttempted || !string.IsNullOrEmpty(prerequisite.JobId))
                    || prerequisite.ExecutionExitConfirmed && BgiJobTerminalPolling.IsTerminal(prerequisite.ObservedTerminal)
                       && prerequisite.State != PrerequisiteActionState.Unknown) continue;
                try
                {
                    using var observation = new CancellationTokenSource(_opt.SkipConfirmTimeout);
                    var actual = await _prerequisites.ConfirmCancellationAsync(run, prerequisite, observation.Token).ConfigureAwait(false);
                    prerequisite.State = PrerequisiteState(actual.Status);
                    prerequisite.Reason = Sanitize(actual.Reason);
                    prerequisite.JobId ??= actual.JobId;
                }
                catch (Exception ex)
                {
                    prerequisite.State = PrerequisiteActionState.Unknown;
                    prerequisite.Reason = "停止后前置退出未确认：" + ex.GetType().Name;
                }
                _runs.Update(run); // Publish this outcome before the next adapter merge/rebase.
            }
            if (run.PendingCompletion is { } pendingCompletion)
            {
                if (!pendingCompletion.SendAttempted && pendingCompletion.State == "pending"
                    && string.IsNullOrEmpty(pendingCompletion.JobId) && string.IsNullOrEmpty(pendingCompletion.Fingerprint))
                    run.PendingCompletion = null;
                else
                {
                    try
                    {
                        using var observation = new CancellationTokenSource(_opt.SkipConfirmTimeout);
                        var actual = await _terminal.ConfirmCancellationAsync(run, pendingCompletion, observation.Token).ConfigureAwait(false);
                        if (actual.State is "executed" or "cancelled" or "rejected"
                            && pendingCompletion.ExecutionExitConfirmed && BgiJobTerminalPolling.IsTerminal(pendingCompletion.ObservedTerminal)
                            && pendingCompletion.EffectState is not (null or "unknown"))
                        {
                            pendingCompletion.State = actual.State;
                            run.CompletionHistory.Add(pendingCompletion);
                            run.PendingCompletion = null;
                        }
                        else pendingCompletion.State = "unknown";
                    }
                    catch (Exception ex)
                    {
                        pendingCompletion.State = "unknown";
                        run.Note = AppendNote(run.Note, "停止后收尾退出/效果未确认：" + ex.GetType().Name);
                    }
                }
            }
            // 已清偿的收尾不能仅因旧Completing标签保留责任；外部事实仍逐项核验。
            run.State = WorkflowRunState.Cancelled;
            run.State = RunStore.HasUnresolvedTerminalResponsibility(run)
                ? WorkflowRunState.Unknown : WorkflowRunState.Cancelled;
            run.Note = AppendNote(run.Note, run.State == WorkflowRunState.Unknown
                ? "停止已请求，远端退出/效果未确认；原身份和责任保留，不推进、不触发收尾。"
                : "流程已停止（执行退出或确定零发送已确认）；不推进、不触发收尾。");
            _runs.Update(run);
            return run;
        }
    }

    /// <summary>提交 + 终态等待（意图先行落盘；SkipCurrent 经叶子令牌取消 + 远端确认，B4）。
    /// RawTerminal = 边界观察到的原始线协议词（I2；未确认路径为 null）。</summary>
    private async Task<(string Result, string? Reason, string? RawTerminal)> SubmitAndAwaitAsync(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNode node, WorkflowNodeOccurrence occurrence, RunControl control)
    {
        var ct = control.RunCts.Token;
        const int attempt = 1; // 有界重试机制挂账 R4.6+（键结构已含 attempt，身份合同就绪）
        var submission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, occurrence.NodeId, occurrence.Occurrence,
                occurrence.LoopIteration, attempt),
            NodeId = occurrence.NodeId,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
        };
        // 预检 Hold 只适用于没有未决外部事实的当前提交；不能用 LocalWaitDeferred 覆盖旧受理/发送事实。
        if (RunStore.HasUnresolvedExternalFact(run))
            return ("unknown", "提交前发现未决发送/收尾事实，保留原提交记录并待对账。", null);
        // 同步类型化裁定必须先于 RecordIntent。ContinueAdmission 不是许可：后续异步边界仍完整准入。
        var waitRequest = CreateWaitDecisionRequest(run, occurrence, attempt);
        var waitDecision = DecideLocalWait(waitRequest, occurrence);
        if (waitDecision is { Kind: not LocalWaitDecisionKind.ContinueAdmission })
        {
            var prepared = waitDecision.Kind == LocalWaitDecisionKind.Wait
                ? TryRegisterLocalWait(run, occurrence, attempt, waitDecision)
                : new LocalWaitRegistrationOutcome
                {
                    Park = true,
                    Reason = waitDecision.Reason,
                    Decision = waitDecision with { Binding = null },
                };
            run.LocalWaitDecision = SanitizeWaitDecision(prepared.Decision ?? waitDecision with
            {
                Kind = LocalWaitDecisionKind.Hold,
                Binding = null,
                Reason = string.IsNullOrWhiteSpace(prepared.Reason) ? "等待登记未完成" : prepared.Reason,
                NoSendConfirmed = true,
            });
            submission.Intent = SubmitIntentState.LocalWaitDeferred;
            run.CurrentSubmission = submission;
            return (LocalWaitResultWord, prepared.Reason, null);
        }

        _runs.RecordIntent(run, submission); // 提交意图先行（B2/B3：崩溃后按意图对账，不重跑）

        // B6/E4' 定案：任务中心提交固定 suppress=true（与流程是否声明 terminal 无关；原生手动入口缺省 false 不变）
        var submit = await _boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), ct)
            .ConfigureAwait(false);
        // 生产边界冻结会用最新盘上记录rebase，旧submission引用不再属于run。
        // 重新绑定同一出现，才能把受理句柄和退出事实持久化到实际记录。
        if (run.CurrentSubmission is not { } currentSubmission
            || currentSubmission.Key != submission.Key || currentSubmission.NodeId != submission.NodeId
            || currentSubmission.Occurrence != submission.Occurrence
            || currentSubmission.LoopIteration != submission.LoopIteration
            || currentSubmission.Attempt != submission.Attempt)
            return ("unknown", "边界返回后提交身份已变，保留责任并禁止推进。", null);
        submission = currentSubmission;
        if (submit.Kind is BoundarySubmitKind.Wait or BoundarySubmitKind.Hold)
        {
            if (submission.Intent != SubmitIntentState.IntentRecorded
                || submission.SendAttempted
                || !string.IsNullOrEmpty(submission.JobId)
                || !string.IsNullOrEmpty(submission.AcceptedSendIdentity)
                || submission.ObservedTerminal is not null)
                return ("unknown", "等待裁定与提交发送事实冲突，保留事实并待对账。", null);

            var boundaryWaitRequest = CreateWaitDecisionRequest(run, occurrence, attempt);
            var boundaryHold = submit.Kind == BoundarySubmitKind.Hold
                || submit.WaitDecision?.Kind == LocalWaitDecisionKind.Hold;
            var boundaryDecision = boundaryHold
                ? new LocalWaitDecisionRecord
                {
                    Kind = LocalWaitDecisionKind.Hold,
                    Context = ContextFromRequest(boundaryWaitRequest),
                    Reason = !string.IsNullOrWhiteSpace(submit.RejectReason)
                        ? submit.RejectReason!
                        : submit.WaitDecision?.Reason ?? "提交边界要求保持，未创建等待队列项",
                    NoSendConfirmed = true,
                }
                : DecideLocalWait(boundaryWaitRequest, occurrence)
                ?? new LocalWaitDecisionRecord
                {
                    Kind = LocalWaitDecisionKind.Hold,
                    Context = ContextFromRequest(boundaryWaitRequest),
                    Reason = "边界返回等待但缺少宿主等待判定来源",
                    NoSendConfirmed = true,
                };
            if (boundaryDecision.Kind == LocalWaitDecisionKind.ContinueAdmission)
                boundaryDecision = boundaryDecision with
                {
                    Kind = LocalWaitDecisionKind.Hold,
                    Binding = null,
                    Reason = "边界返回等待但同步快照未能确认等待条件",
                    NoSendConfirmed = true,
                };
            var settled = boundaryDecision.Kind == LocalWaitDecisionKind.Wait
                ? TryRegisterLocalWait(run, occurrence, attempt, boundaryDecision)
                : new LocalWaitRegistrationOutcome { Park = true, Reason = boundaryDecision.Reason, Decision = boundaryDecision };
            run.LocalWaitDecision = SanitizeWaitDecision(settled.Decision ?? boundaryDecision with
            {
                Kind = LocalWaitDecisionKind.Hold,
                Binding = null,
                Reason = string.IsNullOrWhiteSpace(settled.Reason) ? boundaryDecision.Reason : settled.Reason,
                NoSendConfirmed = true,
            });
            submission.Intent = SubmitIntentState.LocalWaitDeferred;
            return (LocalWaitResultWord, settled.Reason, null);
        }
        if (submit.Uncertain)
        {
            // R4.8 一轮 B1：受理与否不可考——Intent 保持 Submitted（发送已尝试事实），走 Unknown 停驻（调用点）；
            // 不按拒绝推进、不改游标；ObservedTerminal 保持空 = 在飞事实保留，恢复扫描按在飞标 Unknown
            // [R5.2 G6 会诊] **不得降级已确认的受理事实**：边界可能已把本轮落盘为 Accepted（含 jobId），
            // 受理事实是接管/恢复依据，只允许增强不允许回退——否则后续异常会被当作「未受理」处理。
            submission.Intent = submission.Intent == SubmitIntentState.Accepted
                ? SubmitIntentState.Accepted
                : SubmitIntentState.Submitted;
            return ("unknown", "提交结果不可考：" + Sanitize(submit.RejectReason), null);
        }
        if (!submit.Accepted)
        {
            // 同理：已 Accepted 的提交不得因本层拿到 Rejected 而回退（受理事实优先，冲突留待对账）。
            // 且此时**不得按「确定拒绝」推进游标**（会记拒绝结果并可能继续下一节点）——冲突一律按 Unknown 停驻。
            if (submission.Intent == SubmitIntentState.Accepted)
                return ("unknown", "提交被拒但已存在受理事实（冲突，保守 Unknown 留待对账）："
                                   + Sanitize(submit.RejectReason), null);
            submission.Intent = SubmitIntentState.Rejected;
            return ("rejected", "提交被拒绝：" + Sanitize(submit.RejectReason), null);
        }
        submission.Intent = SubmitIntentState.Accepted;
        submission.JobId = submit.JobId;
        _runs.Update(run); // 受理事实落盘（提交仍在飞：ObservedTerminal 未填写）

        CancellationTokenSource leaf;
        lock (control.Sync)
        {
            leaf = CancellationTokenSource.CreateLinkedTokenSource(ct);
            control.LeafCts = leaf;
            if (control.PendingSkip is { } skip)
            {
                control.PendingSkip = null;
                if (skip.Matches(occurrence)) leaf.Cancel(); // B4：空窗到达的跳过在叶子建立时生效
                else Log(run, "过期跳过动作已丢弃（出现身份漂移，不误伤后续节点）。");
            }
        }
        try
        {
            BoundaryTerminalResult terminal;
            try
            {
                terminal = await _boundary.AwaitSubmissionExitAsync(run, submission, leaf.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 叶子被取消（显式跳过）：远端取消确认后再推进（B4 确认阶段；Stop 会先取消运行令牌）
                return await ConfirmSkipAsync(run, submission, ct).ConfigureAwait(false);
            }
            if (terminal.Uncertain)
            {
                // R4.8 一轮 B1：终态查询不可考——Unknown 停驻（调用点），rawTerminal=null（ObservedTerminal 保持空）
                return ("unknown", "终态查询不可考：" + Sanitize(terminal.Reason), null);
            }
            submission.ExecutionExitConfirmed = terminal.ExecutionExitConfirmed;
            submission.ExecutionExitDisposition = terminal.ExecutionExitDisposition;
            submission.EffectState = terminal.ExecutionExitConfirmed ? terminal.Terminal : null;
            var mapped = MapTerminal(terminal.Terminal!);
            return (mapped.Result, mapped.Reason ?? terminal.Reason, terminal.Terminal); // I2：原始词随结果返回，ObservedTerminal 只存它
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>显式跳过确认（B4：请求跳过→取消中→已确认/未知；未确认不得当成功推进）。
    /// 第三元 = 确认阶段观察到的原始线协议词（I2；超时未观察到 = null）。</summary>
    private async Task<(string Result, string? Reason, string? RawTerminal)> ConfirmSkipAsync(
        WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        // R4.8 一轮 B3：取消与观察分离——先请求远端取消一次（独立有界令牌 ≤5s，best-effort 忽略结果），
        // 再纯观察确认；AwaitTerminalAsync 不再承担取消副作用（确认超时不重发取消，15s 预算即实际退出上界）
        try
        {
            using var cancelBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancelBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await _boundary.RequestSubmissionCancelAsync(run, submission, cancelBudget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best-effort：取消请求失败不阻断确认观察，终态以观察为准 */ }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.SkipConfirmTimeout);
        try
        {
            var terminal = await _boundary.AwaitSubmissionExitAsync(run, submission, timeout.Token).ConfigureAwait(false);
            if (terminal.Uncertain || !terminal.ExecutionExitConfirmed)
                return ("cancelUnconfirmed", $"跳过请求后远端终态/退出不可考（{Sanitize(terminal.Reason)}）", null);
            submission.ExecutionExitConfirmed = terminal.ExecutionExitConfirmed;
            submission.ExecutionExitDisposition = terminal.ExecutionExitDisposition;
            submission.EffectState = terminal.Terminal;
            return terminal.Terminal switch
            {
                "cancelled" => ("skippedUser", "显式跳过当前节点（远端取消已确认）", terminal.Terminal),
                // I2：显式跳过意图与远端正常跳过竞态——如实记 skippedFilter（不计 skippedUser；两者均不阻断收尾，D15）
                "skipped" => ("skippedFilter", "跳过请求到达时远端已正常跳过（来源保留，不计入显式跳过）", terminal.Terminal),
                "succeeded" => ("succeeded", "跳过请求到达时节点已完成（留痕，不算跳过）", terminal.Terminal),
                var t => ("failed", $"跳过请求后观察到意外终态 {Sanitize(t)}", terminal.Terminal),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("cancelUnconfirmed", $"跳过请求后 {_opt.SkipConfirmTimeout.TotalSeconds:0}s 内远端终态未确认", null);
        }
    }

    /// <summary>
    /// 边界终态词汇 → 节点结果（R4.6 I2：原始线协议词与归一化业务词分开识别——ObservedTerminal 只存原始词，
    /// 恢复回流（B3-③）经本映射不会把 skippedUser/skippedFilter 误判 failed；未知词汇按 failed，保守不猜成功）。
    /// </summary>
    private static (string Result, string? Reason) MapTerminal(string terminal)
        => terminal switch
        {
            "succeeded" => ("succeeded", null),
            "cancelled" => ("cancelled", "BGI 侧取消事实"),
            "skipped" => ("skippedFilter", "BGI 侧正常跳过（远端词表 skipped；D15 不阻断收尾）"),
            // 归一化业务词恒等映射（仅恢复回流路径可达）
            "skippedUser" => ("skippedUser", null),
            "skippedFilter" => ("skippedFilter", null),
            "failed" => ("failed", null),
            "rejected" => ("rejected", null),
            var t => ("failed", $"作业终态 {t}"),
        };

    /// <summary>
    /// 前置策略执行（D8 + R4.6 E2-8'/B2/B3）：逐策略实例意图先行落盘（PrerequisiteActionRecord，发送前）；
    /// 同键成功事实跳过（崩溃恢复不重发）；在飞/未知记录先对账（查不到不盲目重发）；
    /// 前置段建立叶子令牌（SkipCurrent 绑定出现身份取消在飞前置，远端取消确认链同 B4）。
    /// 返回 null = 全部放行；否则 (结果词, 原因) 由调用点按结构化结果处置。
    /// </summary>
    private static PrerequisiteActionState PrerequisiteState(PrerequisiteStatus status) => status switch
    {
        PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
        PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
        PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
        _ => PrerequisiteActionState.Failed,
    };

    private async Task<(string Result, string? Reason)?> ExecutePrerequisitesAsync(WorkflowRunRecord run, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        if (node.Strategies.All(s => s.Kind == "condition.weekdays")) return null;

        CancellationTokenSource leaf;
        lock (control.Sync)
        {
            leaf = CancellationTokenSource.CreateLinkedTokenSource(ct);
            control.LeafCts = leaf;
            if (control.PendingSkip is { } skip)
            {
                control.PendingSkip = null;
                if (skip.Matches(occurrence)) leaf.Cancel(); // 空窗到达的跳过在叶子建立时生效
                else Log(run, "过期跳过动作已丢弃（出现身份漂移，不误伤后续节点）。");
            }
        }
        try
        {
            for (var i = 0; i < node.Strategies.Count; i++)
            {
                var strategy = node.Strategies[i];
                if (strategy.Kind == "condition.weekdays") continue; // 闸门已评估

                // E2-8' 身份来源：redeemCode 缺 uid 时注入同节点 prerequisite.account 的 uid（均无则适配器响亮失败；Planner 预检已拦截）
                var effective = strategy;
                if (strategy.Kind == "prerequisite.redeemCode" && string.IsNullOrWhiteSpace(strategy.GetString("uid")))
                {
                    var siblingUid = node.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.account")?.GetString("uid");
                    if (!string.IsNullOrWhiteSpace(siblingUid))
                    {
                        var merged = strategy.Params is not null
                            ? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(strategy.Params)
                            : new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();
                        merged["uid"] = System.Text.Json.JsonSerializer.SerializeToElement(siblingUid);
                        effective = new WorkflowStrategy { Kind = strategy.Kind, Params = merged };
                    }
                }
                var accountKey = RunStore.DeriveAccountKey(effective.GetString("uid"));

                // 四轮阻断 3：完整动作身份比对（出现身份 + 策略索引 + 操作类型 + 账号标识哈希）
                var record = run.PrerequisiteActions.FirstOrDefault(r =>
                    r.Matches(occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration, 1, i,
                        effective.Kind, accountKey));
                if (record is { State: PrerequisiteActionState.Succeeded })
                    continue; // 同键成功事实（游戏态事实跨纪元有效：切号/兑换效果不随 BGI 重启消失；同 attempt 恢复不重发）
                if (record is { State: PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled })
                {
                    // 四轮阻断 3/I4：既有终态事实不自动重试（新执行尝试 R4.6 不开放；attempt 恒 1，需人工处置）
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {effective.Kind} 存在既有{record.State}终态事实（不自动重试，需人工处置）：{record.Reason}");
                }
                if (record is { State: PrerequisiteActionState.Intent or PrerequisiteActionState.Submitted or PrerequisiteActionState.Unknown })
                {
                    // 恢复对账：先查远端权威终态，查不到不盲目重发（B2）；叶子令牌可中断对账（四轮阻断 1）
                    PrerequisiteResult reconciled;
                    try
                    {
                        reconciled = await _prerequisites.ReconcileAsync(run, record, leaf.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 对账期叶子取消 = 显式跳过：同走远端取消确认链
                        var confirmed0 = await _prerequisites.ConfirmCancellationAsync(run, record, ct).ConfigureAwait(false);
                        record.State = PrerequisiteState(confirmed0.Status);
                        record.Reason = Sanitize(confirmed0.Reason);
                        _runs.Update(run);
                        return confirmed0.Status != PrerequisiteStatus.Unknown
                            ? ("skippedUser", "前置对账期显式跳过（远端取消已确认）")
                            : ("cancelUnconfirmed", $"前置对账期跳过后远端终态未确认：{Sanitize(confirmed0.Reason)}");
                    }
                    record.State = reconciled.Status switch
                    {
                        PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                        PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                        PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                        _ => PrerequisiteActionState.Failed,
                    };
                    record.Reason = Sanitize(reconciled.Reason);
                    _runs.Update(run);
                    if (record.State == PrerequisiteActionState.Succeeded) continue;
                    if (record.State == PrerequisiteActionState.Unknown)
                        return ("unknown", $"前置策略 {effective.Kind} 结果不确定（对账未决）：{Sanitize(reconciled.Reason)}");
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {effective.Kind}（对账终态）：{Sanitize(reconciled.Reason)}");
                }

                // 意图先行落盘（发送前；E2-8'）
                record = new PrerequisiteActionRecord
                {
                    NodeId = occurrence.NodeId,
                    Occurrence = occurrence.Occurrence,
                    LoopIteration = occurrence.LoopIteration,
                    Attempt = 1,
                    StrategyIndex = i,
                    Kind = effective.Kind,
                    AccountKey = accountKey,
                    State = PrerequisiteActionState.Intent,
                    RecordedAt = _opt.Clock(),
                };
                run.PrerequisiteActions.Add(record);
                _runs.Update(run);

                await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
                PrerequisiteResult result;
                try
                {
                    result = await _prerequisites.ExecuteAsync(effective, run, occurrence, leaf.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 叶子取消 = 显式跳过：远端取消确认链（确认超时 = Unknown，不猜成功）
                    var confirmed = await _prerequisites.ConfirmCancellationAsync(run, record, ct).ConfigureAwait(false);
                    record.State = PrerequisiteState(confirmed.Status);
                    record.Reason = Sanitize(confirmed.Reason);
                    _runs.Update(run);
                    return confirmed.Status != PrerequisiteStatus.Unknown
                        ? ("skippedUser", "前置期显式跳过（远端取消已确认）")
                        : ("cancelUnconfirmed", $"前置期跳过后远端终态未确认：{Sanitize(confirmed.Reason)}");
                }

                // 终态与结果同写（引擎事务）
                record.State = result.Status switch
                {
                    PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                    PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                    PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                    _ => PrerequisiteActionState.Failed,
                };
                record.Reason = Sanitize(result.Reason);
                record.JobId ??= result.JobId;
                _runs.Update(run);

                switch (result.Status)
                {
                    case PrerequisiteStatus.Proceed: continue;
                    case PrerequisiteStatus.Rejected:
                        return ("rejected", $"前置策略 {effective.Kind} 被拒绝：{Sanitize(result.Reason)}");
                    case PrerequisiteStatus.Cancelled:
                        return ("cancelled", $"前置策略 {effective.Kind} 被取消：{Sanitize(result.Reason)}");
                    case PrerequisiteStatus.Unknown:
                        return ("unknown", $"前置策略 {effective.Kind} 结果不确定：{Sanitize(result.Reason)}");
                    default:
                        return ("failed", $"前置策略 {effective.Kind} 未通过：{Sanitize(result.Reason)}");
                }
            }
            return null;
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>可挂载触发器判定（armTrigger 语义，宿主受理预验与驱动起步复验共用同一定义——七轮 重要3 单一事实源）。
    /// 四轮 重要5 收窄：**仅 trigger.time 入口等待才算可挂载**——结构性循环首轮立即执行不算（AwaitLoopRoundStartAsync
    /// 只在 LoopIteration&gt;0 生效），若承认 loop 可挂载，arm 会绕过 start 应有的混用/快照守卫却立即提交。</summary>
    internal static bool HasMountableTrigger(WorkflowDocument doc)
        => doc.Triggers.Any(t => t.Kind == "trigger.time");

    /// <summary>I3：持久化备注脱敏——长数字串（UID 形态）打码；受控原因码+脱敏摘要，不存原始敏感面。</summary>
    internal static string Sanitize(string? text)
        => string.IsNullOrEmpty(text) ? "" : System.Text.RegularExpressions.Regex.Replace(text, "\\d{5,}", "***");

    internal static LocalWaitDecisionRecord SanitizeWaitDecision(LocalWaitDecisionRecord decision)
        => decision with { Reason = Sanitize(decision.Reason) };

    /// <summary>顶层触发器等待（多触发器取最近；未知触发器响亮失败；暂停可打断）。</summary>
    private async Task AwaitFlowTriggersAsync(WorkflowRunRecord run, WorkflowPlan plan, RunControl control, CancellationToken ct)
    {
        if (plan.Document.Triggers.Count == 0) return;
        DateTimeOffset? earliest = null;
        foreach (var trigger in plan.Document.Triggers)
        {
            var next = WorkflowTriggerSchedule.NextFire(trigger, _opt.Clock(), out var reason)
                ?? throw new InvalidOperationException("触发器不可用：" + reason);
            earliest = earliest is null || next < earliest ? next : earliest;
        }
        run.State = WorkflowRunState.Waiting;
        _runs.Update(run);
        await WaitAsync(run, "trigger.time", earliest!.Value, control, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 轮次起点等待（B9：新一轮边界统一入口；skipAcrossDays 公式化，见 WorkflowLoopSchedule）。
    /// 返回 false = 时刻计算失败（流程级失败）。暂停打断时不记 LastScheduledRoundWait（恢复重排本轮）。
    /// </summary>
    private async Task<bool> AwaitLoopRoundStartAsync(WorkflowRunRecord run, WorkflowPlan plan,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        var loop = plan.Document.Loop;
        if (loop is null) return true; // LoopIteration>0 蕴含循环定义；防御性放行
        var next = WorkflowLoopSchedule.NextRoundStart(loop, _opt.Clock(), out var reason);
        if (next is null)
        {
            Log(run, "循环时刻计算失败：" + reason);
            return false;
        }
        if (next.Value > _opt.Clock())
        {
            await WaitAsync(run, "loop.scheduled", next.Value, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return true;
        }
        run.LastScheduledRoundWait = occurrence.LoopIteration;
        _runs.Update(run);
        return true;
    }

    /// <summary>等待（不占槽位：纯本地可取消延时 + RunStore 等待状态持久化；暂停打断保留等待记录）。</summary>
    internal async Task<WorkflowStopAuthorityRecord?> AcquireExplicitIntentStopAuthorityAsync(string intentId, long timestamp, CancellationToken ct)
    {
        var authority = await _boundary.AcquireStopAuthorityAsync(intentId, timestamp, ct).ConfigureAwait(false);
        if (_boundary.RequiresStopAuthority && authority is null)
            throw new InvalidOperationException("BGI 停止权威不可确认，未创建运行；请核对当前连接和能力。");
        return authority;
    }

    internal async Task ValidateInheritedStopAuthorityAsync(WorkflowStopAuthorityRecord? authority, CancellationToken ct)
    {
        if (!_boundary.RequiresStopAuthority) return;
        if (authority is null || authority.Version < 0 || authority.IntentTimestamp <= 0
            || string.IsNullOrWhiteSpace(authority.IntentId))
            throw new InvalidOperationException("来源停止授权缺失；禁止以当前版本代替原挂载/启动意图。");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(5));
        if (await _boundary.InspectStopAuthorityAsync(authority, budget.Token).ConfigureAwait(false) != true)
            throw new InvalidOperationException("来源停止授权已撤销或不可确认；未创建新运行。");
    }

    private async Task VerifyStopAuthorityAsync(WorkflowRunRecord run, CancellationToken ct)
    {
        if (!_boundary.RequiresStopAuthority) return;
        if (_runs.Load(run.RunId) is { StopRequested: true })
            throw new OperationCanceledException("运行已耐久停止，不允许恢复或推进", ct);
        if (run.StopAuthority is not { } authority)
            throw new StopAuthorityUnknownException("旧运行缺少停止基线，禁止自动刷新；需要只读对账或新显式运行。");
        bool? current;
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(5));
            current = _runs.IsStartupSourceRevoked(authority) ? false
                : await _boundary.InspectStopAuthorityAsync(authority, budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { current = null; }
        if (current is null) throw new StopAuthorityUnknownException("停止权威查询不可确认；保留原基线和责任，等待对账。");
        if (current == false)
        {
            if (!_runs.UpdateMergingIf(run.RunId, latest => { latest.StopRequested = true; return true; }, out _))
                throw new StopAuthorityUnknownException("停止记录不可读取，保留责任待对账。");
            if (_runs.Load(run.RunId) is { } latest) RunStore.RebaseOnto(run, latest);
            throw new OperationCanceledException("原运行停止基线已失效", ct);
        }
    }

    private async Task WaitAsync(WorkflowRunRecord run, string kind, DateTimeOffset until, RunControl control, CancellationToken ct)
    {
        run.State = WorkflowRunState.Waiting;
        run.Wait = new WaitStateRecord
        {
            Kind = kind,
            NextTriggerAt = until,
            TriggerOccurrenceId = $"{kind}:{until:yyyyMMddHHmm}",
        };
        _runs.Update(run);
        Log(run, $"进入等待（{kind}）至 {until:yyyy-MM-dd HH:mm}（不持有执行锁、不提交等待作业）");
        var delay = until - _opt.Clock();
        if (_boundary.RequiresStopAuthority)
        {
            while (delay > TimeSpan.Zero)
            {
                await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
                using var sliceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var slice = _opt.DelayAsync(delay < TimeSpan.FromSeconds(2) ? delay : TimeSpan.FromSeconds(2), sliceCts.Token);
                if (await Task.WhenAny(slice, control.PauseSignal.Task).ConfigureAwait(false) != slice)
                {
                    sliceCts.Cancel();
                    try { await slice.ConfigureAwait(false); } catch (OperationCanceledException) { }
                    return;
                }
                await slice.ConfigureAwait(false);
                delay = until - _opt.Clock();
            }
            await VerifyStopAuthorityAsync(run, ct).ConfigureAwait(false);
        }
        else if (delay > TimeSpan.Zero)
        {
            // R4.8 一轮 I5：延时走独立链接令牌——暂停打断即取消遗留 delayTask（手动时钟夹具不悬挂、计时器不泄漏）
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = _opt.DelayAsync(delay, delayCts.Token);
            if (await Task.WhenAny(delayTask, control.PauseSignal.Task).ConfigureAwait(false) != delayTask)
            {
                delayCts.Cancel();
                try { await delayTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* 暂停打断的主动取消，按暂停语义返回 */ }
                return; // 暂停打断：Wait 记录保留，恢复后按 NextTriggerAt 重排剩余
            }
            await delayTask.ConfigureAwait(false); // 传播 Stop 取消
        }
        run.Wait = null;
        run.State = WorkflowRunState.Running;
        _runs.Update(run);
    }

    /// <summary>边界动作处理：修订对账（默认节点边界生效）+ 显式动作消费。</summary>
    private (WorkflowPlan Plan, WorkflowNodeOccurrence? Occurrence) ProcessBoundaryActions(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence? occurrence, RunControl control)
    {
        var reloadRequested = false;
        while (control.Actions.TryDequeue(out var action))
        {
            switch (action)
            {
                case WorkflowRunAction.ReloadDefinition:
                    reloadRequested = true;
                    break;
                case WorkflowRunAction.SkipCurrent:
                    Log(run, "显式跳过请求已登记（绑定请求时出现身份；叶子建立即生效）。");
                    break;
                // Stop/Pause 经运行令牌/暂停标志生效，不在此消费
            }
        }

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId); // 文档+修订同源（B1）
        if (!reloadRequested && snapshot.Revision == run.WorkflowRevision) return (plan, occurrence);

        var newPlan = new WorkflowPlan(snapshot.Document);
        var preflight = newPlan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1：重载入口同接能力协商预检
        if (!preflight.Executable)
        {
            Log(run, "流程新修订预检未通过，沿用旧定义继续：" + string.Join("；", preflight.BlockingReasons));
            return (plan, occurrence);
        }

        // 新修订节点边界生效：从最后完成身份在新定义中重算后继（B1：插入/删除/重排不错位；
        // occurrence 为 null 时即链尾对账——新修订追加的节点会被执行）
        var relocated = RecomputeSuccessor(run, newPlan);
        run.WorkflowRevision = snapshot.Revision;
        ApplyRelocation(run, relocated);
        _runs.Update(run);
        Log(run, $"流程定义已重载（修订 {snapshot.Revision[..Math.Min(8, snapshot.Revision.Length)]}…），按稳定身份重算后继，节点边界生效。");
        return (newPlan, relocated);
    }

    /// <summary>
    /// 按稳定出现身份重算后继（B1：修订插入/删除/重排不错位）。
    /// **[批次 20／Wave1 R5 重要-2]** 锚＝最后一条**完成**结果（排除 <see cref="LocalWaitResultWord"/>
    /// 停驻标记——它零发送、未完成；**不得**按停驻节点算 Next 或按链尾放行）。
    /// **裁决次序（与实现逐字对齐，R15 建议-4）**：①完成锚（含回溯）算出候选；②候选为 null 或
    /// 存在可定位停驻出现时，**停驻义务优先**（不变量②：含前插保全）；③无停驻义务时取候选；
    /// ④完成锚全不可定位且无停驻 ⇒ 链尾。**「无完成节点 ⇒ 取链首」仅在无停驻义务时成立。**
    /// unknown/cancelUnconfirmed 同为非完成词，但其锚污染被 Unknown 禁恢复态挡住（恢复前必先对账并追加
    /// 真实终态词），维持既有口径不动（本批不扩改既有恢复语义）。
    /// **两条不变量（本批会诊逐轮收敛所得）**：
    /// ①**不重跑已完成出现**——返回点本身先过滤完成结果（R12 重要-1）；驱动推进层对**整段推进**
    /// 按稳定出现身份跳过已完成项（[BO-8] DriveAsync 完成过滤），真实链尾仍存活的停驻义务按计划
    /// 全序重入（[BO-9] TryRelocateToLivePark），故本函数只需保证**返回点**未完成且不吞停驻义务；
    /// ②**不吞停驻出现**——引擎自己立下的重驱义务（<see cref="LocalWaitResultWord"/>）：任何返回链尾
    ///   （null/TailReached）的路径都必须先复核「计划中是否仍有可定位的停驻出现」，有则按它继续
    ///   （R14 F2）；停驻出现之前若有**从未执行**的出现，则按更早者继续（R12 建议-1／R14 F1，
    ///   且探针必须**与停驻同轮次**起步，不得锁死在第 0 轮）。
    /// </summary>
    internal WorkflowNodeOccurrence? RecomputeSuccessor(WorkflowRunRecord run, WorkflowPlan plan)
    {
        var completionOutcomes = run.NodeOutcomes.Where(o => o.Result != LocalWaitResultWord).ToList();
        var parkedOutcomes = run.NodeOutcomes.Where(o => o.Result == LocalWaitResultWord).ToList();

        // 锚回溯：最后完成节点在新修订中已删除时，回溯更早的仍可定位完成节点（R11 F-C）。
        for (var i = completionOutcomes.Count - 1; i >= 0; i--)
        {
            var anchor = completionOutcomes[i];
            if (!plan.TryLocate(anchor.NodeId, anchor.Occurrence, anchor.LoopIteration, out var anchorOcc)) continue;
            if (i != completionOutcomes.Count - 1)
            {
                Log(run, $"最后完成身份 {completionOutcomes[^1].NodeId}#{completionOutcomes[^1].Occurrence} "
                         + $"在新修订中已消失，回溯到更早可定位完成身份 {anchor.NodeId}#{anchor.Occurrence} 重算后继。");
            }
            var candidate = plan.Next(anchorOcc);
            while (candidate is not null && HasCompletedOutcome(run, candidate))
                candidate = plan.Next(candidate); // 不变量①：跳过已完成出现
            // 锚可定位路径的返回可能落在**链尾**（candidate == null），也可能落在「锚之后仍有未完成工作」
            // 的节点上——两者都不得吞掉停驻义务（不变量②）：
            // ·链尾（R14 F2）：修订把停驻点重排到锚之前 ⇒ 纯向后走至 null ⇒ 假成功＋零发送节点被吞；
            // ·非链尾（R15 重要-1）：修订把停驻点重排到锚之前**且锚之后还有未执行节点** ⇒ 返回那个
            //   未执行节点、停驻点再也不被重驱 ⇒ 运行最终仍 Succeeded（同一可观察后果、另一条分支）。
            // 故此处**无条件**复核停驻义务：有可定位停驻出现 ⇒ 以停驻义务为准（含其前插保全）；
            // 无停驻 ⇒ 才用纯锚路径结果（保持 B1 修订语义不变）。
            // [Wave1 R18 必改-1；BO-6/7-D1 修订] 下界：锚候选非 null ⇒ 候选本身；candidate 为 null
            // （锚在链尾）⇒ **计划全序最早**的可定位有效停驻点（[R34 重要-F5] 口径统一；不再取追加序
            // 最后者，该口径会漏掉低轮次的有效停驻）。探针不得返回早于下界的未执行出现：早于锚候选的
            // 出现按修订语义由 candidate 自身或其后续承载（不变量②的前插保全只在停驻同轮次内成立）。
            // 停驻点之前的新插节点不在此路径承载（其重驱义务由 C11 重驱合同覆盖，属接线批语义面）。
            // 原「驱动推进段穿越已完成出现并重复提交」的历史理由已由驱动推进层的稳定身份过滤消除
            // （BO-8/BO-9：见 DriveAsync 的 HasCompletedOutcome 调用点与 TryRelocateToLivePark）。
            // [Wave1 R19 必改-1 补强] 锚可定位且 candidate 非 null 时，仍须检查有效停驻点的
            // **位置安全性**：锚前的有效停驻义务优先作为恢复点；驱动推进层会过滤其后已完成出现
            // （BO-6／BO-8，覆盖整段推进），由此同时保留停驻重驱与不重提已完成节点。
            // [Wave1 R25 重要-1] **全部**有效停驻点逐一做位置安全性检查（不得因最后一条安全而
            // 跳过更早的）：任一有效停驻不在锚后安全路径 ⇒ 记录冲突并优先返回最早有效停驻点。
            // R25 反例（删除 P1→执行越过→同身份加回锚前）中更早有效停驻逃出检测的形态由此封死。
            var hasUnsafeParkedBeforeAnchor = false;
            for (var pi = parkedOutcomes.Count - 1; pi >= 0; pi--)
            {
                var po = parkedOutcomes[pi];
                if (!plan.TryLocate(po.NodeId, po.Occurrence, po.LoopIteration, out var unsafeParked)) continue;
                if (HasCompletedOutcome(run, unsafeParked)) continue;
                var onSafePathAfterAnchor = unsafeParked.LoopIteration > anchorOcc.LoopIteration
                    || (unsafeParked.LoopIteration == anchorOcc.LoopIteration
                        && unsafeParked.SequenceIndex > anchorOcc.SequenceIndex);
                if (!onSafePathAfterAnchor)
                {
                    hasUnsafeParkedBeforeAnchor = true;
                    Log(run, $"有效停驻点 {unsafeParked.NodeId}#{unsafeParked.Occurrence} 被修订重排到已完成锚 "
                             + $"{anchorOcc.NodeId}#{anchorOcc.Occurrence} 之前：返回任一侧都会违反不变量①或②"
                             + "；优先保留停驻义务，恢复推进时过滤已完成出现（BO-6）。");
                }
            }
            WorkflowNodeOccurrence? lowerBound = candidate;
            if (lowerBound is null)
            {
                // [Wave1 R19 重要-2；R23 重要-2 更正；R34 重要-F5 口径统一] **跨代际高轮次停驻标记路径**
                // （TryLocate 透传 loopIteration ⇒ 高轮次标记可定位）。下界取**计划全序最早**的有效停驻点
                //（与 ParkedRescue 选取同口径，[R34 重要-F5]——追加序最后者会漏更早有效停驻所在的低轮次，
                // 使其前插探针被过期高轮次位置误挡）。
                foreach (var po in parkedOutcomes)
                {
                    if (!plan.TryLocate(po.NodeId, po.Occurrence, po.LoopIteration, out var tailBound)) continue;
                    if (HasCompletedOutcome(run, tailBound)) continue;
                    if (lowerBound is not null
                        && (tailBound.LoopIteration > lowerBound.LoopIteration
                            || (tailBound.LoopIteration == lowerBound.LoopIteration
                                && tailBound.SequenceIndex > lowerBound.SequenceIndex)))
                    {
                        continue; // 已有更早（全序）的有效停驻点作下界
                    }
                    lowerBound = tailBound;
                }
            }
            var parkedRescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes,
                lowerBound: lowerBound);
            if (hasUnsafeParkedBeforeAnchor && parkedRescue is null)
            {
                // A valid unsafe marker was found above, so a missing rescue is an inconsistent
                // state. The final aggregation guard turns any still-live marker into Failed.
                Log(run, "已发现可定位的锚前停驻义务，但未能构造恢复点；禁止将其当作普通链尾成功。");
            }
            // [Wave1 R24 重要-2；第 1 轮会诊建议-2 修订] rescue 与 candidate 都非 null 时按**计划全序取较早者**：
            // **有循环定义**时线性推进回绕到下一轮，较晚者自然到达（未完成 ⇒ 会被执行）；
            // **无循环定义**时 `plan.Next` 到链尾即 null，较晚者**不会**自然到达——该未履行义务由
            // DriveAsync 的链尾重入（TryRelocateToOutstandingObligation）按计划全序重建并重驱
            // （BO-9；第 1 轮会诊 IMPORTANT-1 反例 `[A,P]` + rescue `A@1` 即此形态）。
            // 固定 rescue 覆盖 candidate 的旧形态（R24 反例：修订在锚后插入 C@0、停驻在更晚轮次 A@1
            // ⇒ 旧代码返回 A@1 ⇒ C@0 永不进 NodeOutcomes ⇒ 假成功丢步）。
            if (parkedRescue is not null && candidate is not null
                && (candidate.LoopIteration < parkedRescue.LoopIteration
                    || (candidate.LoopIteration == parkedRescue.LoopIteration
                        && candidate.SequenceIndex < parkedRescue.SequenceIndex)))
            {
                return candidate; // candidate 早于 rescue ⇒ 先执行 candidate，线性推进自然到达 rescue
            }
            return parkedRescue ?? candidate;
        }

        if (completionOutcomes.Count == 0)
        {
            var noAnchorRescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes,
                lowerBound: null);
            return noAnchorRescue ?? plan.FirstOccurrence();
        }

        // 全部完成锚在新修订中不可定位（R7 重要-1）
        var rescue = ParkedRescue(run, plan, parkedOutcomes, completionOutcomes, lowerBound: null);
        if (rescue is not null) return rescue;
        Log(run, "完成身份与停驻身份在新修订中均已消失，按链尾处理（已完成节点不重跑）。");
        return null;
    }

    /// <summary>
    /// **停驻义务复核**（不变量②）：计划中仍有可定位停驻出现 ⇒ 返回恢复应继续的出现；
    /// 无可定位停驻 ⇒ null（调用方决定链首/链尾）。取**计划全序最早**的可定位停驻标记为基准
    /// （[Wave1 R25 重要-1／R34 重要-F5] 口径统一）；
    /// 若其之前（**同一轮次内**）存在从未执行的出现，返回更早者（不静默跳过未执行节点，
    /// R12 建议-1／R14 F1：探针**直接构造该停驻所在轮次的链首出现**再向前探查——由基准沿 Next
    /// 反向不可行，逐轮推进又会锁死在第 0 轮，故取「同轮链首 + 序号上界」的构造口径）。
    ///
    /// **返回语义**：返回最早仍有效的停驻义务或其前插未执行出现；没有可定位的未完成停驻时返回 null。
    /// 若恢复点越过已完成出现，由驱动推进层在推进时按稳定身份过滤这些完成出现（BO-6／BO-8）。
    /// </summary>
    private WorkflowNodeOccurrence? ParkedRescue(WorkflowRunRecord run, WorkflowPlan plan,
        List<WorkflowNodeOutcome> parkedOutcomes, List<WorkflowNodeOutcome> completionOutcomes,
        WorkflowNodeOccurrence? lowerBound)
    {
        // [Wave1 R25 重要-1] 取**计划全序最早**的有效停驻点（不再「最后追加者首中即返回」）：
        // 更早的有效停驻点（如「删除→执行越过→同身份加回锚前」舞步复活的标记）同样承载重驱义务，
        // 首中即返回会让它逃出救援与冲突检测（静默吞）。最早者重驱后由 BO-6 恢复推进过滤跳过已完成出现。
        var hasEarliest = false;
        var earliestOcc = default(WorkflowNodeOccurrence);
        foreach (var po in parkedOutcomes)
        {
            if (!plan.TryLocate(po.NodeId, po.Occurrence, po.LoopIteration, out var candidateOcc)) continue;
            if (HasCompletedOutcome(run, candidateOcc)) continue; // 过期标记（R16 必改-1）
            if (!hasEarliest
                || candidateOcc.LoopIteration < earliestOcc.LoopIteration
                || (candidateOcc.LoopIteration == earliestOcc.LoopIteration
                    && candidateOcc.SequenceIndex < earliestOcc.SequenceIndex))
            {
                earliestOcc = candidateOcc;
                hasEarliest = true;
            }
        }
        if (!hasEarliest) return null;
        for (var i = parkedOutcomes.Count - 1; i >= 0; i--)
        {
            var parked = parkedOutcomes[i];
            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var parkedOcc)) continue;
            if (!parkedOcc.Equals(earliestOcc)) continue;
            // [Wave1 R16 必改-1] **过期停驻标记**：同一出现先停驻、恢复后完成（NodeOutcomes 为追加式，
            // 旧 waitLocally 条目不删除）⇒ 该标记已失去重驱义务，**不得**把它当恢复点，否则会把恢复点
            // 拖回已完成出现并重复提交（违反不变量①「不重跑已完成出现」）。
            if (HasCompletedOutcome(run, parkedOcc)) continue;
            // 直接构造**与停驻同轮次**的链首出现并向前探查（R14 F1：不得从第 0 轮起步；
            // 也不经 Next 回绕——计划可能无循环段，越尾即 null，会导致探针整体失效）。
            for (var probe = plan.FirstOccurrence() is { } head
                     ? new WorkflowNodeOccurrence(head.NodeId, head.SequenceIndex, head.Occurrence, parkedOcc.LoopIteration)
                     : null;
                 probe is not null && probe.SequenceIndex < parkedOcc.SequenceIndex;
                 probe = plan.Next(probe))
            {
                if (HasCompletedOutcome(run, probe)) continue;
                // [Wave1 R18 必改-1；BO-6/7-D1 修订] **下界约束**：锚可定位路径下，探针结果不得
                // 早于锚候选——否则会越过修订语义下由 candidate 承载的出现（不变量②的前插保全
                // 只在停驻同轮次内成立）。早于下界的未执行出现由「锚可定位路径的 candidate」自身
                // 或其后续承载（修订语义内）。驱动推进段对已完成出现已有稳定身份过滤（BO-8/BO-9），
                // 本下界不再承担「防止穿越已完成出现」的职责。
                // [Wave1 R21 必改-F1] 比较必须用计划全序 **(LoopIteration, SequenceIndex) 字典序**——
                // 序列号与轮次拆成两条独立条件会把「轮次更晚但序列号更小」（实际在下界之后）的错误排除。
                if (lowerBound is not null
                    && (probe.LoopIteration < lowerBound.LoopIteration
                        || (probe.LoopIteration == lowerBound.LoopIteration
                            && probe.SequenceIndex < lowerBound.SequenceIndex)))
                {
                    continue;
                }
                Log(run, $"停驻点 {parkedOcc.NodeId}#{parkedOcc.Occurrence} 前存在从未执行的出现 "
                         + $"{probe.NodeId}#{probe.Occurrence}（轮次 {probe.LoopIteration}），按其继续。");
                return probe;
            }
            if (completionOutcomes.Count == 0 || !completionOutcomes.Any(c =>
                    plan.TryLocate(c.NodeId, c.Occurrence, c.LoopIteration, out _)))
            {
                Log(run, $"完成身份在新修订中不可定位；存在停驻标记 {parked.NodeId}#{parked.Occurrence}，"
                         + "按停驻出现继续（不按链尾放行，零发送节点不得被静默吞）。");
            }
            else
            {
                Log(run, $"锚可定位但向后走至链尾；存在停驻标记 {parked.NodeId}#{parked.Occurrence}，"
                         + "按停驻出现继续（不按链尾放行，零发送节点不得被静默吞）。");
            }
            // RecomputeSuccessor may select a marker before its completed anchors; DriveAsync
            // re-drives this obligation and skips those stable completed identities as it advances.
            return parkedOcc;
        }
        return null;
    }

    /// <summary>
    /// 该出现是否**已在 NodeOutcomes 中有完成结果**（[Wave1 R12 重要-1] 重定位完成性过滤；
    /// 停驻标记 <see cref="LocalWaitResultWord"/> 不算完成——它零发送、未完成，
    /// 与 <see cref="RecomputeSuccessor"/> 的锚口径一致）。
    /// </summary>
    private static bool HasCompletedOutcome(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence)
        => run.NodeOutcomes.Any(o => o.Result != LocalWaitResultWord
            && string.Equals(o.NodeId, occurrence.NodeId, StringComparison.Ordinal)
            && o.Occurrence == occurrence.Occurrence
            && o.LoopIteration == occurrence.LoopIteration);

    /// <summary>
    /// **[BO-9 / R34 F5 重要；第 1 轮会诊 IMPORTANT-1 修复]** 真实链尾处的**未履行恢复义务**重建点：
    /// 返回计划全序最早的一条未履行义务出现；没有时返回 false。义务集合 =
    /// ①仍存活（可在当前修订定位、且无完成结果）的停驻出现——与 <see cref="ParkedRescue"/> 及 tailBound 同口径；
    /// ②每个存活停驻**同轮次**、序号更早的**从未执行**出现（R12 建议-1／R14 F1 的「不静默跳过未执行节点」口径：
    ///   停驻义务被重驱时，其所在轮次在该停驻之前的未执行节点必须先被驱动）。
    /// **前置过滤（第 2 轮会诊 IMPORTANT-2）**：已清偿（同身份已有完成结果）的停驻标记先被剔除，
    ///   既不作重入点也不产生前插义务——否则旧标记会额外执行其同轮更早的未执行节点。
    ///
    /// **为何必须在链尾重建**：`RecomputeSuccessor` 在 candidate 早于 rescue 时返回 candidate 并依赖**线性推进**
    /// 自然到达 rescue（R24）；无循环定义时 `plan.Next` 走到链尾即 null，该依赖不成立 ⇒ rescue 义务被丢。
    /// 反例（第 1 轮会诊 IMPORTANT-1）：计划 `[A,P]`（无循环）+ `A@0` 完成 + `P@1/P@2` 停驻 ⇒
    /// `candidate=P@0`、`rescue=A@1`（同轮前插未执行出现）⇒ 只重入停驻点会让 `A@1` 永不执行而运行假成功。
    ///
    /// **入口即链尾**（持久 `TailReached`）的持久记录由调用方守卫，不在本方法内重开（BO-6 防御语义）。
    /// 判据只用稳定出现身份 `(NodeId, Occurrence, LoopIteration)`＋「无完成结果」：停驻标记本身不算完成；
    /// 过期标记（同身份已补上完成结果）不参与重入。
    /// </summary>
    private static bool TryRelocateToOutstandingObligation(WorkflowRunRecord run, WorkflowPlan plan,
        out WorkflowNodeOccurrence? obligation)
    {
        WorkflowNodeOccurrence? earliest = null;
        void Consider(WorkflowNodeOccurrence candidate)
        {
            if (HasCompletedOutcome(run, candidate)) return;
            if (earliest is null
                || candidate.LoopIteration < earliest.LoopIteration
                || (candidate.LoopIteration == earliest.LoopIteration
                    && candidate.SequenceIndex < earliest.SequenceIndex))
            {
                earliest = candidate;
            }
        }
        foreach (var parked in run.NodeOutcomes.Where(o => o.Result == LocalWaitResultWord))
        {
            if (!plan.TryLocate(parked.NodeId, parked.Occurrence, parked.LoopIteration, out var parkOcc)) continue;
            // [第 2 轮会诊 IMPORTANT-2] **已清偿（同身份已有完成结果）的停驻标记不再承载任何义务**：
            // 它既不是重入点，也**不得**为它扫描同轮前插未执行出现——否则会凭一条旧标记额外执行该轮更早的
            // 未执行节点（反例：`[A,P,T]`→`[A,X,P,T]`，P 已由停驻转为完成，旧标记仍会引出 X 的额外提交）。
            if (HasCompletedOutcome(run, parkOcc)) continue;
            Consider(parkOcc);
            // 同轮次、该停驻之前的未执行出现（与 ParkedRescue 的探针同口径；序号严格递增，不越轮）。
            for (var probe = plan.FirstOccurrence() is { } head
                     ? new WorkflowNodeOccurrence(head.NodeId, head.SequenceIndex, head.Occurrence, parkOcc.LoopIteration)
                     : null;
                 probe is not null && probe.SequenceIndex < parkOcc.SequenceIndex;
                 probe = plan.Next(probe))
            {
                Consider(probe);
            }
        }
        obligation = earliest;
        return earliest is not null;
    }

    /// <summary>游标 → 当前计划中的出现（恢复/推进共用；身份失效按最后完成身份重算，不回链首重跑）。</summary>
    private WorkflowNodeOccurrence? Relocate(WorkflowRunRecord run, WorkflowPlan plan)
    {
        if (run.TailReached) return null;
        if (run.Cursor is null) return plan.FirstOccurrence();
        if (plan.TryLocate(run.Cursor.NodeId, run.Cursor.Occurrence, run.Cursor.LoopIteration, out var occ))
            return occ;
        var relocated = RecomputeSuccessor(run, plan);
        ApplyRelocation(run, relocated);
        _runs.Update(run);
        return relocated;
    }

    private static void ApplyRelocation(WorkflowRunRecord run, WorkflowNodeOccurrence? relocated)
    {
        if (relocated is null)
        {
            run.Cursor = null;
            run.TailReached = true;
        }
        else
        {
            run.TailReached = false;
            run.Cursor = new WorkflowNodeCursor
            {
                NodeId = relocated.NodeId,
                Occurrence = relocated.Occurrence,
                LoopIteration = relocated.LoopIteration,
                Attempt = 1,
            };
        }
    }

    /// <summary>
    /// **[批次 14／D1] 本地持久等待登记（确定零发送的停驻路径）；[批次 20／C4①] 登记载荷合同。**
    /// 产物为结构化 <see cref="LocalWaitRegistrationOutcome"/>：<c>Park=true</c>＝零发送停驻
    /// （**已登记**／**登记被拒**／**接缝命中而队列缺失（接线缺陷）** 三种——[Wave1 R15 重要-2／R17 重要-1]
    /// 后者同样停驻，**不得**回落提交把门面零发送结论改写成真实发送）。当前本方法**全部出口均为
    /// Park=true**；<c>Park=false</c> 仅为接缝未命中预留、无产出方。
    /// 登记动作**不含任何发送许可**：只把等待项落盘（队列本地 <see cref="LocalWaitQueuePolicy.DeriveItemId"/> 幂等键；
    /// **[批次 20／C3]** 准入面 9 元组身份在登记时点由权威组成函数求得并随项落盘）。
    /// **[批次 20／C4①] 登记载荷合同**：新登记**必须**携带持久化稳定前置引用（来自注入的权威来源）；
    /// 来源缺失 ⇒ **拒绝登记**（零发送停驻、不回落提交、队列零变化）——不得登记结构性永不参选的等待项
    /// （那是 C4② 合同前存量的专属形态）。未注入队列时**同样停驻**（[Wave1 R15 重要-2]：接缝命中而
    /// 队列缺失＝接线缺陷，须零发送停驻；批次 14 的「不登记且不短路」口径在本批已按 fail-closed 收紧）。
    /// 纪律：这里是「确定未发送」的本地登记，**不得**借此推进游标或终态化运行。
    /// </summary>
    internal LocalWaitRegistrationOutcome TryRegisterLocalWait(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence,
        int attempt, LocalWaitDecisionRecord decision)
    {
        // 等待来源判定（ShouldRegisterLocalWait 接缝）由**调用点**显式应用，不在此处重复——
        // 本方法是登记机制本体（含登记被拒路径），须可独立验证；判定函数与登记口均已就位，
        // R5 后续批次（D3/D2/D4）接入门面结论后只改判定接缝，不改本方法。
        // [Wave1 R15 重要-2] 队列未注入**不是**「不适用」：调用点只有在接缝命中（门面已给出确定
        // 零发送等待结论）时才会走到这里 ⇒ 此时队列缺失是**接线缺陷**，必须以**零发送停驻**收场
        // （返回 NotParked 会让调用点落进提交路径，把零发送结论改写成真实发送——不可逆 fail-open）。
        if (_localWaitQueue is null)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**拒绝登记**（local_wait）：登记接缝命中但未注入等待队列"
                    + "（接线缺陷）——维持零发送停驻，**不回落提交路径**（门面零发送结论不得被改写成发送）。",
            };
        }
        if (decision.Kind != LocalWaitDecisionKind.Wait
            || !decision.NoSendConfirmed
            || !decision.Context.HasTrustedRankingFacts
            || decision.Context.Tier is null
            || decision.Context.Priority is null)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：缺少可绑定的来源、排序或零发送证明，不创建队列项。",
            };
        }

        // [批次 20／C4①] 登记载荷合同（第一步）：前置引用来源未提供 ⇒ **拒绝登记**。
        // 登记结构性永不参选的等待项＝把死锁写进队列（§24.115 IW-05）；零发送停驻结论不被改写成提交，
        // 也不回落到既有提交路径（门面已给出「零发送」结论，提交即越过准入）。
        // [批次 20／Wave1 R3-F3 合同点登记（接线批必答）]
        // ①异常收敛语义：本方法（含 provider.Invoke 与 Upsert）在 RecordIntent **之前**执行，
        //   任何异常上抛都发生在「确定零发送」停驻路径上，零发送性质不被破坏；接线批必须保证
        //   该异常在驱动循环的收敛终态**不得为 Unknown**（无 job 可查 ⇒ 无法对账收敛，批次 14
        //   注释明文禁止该形态）——应收敛为可重试停驻或显式失败态，并在接线批会诊验证。
        // ①'异常面补充（[Wave1 R26 建议-1]）：BuildAdmissionIdentity → 权威 EncodeInt 对
        //   Occurrence/LoopIteration/Attempt 越界（>99,999,999）抛 ArgumentOutOfRangeException
        //   （结构性循环无轮次封顶，LoopIteration 理论无界、实际 1e8 轮不可达）——同样适用本条
        //   收敛义务（不得收敛为 Unknown）。
        // ②跨代际重登记（**已裁决落地**，[Wave1 R3-F3② → Wave2 R35 重要-2 落字]）：ItemId（4 段裸拼，
        //   无 attempt）× AdmissionIdentity（9 元组，含 attempt）⇒ 同一出现以新 attempt 再停驻、或载荷
        //   漂移（ticket 轮换）时，Upsert 载荷比较不通过 ⇒ **响亮冲突＝合同信号**（owner 裁决方向：不设
        //   「显式新通道」）⇒ 本方法 catch 折为 **Park=true「登记未完成」零发送停驻**（R25 建议采纳，
        //   冲突原文随 Reason 留痕）。收敛义务：收敛终态不得为 Unknown（见①）——Upsert 分支已在本层
        //   闭合，provider 分支仍归 BO-1。
        //   **attempt 维度本批不可达**（SubmitAndAwaitAsync const attempt = 1），但**载荷漂移在
        //   attempt=1 下可达**（R11 F-B）：停驻→取消→队列项置 Cancelled→重驱再停驻时，若前置引用含
        //   ticket 且 ticket 已轮换（"…@ticket-7"→"…@ticket-8"），重登记即撞上「已取消同身份项、
        //   载荷不同 ⇒ 响亮冲突」——收敛义务同 R3-F3①（异常不得使运行收敛为 Unknown）。
        // [批次 21／BO-1] provider.Invoke 异常收敛（R3-F3① 同形）：折为 Park=true「登记未完成」零发送停驻，
        // 绝不穿透驱动循环按「在飞事实」收敛 Unknown/Interrupted（登记路径在 RecordIntent 之前，无 job 可查
        // ⇒ Unknown 无法对账，批次 14 注释明载禁止该形态）；重驱时按可重试停驻重新登记。
        string? reference;
        try
        {
            reference = _localWaitPrerequisiteReferenceProvider?.Invoke(run, occurrence);
        }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：前置引用来源异常（" + ex.GetType().Name
                    + "，「" + ex.Message + "」）——维持零发送停驻，不回落提交路径。",
            };
        }
        var referenceAvailable = !string.IsNullOrWhiteSpace(reference);
        if (!referenceAvailable)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**拒绝登记**（local_wait）：C4① 登记载荷合同——"
                    + "前置引用来源未提供（PrerequisiteReference 缺失的等待项结构性永不参选，"
                    + "登记即把死锁写进队列）；维持零发送停驻，不回落提交路径。",
            };
        }

        // [批次 20／Wave1 R7 重要-2；R9-F1 更正；R10 重要-1 补 canonical] 登记载荷合同（第三步）：
        // 拿不到**权威且规范**的 scope ⇒ **拒绝登记**。
        // scope 权威来源按运行来源类别分流：移交来源运行＝运行台账 AdmissionSourceScope；面板来源运行＝
        // 租约侧 FlowRegistration 反查（宿主职责，经 _localWaitAdmissionScopeProvider 注入——面板来源
        // 运行**并非没有**权威 scope，R9-F1 更正「字段缺省＝无权威 scope」的错误等式）。
        // 解析次序（R23 重要-1）：台账字段规范非空 ⇒ 恒取；provider 仅补缺；两者皆缺 ⇒ 拒绝登记。
        // **采纳判据必须与提交面完全同源**（R10 重要-1）：提交面 ResolveAdmissionParent 对两条来源路径
        // 均强制 TaskCenterHost.IsCanonicalAdmissionScope（`bgi:local:{非空完整 epoch}`）——登记点若只判
        // 「非空白」，接受坏 scope（"garbage"／截断纪元）会把准入身份持久化成提交面永不产生的空间 ⇒
        // 项结构性永不可重入（C4① 禁止形态，从「缺 scope」换成「坏 scope」）。故复用**同一谓词**，
        // 不复制规则；不通过 ⇒ 并入既有拒绝登记分支（零发送停驻、不回落提交）。
        // [Wave1 R23 重要-1] 解析次序＝**台账字段优先，provider 仅补缺**：移交来源运行的权威 scope
        // ＝运行台账 AdmissionSourceScope（受理时捕获、只比较不重写）；provider 只在台账字段缺省
        // （面板来源运行）时取用。若允许 provider 覆盖台账字段，纪元轮换后 provider 返回当前纪元
        // 而台账字段是受理时固定纪元 ⇒ 登记身份与提交面（ResolveAdmissionParent 回落支恒取台账字段）
        // 逐字符不等 ⇒ 等待项结构性永不可参选（双源分歧死锁，IW-05 变体）。台账优先 ⇒ 移交来源双源
        // 恒等（provider 无从分歧）、面板来源行为不变（台账字段本为空）。
        var admissionScope = run.AdmissionSourceScope;
        if (string.IsNullOrWhiteSpace(admissionScope))
            // [批次 21／BO-1] 同上面：scope provider 异常 ⇒ 登记未完成零发送停驻，绝不 Unknown 收敛。
            try
            {
                admissionScope = _localWaitAdmissionScopeProvider?.Invoke(run);
            }
            catch (Exception ex)
            {
                return new LocalWaitRegistrationOutcome
                {
                    Park = true,
                    Reason = "本地持久等待**登记未完成**（local_wait）：授权 scope 反查来源异常（" + ex.GetType().Name
                        + "，「" + ex.Message + "」）——维持零发送停驻，不回落提交路径。",
                };
            }
        var scopeAvailable = TaskCenterHost.IsCanonicalAdmissionScope(admissionScope);
        if (!scopeAvailable)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**拒绝登记**（local_wait）：C4① 登记载荷合同——拿不到运行的"
                    + "**规范形状**固定授权 Scope（来源缺供或非 `bgi:local:{完整 epoch}`；非规范 scope 的"
                    + "身份与 successor 提交面不同身份空间，结构性永不可重入）。维持零发送停驻，不回落提交路径。",
            };
        }
        var context = decision.Context;
        if (!string.Equals(context.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowRevision, run.WorkflowRevision, StringComparison.Ordinal)
            || context.RecordRevision != run.RecordRevision
            || !string.Equals(context.NodeId, occurrence.NodeId, StringComparison.Ordinal)
            || context.SequenceIndex != occurrence.SequenceIndex
            || context.Occurrence != occurrence.Occurrence
            || context.LoopIteration != occurrence.LoopIteration
            || context.Attempt != attempt
            || !string.Equals(context.CursorNodeId, run.Cursor?.NodeId, StringComparison.Ordinal)
            || context.CursorOccurrence != (run.Cursor?.Occurrence ?? 0)
            || context.CursorLoopIteration != (run.Cursor?.LoopIteration ?? 0))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：判定身份与当前运行/游标不一致，不创建队列项。",
            };
        }

        // [Wave1 R27 重要-1] 整数段显式 **InvariantCulture**：与 Translate ③' 重建侧（InvariantCulture）
        // 同一口径——隐式 int.ToString() 取 CurrentCulture，非拉丁数字文化（fa-IR 等）下写侧落盘本土数字、
        // 校验侧重建 ASCII ⇒ 同源校验必败 ⇒ 合法登记项被判「不同源」结构性永不参选。两侧显式统一后
        // 争议彻底消除（R22 曾撤回该怀疑、R27 复提——与其依赖运行时文化数据行为，不如一行确定性消除）。
        var stableIdentity = run.RunId + "|" + occurrence.NodeId + "|"
            + occurrence.Occurrence.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
            + occurrence.LoopIteration.ToString(System.Globalization.CultureInfo.InvariantCulture);
        // [批次 20／C3] 身份翻译在登记时点完成：准入面 9 元组经**共享权威工厂**求得并随项落盘。
        // 该工厂与 successor 提交路径（TaskCenterHost.Admission.cs）**共用**（namespace/triggerOccurrenceId
        // 口径只此一处；锚定夹具 SuccessorCandidateComposition_AnchoredToSharedFactory 机械保证两侧同改）。
        // scope 取上文解析的权威值（台账字段优先，provider 仅补缺；两者皆缺已拒绝登记——R23 重要-1）。
        // [批次 21／BO-1] 身份构造异常（含①'：Occurrence/LoopIteration/Attempt 越界 >99,999,999 抛
        // ArgumentOutOfRangeException）⇒ 同形收敛：Park=true 登记未完成零发送停驻，绝不 Unknown。
        (string, string) admissionIdentityPair;
        try
        {
            admissionIdentityPair = LocalWaitIdentityTranslation.BuildAdmissionIdentity(
                TaskCenterHost.BuildSuccessorIdentityCandidate(admissionScope, run.WorkflowId,
                    run.RunId, occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration, attempt));
        }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：登记身份构造异常（" + ex.GetType().Name
                    + "，「" + ex.Message + "」）——维持零发送停驻，不回落提交路径。",
            };
        }
        var admissionIdentity = admissionIdentityPair.Item1;
        var candidateId = admissionIdentityPair.Item2;
        if (context.Scope is { } decisionScope
            && !string.Equals(decisionScope, admissionScope, StringComparison.Ordinal))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：来源 scope 与当前权威 scope 不一致，不创建队列项。",
            };
        }
        if (context.AdmissionIdentity is { } expectedIdentity
            && !string.Equals(expectedIdentity, admissionIdentity, StringComparison.Ordinal)
            || context.CandidateId is { } expectedCandidate
            && !string.Equals(expectedCandidate, candidateId, StringComparison.Ordinal))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：登记候选身份与判定快照不一致，不创建队列项。",
            };
        }
        if (context.SourceKind is null || string.IsNullOrWhiteSpace(context.SourceIdentity))
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地等待**保持**（local_wait）：缺少来源类别/身份绑定，不创建队列项。",
            };
        }
        var sourceKind = context.SourceKind
            ?? LocalWaitSourceKind.PanelFlowRegistration;
        DateTimeOffset enqueuedAtUtc;
        try { enqueuedAtUtc = _opt.Clock().ToUniversalTime(); }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：登记时钟异常（" + ex.GetType().Name
                    + "，「" + ex.Message + "」）——维持零发送停驻，不创建队列项。",
            };
        }
        var binding = new LocalWaitBinding
        {
            ItemId = LocalWaitQueuePolicy.DeriveItemId(stableIdentity),
            StableIdentity = stableIdentity,
            CandidateId = candidateId,
            AdmissionIdentity = admissionIdentity,
            Namespace = run.WorkflowId,
            WorkflowId = run.WorkflowId,
            SourceKind = sourceKind,
            SourceIdentity = context.SourceIdentity ?? run.RunId!,
            RunId = run.RunId!,
            Scope = admissionScope!,
            WorkflowRevision = run.WorkflowRevision,
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            RecordRevision = run.RecordRevision,
            CursorNodeId = run.Cursor?.NodeId,
            CursorOccurrence = run.Cursor?.Occurrence ?? 0,
            CursorLoopIteration = run.Cursor?.LoopIteration ?? 0,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
            Tier = context.Tier.Value,
            Priority = context.Priority.Value,
            IsHoeingHighest = false,
            PrerequisiteReference = reference,
            EnqueuedAtUtc = enqueuedAtUtc,
        };
        return new LocalWaitRegistrationOutcome
        {
            Park = true,
            Reason = "本地等待绑定已形成（" + LocalWaitReasonCode + "）：运行停驻先落盘，之后发布等待队列项；未获准入前零发送。",
            Decision = decision with { Binding = binding },
        };
    }

    /// <summary>
    /// **[批次 14／D1] 等待结论判定接缝。** 门面结论尚未接线（本批明确不接生产入口）⇒ 恒 false。
    /// 该函数的存在使「等待短路」是**显式判定**而非隐式兜底：后续批次只改这里，不靠在提交路径上加 `_ =&gt;`。
    /// </summary>
    /// <summary>
    /// **[批次 20／Wave3／C11] 等待判定（实例级注入）**：<see cref="WorkflowRunnerOptions.ShouldRegisterLocalWait"/>
    /// 非 null ⇒ 由其判定（测试/接线批注入门面结论判定）；null ⇒ 恒 false（批次 14 既有语义，未接线）。
    /// **实例级**（非进程级静态）——避免跨测试类/跨运行的并行污染（R43 重要-6）。
    /// 判定接缝由调用点显式应用；本方法为登记机制本体（TryRegisterLocalWait）的入口闸。
    /// </summary>
    private LocalWaitDecisionRecord? DecideLocalWait(WaitDecisionRequest request, WorkflowNodeOccurrence occurrence)
    {
        if (_waitDecisionSource is not null) return _waitDecisionSource.Decide(request);
        if (_opt.ShouldRegisterLocalWait?.Invoke(occurrence) != true) return null;
        // 旧测试接缝只用于异常/状态夹具；生产装配始终注入类型化宿主来源。
        return new LocalWaitDecisionRecord
        {
            Kind = LocalWaitDecisionKind.Wait,
            Context = ContextFromRequest(request) with
            {
                SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
                SourceIdentity = request.RunId,
                Tier = ArbitrationTier.Plan,
                Priority = 0,
                HasTrustedRankingFacts = true,
            },
            Reason = "测试接缝命中本地等待",
            NoSendConfirmed = true,
        };
    }

    internal LocalWaitRegistrationOutcome TryRegisterLocalWait(WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, int attempt)
    {
        var request = CreateWaitDecisionRequest(run, occurrence, attempt);
        var context = ContextFromRequest(request) with
        {
            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
            SourceIdentity = request.RunId,
            Scope = run.AdmissionSourceScope,
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedRankingFacts = true,
        };
        var prepared = TryRegisterLocalWait(run, occurrence, attempt, new LocalWaitDecisionRecord
        {
            Kind = LocalWaitDecisionKind.Wait,
            Context = context,
            Reason = "直接登记测试接缝",
            NoSendConfirmed = true,
        });
        if (prepared.Decision?.Binding is not { } binding) return prepared;
        try
        {
            _localWaitQueue!.Upsert(binding.ToQueueItem());
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待已登记（" + LocalWaitReasonCode + "）：测试直调路径。",
                Decision = prepared.Decision,
            };
        }
        catch (Exception ex)
        {
            return new LocalWaitRegistrationOutcome
            {
                Park = true,
                Reason = "本地持久等待**登记未完成**（local_wait）：等待队列拒绝写入或存储异常（"
                    + ex.GetType().Name + "）——「" + ex.Message + "」；维持零发送停驻，不回落提交路径。",
                Decision = prepared.Decision with { Kind = LocalWaitDecisionKind.Hold, Binding = null, Reason = ex.Message },
            };
        }
    }

    private static WaitDecisionRequest CreateWaitDecisionRequest(WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, int attempt)
        => new()
        {
            RunId = run.RunId ?? "",
            WorkflowId = run.WorkflowId ?? "",
            WorkflowRevision = run.WorkflowRevision ?? "",
            RecordRevision = run.RecordRevision,
            CursorNodeId = run.Cursor?.NodeId,
            CursorOccurrence = run.Cursor?.Occurrence ?? 0,
            CursorLoopIteration = run.Cursor?.LoopIteration ?? 0,
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
        };

    private static LocalWaitDecisionContext ContextFromRequest(WaitDecisionRequest request)
        => new()
        {
            RunId = request.RunId,
            WorkflowId = request.WorkflowId,
            WorkflowRevision = request.WorkflowRevision,
            RecordRevision = request.RecordRevision,
            CursorNodeId = request.CursorNodeId,
            CursorOccurrence = request.CursorOccurrence,
            CursorLoopIteration = request.CursorLoopIteration,
            NodeId = request.NodeId,
            SequenceIndex = request.SequenceIndex,
            Occurrence = request.Occurrence,
            LoopIteration = request.LoopIteration,
            Attempt = request.Attempt,
        };

    private void PublishPersistedLocalWait(WorkflowRunRecord run)
    {
        if (run.LocalWaitDecision is not { Kind: LocalWaitDecisionKind.Wait, Binding: { } binding }) return;
        try
        {
            _localWaitQueue?.Upsert(binding.ToQueueItem());
        }
        catch (Exception ex)
        {
            run.Note = AppendNote(run.Note,
                "等待队列发布失败（运行台账已保存绑定，可在显式恢复时重建；" + ex.GetType().Name + "）。");
            _runs.Update(run);
        }
    }

    private void CancelPersistedLocalWait(LocalWaitBinding? binding, string reason)
    {
        if (binding is null) return;
        if (_localWaitQueue is null)
            throw new InvalidOperationException("存在等待绑定但 LocalWaitQueue 未注入；拒绝继续恢复。");
        _localWaitQueue.Cancel(binding.ItemId, reason, _opt.Clock());
    }

    private static bool SameWaitBindingPayload(LocalWaitBinding left, LocalWaitBinding right)
        => string.Equals(left.ItemId, right.ItemId, StringComparison.Ordinal)
           && string.Equals(left.StableIdentity, right.StableIdentity, StringComparison.Ordinal)
           && string.Equals(left.CandidateId, right.CandidateId, StringComparison.Ordinal)
           && string.Equals(left.AdmissionIdentity, right.AdmissionIdentity, StringComparison.Ordinal)
           && string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal)
           && string.Equals(left.WorkflowId, right.WorkflowId, StringComparison.Ordinal)
           && left.SourceKind == right.SourceKind
           && string.Equals(left.SourceIdentity, right.SourceIdentity, StringComparison.Ordinal)
           && string.Equals(left.RunId, right.RunId, StringComparison.Ordinal)
           && string.Equals(left.Scope, right.Scope, StringComparison.Ordinal)
           && string.Equals(left.WorkflowRevision, right.WorkflowRevision, StringComparison.Ordinal)
           && string.Equals(left.NodeId, right.NodeId, StringComparison.Ordinal)
           && left.SequenceIndex == right.SequenceIndex
           && left.CursorNodeId == right.CursorNodeId
           && left.CursorOccurrence == right.CursorOccurrence
           && left.CursorLoopIteration == right.CursorLoopIteration
           && left.Occurrence == right.Occurrence
           && left.LoopIteration == right.LoopIteration
           && left.Attempt == right.Attempt
           && left.Tier == right.Tier
           && left.Priority == right.Priority
           && left.IsHoeingHighest == right.IsHoeingHighest
           && string.Equals(left.PrerequisiteReference, right.PrerequisiteReference, StringComparison.Ordinal);

    /// <summary>
    /// 结果提交（B3-①：观察终态 + 节点结果 + 游标推进单次原子落盘，无中间态窗口）。
    /// I2/四轮重要 9：ObservedTerminal 只存原始线协议词（rawTerminal），业务词与「未确认」（null）绝不写入；
    /// 四轮阻断 5：unknown/cancelUnconfirmed 游标不推进（调用方先置 Unknown 状态再进本方法，保持单次原子落盘）；
    /// I3/四轮重要 10：持久化原因统一脱敏。
    /// </summary>
    private void CommitOutcome(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence occurrence,
        string result, string? reason, string? rawTerminal = null)
    {
        if (rawTerminal is not null && run.CurrentSubmission is { ObservedTerminal: null } sub)
            sub.ObservedTerminal = rawTerminal; // 提交终态与结果/游标同写（仅原始线协议词）
        // G8／§12.3：结果若来自**边界观察到的权威终态**，则把「产生它的提交键＋attempt」随结果落盘——
        // 供租约侧按完整发送关联独立结清该节点 Operation（不得凭「同一出现曾有过某结果」结清另一笔责任）。
        // 仅当当前提交确实属于本次出现身份时才记录（前置/闸门/恢复路径的 CurrentSubmission 可能属别的出现）。
        var producing = run.CurrentSubmission is { } cur
                        && string.Equals(cur.NodeId, occurrence.NodeId, StringComparison.Ordinal)
                        && cur.Occurrence == occurrence.Occurrence
                        && cur.LoopIteration == occurrence.LoopIteration
            ? cur
            : null;
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Result = result,
            RawTerminal = rawTerminal,
            Reason = Sanitize(reason),
            SubmissionKey = rawTerminal is not null ? producing?.Key : null,
            Attempt = rawTerminal is not null ? producing?.Attempt : null,
            // 完整发送身份（租约侧签发）：与 SubmissionKey/Attempt 同规则随结果落盘，
            // 使「节点操作独立终局」可证明该结果属于**这一笔发送**（提交键在同 attempt 多 sendSeq 间可复用）。
            AcceptedSendIdentity = rawTerminal is not null ? producing?.AcceptedSendIdentity : null,
        });
        // **[批次 14／D1]** `waitLocally` 与 `unknown`/`cancelUnconfirmed` 同族：**游标不推进**
        // （等待项就绪后须从同一节点重走完整准入）。若落进 `else` 分支推进游标，等于把「已登记等待」
        // 当成「已完成」——既跳过该节点（作业静默丢步），也让「重新走完整准入」的要求不成立。
        if (result is "unknown" or "cancelUnconfirmed" or LocalWaitResultWord)
            ApplyRelocation(run, occurrence); // 结果不确定／确定等待：游标留在当前出现，绝不推进
        else
            ApplyRelocation(run, plan.Next(occurrence));
        _runs.Update(run);
    }

    private WorkflowRunRecord Pause(WorkflowRunRecord run)
    {
        run.State = WorkflowRunState.Paused;
        run.Note = AppendNote(run.Note, "已暂停（≠停止；修订按节点边界生效；显式 ResumeAsync 恢复）。");
        _runs.Update(run);
        return run;
    }

    private void Log(WorkflowRunRecord run, string message) => Log(run.RunId, message);

    private void Log(string runId, string message) => _opt.Log?.Invoke($"[{runId}] {message}");

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;
}
