#nullable enable

using System;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

/// <summary>
/// FastDragAreaSwitchSkipDecisions 的 PBT 属性测试。
/// **Validates: bugfix.md BC-1~6 (design.md 组件 1+4)**
/// 判据：retryTimes==0 && target!=Teyvat && lastSuccessfulMapName==targetMapName。
/// </summary>
public class FastDragAreaSwitchSkipDecisionsTest
{
    // 地图名池：非提瓦特目标 + Teyvat。空串哨兵表示"已清空/无标记"（跨任务 finally 清空态，BC-1/BC-4）。
    private static readonly string[] LastMapPool =
    [
        "", // 哨兵：无标记（AsNullable → null）
        "TheChasm",
        MapTypes.SeaOfBygoneEras.ToString(),
        MapTypes.Enkanomiya.ToString(),
        MapTypes.Teyvat.ToString(),
        MapTypes.AncientSacredMountain.ToString(),
        MapTypes.MoonCanon.ToString()
    ];

    private static readonly string[] TargetMapPool =
    [
        "TheChasm",
        MapTypes.SeaOfBygoneEras.ToString(),
        MapTypes.Enkanomiya.ToString(),
        MapTypes.Teyvat.ToString(),
        MapTypes.AncientSacredMountain.ToString(),
        MapTypes.MoonCanon.ToString()
    ];

    private static string? AsNullable(string raw) => string.IsNullOrEmpty(raw) ? null : raw;

    private static Gen<int> RetryTimesGen() => Gen.Choose(0, 4);

    private static Gen<string> LastMapGen() => Gen.Elements(LastMapPool);

    private static Gen<string> TargetMapGen() => Gen.Elements(TargetMapPool);

    /// <summary>属性 1（主规格，MaxTest=1000）：判据全等价（含 null 哨兵、Teyvat、异图、重试）。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_EqualsFirstAttemptSameNonTeyvatMap()
    {
        return Prop.ForAll(
            Arb.From(RetryTimesGen()),
            Arb.From(LastMapGen()),
            Arb.From(TargetMapGen()),
            (retryTimes, lastRaw, targetMap) =>
            {
                var lastMap = AsNullable(lastRaw);
                var expected = retryTimes == 0
                               && targetMap != MapTypes.Teyvat.ToString()
                               && string.Equals(lastMap, targetMap, StringComparison.Ordinal);

                return FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes, lastMap, targetMap) == expected;
            });
    }

    /// <summary>属性 2（Preservation / CC2）：重试轮恒不跳过（蕴含用布尔代数表达）。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_RetryNeverSkips()
    {
        return Prop.ForAll(
            Arb.From(RetryTimesGen()),
            Arb.From(LastMapGen()),
            Arb.From(TargetMapGen()),
            (retryTimes, lastRaw, targetMap) =>
            {
                var actual = FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes, AsNullable(lastRaw), targetMap);
                return retryTimes <= 0 || !actual;
            });
    }

    /// <summary>属性 3（BC-1/BC-4）：跨任务清空后（last==null）恒不跳过。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_NullLastMapNeverSkips()
    {
        return Prop.ForAll(
            Arb.From(RetryTimesGen()),
            Arb.From(TargetMapGen()),
            (retryTimes, targetMap) => !FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes, null, targetMap));
    }

    /// <summary>属性 4：异图（last != target）恒不跳过。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_DifferentMapNeverSkips()
    {
        return Prop.ForAll(
            Arb.From(RetryTimesGen()),
            Arb.From(TargetMapGen()),
            (retryTimes, targetMap) =>
            {
                var differentMap = TargetMapPool.First(m => m != targetMap);
                return !FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(retryTimes, differentMap, targetMap);
            });
    }

    /// <summary>属性 5（BC-3/OQ-2）：Teyvat 目标恒不跳过（即使 last==Teyvat）。</summary>
    [Property(MaxTest = 1000)]
    public Property ShouldSkip_TeyvatTargetNeverSkips()
    {
        return Prop.ForAll(
            Arb.From(RetryTimesGen()),
            (retryTimes) => !FastDragAreaSwitchSkipDecisions.ShouldSkipAreaSwitch(
                retryTimes, MapTypes.Teyvat.ToString(), MapTypes.Teyvat.ToString()));
    }
}