using System;
using System.Collections.Generic;
using System.Linq;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 联机锄地"按周期吃食物"的决策纯函数集合（multiplayer-hoeing-auto-eat-food-by-period spec）。
/// 与 bgi-implementation-patterns §1「决策函数纯化」对齐：无外部依赖、可重复调用结果一致、PBT 友好。
/// </summary>
public static class MedicineEatDecisions
{
    /// <summary>一行食物参数：Slot=食物页固定格子序号(1~4)，PeriodSeconds=执行周期(秒,0=周期维度不启用)，RouteKeywords=吃药线路关键词(逗号分隔,空=线路维度不启用)。</summary>
    public readonly record struct FoodRow(int Slot, int PeriodSeconds, string RouteKeywords);

    /// <summary>
    /// 周期维度是否到期（受 CD 约束）。删除了旧的 30 秒提前量：
    /// 周期&lt;=0 → false（周期维度不启用）；序号越界 → false；从未吃过 → true；否则 elapsed &gt;= Period 即 true。
    /// </summary>
    public static bool IsPeriodDue(FoodRow row, DateTime? lastEatUtc, DateTime nowUtc)
    {
        if (row.PeriodSeconds <= 0) return false;
        if (row.Slot < 1 || row.Slot > 4) return false;
        if (lastEatUtc == null) return true;
        var elapsed = (nowUtc - lastEatUtc.Value).TotalSeconds;
        return elapsed >= row.PeriodSeconds;
    }

    /// <summary>
    /// 线路维度是否命中（无视 CD）。该行线路关键词非空 且 当前线路名（文件名去 .json）包含任一关键词（大小写不敏感）。
    /// 复用 RouteKeywordFilterDecisions.ParseKeywords + ShouldSkip（Contains 任一即命中）语义。
    /// 序号越界 → false。空关键词 / 空线路名 → false（线路维度不启用）。
    /// </summary>
    public static bool IsRouteMatched(FoodRow row, string? currentRouteName)
    {
        if (row.Slot < 1 || row.Slot > 4) return false;
        var keywords = BetterGenshinImpact.GameTask.AutoHoeing.Services.RouteKeywordFilterDecisions.ParseKeywords(row.RouteKeywords);
        return BetterGenshinImpact.GameTask.AutoHoeing.Services.RouteKeywordFilterDecisions.ShouldSkip(currentRouteName, keywords);
    }

    /// <summary>
    /// 单行是否要吃 = 周期维度到期(受CD) OR 线路维度命中(无视CD)。任一满足即吃。
    /// </summary>
    public static bool IsRowDue(FoodRow row, DateTime? lastEatUtc, DateTime nowUtc, string? currentRouteName)
    {
        return IsPeriodDue(row, lastEatUtc, nowUtc) || IsRouteMatched(row, currentRouteName);
    }

    /// <summary>
    /// 返回所有到期的合法行序号，升序、去重。无可吃行 → 空列表。
    /// 到期的都吃（OQ-1），不择一；顺序升序(1→4)供执行器从左到右依次点击。
    /// </summary>
    public static IReadOnlyList<int> SelectSlotsToEat(
        IReadOnlyList<FoodRow> rows,
        Func<int, DateTime?> lastEatBySlot,
        DateTime nowUtc,
        string? currentRouteName)
    {
        if (rows == null) return Array.Empty<int>();
        return rows
            .Where(r => IsRowDue(r, lastEatBySlot(r.Slot), nowUtc, currentRouteName))
            .Select(r => r.Slot)
            .Distinct()
            .OrderBy(s => s)
            .ToList();
    }

    /// <summary>
    /// 组装本次同步点的吃药计划。单机(!isMultiplayer)或断线(!isConnected) → 空计划(ShouldEat=false)。
    /// </summary>
    public static MedicineEatPlan BuildPlan(
        IReadOnlyList<FoodRow> rows,
        Func<int, DateTime?> lastEatBySlot,
        DateTime nowUtc,
        bool isMultiplayer,
        bool isConnected,
        string? currentRouteName)
    {
        if (!isMultiplayer || !isConnected) return MedicineEatPlan.Empty;
        var slots = SelectSlotsToEat(rows, lastEatBySlot, nowUtc, currentRouteName);
        return new MedicineEatPlan(slots);
    }

    /// <summary>
    /// 吃药时抑制该同步点上报（语义等价换人抑制模式，见 PerRouteSwitchRolesDecisions.ShouldSuppressReport）。
    /// 恒等返回 shouldEat：需要吃药(true)则抑制上报，等吃完再上报。
    /// </summary>
    public static bool ShouldSuppressReportForMedicine(bool shouldEat) => shouldEat;

    /// <summary>
    /// 吃药时的 fastSyncId 门控：shouldEat 为 true → 返回 null（禁用该点快速抢报，等吃完严格上报）；
    /// shouldEat 为 false → 恒等返回 baseFastSyncId，保持既有 FastSync 路径不变。
    /// </summary>
    public static string? ResolveFastSyncIdForMedicine(string? baseFastSyncId, bool shouldEat)
        => shouldEat ? null : baseFastSyncId;
}
