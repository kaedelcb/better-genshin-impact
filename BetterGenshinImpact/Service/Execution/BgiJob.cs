using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>[A1.4] 作业种类。对应总计划 §6.2 Job.kind。</summary>
public enum JobKind
{
    Group,
    OneDragon,
    Solo,
    Script,
    KeyMouse,
    Pathing,

    /// <summary>R4.6 E1'：流程前置动作（账号/兑换等，作业化生命周期）。尾部追加不改既有数值。</summary>
    Prerequisite,
    /// <summary>R4.6 E1'：流程收尾动作（terminal.completionAction）。尾部追加不改既有数值。</summary>
    Terminal,
}

/// <summary>[A1.4] 作业来源（提交入口）。对应总计划 §6.2 Job.source。</summary>
public enum JobSource
{
    Ui,
    Hotkey,
    Cli,
    V2,
    Ext,
    Resume,
    OneDragonInternal,
    Trigger,
}

/// <summary>
/// [A1.4] 作业状态机（总计划 §6.3，对齐 UiPath Job States）：
/// Queued → Running → (Succeeded | Failed | Cancelled)；提交即拒 → Rejected；
/// Cancelling = 取消已请求、执行体尚未退出的显式过渡态。
/// </summary>
public enum JobState
{
    Queued,
    Running,
    Cancelling,
    Succeeded,
    Failed,
    Cancelled,
    Rejected,

    /// <summary>正常跳过（R4.7 结果通道：子项显式上报 SkippedNormal；终态，与成功/失败分别表达，D6）。尾部追加不改既有数值。</summary>
    Skipped,
}

/// <summary>[A1.4] 终态失败/结束原因受控词表（总计划 §4.6，死信可排查）。</summary>
public static class JobErrorCodes
{
    public const string TaskBusy = "task_busy";
    public const string QueueFull = "queue_full";
    public const string CancelledUser = "cancelled_user";
    public const string CancelledSuperseded = "cancelled_superseded";
    public const string CancelledShutdown = "cancelled_shutdown";
    public const string TaskStartFailed = "task_start_failed";
    public const string SkippedNormal = "skipped_normal"; // R4.7 结果通道：子项显式上报正常跳过
    public const string StaleEpoch = "stale_epoch";
    public const string NotFound = "not_found";

    /// <summary>[A6] 任务让位联机锄地批次（PreemptionGate 让位点，见 TaskRunResult.Preempted）。</summary>
    public const string Preempted = "preempted";

    /// <summary>[A6] 抢占未在有界时间内确认槽位释放（ADR-2026-09-16 有界退出契约）。</summary>
    public const string PreemptTimeout = "preempt_timeout";

    /// <summary>R4.6 E2-9：执行权取得后 UID 复验不符（受保护执行阶段拒绝，账号未被验证前不产生资源副作用）。</summary>
    public const string AccountMismatch = "account_mismatch";
    /// <summary>R4.6 D8：前置动作执行失败（真实结果，非取消非跳过）。</summary>
    public const string PrerequisiteFailed = "prerequisite_failed";
    /// <summary>R4.6 I4：前置副作用结果不明（如切号后未进主界面）——不自动重复副作用，按失败保守处置。</summary>
    public const string PrerequisiteOutcomeUnknown = "prerequisite_outcome_unknown";
    /// <summary>R4.6 B8：收尾动作前校验发现其他活动作业（迟到收尾不误伤新任务）。</summary>
    public const string TerminalConflict = "terminal_conflict";
}

/// <summary>一次状态转换记录（UiPath 式状态时间线，随终态留档）。</summary>
public sealed record JobStateTransition(JobState State, DateTime AtUtc, string? Reason);

/// <summary>JobRegistry.Submit 的返回：作业 + 是否幂等采用了既有作业。</summary>
public sealed record JobSubmitResult(BgiJob Job, bool Adopted);

/// <summary>
/// [A2] 作业提交描述符：各入口传给执行漏斗（TaskRunner/ScriptService）的元数据。
/// 可选参数全有默认值；为 null 的描述符表示"不登记"（旧行为）。
/// </summary>
public sealed record JobDescriptor(
    JobKind Kind,
    string Name,
    JobSource Source,
    int? Generation = null,
    string? IdempotencyKey = null,
    Guid? ParentJobId = null,
    /// <summary>[A2.4] 认领既有作业（协调器入队时已建 Queued，taskHandle==jobId 别名）；null=新建（旧行为）。</summary>
    Guid? JobId = null,
    string? TakeoverTicket = null,
    Action? OnAdmitted = null,
    /// <summary>R3 原生身份：一条龙恢复/起点游标为任务项稳定字符串 ID（GUID）；配置组内项目序号游标不走此字段（组游标见 SuspendedTaskContext.TaskIndex）。</summary>
    string? ResumeTaskId = null,
    Guid? WorkflowRunId = null,
    string? NodeId = null,
    int? Iteration = null,
    string? TaskId = null,
    string? ConfigRevision = null,
    /// <summary>R4.6 E2-9：执行权取得后、首个资源副作用前复验的期望 UID（null=不校验；仅严格合同携带）。</summary>
    string? ExpectedUid = null,
    /// <summary>R4.6 D10/E4'：本次调用收尾权限抑制（任务中心整龙调用固定 true；缺省 false 行为不变）。</summary>
    bool SuppressConfigCompletionAction = false,
    /// <summary>R4.6 B1：出现序号/尝试号（线协议 add-only 扩展，缺省 null）。</summary>
    int? Occurrence = null, int? Attempt = null,
    /// <summary>v2 启动在准备前读到的手动停止水位；建根时在同一锁内核对，null 为旧 UI 调用。</summary>
    long? ExpectedStopVersion = null)
{
    public JobExecutionIdentity? ExecutionIdentity => WorkflowRunId is { } run && NodeId is { } node && Iteration is { } iteration
        ? new(run, node, iteration, TaskId, ConfigRevision, Occurrence, Attempt) : null;
}

public sealed record JobExecutionIdentity(Guid WorkflowRunId, string NodeId, int Iteration,
    string? TaskId = null, string? ConfigRevision = null,
    // R4.6 B1：出现序号/尝试号 add-only 扩展（缺省 null，旧请求零变化）
    int? Occurrence = null, int? Attempt = null);

/// <summary>
/// [A1.4] 统一作业模型（总计划 §6.2）。
/// bgiEpoch 不逐作业携带：纪元是进程级属性，由 JobRegistry.Epoch 在查询/事件出口统一附加。
/// 可变字段仅允许 JobRegistry 在锁内推进（单一写入者纪律）。
/// </summary>
public sealed class BgiJob
{
    public Guid JobId { get; }
    public JobKind Kind { get; }
    public string Name { get; }
    public JobSource Source { get; }
    public int? Generation { get; }
    public string? IdempotencyKey { get; }
    public Guid? ParentJobId { get; }
    public Guid? WorkflowRunId { get; }
    public string? NodeId { get; }
    public int? Iteration { get; }
    public string? TaskId { get; }
    public string? ConfigRevision { get; }
    public int? Occurrence { get; } // R4.6 B1
    public int? Attempt { get; } // R4.6 B1

    public JobState State { get; internal set; } = JobState.Queued;
    public string? ErrorCode { get; internal set; }
    public string? ErrorMessage { get; internal set; }
    public bool WasCancelled { get; internal set; }

    public DateTime EnqueuedAtUtc { get; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; internal set; }
    public DateTime? FinishedAtUtc { get; internal set; }
    public DateTime? LastHeartbeatAtUtc { get; internal set; }

    private readonly List<JobStateTransition> _stateHistory = [];
    public IReadOnlyList<JobStateTransition> StateHistory => _stateHistory;

    public bool IsTerminal => State is JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Rejected or JobState.Skipped;

    public BgiJob(JobKind kind, string name, JobSource source, int? generation, string? idempotencyKey, Guid? parentJobId,
        Guid? jobId = null, JobExecutionIdentity? identity = null)
    {
        // [A2.4] jobId 外部指定：协调器 taskHandle 与注册表 jobId 同一 Guid 双名（别名），
        // 使 ext 事件里的 taskHandle 可直接在注册表查询，A4 双发期无需映射表。
        JobId = jobId ?? Guid.NewGuid();
        Kind = kind;
        Name = name;
        Source = source;
        Generation = generation;
        IdempotencyKey = idempotencyKey;
        ParentJobId = parentJobId;
        WorkflowRunId = identity?.WorkflowRunId;
        NodeId = identity?.NodeId;
        Iteration = identity?.Iteration;
        TaskId = identity?.TaskId;
        ConfigRevision = identity?.ConfigRevision;
        Occurrence = identity?.Occurrence;
        Attempt = identity?.Attempt;
        _stateHistory.Add(new JobStateTransition(JobState.Queued, EnqueuedAtUtc, null));
    }

    /// <summary>仅 JobRegistry 调用：推进状态并记录时间线。</summary>
    internal void TransitionTo(JobState state, string? reason)
    {
        State = state;
        var now = DateTime.UtcNow;
        if (state == JobState.Running) StartedAtUtc ??= now;
        if (IsTerminal) FinishedAtUtc ??= now;
        _stateHistory.Add(new JobStateTransition(state, now, reason));
    }
}
