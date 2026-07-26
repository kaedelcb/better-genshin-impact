using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 一次传送同步点的吃药计划（multiplayer-hoeing-auto-eat-food-by-period spec）。
/// 进程内运行时标记，不落盘、不同步；进入新 loading 间隙由 MedicineEatDecisions.BuildPlan 重算覆盖。
/// FoodSlots：本次要吃的所有食物格序号（升序、去重，仅含 1~4 且已到期的行），可能为空。
/// </summary>
public sealed class MedicineEatPlan
{
    /// <summary>本次同步点要吃的所有食物格（序号升序、去重）；可能为空。</summary>
    public IReadOnlyList<int> FoodSlots { get; }

    /// <summary>列表非空即需要吃。</summary>
    public bool ShouldEat => FoodSlots.Count > 0;

    public MedicineEatPlan(IReadOnlyList<int> foodSlots)
    {
        FoodSlots = foodSlots ?? Array.Empty<int>();
    }

    /// <summary>空计划（不吃）。</summary>
    public static MedicineEatPlan Empty { get; } = new(Array.Empty<int>());
}
