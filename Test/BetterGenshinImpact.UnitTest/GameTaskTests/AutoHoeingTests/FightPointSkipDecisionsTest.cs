#nullable enable

using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 联机"同伴战斗点级跳过"决策纯函数 PBT（hoeing-multiplayer-route-retry-mode spec §9 / design.md §9.8）。
/// **Validates: requirements.md §9 EB-v3-0（战斗点编码与判定语义）**
///
/// 覆盖列（对应 design.md §9.8）：
///   - PBT-1 Encode 往返一致：任意 (segIdx, wpIdx)，IsMatch(Encode(segIdx,wpIdx), Encode(segIdx,wpIdx)) 恒 true。
///   - PBT-2 段范围：ShouldRecordPendingSkip(fp, curSeg)：fp 段 &gt;= curSeg → true；fp 段 &lt; curSeg → false；fp==-1 → false。
///   - PBT-3 不命中：IsMatch(fp, curFp) 当 fp != curFp → false；fp==-1 → false。
///   - PBT-4 编码唯一性：不同 (segIdx, wpIdx) 对 Encode 结果不同（10000 进制无碰撞）。
///
/// 本文件只读引用 FightPointSkipDecisions，不修改任何生产代码。
/// </summary>
public class FightPointSkipDecisionsTest
{
    // =========================================================================
    // PBT-1: Encode 往返一致
    // =========================================================================

    /// <summary>
    /// 属性 1A：任意非负 (segIdx, wpIdx)，对自身 Encode 结果 IsMatch 恒命中。
    /// 用 NonNegativeInt 收敛域，避免负 wpIdx 产生无效编码 -1（-1 表示"无待跳过点"，本就
    /// 不应命中，属性 3B 已单独守护）。**Validates: design.md §9.8 PBT-1**
    /// </summary>
    [Property(MaxTest = 500)]
    public bool Encode_RoundTrip_IsMatchAlwaysTrue(NonNegativeInt segIdx, NonNegativeInt wpIdx)
    {
        var fp = FightPointSkipDecisions.Encode(segIdx.Get, wpIdx.Get);
        return FightPointSkipDecisions.IsMatch(fp, fp);
    }

    // =========================================================================
    // PBT-2: 段范围 ShouldRecordPendingSkip
    // =========================================================================

    /// <summary>
    /// 属性 2A：任意 (fp, curSeg)，ShouldRecordPendingSkip 精确等价于
    /// "fp != -1 &amp;&amp; (fp/10000) &gt;= curSeg"。覆盖 情况2（已越过→false）与 情况3（未到→true）。
    /// **Validates: design.md §9.3 改动 A 段范围校验 / §9.8 PBT-2**
    /// </summary>
    [Property(MaxTest = 500)]
    public Property ShouldRecordPendingSkip_MatchesSegmentDomain(int fp, int curSeg)
    {
        var actual = FightPointSkipDecisions.ShouldRecordPendingSkip(fp, curSeg);
        var expected = fp != -1 && (fp / 10000) >= curSeg;
        return (actual == expected).ToProperty();
    }

    /// <summary>
    /// 属性 2B：fp==-1 恒不记录（无论当前段）。
    /// **Validates: design.md §9.8 PBT-2**
    /// </summary>
    [Property(MaxTest = 200)]
    public bool ShouldRecordPendingSkip_NegativeOne_NeverRecords(int curSeg)
    {
        return !FightPointSkipDecisions.ShouldRecordPendingSkip(-1, curSeg);
    }

    /// <summary>
    /// 属性 2C：fp 的段索引小于当前段 → false（已越过，情况2）。
    /// **Validates: design.md §9.3 改动 A 段范围校验（情况2）**
    /// </summary>
    [Property(MaxTest = 200)]
    public bool ShouldRecordPendingSkip_PastSegment_ReturnsFalse(NonNegativeInt segIdx, NonNegativeInt pastSegOffset)
    {
        var curSeg = segIdx.Get + pastSegOffset.Get + 1;   // curSeg 严格大于 fp 段
        var fp = FightPointSkipDecisions.Encode(segIdx.Get, 5);
        return !FightPointSkipDecisions.ShouldRecordPendingSkip(fp, curSeg);
    }

    /// <summary>
    /// 属性 2D：fp 的段索引大于等于当前段 → true（未到，情况3）。
    /// curSeg 为当前段，fpSeg = curSeg + ahead（fp 段恒不小于当前段）。
    /// **Validates: design.md §9.3 改动 A 段范围校验（情况3）**
    /// </summary>
    [Property(MaxTest = 200)]
    public bool ShouldRecordPendingSkip_SameOrFutureSegment_ReturnsTrue(NonNegativeInt curSeg, NonNegativeInt fpSegAhead)
    {
        var fpSeg = curSeg.Get + fpSegAhead.Get;          // fp 的段 >= 当前段（未越过或同段）
        var fp = FightPointSkipDecisions.Encode(fpSeg, 5);
        return FightPointSkipDecisions.ShouldRecordPendingSkip(fp, curSeg.Get);
    }

    // =========================================================================
    // PBT-3: 不命中 IsMatch
    // =========================================================================

    /// <summary>
    /// 属性 3A：fp != curFp → false；fp==-1 → false。
    /// **Validates: design.md §9.8 PBT-3**
    /// </summary>
    [Property(MaxTest = 500)]
    public bool IsMatch_DifferentOrNegativeOne_ReturnsFalse(int fp, int curFp)
    {
        if (fp == -1 || fp != curFp) return !FightPointSkipDecisions.IsMatch(fp, curFp);
        return true;   // fp == curFp 且非 -1 的命中情形由 PBT-1 覆盖
    }

    /// <summary>
    /// 属性 3B：fp==-1 时无论 curFp 都不命中。
    /// **Validates: design.md §9.8 PBT-3**
    /// </summary>
    [Property(MaxTest = 200)]
    public bool IsMatch_NegativeOne_NeverMatches(int curFp)
    {
        return !FightPointSkipDecisions.IsMatch(-1, curFp);
    }

    // =========================================================================
    // PBT-4: 编码唯一性（10000 进制无碰撞）
    // =========================================================================

    /// <summary>
    /// 属性 4A：不同 (segIdx, wpIdx) 对（至少一项不同）Encode 结果不同。
    /// wpIdx 用 (x % 10000 + 10000) % 10000 归一化到 [0,10000)，segIdx 非负，避免负数取模
    /// 产生的伪碰撞（C# 的负数取模为负）。**Validates: design.md §9.8 PBT-4**
    /// </summary>
    [Property(MaxTest = 500)]
    public Property Encode_DifferentPairs_AreDistinct(NonNegativeInt segA, NonNegativeInt segB, int wpA, int wpB)
    {
        var wpAConstrained = ((wpA % 10000) + 10000) % 10000;
        var wpBConstrained = ((wpB % 10000) + 10000) % 10000;
        var differentPair = segA.Get != segB.Get || wpAConstrained != wpBConstrained;
        var fa = FightPointSkipDecisions.Encode(segA.Get, wpAConstrained);
        var fb = FightPointSkipDecisions.Encode(segB.Get, wpBConstrained);
        return (!differentPair || fa != fb).ToProperty();
    }

    // =========================================================================
    // 基础单元测试（边界/域约束）
    // =========================================================================

    [Fact]
    public void Encode_ZeroZero_IsZero()
    {
        Assert.Equal(0, FightPointSkipDecisions.Encode(0, 0));
    }

    [Fact]
    public void Encode_Seg0Wp1234_Is1234()
    {
        Assert.Equal(1234, FightPointSkipDecisions.Encode(0, 1234));
    }

    [Fact]
    public void Encode_Seg1Wp0_Is10000()
    {
        Assert.Equal(10000, FightPointSkipDecisions.Encode(1, 0));
    }

    [Fact]
    public void ShouldRecordPendingSkip_NegativeOne_ReturnsFalse()
    {
        Assert.False(FightPointSkipDecisions.ShouldRecordPendingSkip(-1, 0));
    }

    [Fact]
    public void ShouldRecordPendingSkip_SameSegment_ReturnsTrue()
    {
        // fp = seg1 的某个点，curSeg=1（同段）→ true
        Assert.True(FightPointSkipDecisions.ShouldRecordPendingSkip(FightPointSkipDecisions.Encode(1, 3), 1));
    }

    [Fact]
    public void ShouldRecordPendingSkip_PastSegment_ReturnsFalse_Unit()
    {
        // fp = seg1，curSeg=2（已越过 seg1）→ false
        Assert.False(FightPointSkipDecisions.ShouldRecordPendingSkip(FightPointSkipDecisions.Encode(1, 3), 2));
    }

    [Fact]
    public void IsMatch_Equal_ReturnsTrue()
    {
        Assert.True(FightPointSkipDecisions.IsMatch(FightPointSkipDecisions.Encode(2, 7), FightPointSkipDecisions.Encode(2, 7)));
    }

    [Fact]
    public void IsMatch_Different_ReturnsFalse()
    {
        Assert.False(FightPointSkipDecisions.IsMatch(FightPointSkipDecisions.Encode(2, 7), FightPointSkipDecisions.Encode(2, 8)));
    }

    [Fact]
    public void IsMatch_NegativeOne_ReturnsFalse()
    {
        Assert.False(FightPointSkipDecisions.IsMatch(-1, FightPointSkipDecisions.Encode(2, 7)));
    }
}
