using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace BetterGenshinImpact.Core.Config;

/// <summary>
/// 联机锄地"按周期吃食物"的全局 CD 时间戳存储（multiplayer-hoeing-auto-eat-food-by-period spec）。
/// 键 = 食物页固定格子序号(1~4)；值 = 上次吃该格的 UTC 时间戳(ISO 8601 "o")。
/// 全局落盘于 config.json 顶层，跨配置组/跨任务共享，纯本地不同步给其他玩家。
/// </summary>
[Serializable]
public partial class MedicineEatCdConfig : ObservableObject
{
    /// <summary>键：食物序号(1~4)；值：上次吃该格的 UTC 时间戳字符串。直接 mutate 字典不触发持久化，请用 SetLastEatTime。</summary>
    [ObservableProperty]
    private Dictionary<int, string> _lastEatTimeBySlot = new();

    /// <summary>读取某格上次吃药时间；无记录或解析失败 → 返回 null（视为从未吃过）。</summary>
    public DateTime? GetLastEatTime(int slot)
    {
        if (LastEatTimeBySlot.TryGetValue(slot, out var s)
            && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t))
        {
            return t;
        }
        return null;
    }

    /// <summary>写入某格的吃药时间戳（UTC ISO 8601），显式 OnPropertyChanged 触发持久化。</summary>
    public void SetLastEatTime(int slot, DateTime utc)
    {
        LastEatTimeBySlot[slot] = utc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(LastEatTimeBySlot));
    }
}
