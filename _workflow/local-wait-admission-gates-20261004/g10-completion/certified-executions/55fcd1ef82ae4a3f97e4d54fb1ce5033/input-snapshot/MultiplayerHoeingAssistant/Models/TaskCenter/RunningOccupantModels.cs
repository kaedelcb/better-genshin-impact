namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// **运行中占用事实**（R5 批次 4，A6）：owner 裁决 B.1 要求"上线锄地／一键锄地恒为最高优先级、同级后来者打断、
/// 低优先级本地等待、未知不当作空闲"，而生产现有事实只有布尔占用 ⇒ 本类型把"当前占用者是谁、级别多少、
/// 能否安全绑定停止"显式化，未知一律如实留空（**不得**用默认值冒充已知）。
/// </summary>
public enum OccupantFactsState
{
    /// <summary>权威事实未知（快照缺失/过期/台账不可读）：**不是空闲**。</summary>
    Unknown = 0,
    Idle = 1,
    Occupied = 2,
}

/// <summary>到来者与当前占用者的相遇判定（owner B.1 最小裁决矩阵的实现）。</summary>
public enum RunningEncounterVerdict
{
    /// <summary>占用事实未知：保守停驻、零发送（不当作空闲、不换键补发）。</summary>
    HoldFactsUnknown = 0,
    /// <summary>确认空闲：可按常规准入继续（准入本身仍单独判定）。</summary>
    ProceedIdle = 1,
    /// <summary>可抢占：需绑定被切任务身份并完成退出确认后执行到来者。</summary>
    PreemptNow = 2,
    /// <summary>占用者身份/级别/优先级不可核验：无法安全抢占，保守停驻、零新发送。</summary>
    HoldUnknownOccupant = 3,
    /// <summary>到来者级别更低：本地持久等待，当前结束后重新比较（非 BGI 入队、非已受理）。</summary>
    WaitLocally = 4,
}

/// <summary>恢复（S8b／resume）资格判定结果。</summary>
public enum ResumeVerdict
{
    AllowResume = 0,
    HoldFactsUnknown = 1,
    RefuseOccupiedByOther = 2,
    RefuseAlreadyOwner = 3,
    RefuseTicketInvalid = 4,
    /// <summary>被暂停身份缺失：无法证明恢复对象，保守拒绝（不得凭布尔票据自证）。</summary>
    RefuseSuspendedIdentityUnknown = 5,
}

/// <summary>
/// 当前占用者的权威事实（单值；未知字段必须保持 null）。**生产映射唯一事实点**＝
/// <see cref="FromStatus"/>：BGI 控制面状态快照 ∪ 外部启动台账占用。
/// </summary>
public sealed class RunningOccupantFacts
{
    public OccupantFactsState State { get; init; } = OccupantFactsState.Unknown;

    /// <summary>未知时的具体引用/原因（未知分支必填，供审计与对账）。</summary>
    public string? Reference { get; init; }

    /// <summary>是否持有可核验的执行根身份（能绑定定向停止请求）。false ⇒ 不可抢占（fail-closed）。</summary>
    public bool HasTrustedIdentity { get; init; }

    public string? ExecutionInstanceId { get; init; }
    public string? RunId { get; init; }
    public string? JobId { get; init; }
    public string? Kind { get; init; }
    public string? Source { get; init; }
    public string? Name { get; init; }

    /// <summary>占用者是否属于锄地类（联机锄地/上线锄地/一键锄地）。</summary>
    public bool HoeingClass { get; init; }

    /// <summary>
    /// 占用者是否**已被证明**属于 B.1 的两类最高级（上线锄地／一键锄地）：
    /// true＝证明是；false＝证明不是；**null＝不可判定**（例如只知道"联机锄地在跑"，不足以等同最高级）。
    /// **当前仍无受信来源**（自报 key 不作证明；运行台账的候选快照也未携带最高级标记）⇒ 生产路径恒 null（残余），
    /// 因此普通任务不得据此判断「可以抢占锄地占用者」。
    /// </summary>
    public bool? HighestClass { get; init; }

    /// <summary>
    /// 占用者级别／优先级：**只有能证明来源时才有值**。生产路径由宿主 `TaskCenterHost.ResolveOccupantLevels`
    /// 经 `OccupantLevelResolver`（BGI 执行运行 → 运行台账 `WireRunId` → 流程级登记操作的候选快照）填充；
    /// 任一环节缺失、多命中或候选冲突 ⇒ 保持 null（不得凭空推断归属）。
    /// </summary>
    public ArbitrationTier? Tier { get; init; }
    public int? Priority { get; init; }

    public bool StopRequested { get; init; }

    public static RunningOccupantFacts Unknown(string reference)
        => new() { State = OccupantFactsState.Unknown, Reference = reference };

    public static RunningOccupantFacts Idle() => new() { State = OccupantFactsState.Idle };

    /// <summary>
    /// 用解析出的级别事实复制一份占用者事实（其余字段逐项保留）：仅当解析确实命中时才填 Tier/Priority/HighestClass；
    /// 解析结果未知（null 值）时保持原样，**不得**用默认值冒充已知。
    /// </summary>
    public RunningOccupantFacts WithLevelFacts(ArbitrationTier? tier, int? priority, bool? highestClass)
        => new()
        {
            State = State,
            Reference = Reference,
            HasTrustedIdentity = HasTrustedIdentity,
            ExecutionInstanceId = ExecutionInstanceId,
            RunId = RunId,
            JobId = JobId,
            Kind = Kind,
            Source = Source,
            Name = Name,
            HoeingClass = HoeingClass,
            HighestClass = highestClass ?? HighestClass,
            Tier = tier ?? Tier,
            Priority = priority ?? Priority,
            StopRequested = StopRequested,
        };

    /// <summary>
    /// 生产映射（单一事实点）：**不知道的一律留空**。
    /// ①台账不可读 ⇒ 未知；②快照缺失/过期 ⇒ 未知（不当作空闲）；③台账有已受理未终结记录 ⇒ 占用但**无身份**
    /// （台账不能证明当前 BGI 根归属）；④显式 running=false 且快照新鲜 ⇒ 空闲；⑤占用时取快照里的执行身份，
    /// 身份缺失 ⇒ <see cref="HasTrustedIdentity"/>=false；级别/优先级来源未接线 ⇒ 保持 null。
    /// </summary>
    public static RunningOccupantFacts FromStatus(ControlStatus? status, bool ledgerOccupied, bool ledgerUnknown,
        bool bgiEpochVerified)
    {
        if (ledgerUnknown) return Unknown("external_start_ledger_unreadable");
        if (status is null) return Unknown("bgi_status_unavailable");
        if (!bgiEpochVerified) return Unknown("bgi_epoch_unverified");
        if (!status.HasFreshTaskStatus(DateTimeOffset.UtcNow)) return Unknown("bgi_status_stale");

        if (ledgerOccupied)
        {
            return new RunningOccupantFacts
            {
                State = OccupantFactsState.Occupied,
                Reference = "external_start_ledger_accepted_unfinished",
                HasTrustedIdentity = false,
                HoeingClass = status.AutoHoeingRunning,
            };
        }

        if (status.TaskRunning != true) return Idle();

        var execution = status.CurrentExecution;
        var identityValid = execution is not null && execution.ExecutionInstanceId != Guid.Empty;
        return new RunningOccupantFacts
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = identityValid,
            ExecutionInstanceId = execution?.ExecutionInstanceId.ToString("N"),
            RunId = execution?.RunId.ToString("N"),
            JobId = execution?.JobId?.ToString("N"),
            Kind = execution?.Kind,
            Source = execution?.Source,
            Name = execution?.Name ?? status.CurrentTaskName,
            HoeingClass = status.AutoHoeingRunning,
            HighestClass = null,
            Tier = null,
            Priority = null,
            StopRequested = execution?.StopRequested ?? false,
        };
    }
}

/// <summary>到来者事实（级别/优先级来自**可信入口映射**；自报 key 不构成最高级证明）。</summary>
public sealed class IncomingRequestFacts
{
    /// <summary>是否为锄地类最高优先级（上线锄地／一键锄地，由可信入口身份判定）。</summary>
    public bool IsHoeingHighest { get; init; }
    public ArbitrationTier Tier { get; init; } = ArbitrationTier.Plan;
    public int Priority { get; init; }
    public bool HasTrustedIdentity { get; init; } = true;
}

/// <summary>本地等待集合项（等待不是 BGI 已入队/已受理）。</summary>
public sealed class WaitingRequestFacts
{
    public string CandidateId { get; init; } = "";
    public string StableIdentity { get; init; } = "";
    public bool IsHoeingHighest { get; init; }
    public ArbitrationTier Tier { get; init; }
    public int Priority { get; init; }
    public DateTimeOffset? ScheduledAt { get; init; }
    public bool PrerequisiteReady { get; init; } = true;
}

/// <summary>恢复事实（S8b：须沿用被暂停任务的票据与身份）。</summary>
public sealed class ResumptionFacts
{
    public string SuspendedIdentity { get; init; } = "";
    public string? SuspendedExecutionInstanceId { get; init; }
    public bool HasValidTicket { get; init; }
}

/// <summary>相遇判定结果（含原因与可抢占目标身份）。</summary>
public sealed record RunningEncounter(RunningEncounterVerdict Verdict, string Reason, string? PreemptTargetInstanceId);
