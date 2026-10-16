namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 记录原生锄地（单机/联机）当前进度的全局载体（供 IPC task.status 查询）。
/// 由 <see cref="AutoHoeingTask"/> 在执行路线循环时写入，外部只读。
/// </summary>
public static class AutoHoeingProgress
{
    public static readonly object Sync = new();

    /// <summary>是否已进入锄地进度；单机和联机共用，不能据此判断联机身份。</summary>
    public static volatile bool IsRunning;

    private static bool? _multiplayerMode;
    /// <summary>当前实际执行配置的模式；null 表示没有已确认的原生锄地模式。</summary>
    public static bool? MultiplayerMode { get { lock (Sync) return _multiplayerMode; } }

    /// <summary>在进入组队/路线阶段前记录覆盖配置后的实际模式，不改变原有进度信号时机。</summary>
    public static void BeginExecution(bool multiplayerEnabled)
    {
        lock (Sync) _multiplayerMode = multiplayerEnabled;
    }

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

    /// <summary>SignalR 房间当前人数（CoordinatorClient players 事件回填；0=未知/未在房间）。</summary>
    public static volatile int RoomPlayerCount;

    public static void Clear()
    {
        lock (Sync)
        {
            IsRunning = false;
            _multiplayerMode = null;
            RoundPrefix = string.Empty;
            CurrentRouteIndex = 0;
            TotalRoutes = 0;
            RouteFileName = string.Empty;
            RouteEstimatedSeconds = 0;
            RoundRemainingSeconds = 0;
            RoomPlayerCount = 0;
        }
    }
}