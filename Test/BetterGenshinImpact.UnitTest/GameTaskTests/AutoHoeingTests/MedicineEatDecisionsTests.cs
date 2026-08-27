#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Feature: multiplayer-hoeing-auto-eat-food-by-period 决策层属性测试。
/// 覆盖 MedicineEatDecisions 的周期维度/线路维度/OR 组合/选择/门控/上报抑制/fastSyncId 门控。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// 为避免 DateTime 生成极端值溢出/夏令时干扰，now 统一用固定 UTC 基准，last 由秒偏移构造。
/// </summary>
public class MedicineEatDecisionsTests
{
    private static readonly DateTime Now = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>将任意 int 规约为合法格子序号 1~4。</summary>
    private static int ToSlot(int seed) => Math.Abs(seed % 4) + 1;

    // ========== Property 1：周期为 0 的行不因周期维度进入可吃窗口 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 1
    // Validates: Requirements 1.2
    [Property(MaxTest = 100)]
    public bool Property1_PeriodZero_NeverDueByPeriod(int slotSeed, int lastOffsetSec)
    {
        var slot = ToSlot(slotSeed);
        var row = new MedicineEatDecisions.FoodRow(slot, 0, "");
        DateTime? last = Now.AddSeconds(-Math.Abs(lastOffsetSec % 100000));

        // 周期=0 → IsPeriodDue 恒 false（含 last=null 与有 last）
        var periodDueWithLast = MedicineEatDecisions.IsPeriodDue(row, last, Now);
        var periodDueNoLast = MedicineEatDecisions.IsPeriodDue(row, null, Now);
        // 关键词空 → 线路维度不启用 → IsRowDue 恒 false
        var rowDue = MedicineEatDecisions.IsRowDue(row, last, Now, "anyRoute");
        return !periodDueWithLast && !periodDueNoLast && !rowDue;
    }

    // ========== Property 2：周期维度阈值即完整周期（取消提前量） ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 2
    // Validates: Requirements 2.1, 2.3
    [Property(MaxTest = 100)]
    public bool Property2_ThresholdIsFullPeriod_NoLead(PositiveInt period, uint elapsed)
    {
        var p = period.Get;
        // 约束 elapsed 到合理范围避免 DateTime 溢出
        var elapsedSec = (int)(elapsed % 1_000_000);
        var row = new MedicineEatDecisions.FoodRow(1, p, "");
        var last = Now.AddSeconds(-elapsedSec);
        var actual = MedicineEatDecisions.IsPeriodDue(row, last, Now);
        return actual == (elapsedSec >= p);
    }

    // ========== Property 3：从未吃过立即可吃（周期维度） ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 3
    // Validates: Requirements 2.2
    [Property(MaxTest = 100)]
    public bool Property3_NeverEaten_ImmediatelyDue(int slotSeed, PositiveInt period)
    {
        var slot = ToSlot(slotSeed);
        var row = new MedicineEatDecisions.FoodRow(slot, period.Get, "");
        return MedicineEatDecisions.IsPeriodDue(row, null, Now);
    }

    // ========== Property 4：越界序号不进入可吃窗口 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 4
    // Validates: Requirements 2.4
    [Property(MaxTest = 100)]
    public bool Property4_OutOfRangeSlot_NeverDue(int seed, PositiveInt period, int lastOffsetSec)
    {
        // 构造恒越界序号（>=5）；符号交替也覆盖到负数越界
        var big = Math.Abs(seed % 1000) + 5;      // >= 5 越界
        var neg = -(Math.Abs(seed % 1000) + 1);   // <= -1 越界
        DateTime? last = Now.AddSeconds(-Math.Abs(lastOffsetSec % 100000));

        foreach (var slot in new[] { big, neg })
        {
            var row = new MedicineEatDecisions.FoodRow(slot, period.Get, "Route");
            if (MedicineEatDecisions.IsPeriodDue(row, last, Now)) return false;
            if (MedicineEatDecisions.IsRouteMatched(row, "Mondstadt_Route_1")) return false;
            if (MedicineEatDecisions.IsRowDue(row, last, Now, "Mondstadt_Route_1")) return false;
        }
        return true;
    }

    [Fact]
    public void Property4_FixedOutOfRangeSlots_NeverDue()
    {
        foreach (var slot in new[] { 0, 5, -1, 100 })
        {
            var row = new MedicineEatDecisions.FoodRow(slot, 60, "Route");
            Assert.False(MedicineEatDecisions.IsPeriodDue(row, null, Now));
            Assert.False(MedicineEatDecisions.IsRouteMatched(row, "Some_Route_Name"));
            Assert.False(MedicineEatDecisions.IsRowDue(row, null, Now, "Some_Route_Name"));
        }
    }

    // ========== Property 5：线路命中即吃且无视 CD ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 5
    // Validates: Requirements 12.1, 12.2, 12.4
    [Property(MaxTest = 100)]
    public bool Property5_RouteMatched_AlwaysEat_IgnoresCd(int slotSeed, int lastOffsetSec, int period)
    {
        var slot = ToSlot(slotSeed);
        // period 含 0 与正数（无视 CD 应与周期无关）
        var p = Math.Abs(period % 100000);
        var route = "Mondstadt_Route_" + slot;
        // 关键词 "Route" 是 route 的子串（大小写不敏感 Contains）
        var row = new MedicineEatDecisions.FoodRow(slot, p, "Route");
        // last 任意（含很近，模拟 CD 未到）
        DateTime? last = Now.AddSeconds(-Math.Abs(lastOffsetSec % 100000));
        return MedicineEatDecisions.IsRouteMatched(row, route)
            && MedicineEatDecisions.IsRowDue(row, last, Now, route);
    }

    // ========== Property 6：关键词为空则线路维度不生效 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 6
    // Validates: Requirements 12.3
    [Property(MaxTest = 100)]
    public bool Property6_EmptyKeywords_RouteNeverMatches(int slotSeed, string? routeName, int period)
    {
        var slot = ToSlot(slotSeed);
        var p = Math.Abs(period % 100000);
        var rowEmpty = new MedicineEatDecisions.FoodRow(slot, p, "");
        var rowNull = new MedicineEatDecisions.FoodRow(slot, p, null!);
        return !MedicineEatDecisions.IsRouteMatched(rowEmpty, routeName)
            && !MedicineEatDecisions.IsRouteMatched(rowNull, routeName);
    }

    // ========== Property 7：可吃 = 周期到期 OR 线路命中 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 7
    // Validates: Requirements 12.5
    [Property(MaxTest = 100)]
    public bool Property7_RowDue_IsOrOfPeriodAndRoute(
        int slotSeed, int period, string? keywords, int lastOffsetSec, string? routeName)
    {
        var slot = ToSlot(slotSeed);
        var p = Math.Abs(period % 100000);
        var row = new MedicineEatDecisions.FoodRow(slot, p, keywords!);
        DateTime? last = Now.AddSeconds(-Math.Abs(lastOffsetSec % 100000));
        var expected = MedicineEatDecisions.IsPeriodDue(row, last, Now)
                       || MedicineEatDecisions.IsRouteMatched(row, routeName);
        return MedicineEatDecisions.IsRowDue(row, last, Now, routeName) == expected;
    }

    // ========== Property 8：SelectSlotsToEat 正确性 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 8
    // Validates: Requirements 3.1, 3.2, 3.3, 3.4
    [Property(MaxTest = 100)]
    public bool Property8_SelectSlotsToEat_Correctness(
        int[] slotSeeds, int[] periodSeeds, string?[] keywordSeeds, int[] cdSeeds, string? routeName)
    {
        // 构造 4 行 FoodRow（随机 slot 1~4、period、keywords）
        var rows = new List<MedicineEatDecisions.FoodRow>();
        for (int i = 0; i < 4; i++)
        {
            var slot = ToSlot(GetOr(slotSeeds, i, i));
            var period = Math.Abs(GetOr(periodSeeds, i, 0) % 100000);
            var kw = GetOr<string?>(keywordSeeds, i, "");
            rows.Add(new MedicineEatDecisions.FoodRow(slot, period, kw!));
        }

        // CD 字典（Func）：某些 slot 有 last、某些为 null
        var cdMap = new Dictionary<int, DateTime?>();
        for (int s = 1; s <= 4; s++)
        {
            var raw = GetOr(cdSeeds, s - 1, int.MinValue);
            cdMap[s] = raw == int.MinValue ? (DateTime?)null : Now.AddSeconds(-Math.Abs(raw % 100000));
        }
        Func<int, DateTime?> lastEatBySlot = s => cdMap.TryGetValue(s, out var t) ? t : null;

        var actual = MedicineEatDecisions.SelectSlotsToEat(rows, lastEatBySlot, Now, routeName);

        // 独立算出期望：所有 IsRowDue=true 的行 slot，去重升序
        var expected = rows
            .Where(r => MedicineEatDecisions.IsRowDue(r, lastEatBySlot(r.Slot), Now, routeName))
            .Select(r => r.Slot)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        if (!actual.SequenceEqual(expected)) return false;
        // 结果自身升序无重复
        for (int i = 1; i < actual.Count; i++)
            if (actual[i] <= actual[i - 1]) return false;
        return true;
    }

    private static T GetOr<T>(T[]? arr, int idx, T fallback)
        => arr != null && idx < arr.Length ? arr[idx] : fallback;

    // ========== Property 9：单机与断线门控 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 9
    // Validates: Requirements 4.1, 4.2
    [Property(MaxTest = 100)]
    public bool Property9_SinglePlayerOrDisconnected_NeverEat(
        int[] slotSeeds, int[] periodSeeds, string? routeName, bool connected)
    {
        var rows = new List<MedicineEatDecisions.FoodRow>();
        for (int i = 0; i < 4; i++)
        {
            var slot = ToSlot(GetOr(slotSeeds, i, i));
            var period = Math.Abs(GetOr(periodSeeds, i, 0) % 100000);
            // 给正周期 + 匹配关键词，确保若非门控则可能会吃，从而门控效果可观察
            rows.Add(new MedicineEatDecisions.FoodRow(slot, period, "Route"));
        }
        Func<int, DateTime?> lastEatBySlot = _ => null; // 从未吃过 → 周期维度可吃

        // 单机（任意连接状态）
        var singlePlayer = MedicineEatDecisions.BuildPlan(rows, lastEatBySlot, Now, false, connected, routeName);
        // 联机但断线
        var disconnected = MedicineEatDecisions.BuildPlan(rows, lastEatBySlot, Now, true, false, routeName);
        return !singlePlayer.ShouldEat && !disconnected.ShouldEat;
    }

    // ========== Property 10：吃药抑制上报判定 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 10
    // Validates: Requirements 6.1
    [Property(MaxTest = 100)]
    public bool Property10_SuppressReport_EqualsShouldEat(bool shouldEat)
    {
        return MedicineEatDecisions.ShouldSuppressReportForMedicine(shouldEat) == shouldEat;
    }

    // ========== Property 11：fastSyncId 吃药门控 ==========
    // Feature: multiplayer-hoeing-auto-eat-food-by-period, Property 11
    // Validates: Requirements 11.2
    [Property(MaxTest = 100)]
    public bool Property11_FastSyncId_Gating(string? baseId)
    {
        var whenNotEat = MedicineEatDecisions.ResolveFastSyncIdForMedicine(baseId, false);
        var whenEat = MedicineEatDecisions.ResolveFastSyncIdForMedicine(baseId, true);
        return whenNotEat == baseId && whenEat == null;
    }
}

