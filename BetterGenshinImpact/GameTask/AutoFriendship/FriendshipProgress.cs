using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFriendship.Model;
using BetterGenshinImpact.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

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

/// <summary>
/// 好感任务每日累计次数（以服务器时间凌晨 4 点为分界，按敌人类型分别计数）。
/// 中途停止后再次执行时，从当日已完成次数继续累计，而不是重新从 0 开始。
/// </summary>
public static class FriendshipDailyCounter
{
    private const int DayBoundaryHour = 4;
    private static readonly object Gate = new();

    private static string FilePath => Global.Absolute(@"User\AutoFriendship\daily_count.json");

    private sealed class Entry
    {
        public string Day { get; set; } = "";
        public int Count { get; set; }
    }

    /// <summary>当前游戏日标识（服务器时间减 4 小时后的日期）。</summary>
    public static string CurrentDayKey()
    {
        DateTimeOffset now;
        try
        {
            now = ServerTimeHelper.GetServerTimeNow();
        }
        catch (InvalidOperationException)
        {
            now = DateTimeOffset.Now;
        }

        return now.AddHours(-DayBoundaryHour).ToString("yyyy-MM-dd");
    }

    /// <summary>读取指定敌人类型当日已完成次数。</summary>
    public static int GetTodayCount(EnemyType type)
    {
        lock (Gate)
        {
            var all = Load();
            return all.TryGetValue(type.ToString(), out var e) && e.Day == CurrentDayKey() ? e.Count : 0;
        }
    }

    /// <summary>指定敌人类型当日完成次数 +1，返回累加后的次数。</summary>
    public static int Increment(EnemyType type)
    {
        lock (Gate)
        {
            var all = Load();
            var day = CurrentDayKey();
            var key = type.ToString();
            if (!all.TryGetValue(key, out var e) || e.Day != day)
            {
                e = new Entry { Day = day, Count = 0 };
                all[key] = e;
            }

            e.Count++;
            Save(all);
            return e.Count;
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            var path = FilePath;
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    private static void Save(Dictionary<string, Entry> all)
    {
        var path = FilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(tmp, path, true);
    }
}
