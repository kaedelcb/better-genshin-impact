using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class MultiWorldGuardResumeProgressTests
{
    [Fact]
    public void RestartSnapshotContainsOnlyConfirmedWorlds_AndPreservesFullPlan()
    {
        var initial = MultiWorldGuardResumeProgress.Create("a|b|c|d", 4, new[] { "a", "b", "c", "d" });
        var restart = initial.ConfirmCompleted("a").ConfirmCompleted("b").ConfirmCompleted("c");
        Assert.Empty(initial.CompletedForReport("a|b|c|d", 4, true));
        Assert.Equal(new[] { "a", "b", "c" }, restart.CompletedForReport("a|b|c|d", 4, true).OrderBy(x => x));
        Assert.Equal(4, restart.PlannedHostUids.Count); // 剩一个世界，也仍保留原计划，不能变成一人组队。
        Assert.DoesNotContain("d", restart.CompletedForReport("a|b|c|d", 4, true));
    }

    [Theory]
    [InlineData(false, "a|b", 2)]
    [InlineData(true, "a|c", 2)]
    [InlineData(true, "a|b", 1)]
    [InlineData(true, "", 2)]
    public void DisabledChangedRosterOrRoundLimit_DoesNotSkipWorlds(bool enabled, string signature, int rounds)
    {
        var progress = MultiWorldGuardResumeProgress.Create("a|b", 2, new[] { "a", "b" }).ConfirmCompleted("a");
        Assert.Empty(progress.CompletedForReport(signature, rounds, enabled));
    }

    [Fact]
    public void TwoRoundPlan_DoesNotAdmitThirdWorldAfterCropping()
    {
        var progress = MultiWorldGuardResumeProgress.Create("a|b|c|d", 2, new[] { "a", "b", "c", "d" })
            .ConfirmCompleted("a").ConfirmCompleted("outside");
        Assert.Equal(new[] { "a", "b" }, progress.PlannedHostUids.OrderBy(x => x));
        Assert.Equal(new[] { "a" }, progress.CompletedForReport("a|b|c|d", 2, true));
    }

    [Fact]
    public void NewTaskNeverInheritsOtherTaskOrDateProgress()
    {
        var old = MultiWorldGuardResumeProgress.Create("a|b", 2, new[] { "a", "b" }).ConfirmCompleted("a");
        var fresh = MultiWorldGuardResumeProgress.Create("a|b", 2, new[] { "a", "b" });
        Assert.Single(old.CompletedForReport("a|b", 2, true));
        Assert.Empty(fresh.CompletedForReport("a|b", 2, true));
    }

    [Fact]
    public void ReportedCollectionCannotMutateTheRestartSnapshot()
    {
        var progress = MultiWorldGuardResumeProgress.Create("a|b", 2, new[] { "a", "b" }).ConfirmCompleted("a");
        ((HashSet<string>)progress.CompletedForReport("a|b", 2, true)).Clear();
        Assert.Single(progress.CompletedForReport("a|b", 2, true));
    }
}
