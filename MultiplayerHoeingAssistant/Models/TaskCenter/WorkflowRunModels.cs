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
    /// <summary>
    /// **[批次 20／Wave3／C11=(a)] 本地等待停驻**（D-E4=(a)：等待停驻**不复用** Waiting/Running）：
    /// 门面已给出确定零发送等待结论、等待项已登记（或登记被拒＝接线缺陷停驻），运行**无活动驱动**、
    /// 游标停在停驻出现、等待项就绪后**可显式重驱**（ResumeAsync 接受本状态）。尾部追加，不改既有枚举数值。
    /// </summary>
    LocalWaitParking,
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
    /// <summary>门面给出确定零发送等待；本地停驻已原子落盘，可显式重评。</summary>
    LocalWaitDeferred,
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

    /// <summary>succeeded / failed / rejected / skippedFilter / skippedUser / cancelled / cancelUnconfirmed / unknown。</summary>
    [JsonPropertyName("result")]
    public string Result { get; set; } = "";

    /// <summary>原始线协议词（I2：与业务结果分开；仅边界观察到的原始终态词，前置/闸门/未确认路径为 null）。</summary>
    [JsonPropertyName("rawTerminal")]
    public string? RawTerminal { get; set; }

    /// <summary>
    /// **产生本结果的提交键**（R5.2 B2-γ G8：仅当 `RawTerminal` 非空且该提交属于本出现身份时填写）。
    /// 与租约 `OperationRecord.WireSubmitKey` 比对，使「节点操作独立终局」可证明**该结果属于本笔发送**——
    /// 不得凭「同一出现身份曾有某结果」结清另一笔 Accepted 责任。加法字段（缺失=null）。
    /// </summary>
    [JsonPropertyName("submissionKey")]
    public string? SubmissionKey { get; set; }

    /// <summary>产生本结果的发送 attempt（与 `SubmissionKey` 同规则填写；加法字段）。</summary>
    [JsonPropertyName("attempt")]
    public int? Attempt { get; set; }

    /// <summary>
    /// **产生本结果的完整发送身份**（`sub:&lt;requestIdentity&gt;:&lt;sendSeq&gt;`，取自提交记录的
    /// `AcceptedSendIdentity`；与 `SubmissionKey`/`Attempt` 同规则填写，加法字段）。
    /// 结清节点 Operation 时按本字段与 `OperationRecord.SubmissionIdentity` **精确匹配**——
    /// 提交键在同 attempt 的多个 sendSeq 间可复用，只有完整发送身份才能区分「两笔发送」。
    /// </summary>
    [JsonPropertyName("acceptedSendIdentity")]
    public string? AcceptedSendIdentity { get; set; }

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
public sealed record WorkflowTriggerTiming(string Kind, DateTimeOffset ScheduledAt, DateTimeOffset? WindowEndsAt);

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
public sealed record WorkflowStopAuthorityRecord(
    [property: JsonPropertyName("epoch")] string Epoch,
    [property: JsonPropertyName("version")] long Version,
    [property: JsonPropertyName("intentId")] string IntentId,
    [property: JsonPropertyName("intentTimestamp")] long IntentTimestamp,
    [property: JsonPropertyName("monotonicFrequency")] long MonotonicFrequency);

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

    /// <summary>仅非执行拒绝诊断；存储创建后永久不可变，不能恢复为执行运行。</summary>
    [JsonPropertyName("nonExecutingDiagnostic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool NonExecutingDiagnostic { get; set; }

    [JsonPropertyName("stopRequested")]
    public bool StopRequested { get; set; }

    [JsonPropertyName("stopAuthority")]
    public WorkflowStopAuthorityRecord? StopAuthority { get; set; }

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

    /// <summary>线协议运行身份（R4.6 B1：BGI 合同要求 Guid——建 run 时生成 Guid "N" 串并持久化；与 RunId 并存，绝不更换）。</summary>
    [JsonPropertyName("wireRunId")]
    public string WireRunId { get; set; } = "";

    /// <summary>移交身份绑定清单（R4.9 §3 + ASTRA 二轮 B1：追加式多绑定——resume/幂等挂载追加新绑定，旧绑定永不替换，
    /// 旧意图的去重依据永存；台账查询键=IntentKey；面板/手工启动为空表）。</summary>
    [JsonPropertyName("handoffs")]
    public List<HandoffIdentity> Handoffs { get; set; } = [];

    /// <summary>
    /// **准入来源固定 Scope（G4a／AMD-1-5 第 2 类来源「启动移交」）**：移交**受理时捕获**的目标实例与完整 epoch
    /// （`bgi:{实例}:{epoch}`），随受理**同一次落盘**写入，随后**只比较、不重写**；后继节点提交据此**继承**来源
    /// （缺省/空 ⇒ 无固定来源 ⇒ 后继准入**不签发、不发送**，不得读当前 epoch 补造）。
    /// **面板启动**不需本字段（其来源权威＝租约中的流程登记操作）；**暂停续行/Interrupted 恢复**继承其原始来源。
    /// </summary>
    [JsonPropertyName("admissionSourceScope")]
    public string? AdmissionSourceScope { get; set; }

    [JsonPropertyName("admissionParentSource")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdmissionParentSource? AdmissionParentSource { get; set; }

    /// <summary>原真实分派前冻结的运行准入映射；用于停止/恢复识别丢失账本，不能授予新发送能力。</summary>
    [JsonPropertyName("admissionMappings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RunAdmissionMapping>? AdmissionMappings { get; set; }


    /// <summary>当前提交（B2：一提交一身份——幂等键/意图/jobId/观察终态同属一个提交身份，不跨节点复用残留）。</summary>
    [JsonPropertyName("currentSubmission")]
    public WorkflowSubmission? CurrentSubmission { get; set; }

    /// <summary>本地等待/保持的类型化决定与不可变队列绑定；Hold 可无 binding，且无 binding 不可重建队列。</summary>
    [JsonPropertyName("localWaitDecision")]
    public LocalWaitDecisionRecord? LocalWaitDecision { get; set; }

    /// <summary>链尾已达（B3：游标 null 消歧——false+null=未开始/中断；true+null=全部节点已完成）。</summary>
    [JsonPropertyName("tailReached")]
    public bool TailReached { get; set; }

    /// <summary>顶层触发器已消费（B3：恢复后不重等已触发过的触发器；暂停在触发等待中保持 false 以便恢复重排）。</summary>
    [JsonPropertyName("triggerConsumed")]
    public bool TriggerConsumed { get; set; }

    /// <summary>本次启动已选定的原触发时刻与窗口；暂停/重启不改绑到次日。</summary>
    [JsonPropertyName("triggerTiming")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public WorkflowTriggerTiming? TriggerTiming { get; set; }

    /// <summary>已完成起点等待的循环轮次（B9：轮次等待统一在新一轮边界执行，恢复后不重等同一轮）。</summary>
    [JsonPropertyName("lastScheduledRoundWait")]
    public int LastScheduledRoundWait { get; set; }

    /// <summary>前置动作记录（R4.6 E2-8'：意图-受理-终态逐策略实例，发送前落盘；事实只信本清单，不信流程文件标记）。</summary>
    [JsonPropertyName("prerequisiteActions")]
    public List<PrerequisiteActionRecord> PrerequisiteActions { get; set; } = [];

    /// <summary>待执行收尾动作（R4.6 E3'/B8 结构化：动作身份+指纹+状态机 pending/submitted/executed/unknown；
    /// D10：失败/取消/未知/手动停止不得触发；已提交未确认事实持久保留，禁止补发）。
    /// 旧 pendingCompletionAction 字符串键退役（落入 ExtensionData 忽略——R4 开发期运行记录无存量包袱）。</summary>
    [JsonPropertyName("pendingCompletion")]
    public PendingCompletionRecord? PendingCompletion { get; set; }

    [JsonPropertyName("submissionHistory")]
    public List<WorkflowSubmission> SubmissionHistory { get; set; } = new();

    [JsonPropertyName("nodeReleaseSeals")]
    public List<MultiplayerHoeingAssistant.Services.TerminalReleaseSeal> NodeReleaseSeals { get; set; } = new();

    [JsonPropertyName("terminalRelease")]
    public MultiplayerHoeingAssistant.Services.TerminalReleaseSeal? TerminalRelease { get; set; }

    [JsonPropertyName("completionHistory")]
    public List<PendingCompletionRecord> CompletionHistory { get; set; } = new();

    /// <summary>逐节点结果（追加；聚合判定只看此清单，不信终态单字段）。</summary>
    [JsonPropertyName("nodeOutcomes")]
    public List<WorkflowNodeOutcome> NodeOutcomes { get; set; } = [];

    /// <summary>**追加式恢复关联（G7-residual·本批；只增不删/不改）**：把缺完整发送身份的历史提交/节点结果
    /// 绑定到其真实原发送轮次（`submissionIdentity+sendSeq`），供终态判据在「不改历史原件、不放宽守卫」前提下
    /// 定位旧记录。每条绑定原历史键、原发送身份、jobId、纪元、证据来源与观测时间；冲突/歧义不得写入。
    /// </summary>
    [JsonPropertyName("recoveryAssociations")]
    public List<RecoveryAssociationRecord> RecoveryAssociations { get; set; } = [];
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
    /// <summary>发送意图同次固定的原节点仲裁模式；缺字段为未知历史，不能用当前宿主开关补造。</summary>
    [JsonPropertyName("nodeAdmissionRequired")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? NodeAdmissionRequired { get; set; }

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

    [JsonPropertyName("serverRejectionEvidence")]
    public ServerRejectionEvidence? ServerRejectionEvidence { get; set; }

    [JsonPropertyName("executionExitConfirmed")]
    public bool ExecutionExitConfirmed { get; set; }

    [JsonPropertyName("executionExitDisposition")]
    public string? ExecutionExitDisposition { get; set; }

    [JsonPropertyName("effectState")]
    public string? EffectState { get; set; }

    /// <summary>执行纪元（"pid:ticks"，发送时冻结；跨纪元事实不沿用，R4.8 §4.4）。</summary>
    [JsonPropertyName("epoch")]
    public string? Epoch { get; set; }

    [JsonPropertyName("wireRunId")]
    public string? WireRunId { get; set; }

    /// <summary>请求有效期（+10min 冻结不刷新，I4 同构；传输重投同键同载荷）。</summary>
    [JsonPropertyName("expiresAtUtc")]
    public string? ExpiresAtUtc { get; set; }

    /// <summary>完整载荷指纹（SHA256 截断 24 hex，冻结后计算）。</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; set; }

    // Additive original-wire evidence; null is omitted to preserve legacy sealed bytes.
    [JsonPropertyName("originalRequestEvidence")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public FrozenOriginalRequestEvidence? OriginalRequestEvidence { get; set; }

    [JsonPropertyName("sendPermit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PreparedSendPermit? SendPermit { get; set; }

    [JsonPropertyName("previousSendRounds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DischargedNodeSendRound>? PreviousSendRounds { get; set; }

    /// <summary>发送已尝试（发送前持久化：此后缺 jobId ≠ 未发送，取消确认不得当未发送处置）。</summary>
    [JsonPropertyName("sendAttempted")]
    public bool SendAttempted { get; set; }

    /// <summary>
    /// **本轮受理回执所属的完整发送身份**（R5.2 B2-γ：`sub:&lt;requestIdentity&gt;:&lt;sendSeq&gt;`）。
    /// 由宿主节点 Sender 在「先接管、后关闭」的接管落盘时写入；门面 `TakeoverPersist` 据此证明
    /// 「本笔 Submission 拿到的回执确实是这一轮的发送结果」，而不是同一 run 下另一笔同业务身份/同纪元的回执。
    /// 加法字段（缺失=null）：旧记录与不经节点 Sender 的路径不受影响。
    /// </summary>
    [JsonPropertyName("acceptedSendIdentity")]
    public string? AcceptedSendIdentity { get; set; }

    [JsonPropertyName("localNoSendProof")]
    public MultiplayerHoeingAssistant.Services.LocalNoSendProof? LocalNoSendProof { get; set; }

    [JsonPropertyName("recordedAt")]
    public DateTimeOffset RecordedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>提交在飞（意图已录/已提交/已受理且终态未观察；恢复扫描据此标 Unknown，禁止自动重跑）。</summary>
    [JsonIgnore]
    public bool InFlight => ObservedTerminal is null
        && Intent is SubmitIntentState.IntentRecorded or SubmitIntentState.Submitted or SubmitIntentState.Accepted;
}
/// <summary>前置动作状态机（R4.6 E2-8'）：Intent（意图已落盘）→ Submitted（已受理在飞）→ Succeeded/Failed/Cancelled/Unknown。</summary>
public enum PrerequisiteActionState
{
    Intent,
    Submitted,
    Succeeded,
    Failed,
    Cancelled,
    Unknown,
}

/// <summary>
/// 前置动作记录（R4.6 E2-8'/B2）：键 = runId+nodeId+occurrence+loopIteration+attempt+策略索引+操作类型+账号标识+执行纪元。
/// 发送前落盘意图；恢复时对账（先查远端权威终态）；传输重投同键同载荷（expiresAtUtc 不刷新，I4），重投计数持久化。
/// </summary>
public sealed class PrerequisiteActionRecord
{
    [JsonPropertyName("wireRunId")] public string? WireRunId { get; set; }
    [JsonPropertyName("takeoverTicket")] public string? TakeoverTicket { get; set; }
    [JsonPropertyName("observedTerminal")] public string? ObservedTerminal { get; set; }
    [JsonPropertyName("executionExitConfirmed")] public bool ExecutionExitConfirmed { get; set; }
    [JsonPropertyName("executionExitDisposition")] public string? ExecutionExitDisposition { get; set; }
    [JsonPropertyName("effectState")] public string? EffectState { get; set; }
    [JsonPropertyName("serverRejectionEvidence")] public ServerRejectionEvidence? ServerRejectionEvidence { get; set; }

    [JsonPropertyName("nodeId")] public string NodeId { get; set; } = "";
    [JsonPropertyName("occurrence")] public int Occurrence { get; set; }
    [JsonPropertyName("loopIteration")] public int LoopIteration { get; set; }
    [JsonPropertyName("attempt")] public int Attempt { get; set; }
    [JsonPropertyName("strategyIndex")] public int StrategyIndex { get; set; }
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    /// <summary>账号标识（SHA256 截断哈希，非可逆掩码——稳定可比对且碰撞隔离，原值不落记录；I3/四轮阻断 3）。</summary>
    [JsonPropertyName("accountKey")] public string? AccountKey { get; set; }

    /// <summary>执行纪元（"processId:startTicksUtc"；跨纪元事实不沿用，入 Unknown 对账——B2 窗口过期语义）。</summary>
    [JsonPropertyName("epoch")] public string? Epoch { get; set; }

    [JsonPropertyName("idempotencyKey")] public string IdempotencyKey { get; set; } = "";
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";

    /// <summary>I4：重投沿用原载荷（含原 expiresAtUtc），指纹不变。</summary>
    [JsonPropertyName("expiresAtUtc")] public string ExpiresAtUtc { get; set; } = "";

    /// <summary>发送已尝试（发送窗口崩溃/取消时区分「确定未发送」与「可能已发送」；后者禁止当 Cancelled——四轮阻断 1）。</summary>
    [JsonPropertyName("sendAttempted")] public bool SendAttempted { get; set; }

    [JsonPropertyName("jobId")] public string? JobId { get; set; }
    [JsonPropertyName("state")] public PrerequisiteActionState State { get; set; } = PrerequisiteActionState.Intent;
    [JsonPropertyName("reason")] public string? Reason { get; set; }
    [JsonPropertyName("redeliveryCount")] public int RedeliveryCount { get; set; }
    [JsonPropertyName("recordedAt")] public DateTimeOffset RecordedAt { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }

    /// <summary>完整动作身份比对（四轮阻断 3：含操作类型与账号标识，防同索引换策略/换账号复用旧事实）。</summary>
    public bool Matches(string nodeId, int occurrence, int loopIteration, int attempt, int strategyIndex,
        string kind, string? accountKey)
        => NodeId == nodeId && Occurrence == occurrence && LoopIteration == loopIteration
           && Attempt == attempt && StrategyIndex == strategyIndex
           && Kind == kind && AccountKey == accountKey;
}

/// <summary>
/// 待执行收尾动作（R4.6 E3'/B8）：pending（意图已落盘未提交）→ submitted（已提交未确认）→ executed；
/// submitted 后断线/取消 → unknown（事实持久保留，禁止盲目补发）。每流程至多一个 completionAction（Planner 预检）。
/// </summary>
public sealed class PendingCompletionRecord
{
    [JsonPropertyName("epoch")] public string? Epoch { get; set; }
    [JsonPropertyName("sendAttempted")] public bool SendAttempted { get; set; }
    [JsonPropertyName("wireRunId")] public string? WireRunId { get; set; }
    [JsonPropertyName("takeoverTicket")] public string? TakeoverTicket { get; set; }
    [JsonPropertyName("observedTerminal")] public string? ObservedTerminal { get; set; }
    [JsonPropertyName("executionExitConfirmed")] public bool ExecutionExitConfirmed { get; set; }
    [JsonPropertyName("executionExitDisposition")] public string? ExecutionExitDisposition { get; set; }
    [JsonPropertyName("effectState")] public string? EffectState { get; set; }
    [JsonPropertyName("serverRejectionEvidence")] public ServerRejectionEvidence? ServerRejectionEvidence { get; set; }

    /// <summary>动作身份（B1：nodeId="$flow"，iteration=动作序号）。</summary>
    [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("action")] public string? Action { get; set; }
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; set; } = "";
    [JsonPropertyName("idempotencyKey")] public string IdempotencyKey { get; set; } = "";

    /// <summary>I4：重投沿用原载荷（含原 expiresAtUtc），指纹不变。</summary>
    [JsonPropertyName("expiresAtUtc")] public string ExpiresAtUtc { get; set; } = "";

    [JsonPropertyName("jobId")] public string? JobId { get; set; }

    /// <summary>E1' 扩展字段（add-only）：收尾身份 occurrence=0 / attempt=1。</summary>
    [JsonPropertyName("occurrence")] public int Occurrence { get; set; }
    [JsonPropertyName("attempt")] public int Attempt { get; set; } = 1;

    /// <summary>pending（未发送）/ dispatching（发送窗口，可能已发送）/ submitted（已受理）/ executed / unknown。</summary>
    [JsonPropertyName("state")] public string State { get; set; } = "pending";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>已进入线路但在建job前被可信执行端拒绝；与本地零发送事实分开保存。</summary>
public sealed record ServerRejectionEvidence(string Epoch, string Key, string Fingerprint, string Operation);
public sealed record PreparedSendPermit(int Version, string Nonce, bool Consumed,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? OriginalSendIdentity = null);

public sealed record FrozenOriginalRequestEvidence(int Version, string Fingerprint, string Operation,
    string? TaskId, string? ConfigRevision);

/// <summary>追加式恢复关联（G7-residual）：缺身份历史提交 ↔ 真实原发送轮次的合法绑定载体。</summary>
public sealed class RecoveryAssociationRecord
{
    [JsonPropertyName("historyIndex")] public int HistoryIndex { get; set; } = -1;
    [JsonPropertyName("historyHash")] public string HistoryHash { get; set; } = "";
    [JsonPropertyName("outcomeIndex")] public int OutcomeIndex { get; set; } = -1;
    [JsonPropertyName("outcomeHash")] public string? OutcomeHash { get; set; }
    /// <summary>被恢复关联的历史提交幂等键（原历史原件不改，仅按 Key 定位）。</summary>
    [JsonPropertyName("submissionKey")] public string SubmissionKey { get; set; } = "";
    [JsonPropertyName("submissionIdentity")] public string SubmissionIdentity { get; set; } = "";
    [JsonPropertyName("sendSeq")] public int SendSeq { get; set; }
    [JsonPropertyName("jobId")] public string? JobId { get; set; }
    [JsonPropertyName("epoch")] public string? Epoch { get; set; }
    [JsonPropertyName("evidenceSource")] public string EvidenceSource { get; set; } = "";
    [JsonPropertyName("observedAtUtc")] public DateTimeOffset ObservedAtUtc { get; set; }
    [JsonPropertyName("observedExecution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MultiplayerHoeingAssistant.Services.BgiJobInfo? ObservedExecution { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record DischargedNodeSendRound(PreparedSendPermit Permit,
    MultiplayerHoeingAssistant.Services.LocalNoSendProof Proof, FrozenOriginalRequestEvidence RequestEvidence);

public sealed record RunAdmissionMapping(int Version, string RequestIdentity, string SubmissionIdentity, int SendSeq, OperationType OperationType);
