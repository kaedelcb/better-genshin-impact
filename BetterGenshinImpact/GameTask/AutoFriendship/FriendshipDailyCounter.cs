using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFriendship.Model;
using BetterGenshinImpact.Helpers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace BetterGenshinImpact.GameTask.AutoFriendship;

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
