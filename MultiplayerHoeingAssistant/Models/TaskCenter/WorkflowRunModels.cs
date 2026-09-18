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

    /// <summary>固定幂等键（重复投递同一请求复用；新一轮/新尝试生成新身份；崩溃恢复绝不换键重跑）。</summary>
    [JsonPropertyName("idempotencyKey")]
    public string IdempotencyKey { get; set; } = "";

    [JsonPropertyName("submitIntent")]
    public SubmitIntentState SubmitIntent { get; set; } = SubmitIntentState.None;

    /// <summary>BGI 受理的作业 ID（受理后填写；返回 jobId 后由查询确认终态）。</summary>
    [JsonPropertyName("jobId")]
    public string? JobId { get; set; }

    /// <summary>观察到的作业终态（succeeded/failed/cancelled/rejected 等；未填写 ≠ 成功）。</summary>
    [JsonPropertyName("observedTerminal")]
    public string? ObservedTerminal { get; set; }

    /// <summary>待执行收尾动作（D10：失败/取消/未知/手动停止不得触发；结果不确定不得补发）。</summary>
    [JsonPropertyName("pendingCompletionAction")]
    public string? PendingCompletionAction { get; set; }

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
