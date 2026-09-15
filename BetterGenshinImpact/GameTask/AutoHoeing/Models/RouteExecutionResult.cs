namespace BetterGenshinImpact.GameTask.AutoHoeing.Models;

/// <summary>
/// 单条路线执行结果
/// </summary>
public class RouteExecutionResult
{
    public BetterGenshinImpact.Shared.CooperativeRerun.RerunRouteOutcome CooperativeOutcome { get; set; }
    public double ActualDuration { get; set; }
    public bool ShouldSwitchFurina { get; set; }
    public bool Success { get; set; }

    /// <summary>
    /// 路线是否完整执行到最后一个节点（用于判断是否记录CD和运行时长）
    /// </summary>
    public bool FullyCompleted { get; set; }

    /// <summary>
    /// 联机模式：路线因异常被跳过（不记录 CD）
    /// </summary>
    public bool SkipRouteRequested { get; set; }

    /// <summary>
    /// 联机模式：跳过原因
    /// </summary>
    public string? SkipRouteReason { get; set; }
}
