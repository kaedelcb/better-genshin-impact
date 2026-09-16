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

    /// <summary>
    /// 合成进度展示文本（IPC task.status 的 friendshipProgress 字段与 ext 观察器的事件载荷共用单一口径）。
    /// 不在跑返回 null。预计剩余需要首个成功轮次的耗时样本，首轮期间显示"首轮计速中"。
    /// </summary>
    public static string? BuildDisplayText()
    {
        lock (Sync)
        {
            if (!IsRunning) return null;
            var tsRemain = TimeSpan.FromSeconds(Math.Max(0, EstimatedRemainingSeconds));
            var remainText = EstimatedRemainingSeconds > 0
                ? $"，预计剩余 {(int)tsRemain.TotalMinutes}分{tsRemain.Seconds:00}秒"
                : (CurrentRound <= 1 ? "，首轮计速中" : "");
            var finishText = EstimatedFinishTime > DateTime.MinValue
                ? $"，预计 {EstimatedFinishTime:HH:mm} 完成"
                : "";
            return $"好感任务：第 {CurrentRound}/{TotalRounds} 轮{remainText}{finishText}";
        }
    }
}
