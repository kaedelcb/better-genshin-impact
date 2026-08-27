#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Feature: multiplayer-hoeing-exp-cap-stop 决策层属性测试。
/// 覆盖 ExpCapDecisions 的连续无经验计数（NextCount）、上报/撤回状态机（NextReportAction）、启用门控（IsEnabled）。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// </summary>
public class ExpCapDecisionsTests
{
    // ========== NextCount：有经验归零 ==========
    [Property(MaxTest = 100)]
    public bool NextCount_HasExp_ResetsToZero(int prev)
    {
        var p = Math.Abs(prev % 100);
        return ExpCapDecisions.NextCount(p, true) == 0;
    }

    // ========== NextCount：无经验 +1 ==========
    [Property(MaxTest = 100)]
    public bool NextCount_NoExp_Increments(int prev)
    {
        var p = Math.Abs(prev % 100);
        return ExpCapDecisions.NextCount(p, false) == p + 1;
    }

    // ========== NextReportAction：仅"未上报+无经验+达阈值"返回 Report ==========
    [Property(MaxTest = 200)]
    public bool NextReportAction_ReportOnlyOnUpwardFlip(int count, bool hasExp, bool reported)
    {
        var c = Math.Abs(count % 10);
        var action = ExpCapDecisions.NextReportAction(c, hasExp, reported);
        var expectReport = !reported && !hasExp && c >= ExpCapDecisions.ConsecutiveNoExpThreshold;
        return (action == ExpCapReportAction.Report) == expectReport;
    }

    // ========== NextReportAction：仅"已上报+有经验"返回 Clear ==========
    [Property(MaxTest = 200)]
    public bool NextReportAction_ClearOnlyOnDownwardFlip(int count, bool hasExp, bool reported)
    {
        var c = Math.Abs(count % 10);
        var action = ExpCapDecisions.NextReportAction(c, hasExp, reported);
        var expectClear = reported && hasExp;
        return (action == ExpCapReportAction.Clear) == expectClear;
    }

    // ========== IsEnabled：配置开 ∧ 已连接 ==========
    [Property(MaxTest = 100)]
    public bool IsEnabled_RequiresBothFlags(bool enable, bool connected)
    {
        return ExpCapDecisions.IsEnabled(enable, connected) == (enable && connected);
    }

    // ========== ShouldForceArm：连续无经验计数达无条件阈值才兜底自点亮 arming ==========
    [Property(MaxTest = 100)]
    public bool ShouldForceArm_MatchesThreshold(int count)
    {
        var c = Math.Abs(count % 20);
        return ExpCapDecisions.ShouldForceArm(c) == (c >= ExpCapDecisions.ConsecutiveNoExpUnconditionalThreshold);
    }

    // ========== ShouldForceArm：边界点（阈值 6）——5 不触发、6 触发、7 触发 ==========
    [Fact]
    public void ShouldForceArm_Boundary()
    {
        Assert.False(ExpCapDecisions.ShouldForceArm(5));
        Assert.True(ExpCapDecisions.ShouldForceArm(6));
        Assert.True(ExpCapDecisions.ShouldForceArm(7));
    }

    // ========== 上报→撤回→再上报 序列（状态机反复，非一次性闩锁）==========
    [Fact]
    public void ReportClearReport_Cycle()
    {
        // 未上报，连续4场无经验（达阈值）→ Report
        Assert.Equal(ExpCapReportAction.Report, ExpCapDecisions.NextReportAction(4, false, false));
        // 未上报，连续3场无经验（未达阈值4）→ None
        Assert.Equal(ExpCapReportAction.None, ExpCapDecisions.NextReportAction(3, false, false));
        // 已上报，又见经验 → Clear
        Assert.Equal(ExpCapReportAction.Clear, ExpCapDecisions.NextReportAction(0, true, true));
        // 未上报，再连续4场无经验 → 再 Report
        Assert.Equal(ExpCapReportAction.Report, ExpCapDecisions.NextReportAction(4, false, false));
        // 已上报，继续无经验 → None（不重复上报）
        Assert.Equal(ExpCapReportAction.None, ExpCapDecisions.NextReportAction(5, false, true));
        // 未上报，有经验但未达阈值 → None
        Assert.Equal(ExpCapReportAction.None, ExpCapDecisions.NextReportAction(0, true, false));
    }

    // ========== NextTwoConsecutiveReportAction：未上报+无经验+≥2 → TwoConsecutiveReport ==========
    [Property(MaxTest = 200)]
    public bool TwoConsecutiveReport_TriggerOnlyOnThreshold(int count, bool hasExp, bool reported)
    {
        var c = Math.Abs(count % 10);
        var action = ExpCapDecisions.NextTwoConsecutiveReportAction(c, hasExp, reported);
        var expectReport = !reported && !hasExp && c >= ExpCapDecisions.TwoConsecutiveNoExpThreshold;
        return (action == ExpCapReportAction.TwoConsecutiveReport) == expectReport;
    }

    // ========== NextTwoConsecutiveReportAction：已上报+有经验 → Clear ==========
    [Property(MaxTest = 200)]
    public bool TwoConsecutiveReport_ClearOnlyOnExp(int count, bool hasExp, bool reported)
    {
        var c = Math.Abs(count % 10);
        var action = ExpCapDecisions.NextTwoConsecutiveReportAction(c, hasExp, reported);
        var expectClear = reported && hasExp;
        return (action == ExpCapReportAction.Clear) == expectClear;
    }

    // ========== NextTwoConsecutiveReportAction 边界：阈值 2 —— 1 不触发、2 触发 ==========
    [Fact]
    public void TwoConsecutiveReport_Boundary()
    {
        Assert.Equal(ExpCapReportAction.None, ExpCapDecisions.NextTwoConsecutiveReportAction(1, false, false));
        Assert.Equal(ExpCapReportAction.TwoConsecutiveReport, ExpCapDecisions.NextTwoConsecutiveReportAction(2, false, false));
        Assert.Equal(ExpCapReportAction.TwoConsecutiveReport, ExpCapDecisions.NextTwoConsecutiveReportAction(3, false, false));
    }

    // ========== NextTwoConsecutiveReportAction 序列：上报→撤回→再上报 ==========
    [Fact]
    public void TwoConsecutiveReport_Cycle()
    {
        // 未上报，连续2场无经验 → TwoConsecutiveReport
        Assert.Equal(ExpCapReportAction.TwoConsecutiveReport, ExpCapDecisions.NextTwoConsecutiveReportAction(2, false, false));
        // 已上报，又见经验 → Clear
        Assert.Equal(ExpCapReportAction.Clear, ExpCapDecisions.NextTwoConsecutiveReportAction(0, true, true));
        // 未上报，再连续2场无经验 → 再 TwoConsecutiveReport
        Assert.Equal(ExpCapReportAction.TwoConsecutiveReport, ExpCapDecisions.NextTwoConsecutiveReportAction(2, false, false));
        // 已上报，继续无经验 → None（不重复上报）
        Assert.Equal(ExpCapReportAction.None, ExpCapDecisions.NextTwoConsecutiveReportAction(3, false, true));
    }
}
