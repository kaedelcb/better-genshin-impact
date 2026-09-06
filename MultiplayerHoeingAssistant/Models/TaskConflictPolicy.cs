namespace MultiplayerHoeingAssistant.Models;

/// <summary>
/// 任务冲突策略：本机正在运行任务时，新任务（上线锄地 / 成员卡片按键启动）抢占后，
/// 如何处置被中断的原任务。三个取值都意味着"抢占"（先 suspend 再执行新任务）。
/// </summary>
public enum TaskConflictPolicy
{
    /// <summary>恢复：suspend 保存上下文 → 执行新任务 → resume 恢复原任务流。</summary>
    Resume = 0,

    /// <summary>不恢复，执行完停止：suspend → 执行新任务 → resume(cancel:true) 清上下文。</summary>
    Stop = 1,

    /// <summary>不恢复，改跑指定任务：suspend → 执行新任务 → resume(cancel:true) → 启动预先配置的指定配置组/一条龙。</summary>
    RunSpecified = 2,
}

/// <summary>
/// 任务冲突策略运行时快照（由 MainViewModel 从 AssistConfig 的运行时字段快照生成，
/// 传给 CommandExecutor；避免 CommandExecutor 反向依赖 AssistConfig 生命周期）。
/// </summary>
public class TaskConflictPolicySettings
{
    /// <summary>冲突策略（默认 Stop，与按键任务策略默认值一致）。</summary>
    public TaskConflictPolicy Policy { get; set; } = TaskConflictPolicy.Stop;

    /// <summary>指定任务类型："group"=配置组，"onedragon"=一条龙，"schedule"=连续一条龙（计划表）。仅 RunSpecified 策略使用。</summary>
    public string SpecifiedTaskType { get; set; } = "group";

    /// <summary>指定任务名称。仅 RunSpecified 策略使用；执行时校验存在性，不存在则日志报错退化为停止。</summary>
    public string SpecifiedTaskName { get; set; } = "";

    /// <summary>策略显示名（日志用）。</summary>
    public string PolicyDisplayName => Policy switch
    {
        TaskConflictPolicy.Resume => "恢复",
        TaskConflictPolicy.Stop => "不恢复直接停止",
        TaskConflictPolicy.RunSpecified => "不恢复并执行指定任务",
        _ => Policy.ToString()
    };
}
