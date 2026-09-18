using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>流程运行状态机（R4 分解 D12）。Unknown=结果不确定（需对账，不得当成功）。</summary>
public enum WorkflowRunState
{
    /// <summary>已计划，尚未开始推进。</summary>
    Planned,
    /// <summary>等待触发/条件（不持有叶子执行锁、不提交等待作业）。</summary>
    Waiting,
    /// <summary>正在执行。</summary>
    Running,
    /// <summary>已暂停（≠ 停止；暂停期间修订按节点边界生效）。</summary>
    Paused,
    /// <summary>流程成功边界到达（终态）。</summary>
    Succeeded,
    /// <summary>失败（终态；失败不被后续成功覆盖——聚合规则 D6）。</summary>
    Failed,
    /// <summary>被取消（终态）。</summary>
    Cancelled,
    /// <summary>中断（如助手重启；可恢复候选，非终态结论）。</summary>
    Interrupted,
    /// <summary>结果不确定（提交在飞/终态未证实；禁止自动重跑）。</summary>
    Unknown,
    /// <summary>收尾动作执行中（B5：成功边界先落盘收尾意图再执行；恢复扫描见此状态标 Unknown）。尾部追加，不改既有枚举数值。</summary>
    Completing,
}

/// <summary>提交意图状态（D11：提交意图先行持久化，回执丢失按账本+job 查询对账）。</summary>
public enum SubmitIntentState
{
    /// <summary>无提交。</summary>
    None,
    /// <summary>提交意图已记录（BGI 提交前）。</summary>
    IntentRecorded,
    /// <summary>已向 BGI 提交，受理回执未确认。</summary>
    Submitted,
    /// <summary>BGI 已受理（拿到 jobId）。</summary>
    Accepted,
    /// <summary>BGI 拒绝（响亮拒绝，带原因）。</summary>
    Rejected,
}

/// <summary>节点游标：节点出现身份 = nodeId + 出现位置 + 循环轮次 + 尝试（§3.4 身份合同）。</summary>
public sealed class WorkflowNodeCursor
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    /// <summary>同名/同节点在流程中的出现序号（同名不等于同任务）。</summary>
    [JsonPropertyName("occurrence")]
    public int Occurrence { get; set; }

    /// <summary>循环轮次（结构性循环每轮递增）。</summary>
    [JsonPropertyName("loopIteration")]
    public int LoopIteration { get; set; }

    /// <summary>当前尝试（有界重试递增；新尝试新身份）。</summary>
    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    /// <summary>祖先链（父作业/工作流关系，§3.2 恢复与联机行）。</summary>
    [JsonPropertyName("ancestors")]
    public List<string> Ancestors { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>本次执行绑定的资源（不复制配置内容）。</summary>
public sealed class BoundResourceRef
{
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("config")]
    public string? Config { get; set; }

    [JsonPropertyName("taskId")]
    public string? TaskId { get; set; }

    [JsonPropertyName("configRevision")]
    public string? ConfigRevision { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>执行目标身份（实例 + BGI 进程纪元；换 epoch 后旧作业终态不可信）。</summary>
/// <summary>节点结果（D6 聚合规则的事实来源：失败/拒绝不被后续成功覆盖）。</summary>
public sealed class WorkflowNodeOutcome
{
    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    [JsonPropertyName("sequenceIndex")]
    public int SequenceIndex { get; set; }

    [JsonPropertyName("occurrence")]
    public int Occurrence { get; set; }

    [JsonPropertyName("loopIteration")]
    public int LoopIteration { get; set; }

    /// <summary>succeeded / failed / rejected / skippedFilter / skippedUser / cancelled。</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
public sealed class ExecutionTargetRef
{
    [JsonPropertyName("instanceId")]
    public string? InstanceId { get; set; }

    [JsonPropertyName("bgiEpoch")]
    public string? BgiEpoch { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>等待状态（等待不占 BGI 槽位；触发出现身份防重复触发）。</summary>
public sealed class WaitStateRecord
{
    /// <summary>等待种类（trigger.time / loop.scheduled 等）。</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    /// <summary>下次触发时刻（本机时间；时区/日界语义见流程触发器 missPolicy）。</summary>
    [JsonPropertyName("nextTriggerAt")]
    public DateTimeOffset? NextTriggerAt { get; set; }

    /// <summary>触发出现身份（同一触发时刻只触发一次）。</summary>
    [JsonPropertyName("triggerOccurrenceId")]
    public string? TriggerOccurrenceId { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>
/// 槲寄生 · 任务中心——RunStore 运行记录（R4.2，R4 分解 D11）。
/// 与流程定义分离；记录推进事实和恢复选择，不做事件历史重放。
/// 持久化字段覆盖 B3 会诊清单：身份/轮次/尝试/祖先、绑定资源+修订、目标实例+epoch、
/// 固定幂等键、提交意图、受理 jobId、观察终态、待执行收尾、等待条件与触发身份。
/// </summary>
public sealed class WorkflowRunRecord
{
    /// <summary>运行身份（"run-" + 12 位十六进制，创建即固定）。</summary>
    [JsonPropertyName("runId")]
    public string RunId { get; set; } = "";

    /// <summary>稳定流程身份（不靠文件名）。</summary>
    [JsonPropertyName("workflowId")]
    public string WorkflowId { get; set; } = "";

    /// <summary>启动时的流程定义修订号（锚点 2：起步快照拒绝 config_changed，不锁死流程定义）。</summary>
    [JsonPropertyName("workflowRevision")]
    public string WorkflowRevision { get; set; } = "";

    [JsonPropertyName("state")]
    public WorkflowRunState State { get; set; } = WorkflowRunState.Planned;

    /// <summary>当前节点游标（运行水位，C06 入口的落地处）。</summary>
    [JsonPropertyName("cursor")]
    public WorkflowNodeCursor? Cursor { get; set; }

    [JsonPropertyName("boundResource")]
    public BoundResourceRef? BoundResource { get; set; }

    [JsonPropertyName("target")]
    public ExecutionTargetRef? Target { get; set; }

    /// <summary>运行级固定身份键（创建即固定，绝不更换；单次提交键由它+节点出现身份确定性派生，B2）。</summary>
    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = "";

    /// <summary>当前提交（B2：一提交一身份——幂等键/意图/jobId/观察终态同属一个提交身份，不跨节点复用残留）。</summary>
    [JsonPropertyName("currentSubmission")]
    public WorkflowSubmission? CurrentSubmission { get; set; }

    /// <summary>链尾已达（B3：游标 null 消歧——false+null=未开始/中断；true+null=全部节点已完成）。</summary>
    [JsonPropertyName("tailReached")]
    public bool TailReached { get; set; }

    /// <summary>顶层触发器已消费（B3：恢复后不重等已触发过的触发器；暂停在触发等待中保持 false 以便恢复重排）。</summary>
    [JsonPropertyName("triggerConsumed")]
    public bool TriggerConsumed { get; set; }

    /// <summary>已完成起点等待的循环轮次（B9：轮次等待统一在新一轮边界执行，恢复后不重等同一轮）。</summary>
    [JsonPropertyName("lastScheduledRoundWait")]
    public int LastScheduledRoundWait { get; set; }

    /// <summary>待执行收尾动作（D10：失败/取消/未知/手动停止不得触发；结果不确定不得补发）。</summary>
    [JsonPropertyName("pendingCompletionAction")]
    public string? PendingCompletionAction { get; set; }

    /// <summary>逐节点结果（追加；聚合判定只看此清单，不信终态单字段）。</summary>
    [JsonPropertyName("nodeOutcomes")]
    public List<WorkflowNodeOutcome> NodeOutcomes { get; set; } = [];
    [JsonPropertyName("wait")]
    public WaitStateRecord? Wait { get; set; }

    /// <summary>记录级单调修订（RunStore 内部乐观并发；每次成功写入 +1）。</summary>
    [JsonPropertyName("recordRevision")]
    public int RecordRevision { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("updatedAt")]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>人类可读备注（恢复原因/拒绝原因等；不含敏感账号字段——脱敏纪律）。</summary>
    [JsonPropertyName("note")]
    public string? Note { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>是否终态（Succeeded/Failed/Cancelled；Interrupted/Unknown 非终态结论）。</summary>
    [JsonIgnore]
    public bool IsTerminal => State is WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.Cancelled;
}

/// <summary>
/// 单次提交记录（B2 会诊：一提交一身份）。
/// 幂等键按 runId+nodeId+出现序号+循环轮次+attempt 确定性派生（RunStore.DeriveSubmissionKey）：
/// 崩溃恢复后同一出现身份重算同一键（重复投递复用），不同节点/轮次/尝试绝不复用同一键。
/// 意图/jobId/观察终态同属本对象，禁止跨提交残留（旧字段跨节点复用导致恢复误判已废除）。
/// </summary>
public sealed class WorkflowSubmission
{
    /// <summary>确定性派生幂等键（"idem-" + 24 位十六进制）。</summary>
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("nodeId")]
    public string NodeId { get; set; } = "";

    [JsonPropertyName("occurrence")]
    public int Occurrence { get; set; }

    [JsonPropertyName("loopIteration")]
    public int LoopIteration { get; set; }

    [JsonPropertyName("attempt")]
    public int Attempt { get; set; }

    [JsonPropertyName("intent")]
    public SubmitIntentState Intent { get; set; } = SubmitIntentState.None;

    /// <summary>BGI 受理的作业 ID（受理后填写；返回 jobId 后由查询确认终态）。</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    /// <summary>观察到的作业终态（succeeded/failed/cancelled/rejected/skippedUser 等；未填写 ≠ 成功）。</summary>
    [JsonPropertyName("observedTerminal")]
    public string? ObservedTerminal { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>提交在飞（意图已录/已提交/已受理且终态未观察；恢复扫描据此标 Unknown，禁止自动重跑）。</summary>
    [JsonIgnore]
    public bool InFlight => ObservedTerminal is null
        && Intent is SubmitIntentState.IntentRecorded or SubmitIntentState.Submitted or SubmitIntentState.Accepted;
}