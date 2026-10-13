using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoPick;

/// <summary>
/// 自动拾取记录器（仅莫版拾取路径记录，原版拾取不记录）。
/// 记录每次拾取交互的物品名称与时间戳，供 JS 侧通过 <c>dispatcher.getPickRecords()</c> 获取。
///
/// JS 侧用法（注意兼容旧版 C#，使用特性检测/可选链，旧版无此方法时返回空数组不报错）：
/// <code>
/// // 获取自上次调用以来的拾取记录（取走后自动清空）
/// const records = typeof dispatcher.getPickRecords === 'function' ? dispatcher.getPickRecords() : [];
/// // 或更简洁：const records = dispatcher.getPickRecords?.() ?? [];
/// for (const r of records) {
///     log.info(`拾取: ${r.Name} @ ${r.Time}`);
/// }
/// </code>
/// </summary>
public static class AutoPickRecordStore
{
    /// <summary>记录上限，超出后丢弃最旧记录，防止无限增长。</summary>
    private const int MaxRecords = 100;

    /// <summary>线程安全队列：拾取记录（时间戳 + 物品名），拾取循环线程写入、JS 请求线程读出。</summary>
    private static readonly ConcurrentQueue<PickRecord> Records = new();

    // JS 消费不应清空满背包反查所需的近期交互事实。
    private static readonly ConcurrentQueue<PickRecord> RecentRecords = new();

    /// <summary>单条拾取记录（Name 为背包名/获得物品名，JS 侧 r.Name 显示此值；InteractName 为交互列表显示名）。</summary>
    public sealed record PickRecord(DateTime Time, string Name, string InteractName);

    /// <summary>记录一次拾取交互（仅由莫版拾取路径调用，线程安全）。</summary>
    /// <param name="bagName">背包名（获得物品名，满背包提示显示，JS 侧 r.Name 显示此值）</param>
    /// <param name="interactName">交互名（交互列表显示名，黑名单按此匹配）</param>
    public static void Record(string bagName, string interactName)
    {
        var record = new PickRecord(DateTime.Now, bagName, interactName);
        Records.Enqueue(record);
        RecentRecords.Enqueue(record);
        while (RecentRecords.Count > MaxRecords && RecentRecords.TryDequeue(out _)) { }
        while (Records.Count > MaxRecords && Records.TryDequeue(out _))
        {
        }
    }

    /// <summary>获取最近 window 内的拾取记录（不消费队列，线程安全），供满背包自动加入黑名单匹配使用。</summary>
    public static PickRecord[] PeekRecent(TimeSpan window)
    {
        var cutoff = DateTime.Now - window;
        return RecentRecords.Where(r => r.Time >= cutoff).ToArray();
    }

    /// <summary>取出全部记录并清空（线程安全），JS 侧通过 dispatcher.getPickRecords() 调用。</summary>
    public static PickRecord[] Drain()
    {
        var list = new List<PickRecord>(Records.Count);
        while (Records.TryDequeue(out var record))
        {
            list.Add(record);
        }

        return list.ToArray();
    }
}
