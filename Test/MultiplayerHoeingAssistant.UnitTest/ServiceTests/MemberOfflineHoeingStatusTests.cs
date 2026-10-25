using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class MemberOfflineHoeingStatusTests
{
    [Theory]
    [InlineData("completionPending", "联机锄地已完成，后续任务执行中")]
    [InlineData("completionPending", "绑定完成动作待接续：等待连接原 BGI")]
    [InlineData("reconciling", "控制通道暂不可用，保留原任务并继续连接")]
    [InlineData("cleanupPending", "联机锄地已取消，正在确认清理")]
    [InlineData("cancelled", "联机锄地已取消，清理已确认")]
    [InlineData("cancelled", "联机锄地已取消，正在确认清理")]
    [InlineData("completed", "联机锄地已取消，清理已确认")]
    [InlineData("failed", "本机后续任务失败，清理待确认")]
    [InlineData("unknown", "原任务结束状态暂不可读")]
    public void OfflineBatchDoesNotPresentTheLastRunningOrCleanupReportAsCurrent(string phase, string message)
    {
        var member = Member(false, "batch-a", phase, message);

        Assert.Equal(MemberViewModel.PostHoeingOfflineMessage, member.HoeingStartMessage);
        Assert.Contains("结束状态未确认", member.HoeingStartMessage);
        Assert.True(member.IsPostHoeingTaskOffline);
        Assert.False(member.IsPostHoeingTaskRunning);
        Assert.False(member.IsPostHoeingWaitReleased);
        Assert.Equal("batch-a", member.HoeingBatchId);
        Assert.Equal(phase, member.HoeingStartPhase);

        member.Online = true;
        Assert.Equal(message, member.HoeingStartMessage); // The offline view did not rewrite the received report.
        Assert.Equal(phase, member.HoeingStartPhase);
        Assert.False(member.IsPostHoeingTaskOffline);
    }

    [Theory]
    [InlineData(MemberViewModel.PostHoeingCompletedMessage)]
    [InlineData("联机锄地已完整完成")]
    public void ConfirmedWholeCompletionRemainsVisibleAfterDisconnect(string message)
    {
        var member = Member(false, "batch-a", "completed", message);

        Assert.Equal(MemberViewModel.PostHoeingCompletedMessage, member.HoeingStartMessage);
        Assert.Equal("completed", member.HoeingStartPhase);
        Assert.False(member.IsPostHoeingTaskOffline);
        Assert.False(member.IsPostHoeingTaskRunning);
    }

    [Theory]
    [InlineData("completionPending", null, false)]
    [InlineData("completionPending", false, false)]
    [InlineData("completionPending", true, true)]
    [InlineData("offlineCompletion", null, true)]
    [InlineData("offlineCompletion", false, false)]
    [InlineData("offlineCompletion", true, true)]
    public void ReleasedWaitWordingRequiresTheExistingServerReleaseContract(string phase, bool? released, bool expectedReleased)
    {
        var member = Member(false, "batch-a", phase, MemberViewModel.PostHoeingRunningMessage, released);

        Assert.Equal(expectedReleased, member.IsPostHoeingWaitReleased);
        Assert.Equal(expectedReleased ? MemberViewModel.PostHoeingOfflineReleasedMessage
            : MemberViewModel.PostHoeingOfflineMessage, member.HoeingStartMessage);
        Assert.Equal(expectedReleased, member.HoeingStartMessage!.Contains("不影响下次"));
        Assert.Equal(released, member.HoeingCompletionWaitReleased);
    }

    [Fact]
    public void SameIdentifiedBatchKeepsConfirmedCompletionOfflineWithoutReplacingTheOnlineReport()
    {
        var member = Member(true, "batch-a", "completed", MemberViewModel.PostHoeingCompletedMessage);
        const string shutdownReport = "联机锄地已取消，正在确认清理";

        member.ApplyHoeingStatus("batch-a", "cleanupPending", shutdownReport, null);
        Assert.Equal(shutdownReport, member.HoeingStartMessage);
        member.Online = false;

        Assert.Equal(MemberViewModel.PostHoeingCompletedMessage, member.HoeingStartMessage);
        Assert.Equal("cleanupPending", member.HoeingStartPhase);
        Assert.Equal("batch-a", member.HoeingBatchId);
        Assert.False(member.IsPostHoeingTaskOffline);
        member.Online = true;
        Assert.Equal(shutdownReport, member.HoeingStartMessage);

        member.ApplyHoeingStatus("batch-a", "completionPending", MemberViewModel.PostHoeingRunningMessage, false);
        Assert.Equal(MemberViewModel.PostHoeingRunningMessage, member.HoeingStartMessage);
        Assert.True(member.IsPostHoeingTaskRunning);
    }

    [Fact]
    public void NewBatchAndExplicitResetCannotInheritPreviousCompletion()
    {
        var member = Member(true, "batch-a", "completed", MemberViewModel.PostHoeingCompletedMessage);
        member.ApplyHoeingStatus("batch-b", "completionPending", MemberViewModel.PostHoeingRunningMessage, false);
        member.Online = false;
        Assert.Equal(MemberViewModel.PostHoeingOfflineMessage, member.HoeingStartMessage);

        member.Online = true;
        member.ApplyHoeingStatus("batch-b", "completed", MemberViewModel.PostHoeingCompletedMessage, null);
        member.ApplyHoeingStatus("batch-b", "cancelled", "联机状态已重置，可重新开始", null);
        member.Online = false;
        Assert.Equal(MemberViewModel.PostHoeingOfflineMessage, member.HoeingStartMessage);
        Assert.Equal("cancelled", member.HoeingStartPhase);

        member.Online = true;
        member.ApplyHoeingStatus("batch-a", "cancelled", "联机锄地已取消，清理已确认", null);
        member.Online = false;
        Assert.Equal(MemberViewModel.PostHoeingOfflineMessage, member.HoeingStartMessage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void UnidentifiedCompletionIsDisplayedForItsPacketButCannotBeInherited(string? batchId)
    {
        var member = Member(false, batchId, "completed", MemberViewModel.PostHoeingCompletedMessage);
        Assert.Equal(MemberViewModel.PostHoeingCompletedMessage, member.HoeingStartMessage);

        member.ApplyHoeingStatus(batchId, "cancelled", "联机锄地已取消，清理已确认", null);
        Assert.Equal(MemberViewModel.PostHoeingOfflineMessage, member.HoeingStartMessage);

        member.ApplyHoeingStatus(null, null, null, null);
        Assert.Null(member.HoeingStartMessage);
        Assert.False(member.IsPostHoeingTaskOffline);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", "", "")]
    public void OfflineMemberWithoutBatchStatusDoesNotAcquireAWarning(string? batchId, string? phase, string? message)
    {
        var member = Member(false, batchId, phase, message);
        Assert.Equal(message, member.HoeingStartMessage);
        Assert.False(member.IsPostHoeingTaskOffline);
        Assert.False(member.IsPostHoeingTaskRunning);
    }

    [Fact]
    public void ApplyingPacketsAndChangingOnlineStateNotifyTheVisibleCardProperties()
    {
        var member = Member(true, "batch-a", "completionPending", MemberViewModel.PostHoeingRunningMessage);
        var changed = new List<string?>();
        member.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        member.Online = false;
        Assert.Contains(nameof(MemberViewModel.HoeingStartMessage), changed);
        Assert.Contains(nameof(MemberViewModel.IsPostHoeingTaskOffline), changed);
        Assert.Contains(nameof(MemberViewModel.IsPostHoeingTaskRunning), changed);

        changed.Clear();
        member.ApplyHoeingStatus("batch-a", "completed", MemberViewModel.PostHoeingCompletedMessage, true);
        Assert.Contains(nameof(MemberViewModel.HoeingStartMessage), changed);
        Assert.Contains(nameof(MemberViewModel.IsPostHoeingTaskOffline), changed);
        Assert.Contains(nameof(MemberViewModel.IsPostHoeingTaskRunning), changed);
        Assert.Contains(nameof(MemberViewModel.IsPostHoeingWaitReleased), changed);
        Assert.Equal(MemberViewModel.PostHoeingCompletedMessage, member.HoeingStartMessage);
    }

    private static MemberViewModel Member(bool online, string? batchId, string? phase, string? message, bool? released = null)
    {
        var member = new MemberViewModel { Online = online };
        member.ApplyHoeingStatus(batchId, phase, message, released);
        return member;
    }
}
