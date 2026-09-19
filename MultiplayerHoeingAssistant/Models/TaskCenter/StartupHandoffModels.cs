using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Models;

/// <summary>移交语义（R4.9 设计稿 §4）：start=立即执行 / resume=恢复既有运行 / armTrigger=挂载流程触发器。</summary>
public static class StartupHandoffModes
{
    public const string Start = "start";
    public const string Resume = "resume";
    public const string ArmTrigger = "armTrigger";

    public static readonly string[] All = [Start, Resume, ArmTrigger];

    /// <summary>显式未知值响亮拒绝（不静默回落 start）；缺省默认 start 由 StartupStep.TaskCenterHandoffMode 属性初始值保证。</summary>
    public static bool IsKnown(string? mode) => mode is Start or Resume or ArmTrigger;
}

/// <summary>
/// 移交拒绝原因码（R4.9 §7：链策略按原因码单点分类——仅 ConfigMissing 链继续（旧配置兼容），
/// 其余一律终止本次启动链，防止残余节点（如 StopBgi）破坏宿主管理的在册运行）。
/// </summary>
public static class HandoffReasonCodes
{
    /// <summary>未配置目标流程（旧配置兼容：节点 Failed、链继续）。</summary>
    public const string ConfigMissing = "ConfigMissing";
    /// <summary>显式未知移交语义。</summary>
    public const string UnsupportedMode = "UnsupportedMode";
    /// <summary>本启动链已直接提交 BGI 任务（混用检测主证据）。</summary>
    public const string MixedUsage = "MixedUsage";
    /// <summary>快照显示 BGI 任务在跑（非本执行提交）。</summary>
    public const string BgiBusy = "BgiBusy";
    /// <summary>任务状态快照不可考（授权判断要求可考，锚点 3）。</summary>
    public const string StatusUncertain = "StatusUncertain";
    /// <summary>同流程已有活动运行/正在驱动。</summary>
    public const string AlreadyRunning = "AlreadyRunning";
    /// <summary>结果不确定（Unknown），需先对账。</summary>
    public const string Unknown = "Unknown";
    /// <summary>同一 IntentKey 已受理为不同内容（流程/语义不符）。</summary>
    public const string IdentityConflict = "IdentityConflict";
    /// <summary>监控端无本地执行能力（宿主服务边界守卫）。</summary>
    public const string NoCapability = "NoCapability";
    /// <summary>无可恢复运行（resume 语义）。</summary>
    public const string NoResumableRun = "NoResumableRun";
    /// <summary>流程无可挂载触发器（armTrigger 语义）。</summary>
    public const string NoTrigger = "NoTrigger";
    /// <summary>流程缺失/隔离/候选只读。</summary>
    public const string FlowUnavailable = "FlowUnavailable";
    /// <summary>BGI 离线或 ext 通道未就绪。</summary>
    public const string NotReady = "NotReady";
    /// <summary>宿主已关闭。</summary>
    public const string Shutdown = "Shutdown";
    /// <summary>受理路径异常（兜底；按终止链处置）。</summary>
    public const string HandoffError = "HandoffError";
    /// <summary>权威台账不完整（存在无法解析的运行记录文件）——读不到 ≠ 未受理，拒绝新受理先修复（ASTRA 二轮 I5）。</summary>
    public const string LedgerIncomplete = "LedgerIncomplete";
}

/// <summary>移交回执结果（R4.9 §2/§3）。</summary>
public enum HandoffOutcome
{
    /// <summary>新受理（受理点=RunStore 落盘；受理≠执行成功）。</summary>
    Accepted,
    /// <summary>同一计划出现此前已受理（幂等回执，附原 runId 与当前状态）。</summary>
    AlreadyAccepted,
    /// <summary>拒绝（原因可读；链策略按 ReasonCode）。</summary>
    Rejected,
}

/// <summary>
/// 触发来源（定时/电子狗/日志触发链执行时由宿主 VM 传入 RunAsync；手动/自动主流程为 null）。
/// OccurrenceKey=计划出现键材料：当前统一取触发日（yyyy-MM-dd）——同一触发器同一日程出现共享身份，
/// 无论从哪个入口到达、重试多少次都去重（R4.9 §2；电子狗/日志同日多次触发按同一计划出现去重，为保守口径，见设计稿会诊记录）。
/// </summary>
public sealed record StartupTriggerInfo(string Kind, string InstanceId, string OccurrenceKey);

/// <summary>
/// 移交请求（R4.9 §2 双重身份）：ExecutionId=一次启动链执行身份（每次 RunAsync 一个 GUID，参数透传）；
/// IntentKey=业务计划出现身份（跨 ExecutionId 的去重键）。
/// </summary>
public sealed class StartupHandoffRequest
{
    public string ExecutionId { get; init; } = "";
    public string StepId { get; init; } = "";
    public string? TriggerKind { get; init; }
    public string? TriggerInstanceId { get; init; }
    /// <summary>计划出现键材料（触发器=日程日 yyyy-MM-dd；手动=null）。</summary>
    public string? FireDate { get; init; }
    public string IntentKey { get; init; } = "";
    public string WorkflowId { get; init; } = "";
    public string Mode { get; init; } = StartupHandoffModes.Start;
    /// <summary>人类可读意图备注（节点显示名等，仅日志/留痕用）。</summary>
    public string? IntentNote { get; init; }
}

/// <summary>
/// 宿主受理回执（R4.9 §3）：受理点=RunStore 落盘，runId 直接来自受理记录（绝不倒推查询）；
/// 激活失败不撤回受理（Outcome 仍为 Accepted，Reason 区分「受理≠执行成功」）。
/// </summary>
public sealed record HandoffRegisterResult(
    HandoffOutcome Outcome, string? RunId, string? Reason, string? ReasonCode, WorkflowRunState? CurrentRunState)
{
    public static HandoffRegisterResult Accepted(string runId, string? reason, WorkflowRunState? state)
        => new(HandoffOutcome.Accepted, runId, reason, null, state);
    public static HandoffRegisterResult AlreadyAccepted(string runId, string? reason, WorkflowRunState? state)
        => new(HandoffOutcome.AlreadyAccepted, runId, reason, null, state);
    public static HandoffRegisterResult Rejected(string reasonCode, string reason)
        => new(HandoffOutcome.Rejected, null, reason, reasonCode, null);
}

/// <summary>
/// 委托级移交结果（R4.9 §2/§7）：TerminateChain 由 Rejected 工厂按原因码单点分类——
/// 仅 ConfigMissing 链继续（旧配置兼容语义），其余一律终止本次启动链。
/// </summary>
public sealed record StartupHandoffResult(
    HandoffOutcome Outcome, string? RunId, string? Reason, string? ReasonCode, bool TerminateChain)
{
    public static StartupHandoffResult Accepted(string? runId, string? reason)
        => new(HandoffOutcome.Accepted, runId, reason, null, false);
    public static StartupHandoffResult AlreadyAccepted(string? runId, string? reason)
        => new(HandoffOutcome.AlreadyAccepted, runId, reason, null, false);
    public static StartupHandoffResult Rejected(string reasonCode, string reason)
        => new(HandoffOutcome.Rejected, null, reason, reasonCode,
            reasonCode is not HandoffReasonCodes.ConfigMissing);
}

/// <summary>台账查询结论（ASTRA 二轮 I5：命中/确定未命中/查询不完整三态；不完整时拒绝新受理，绝不把读不到当成没受理）。</summary>
public enum HandoffLedgerState
{
    /// <summary>命中（Run/Binding 非空）。</summary>
    Hit,
    /// <summary>确定未命中（全部记录可解析且无匹配）。</summary>
    Miss,
    /// <summary>查询不完整（存在无法解析的运行记录文件——坏文件可能藏着受理事实）。</summary>
    Incomplete,
}

/// <summary>台账查询结果（含命中的运行与具体绑定——内容核对按该绑定的 Mode，多绑定模型见 HandoffIdentity 注释）。</summary>
public sealed record HandoffLedgerQuery(HandoffLedgerState State, WorkflowRunRecord? Run, HandoffIdentity? Binding)
{
    public static HandoffLedgerQuery Hit(WorkflowRunRecord run, HandoffIdentity binding) => new(HandoffLedgerState.Hit, run, binding);
    public static readonly HandoffLedgerQuery MissInstance = new(HandoffLedgerState.Miss, null, null);
    public static readonly HandoffLedgerQuery IncompleteInstance = new(HandoffLedgerState.Incomplete, null, null);
}
/// <summary>
/// 移交身份绑定（R4.9 §3 + ASTRA 二轮 B1/B2：一个运行可携带多条不可变受理绑定——resume/arm 幂等挂载
/// 原子追加、绝不替换旧绑定（旧意图的去重依据永存）；台账=RunStore 无淘汰；
/// 崩溃窗恢复 Interrupted → 重放同 IntentKey 直接 AlreadyAccepted，禁止自动换键重跑）。
/// </summary>
public sealed class HandoffIdentity
{
    /// <summary>业务计划出现身份（台账查询键）。</summary>
    [JsonPropertyName("intentKey")]
    public string IntentKey { get; set; } = "";

    /// <summary>受理时的启动链执行身份（留痕/对账用）。</summary>
    [JsonPropertyName("executionId")]
    public string ExecutionId { get; set; } = "";

    /// <summary>发起移交的启动节点身份。</summary>
    [JsonPropertyName("stepId")]
    public string StepId { get; set; } = "";

    /// <summary>触发来源种类（timerTrigger/watchdog/logTrigger；手动执行为 null）。</summary>
    [JsonPropertyName("triggerKind")]
    public string? TriggerKind { get; set; }

    /// <summary>受理时的移交语义（§2 内容核对：同键不同 mode = 身份冲突）。</summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "";

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}