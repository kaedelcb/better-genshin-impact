using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 槲寄生 · R5.1 仲裁面模型（设计冻结稿 v5 §2/§6，2026-09-20 签署可冻结）。
/// 候选三件套：ArbitrationCandidate（身份+排序键+执行意图引用）/ CandidateEligibility（资格快照）/
/// PendingHandoffIntent（交接上下文持久化段）。七入口接线归 R5.2，本层只承载合同类型。
/// </summary>
public enum ArbitrationTier
{
    /// <summary>计划层（flexible/chain 来源类别映射同一层级值，§2.2 终审冻结——不得漂移回四级）。</summary>
    Plan = 0,
    /// <summary>固定排程层。</summary>
    Fixed = 1,
    /// <summary>系统层（A6/人齐/人工显式动作；来源本身不证明 system 授权，仅由动作类型+既有调用方身份判定）。</summary>
    System = 2,
}

/// <summary>仲裁判定显式结果类型（§3 S1 冻结六值）。</summary>
public enum ArbitrationOutcome
{
    /// <summary>无参选资格候选（载荷：候选引用可空、压制来源/原因必填）。</summary>
    NoEligibleCandidate,
    /// <summary>F11 独立停止闸门先行（任何候选集+F11→全拒；不作候选、不参与排序）。</summary>
    F11Blocked,
    /// <summary>票据压制无关候选生效（授权抢占方保留资格）。</summary>
    TicketSuppressed,
    /// <summary>权威执行事实未知→待对账（禁止换键重跑、禁止回 Idle 重提交）。</summary>
    NeedReconcile,
    /// <summary>有胜者但执行占用→需安全交接确认（§4.1/§4.1a 转换表）。</summary>
    NeedPreemptConfirm,
    /// <summary>允许请求执行（仍须经租约锁内意图发布，§6.2 原子准入边界）。</summary>
    AllowRequestExecution,
}

/// <summary>租约五态（§6.2 语义分离：TTL 过期仅撤资格，不代表执行锁释放/任务取消/票据失效）。</summary>
public enum ArbitrationLeaseStatus
{
    /// <summary>无正式文件。</summary>
    Absent,
    /// <summary>有效（身份匹配且未过期）。</summary>
    Valid,
    /// <summary>TTL 失效（代次相等≠租约有效；过期不得续期复活，只能重新获取）。</summary>
    Expired,
    /// <summary>损坏（保留文件留痕，不得降级按 Absent 处理）。</summary>
    Corrupt,
    /// <summary>更高 version（响亮拒绝执行，不降级解析）。</summary>
    Unsupported,
}

/// <summary>交接阶段（§4/§4.1a 转换表持久化段）。</summary>
public enum HandoffPhase
{
    None = 0,
    /// <summary>已向在跑方发取消或 A6 挂起请求。</summary>
    PreemptRequested,
    /// <summary>等权威事实（受理≠已退出，有界超时）。</summary>
    Confirming,
    /// <summary>抢占方终态仅进入本阶段——不直接解除票据压制。</summary>
    SettlePending,
    /// <summary>已选择原票据恢复但恢复未确认——继续压制，不得另建替代作业。</summary>
    RestorePending,
    /// <summary>事实未知/关联不符→保守待对账（禁止推导空闲）。</summary>
    ReconcilePending,
}

/// <summary>
/// 仲裁候选（§2.1）：规范化身份元组各段为原始值，编码规则由 ArbitrationIdentityEncoding 唯一负责。
/// stableIdentity 元组固定顺序：scope | namespace | workflowId | 触发出现身份 | runId | nodeOccurrenceIdentity(nodeId,occurrence) | 轮次 | attempt。
/// </summary>
public sealed class ArbitrationCandidate
{
    /// <summary>BGI 目标实例+epoch 命名空间。</summary>
    public string Scope { get; set; } = "";
    /// <summary>入口类别命名空间（trigger / manual / v2 / resume / system）；不同领域身份不跨命名空间比较唯一性。</summary>
    public string Namespace { get; set; } = "";
    public string WorkflowId { get; set; } = "";
    /// <summary>触发出现身份（手工/v2 无=空，编码为 ~）。</summary>
    public string TriggerOccurrenceId { get; set; } = "";
    public string RunId { get; set; } = "";
    public string NodeId { get; set; } = "";
    /// <summary>节点出现序号（身份编码整数：非负 0..99,999,999，越界响亮拒绝；与 priority 的 int32 负值规则互不相干）。</summary>
    public int Occurrence { get; set; }
    public int LoopIteration { get; set; }
    public int Attempt { get; set; }

    /// <summary>层级（三值；flexible/chain 为来源类别映射同层 Plan）。</summary>
    public ArbitrationTier Tier { get; set; } = ArbitrationTier.Plan;
    /// <summary>int32 大者优先，缺省 0（允许负值；不属于身份编码 8 位规则）。</summary>
    public int Priority { get; set; }
    /// <summary>计划时刻；手工/v2=null 排最后，同到点组内以 stableIdentity 兜底。</summary>
    public DateTimeOffset? ScheduledAt { get; set; }

    /// <summary>执行意图引用（resourceRef）。</summary>
    public string ResourceRef { get; set; } = "";
    /// <summary>执行意图（start/preempt；stop/resume 按三分类分流不进排序）。</summary>
    public string Intent { get; set; } = "start";
    /// <summary>执行载荷指纹（去重/冲突判定要件：同 ID+同载荷+同排序键才幂等去重）。</summary>
    public string PayloadFingerprint { get; set; } = "";
    /// <summary>输入携带的关联动作号（null=确定性派生；纯函数内部不得生成随机身份）。</summary>
    public string? ActionId { get; set; }
}

/// <summary>资格快照（§2 三件套之二；资格判定的全部输入，比较器不自行探测）。</summary>
public sealed class CandidateEligibility
{
    /// <summary>已到点/窗口内（过期窗口候选按 missPolicy 处置，不参与排序）。</summary>
    public bool IsDue { get; set; } = true;
    /// <summary>链内前置就绪（顺序链内未就绪节点不参选）。</summary>
    public bool PrerequisiteReady { get; set; } = true;
    /// <summary>灵活型全局空闲约束（灵活型约束在资格判定，不在排序层级）。</summary>
    public bool FlexibleWindowOpen { get; set; } = true;
}

/// <summary>候选+资格快照配对（Decide 输入项）。</summary>
public sealed class CandidateEntry
{
    public ArbitrationCandidate Candidate { get; set; } = new();
    public CandidateEligibility Eligibility { get; set; } = new();
    /// <summary>运行绑定判别量（进程内裁决输入，不序列化：同候选号不同 runBinding/cursor 绑定=身份冲突整组拒绝，不去重共享——B7 复核）。</summary>
    public string BindingDiscriminator { get; set; } = "";
}

/// <summary>票据压制事实（§5：权威校验在 BGI 侧 PreemptionGate/SuspendedTaskContext；本结构只是资格快照输入，候选自带绑定不作授权证据）。</summary>
public sealed class TicketBinding
{
    /// <summary>被挂起运行身份。</summary>
    public string SuspendedRunIdentity { get; set; } = "";
    /// <summary>授权抢占方 stableIdentity（仅此候选保留资格）。</summary>
    public string AuthorizedPreemptorIdentity { get; set; } = "";
    /// <summary>票据 epoch 要素。</summary>
    public string Epoch { get; set; } = "";
}

/// <summary>仲裁全局事实（Decide 显式输入；I1：判定不自行探测环境）。</summary>
public sealed class ArbitrationFacts
{
    /// <summary>F11 独立停止闸门（先于一切排序与租约判断；租约释放/重新获取不解除）。</summary>
    public bool F11Active { get; set; }
    /// <summary>存续 A6 票据（null=无）。</summary>
    public TicketBinding? ActiveTicket { get; set; }
    /// <summary>BGI 权威执行占用（有在跑任务）。</summary>
    public bool ExecutionOccupied { get; set; }
    /// <summary>权威事实未知（发送窗口崩溃/超时/对账未完成）→ NeedReconcile，禁止启动。</summary>
    public bool ExecutionFactsUnknown { get; set; }
    /// <summary>请求方持有有效租约（§6.2 过期即禁启：代次相等≠有效）。</summary>
    public bool RequesterHoldsValidLease { get; set; } = true;
    /// <summary>关联事实引用（NeedReconcile 载荷必填：待对账的提交身份/动作号等；无=空串）。</summary>
    public string FactsReference { get; set; } = "";
}

/// <summary>结构化拒绝（§3 载荷：候选引用+原因；日志与夹具按载荷断言，不只断言枚举值）。</summary>
public sealed class CandidateRejection
{
    public string CandidateId { get; set; } = "";
    public string StableIdentity { get; set; } = "";
    /// <summary>结构化原因码：f11_active / ticket_suppressed / not_due / prerequisite_not_ready / window_closed / lease_not_valid / identity_conflict。</summary>
    public string Reason { get; set; } = "";
}

/// <summary>仲裁判定结果（§3 显式结果类型+载荷冻结）。</summary>
public sealed class ArbitrationDecision
{
    public ArbitrationOutcome Outcome { get; set; }
    /// <summary>胜者候选引用（NoEligibleCandidate/F11Blocked 可空）。</summary>
    public ArbitrationCandidate? Winner { get; set; }
    public string? WinnerCandidateId { get; set; }
    /// <summary>被拒候选引用+结构化原因（按 candidateId Ordinal 排序，确定性）。</summary>
    public List<CandidateRejection> Rejections { get; set; } = [];
    /// <summary>压制来源：f11 / ticket / eligibility / lease / facts_unknown / none（无候选时显式 none，不留空）。</summary>
    public string SuppressionSource { get; set; } = "";
    /// <summary>压制来源明细（ticket=被挂起运行身份；facts_unknown=关联事实引用；其余空串）。</summary>
    public string SuppressionDetail { get; set; } = "";
    /// <summary>关联动作号（输入携带或确定性派生，同输入同号）。</summary>
    public string ActionId { get; set; } = "";
    /// <summary>人类可读原因（脱敏纪律）。</summary>
    public string Reason { get; set; } = "";
}

/// <summary>未决意图消解的关联权威证据（§6.2：消解必须基于关联的权威证据——身份/epoch/提交身份关联匹配，查询未命中或超时不能单独消解）。</summary>
public sealed class IntentResolveEvidence
{
    /// <summary>动作关联号（须匹配 Pending.ActionId）。</summary>
    public string ActionId { get; set; } = "";
    /// <summary>证据对应的提交身份（须匹配 Pending.SubmissionIdentity）。</summary>
    public string SubmissionIdentity { get; set; } = "";
    /// <summary>证据来源 epoch（须匹配 Pending.TargetEpoch——旧 epoch 迟到事实不构成证据）。</summary>
    public string Epoch { get; set; } = "";
    /// <summary>权威事实描述（终态词/恢复确认等；非空——空=无证据）。</summary>
    public string ObservedFact { get; set; } = "";
}

/// <summary>
/// 逻辑所有者租约文件（§6.4 单文件三段：lease/handoff/diag；单例=单 BGI scope，v1 单目标）。
/// 文件名 arbitration-lease.json（RunStore 目录旁，RunStore 只枚举 *.run.json 不互读）；
/// 锁对象=固定 arbitration-lease.lock（永不原子替换/删除）。
/// </summary>
public sealed class LogicalOwnerLeaseFile
{
    /// <summary>自版本化；更高 version=Unsupported 响亮拒绝。</summary>
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    /// <summary>数据单调修订（文件级）：每次写入+1（含所有者/非所有者 diag/释放全部写入）——释放清空 Lease 段后仍单调延续，禁止回退。</summary>
    [JsonPropertyName("revision")] public long Revision { get; set; }
    /// <summary>最近所有权代次（释放清空 Lease 段后仍保留——重新获取=LastGeneration+1，世代单调不回退）。</summary>
    [JsonPropertyName("lastGeneration")] public int LastGeneration { get; set; }
    [JsonPropertyName("lease")] public LeaseSegment? Lease { get; set; }
    /// <summary>handoff 生命周期独立于租约有效期：更替/释放不清空未决意图。</summary>
    [JsonPropertyName("handoff")] public LeaseHandoffSegment? Handoff { get; set; }
    /// <summary>非所有者对账留痕/切换闸门（写本段不刷新 heartbeatSeq）。</summary>
    [JsonPropertyName("diag")] public LeaseDiagSegment? Diag { get; set; }
}

/// <summary>所有权段（四代次分离：ownerEpoch/BGI epoch/generation/revision；heartbeatSeq 仅所有者写入刷新）。</summary>
public sealed class LeaseSegment
{
    /// <summary>租约实例 ID（Guid "N" 小写）。</summary>
    [JsonPropertyName("leaseId")] public string LeaseId { get; set; } = "";
    /// <summary>助手进程纪元（pid:ticks）。</summary>
    [JsonPropertyName("ownerEpoch")] public string OwnerEpoch { get; set; } = "";
    /// <summary>所有权代次：首次获取=1，重新获取+1，续期/释放不变。</summary>
    [JsonPropertyName("generation")] public int Generation { get; set; }
    /// <summary>心跳序号：仅所有者锁内写入（获取/续期/handoff 更新）刷新；TTL 观察对象。</summary>
    [JsonPropertyName("heartbeatSeq")] public long HeartbeatSeq { get; set; }
    [JsonPropertyName("acquiredAtUtc")] public DateTimeOffset AcquiredAtUtc { get; set; }
    /// <summary>UTC 仅记录与诊断（跨进程判断以 heartbeatSeq 观察+锁内复核为准）。</summary>
    [JsonPropertyName("lastHeartbeatUtc")] public DateTimeOffset LastHeartbeatUtc { get; set; }
    /// <summary>心跳 TTL（设计值 15s，可调）。</summary>
    [JsonPropertyName("ttlSeconds")] public int TtlSeconds { get; set; } = 15;
}

/// <summary>
/// 交接上下文段（§6.4：获取新租约只替换所有权部分、保留未决 handoff；释放不删除未决事实）。
/// R5.2 冻结稿 §4.1 三字段结构：Pending（交接责任，六值阶段机不动）+ Submission（当前未决发送，至多一笔）+
/// Operations（逻辑操作权威记录=恢复权威，不依赖可缺失的镜像）。关闭事务=同一次锁内原子发布内
/// 「更新 Operations 记录 + 移除 Submission」，无「已关闭、状态未记录」崩窗。
/// </summary>
public sealed class LeaseHandoffSegment
{
    [JsonPropertyName("pending")] public PendingHandoffIntent? Pending { get; set; }
    /// <summary>当前未决发送（至多一笔）；受理/确定拒绝经 §4.2c 统一关闭接口移除，绝不顺带消解 Pending。</summary>
    [JsonPropertyName("submission")] public SubmissionRecord? Submission { get; set; }
    /// <summary>逻辑操作权威记录（本段新增，恢复权威）：runBinding/cursorRef/submissionIdentity/targetEpoch 不可改写。</summary>
    [JsonPropertyName("operations")] public List<OperationRecord> Operations { get; set; } = [];
}

/// <summary>未决交接/提交意图（§6.2 原子准入边界：意图先在跨进程锁内持久化再发送；消解必须基于关联的权威证据）。</summary>
public sealed class PendingHandoffIntent
{
    /// <summary>动作关联号（输入携带或确定性派生）。</summary>
    [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
    /// <summary>被切方身份。</summary>
    [JsonPropertyName("suspendedRunIdentity")] public string SuspendedRunIdentity { get; set; } = "";
    /// <summary>授权抢占方 stableIdentity。</summary>
    [JsonPropertyName("authorizedPreemptor")] public string AuthorizedPreemptor { get; set; } = "";
    /// <summary>目标 epoch。</summary>
    [JsonPropertyName("targetEpoch")] public string TargetEpoch { get; set; } = "";
    [JsonPropertyName("phase")] public HandoffPhase Phase { get; set; }
    /// <summary>进入 ReconcilePending 前的责任阶段（五轮 P1-①：恢复责任持久化——退出待对账时按本字段校验目标阶段，禁止经对账降级恢复责任；非待对账阶段为 null）。</summary>
    [JsonPropertyName("reconcileFromPhase")] public HandoffPhase? ReconcileFromPhase { get; set; }
    /// <summary>恢复分支（原票据恢复/协议终结等）。</summary>
    [JsonPropertyName("restoreBranch")] public string RestoreBranch { get; set; } = "";
    /// <summary>提交身份（在途请求按原提交身份对账）。</summary>
    [JsonPropertyName("submissionIdentity")] public string SubmissionIdentity { get; set; } = "";
    [JsonPropertyName("recordedAtUtc")] public DateTimeOffset RecordedAtUtc { get; set; }
}

/// <summary>诊断段（diag 写入不刷新 heartbeatSeq；切换闸门持久化——控制面崩溃重启恢复闸门状态，不复用旧通过结果）。</summary>
public sealed class LeaseDiagSegment
{
    /// <summary>§7 切换闸门：激活期间租约锁内意图发布一律拒绝（封锁持续至切换提交接管完成或安全回退）。</summary>
    [JsonPropertyName("switchGateActive")] public bool SwitchGateActive { get; set; }
    /// <summary>崩窗残件已隔离但未完成对账（§6.4 复核：隔离≠不确定性消除——持续准入约束，直到关联证据对账后显式清除）。</summary>
    [JsonPropertyName("residueReconcilePending")] public bool ResidueReconcilePending { get; set; }
    [JsonPropertyName("switchGateReason")] public string? SwitchGateReason { get; set; }
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];
}

// ============================================================
// R5.2 提交责任模型（接线设计稿 v8 §4.1 冻结——加法不动序列化框架，锚点 6/D3）
// ============================================================

/// <summary>提交子状态（§4.1：回答「本次提交是否仍可能被受理」；Reconciling=发送结果未知，等价保守语义，不换键重跑）。</summary>
public enum SubmissionState
{
    /// <summary>占位成功、当次锁外发送责任存续（是责任状态，不是发送许可——重启/对账回退/重复调用不得凭此重新取得发送权）。</summary>
    Submitting,
    /// <summary>发送结果未知→保守待对账；Reconciling→Submitting 仅恢复责任阶段，不得重新触发发送。</summary>
    Reconciling,
}

/// <summary>Operations 状态三区（§4.1a：同一 Operations[] 内的记录状态，非物理分区）。</summary>
public enum OperationZone
{
    /// <summary>非终局：永不清理；占用主槽位（primarySlotsUsed=Active+TerminalPendingTransfer ≤ 32）。</summary>
    Active,
    /// <summary>已终局待迁墓碑：占用主槽位——终局只改状态不申请新槽位，迁移成功才释放原槽位。</summary>
    TerminalPendingTransfer,
    /// <summary>终局墓碑：环形上限 256 且最短保留 24h；迁入即释放主槽位。</summary>
    Tombstone,
}

/// <summary>请求状态（§3.3 分类表——操作身份与处理状态分离；状态读取/迁移在权威串行边界内完成）。</summary>
public enum OperationRequestState
{
    /// <summary>已登记待裁决（入队快照前）。</summary>
    Queued,
    /// <summary>当轮裁决中。</summary>
    InRound,
    /// <summary>已占位（Submission 落盘、发送责任存续）。</summary>
    Granted,
    /// <summary>当次锁外发送在飞。</summary>
    Sending,
    /// <summary>发送结果未知/待对账（返回对账状态，不重发）。</summary>
    Reconciling,
    /// <summary>已受理（接管台账已持久化，返回既有结果）。</summary>
    Accepted,
    /// <summary>终局完成（关联执行权威终态+台账一致，返回既有结果）。</summary>
    TerminalCompleted,
    /// <summary>可重试拒绝（操作级白名单内，已关闭 Submission；预算/窗口内可由唯一重试者重新 Admit）。</summary>
    RetryableRejected,
    /// <summary>终局拒绝（不得静默返回成功）。</summary>
    TerminalRejected,
    /// <summary>未获选（当轮裁决完成即终局，含胜者引用与压制来源，不悬置不自动进入下一轮）。</summary>
    NotSelected,
}

/// <summary>操作受理结论（§4.1 五轮重要 1：仅两值，不设第三关闭依据；对账结论写入 evidenceSource）。</summary>
public enum OperationOutcome
{
    Accepted,
    Rejected,
}

/// <summary>操作最近一轮结果（§4.1：answeredSendSeq 标注结果对应的发送轮次——下一轮占位后不得把上一轮拒绝误当本轮结果）。</summary>
public sealed class OperationResult
{
    [JsonPropertyName("outcome")] public OperationOutcome Outcome { get; set; }
    /// <summary>结构化原因码（副作用前拒绝七码/协议映射登记词；无=空串）。</summary>
    [JsonPropertyName("reasonCode")] public string ReasonCode { get; set; } = "";
    /// <summary>是否操作级重试白名单内（stale_epoch/request_expired 等身份/时限类一律 false——不透明重试）。</summary>
    [JsonPropertyName("retryable")] public bool Retryable { get; set; }
    /// <summary>已消耗重试预算（无损拒绝重试不增加业务 attempt）。</summary>
    [JsonPropertyName("retryBudgetUsed")] public int RetryBudgetUsed { get; set; }
    /// <summary>证据来源（原始回执词/对账结论+产生端——保留原始证据来源、不伪造远端回执词）。</summary>
    [JsonPropertyName("evidenceSource")] public string EvidenceSource { get; set; } = "";
    /// <summary>结果对应的发送轮次（关联校验含 sendSeq，迟到结果不得跨轮完成）。</summary>
    [JsonPropertyName("answeredSendSeq")] public int AnsweredSendSeq { get; set; }
    /// <summary>未获选终局的胜者候选引用（持久化——续用/恢复返回不丢压制依据）。</summary>
    [JsonPropertyName("winnerRef")] public string? WinnerRef { get; set; }
    /// <summary>压制来源（票据/冲突组等——持久化，续用/恢复返回不丢压制依据）。</summary>
    [JsonPropertyName("suppressionSource")] public string? SuppressionSource { get; set; }
}

/// <summary>当前未决发送（§4.1：至多一笔；只承载当前未决发送，操作全史由 Operations 承载）。</summary>
public sealed class SubmissionRecord
{
    /// <summary>完整发送关联身份（授权签发=租约锁内一次原子发布，不可由外部请求自报）。</summary>
    [JsonPropertyName("submissionIdentity")] public string SubmissionIdentity { get; set; } = "";
    /// <summary>发送序号（同请求的各次发送区分轮次；上一轮迟到结果不得完成下一轮占位）。</summary>
    [JsonPropertyName("sendSeq")] public int SendSeq { get; set; }
    [JsonPropertyName("actionId")] public string ActionId { get; set; } = "";
    /// <summary>目标 bgiEpoch（首次构造固定，只比较不重写）。</summary>
    [JsonPropertyName("targetEpoch")] public string TargetEpoch { get; set; } = "";
    [JsonPropertyName("candidateId")] public string CandidateId { get; set; } = "";
    [JsonPropertyName("state")] public SubmissionState State { get; set; }
    [JsonPropertyName("recordedAtUtc")] public DateTimeOffset RecordedAtUtc { get; set; }
}

/// <summary>
/// 逻辑操作权威记录（§4.1 恢复权威——不依赖可缺失的镜像；runBinding/cursorRef 绑定不可改写，
/// submissionIdentity/targetEpoch 原目标实例不可改写；wireSubmitKey 无法确定性推导时显式存储，逐操作 §6.1 映射表）。
/// </summary>
public sealed class OperationRecord
{
    /// <summary>入口适配器首次接纳分配一次（Guid N 小写）；同次用户操作的内部重试复用同一身份。</summary>
    [JsonPropertyName("requestIdentity")] public string RequestIdentity { get; set; } = "";
    [JsonPropertyName("candidateId")] public string CandidateId { get; set; } = "";
    [JsonPropertyName("payloadFingerprint")] public string PayloadFingerprint { get; set; } = "";
    [JsonPropertyName("sortKeyFingerprint")] public string SortKeyFingerprint { get; set; } = "";
    /// <summary>candidateId→runId→首节点提交键（绑定后不可改写）。</summary>
    [JsonPropertyName("runBinding")] public string? RunBinding { get; set; }
    [JsonPropertyName("cursorRef")] public string? CursorRef { get; set; }
    [JsonPropertyName("cursorRevision")] public long? CursorRevision { get; set; }
    [JsonPropertyName("requestState")] public OperationRequestState RequestState { get; set; }
    [JsonPropertyName("lastSendSeq")] public int LastSendSeq { get; set; }
    /// <summary>首次确定拒绝派生的有界重试窗口截止（§3.3-6：持久化后不得重置；到期锁内复核才转终局，不用于 Unknown/Reconciling）。</summary>
    [JsonPropertyName("retryWindowDeadlineUtc")] public DateTimeOffset? RetryWindowDeadlineUtc { get; set; }
    [JsonPropertyName("lastResult")] public OperationResult? LastResult { get; set; }
    /// <summary>接管台账关联引用（受理分支关闭前已持久化并可重建）。</summary>
    [JsonPropertyName("takeoverRef")] public string? TakeoverRef { get; set; }
    [JsonPropertyName("zone")] public OperationZone Zone { get; set; } = OperationZone.Active;
    [JsonPropertyName("updatedRevision")] public long UpdatedRevision { get; set; }
    [JsonPropertyName("updatedAtUtc")] public DateTimeOffset UpdatedAtUtc { get; set; }
    /// <summary>完整发送关联身份（关闭后全量保留，六轮重要 1）。</summary>
    [JsonPropertyName("submissionIdentity")] public string SubmissionIdentity { get; set; } = "";
    /// <summary>原目标实例/epoch（不可改写）。</summary>
    [JsonPropertyName("targetEpoch")] public string TargetEpoch { get; set; } = "";
    /// <summary>登记时冻结的完整候选快照（不可变消费记录：占位/重试按本快照比对与重建，不凭调用方后置可变对象）。</summary>
    [JsonPropertyName("candidate")] public ArbitrationCandidate? Candidate { get; set; }
    [JsonPropertyName("resourceRef")] public string ResourceRef { get; set; } = "";
    [JsonPropertyName("intent")] public string Intent { get; set; } = "";
    /// <summary>线上提交键（无法确定性推导时显式存储，逐操作 §6.1）。</summary>
    [JsonPropertyName("wireSubmitKey")] public string? WireSubmitKey { get; set; }
    /// <summary>去重合并关联（发送前持久化——本项由该胜者请求身份承担发送责任；共同结清/恢复时镜像终态）。</summary>
    [JsonPropertyName("mergedInto")] public string? MergedInto { get; set; }
}
