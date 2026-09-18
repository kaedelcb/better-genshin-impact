namespace OneDragonMigration.Core;

/// <summary>三种输入格式（审计 §5.1）。按 JSON 形状判别，版本号只作辅助。</summary>
public enum OneDragonFormat
{
    Unknown = 0,
    /// <summary>当前公版：TaskEnabledList {id:bool} + TaskOrder + TaskDefinitions。</summary>
    PublicCurrent,
    /// <summary>旧公版/旧茶包：任务名→bool，无 definitions/order。</summary>
    LegacyNameBool,
    /// <summary>茶包 Version 1：{数字键:{Item1,Item2}} + NextTaskIndex。</summary>
    TeabagTuple,
}

/// <summary>单个任务条目（按文档枚举顺序，即旧真实执行顺序）。</summary>
public sealed record TaskEntry(string SourceKey, string Name, bool Enabled, int Order);

/// <summary>单份配置的解析快照。</summary>
public sealed class ConfigSnapshot
{
    public required string SourceFile { get; init; }
    public required string Sha256 { get; set; }
    public OneDragonFormat Format { get; set; } = OneDragonFormat.Unknown;
    public string Name { get; set; } = string.Empty;
    /// <summary>稳定身份键：Name#来源哈希，与冲突无关，跨迁移/重跑不变（映射键）。</summary>
    public string ConfigKey { get; set; } = string.Empty;
    /// <summary>输出文件名（不含扩展名）：批内唯一时用配置名，冲突时用 ConfigKey。</summary>
    public string OutputName { get; set; } = string.Empty;
    public int IndexId { get; set; } = 1;
    public bool NextConfiguration { get; set; }
    public int NextTaskIndex { get; set; }
    public string? NextTaskIdRaw { get; set; }
    public string ScheduleName { get; set; } = "默认计划表";
    public string Period { get; set; } = "每日";
    public Dictionary<string, bool> PeriodList { get; } = new();
    public bool AccountBinding { get; set; }
    public string GenshinUid { get; set; } = string.Empty;
    public string AccountBindingCode { get; set; } = string.Empty;
    public string CompletionAction { get; set; } = string.Empty;
    public List<string> CustomDomainList { get; } = new();
    public bool HasResinOverride { get; set; }
    public List<TaskEntry> Tasks { get; } = new();
    /// <summary>原始 JSON DOM（只读使用，绝不写回来源）。</summary>
    public System.Text.Json.Nodes.JsonObject? Raw { get; set; }
    public string? ParseError { get; set; }
}

/// <summary>全局计划字段快照（AllConfig 形状中的调度子集 + 兑换码开关 + 计划选择）。</summary>
public sealed class ScheduleSnapshot
{
    public bool ScheduleStartOnTime { get; set; }
    public string ScheduleStartTime { get; set; } = "00:00";
    public bool ScheduleLoop { get; set; }
    public bool CycleMode { get; set; }
    public string CycleTime { get; set; } = "04:00";
    public bool ScheduleLoopSkip { get; set; }
    public string ContinuousCompletionAction { get; set; } = string.Empty;
    public bool AutoRedeemCodeCheckEnabled { get; set; }
    /// <summary>旧全局调度只作用于当前选中的计划。</summary>
    public string SelectedPlanName { get; set; } = string.Empty;
    public List<string> ScheduleList { get; } = new();
    public bool Present { get; set; }
    public string? ParseError { get; set; }
    public string? Sha256 { get; set; }
}

public sealed record MigrationIssue(string Severity, string Config, string Message);
public sealed record DroppedItem(string Config, string Field, string Reason, string? Detail = null);

/// <summary>一次 dry-run 的完整结果。</summary>
public sealed class MigrationResult
{
    public List<ConfigSnapshot> Configs { get; } = new();
    public ScheduleSnapshot Schedule { get; set; } = new();
    public List<MigrationIssue> Issues { get; } = new();
    public List<DroppedItem> Dropped { get; } = new();
    /// <summary>configKey → (sourceKey → taskId) 映射（重复迁移复用，幂等）。</summary>
    public Dictionary<string, Dictionary<string, string>> IdMappings { get; } = new();
    /// <summary>未识别字段（保留在标准投影中，但入报告）。</summary>
    public List<DroppedItem> Unrecognized { get; } = new();
    /// <summary>configKey → 任务引用的配置组名（存在性未验证，R2 对账）。</summary>
    public Dictionary<string, List<string>> Dependencies { get; } = new();
    /// <summary>configKey → 标准产物哈希（流程引用修订）。</summary>
    public Dictionary<string, string> StandardHashes { get; } = new();
    /// <summary>阻断激活的原因（error 级问题的结构化汇总）。</summary>
    public List<string> ActivationBlockers { get; } = new();
    public string? CandidateDir { get; set; }
    public List<string> WrittenFiles { get; } = new();
}
