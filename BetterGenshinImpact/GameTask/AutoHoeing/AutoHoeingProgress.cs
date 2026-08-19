namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 记录联机锄地当前进度的全局载体（供 IPC task.status 查询）。
/// 由 <see cref="AutoHoeingTask"/> 在执行路线循环时写入，外部只读。
/// </summary>
public static class AutoHoeingProgress
{
    public static readonly object Sync = new();

    /// <summary>是否正在联机锄地。</summary>
    public static volatile bool IsRunning;

    /// <summary>轮次前缀，如 "[第 1/4 轮 茶包s] "。</summary>
    public static string RoundPrefix = string.Empty;

    /// <summary>当前第几条线路（1-based）。</summary>
    public static int CurrentRouteIndex;

    /// <summary>总路线数。</summary>
    public static int TotalRoutes;

    /// <summary>当前路线文件名。</summary>
    public static string RouteFileName = string.Empty;

    /// <summary>本线路预计用时（秒）。</summary>
    public static double RouteEstimatedSeconds;

    /// <summary>本轮预计剩余（秒）。</summary>
    public static double RoundRemainingSeconds;

    public static void Clear()
    {
        lock (Sync)
        {
            IsRunning = false;
            RoundPrefix = string.Empty;
            CurrentRouteIndex = 0;
            TotalRoutes = 0;
            RouteFileName = string.Empty;
            RouteEstimatedSeconds = 0;
            RoundRemainingSeconds = 0;
        }
    }
}