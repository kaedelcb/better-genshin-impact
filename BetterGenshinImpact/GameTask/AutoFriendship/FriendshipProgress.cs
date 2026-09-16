using System;

namespace BetterGenshinImpact.GameTask.AutoFriendship;

/// <summary>
/// 记录好感任务当前进度的全局载体（供 IPC task.status 查询），模式与 AutoHoeingProgress 一致。
/// 由 <see cref="AutoFriendshipTask"/> 在主循环写入，外部只读。
/// </summary>
public static class FriendshipProgress
{
    public static readonly object Sync = new();

    /// <summary>是否正在执行好感任务。</summary>
    public static volatile bool IsRunning;

    /// <summary>当前轮次（1-based）。</summary>
    public static int CurrentRound;

    /// <summary>总轮数（RunTimes；首轮失败会 +1，以任务运行中实际值为准）。</summary>
    public static int TotalRounds;

    /// <summary>预计剩余时间（秒；按单轮均耗时估算，0=未知）。</summary>
    public static double EstimatedRemainingSeconds;

    /// <summary>预计完成时刻（本地时间；未知为 DateTime.MinValue）。</summary>
    public static DateTime EstimatedFinishTime;

    public static void Clear()
    {
        lock (Sync)
        {
            IsRunning = false;
            CurrentRound = 0;
            TotalRounds = 0;
            EstimatedRemainingSeconds = 0;
            EstimatedFinishTime = DateTime.MinValue;
        }
    }
}
