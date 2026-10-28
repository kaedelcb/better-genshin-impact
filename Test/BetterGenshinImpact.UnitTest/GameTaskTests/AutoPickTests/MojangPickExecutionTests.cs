using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoPick;
using Moq;
using Vanara.PInvoke;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

public sealed class MojangPickExecutionTests
{
    [Fact]
    public void StopAfterFirstPress_BlocksEveryFollowingKeyAndScroll()
    {
        var allowed = true;
        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        keyboard.Setup(x => x.KeyPress(User32.VK.VK_F))
            .Callback(() => allowed = false).Returns(keyboard.Object);
        var channel = CreateChannel(keyboard, new Mock<IMouseInput>(MockBehavior.Strict));
        var resolutions = 0;
        var execution = new MojangPickExecution(() => allowed, () =>
        {
            resolutions++;
            return channel.Object;
        });

        Assert.True(execution.Press(User32.VK.VK_F));
        Assert.False(execution.Press(User32.VK.VK_F));
        Assert.False(execution.ScrollDelta(360));

        Assert.Equal(1, resolutions);
        keyboard.Verify(x => x.KeyPress(User32.VK.VK_F), Times.Once);
    }

    [Fact]
    public void StopAfterFirstScroll_BlocksEveryFollowingScrollAndKey()
    {
        var allowed = true;
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        mouse.Setup(x => x.VerticalScroll(3)).Callback(() => allowed = false).Returns(mouse.Object);
        var channel = CreateChannel(new Mock<IKeyboardInput>(MockBehavior.Strict), mouse);
        var execution = new MojangPickExecution(() => allowed, () => channel.Object);

        Assert.True(execution.ScrollDelta(360));
        Assert.False(execution.ScrollDelta(360));
        Assert.False(execution.Press(User32.VK.VK_F));

        mouse.Verify(x => x.VerticalScroll(3), Times.Once);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StopWhileResolvingInputChannel_PreventsThePendingInput(bool scrolling)
    {
        var allowed = true;
        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        var channel = CreateChannel(keyboard, mouse);
        var execution = new MojangPickExecution(() => allowed, () =>
        {
            allowed = false;
            return channel.Object;
        });

        Assert.False(scrolling ? execution.ScrollDelta(360) : execution.Press(User32.VK.VK_F));
        keyboard.VerifyNoOtherCalls();
        mouse.VerifyNoOtherCalls();
    }

    [Fact]
    public void StopDuringLongWait_ReturnsAtTheNextShortSliceAndBlocksInput()
    {
        var allowed = true;
        var slices = new List<int>();
        var channelResolutions = 0;
        var execution = new MojangPickExecution(() => allowed, () =>
        {
            channelResolutions++;
            throw new InvalidOperationException("停止后不应解析输入通道。");
        }, milliseconds =>
        {
            slices.Add(milliseconds);
            allowed = false;
        });

        Assert.False(execution.Wait(5000));
        Assert.Single(slices);
        Assert.InRange(slices[0], 1, 10);
        Assert.False(execution.Press(User32.VK.VK_F));
        Assert.False(execution.ScrollDelta(360));
        Assert.Equal(0, channelResolutions);
    }

    [Theory]
    [InlineData(360, 3)]
    [InlineData(-360, -3)]
    public void RawWheelDelta_IsConvertedToInputChannelClicks(int delta, int expectedClicks)
    {
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        mouse.Setup(x => x.VerticalScroll(expectedClicks)).Returns(mouse.Object);
        var channel = CreateChannel(new Mock<IKeyboardInput>(MockBehavior.Strict), mouse);
        var execution = new MojangPickExecution(() => true, () => channel.Object);

        Assert.True(execution.ScrollDelta(delta));

        mouse.Verify(x => x.VerticalScroll(expectedClicks), Times.Once);
        mouse.VerifyNoOtherCalls();
        Assert.Equal(360, new AutoPickConfig().ScrollDistance);
    }

    [Fact]
    public void ConsecutiveInputs_ResolveTheCurrentChannelEachTime()
    {
        var firstKeyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        firstKeyboard.Setup(x => x.KeyPress(User32.VK.VK_F)).Returns(firstKeyboard.Object);
        var secondKeyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        secondKeyboard.Setup(x => x.KeyPress(User32.VK.VK_E)).Returns(secondKeyboard.Object);
        var first = CreateChannel(firstKeyboard, new Mock<IMouseInput>(MockBehavior.Strict));
        var second = CreateChannel(secondKeyboard, new Mock<IMouseInput>(MockBehavior.Strict));
        var current = first.Object;
        var execution = new MojangPickExecution(() => true, () => current);

        Assert.True(execution.Press(User32.VK.VK_F));
        current = second.Object;
        Assert.True(execution.Press(User32.VK.VK_E));

        firstKeyboard.Verify(x => x.KeyPress(User32.VK.VK_F), Times.Once);
        firstKeyboard.VerifyNoOtherCalls();
        secondKeyboard.Verify(x => x.KeyPress(User32.VK.VK_E), Times.Once);
        secondKeyboard.VerifyNoOtherCalls();
    }

    [Fact]
    public void ScrollDiagnostics_DistinguishDenialZeroAndSubmission()
    {
        var allowed = false;
        var reports = new List<(int Delta, int? Clicks, MojangScrollOutcome Outcome)>();
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        mouse.Setup(x => x.VerticalScroll(-3)).Returns(mouse.Object);
        var channel = CreateChannel(new Mock<IKeyboardInput>(MockBehavior.Strict), mouse);
        var execution = new MojangPickExecution(() => allowed, () => channel.Object,
            onScroll: (delta, clicks, outcome) => reports.Add((delta, clicks, outcome)));

        Assert.False(execution.ScrollDelta(-360));
        allowed = true;
        Assert.True(execution.ScrollDelta(0));
        Assert.True(execution.ScrollDelta(-360));

        Assert.Equal(new[]
        {
            (-360, (int?)null, MojangScrollOutcome.BlockedBeforeChannel),
            (0, (int?)0, MojangScrollOutcome.ZeroDelta),
            (-360, (int?)-3, MojangScrollOutcome.Submitted)
        }, reports);
        mouse.Verify(x => x.VerticalScroll(-3), Times.Once);
        mouse.VerifyNoOtherCalls();
    }

    [Fact]
    public void ScrollDiagnostics_ChannelResolutionDenialDoesNotReportSubmission()
    {
        var allowed = true;
        var reports = new List<MojangScrollOutcome>();
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        var channel = CreateChannel(new Mock<IKeyboardInput>(MockBehavior.Strict), mouse);
        var execution = new MojangPickExecution(() => allowed, () =>
        {
            allowed = false;
            return channel.Object;
        }, onScroll: (_, _, outcome) => reports.Add(outcome));

        Assert.False(execution.ScrollDelta(360));
        Assert.Equal(new[] { MojangScrollOutcome.BlockedBeforeSend }, reports);
        mouse.VerifyNoOtherCalls();
    }

    [Fact]
    public void ScrollDiagnostics_BackendFailureDoesNotReportSubmission()
    {
        var reports = new List<MojangScrollOutcome>();
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        mouse.Setup(x => x.VerticalScroll(3)).Throws(new InvalidOperationException("backend failed"));
        var channel = CreateChannel(new Mock<IKeyboardInput>(MockBehavior.Strict), mouse);
        var execution = new MojangPickExecution(() => true, () => channel.Object,
            onScroll: (_, _, outcome) => reports.Add(outcome));

        Assert.Throws<InvalidOperationException>(() => execution.ScrollDelta(360));
        Assert.Empty(reports);
    }

    [Theory]
    [InlineData(16, 1)]
    [InlineData(50, 4)]
    [InlineData(64, 4)]
    public void Wait_OversleepCountsTowardTheRequestedTotal(int duration, int expectedSleeps)
    {
        var clock = new MillisecondTimeProvider();
        var slices = new List<int>();
        var execution = new MojangPickExecution(() => true, () => throw new InvalidOperationException("等待不应获取输入通道"),
            sleep: slice =>
            {
                Assert.InRange(slice, 1, 10);
                slices.Add(slice);
                clock.ElapsedMilliseconds += 16; // 模拟短 Sleep 被系统调度延长。
            }, timeProvider: clock);

        Assert.True(execution.Wait(duration));
        Assert.Equal(expectedSleeps, slices.Count);
        Assert.InRange(clock.ElapsedMilliseconds, (long)duration, (long)duration + 15);
    }

    [Fact]
    public void Wait_PermissionCheckTimeAlsoCountsTowardTheRequestedTotal()
    {
        var clock = new MillisecondTimeProvider();
        var slices = new List<int>();
        var execution = new MojangPickExecution(() =>
        {
            clock.ElapsedMilliseconds += 20;
            return true;
        }, () => throw new InvalidOperationException("等待不应获取输入通道"),
            sleep: slice =>
            {
                slices.Add(slice);
                clock.ElapsedMilliseconds += slice;
            }, timeProvider: clock);

        Assert.True(execution.Wait(50));
        Assert.Equal(50, clock.ElapsedMilliseconds);
        Assert.Equal(new[] { 10 }, slices);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Wait_ZeroDurationOnlyChecksPermission(int duration)
    {
        var checks = 0;
        var execution = new MojangPickExecution(() => { checks++; return true; },
            () => throw new InvalidOperationException("等待不应获取输入通道"),
            sleep: _ => throw new InvalidOperationException("零等待不应 Sleep"));

        Assert.True(execution.Wait(duration));
        Assert.Equal(1, checks);
    }

    [Fact]
    public void MissingPickKeyRecovery_MatchesOriginalPositiveTwoClicksAnd50Milliseconds()
    {
        var clock = new MillisecondTimeProvider();
        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        mouse.Setup(x => x.VerticalScroll(2)).Returns(mouse.Object);
        var channel = CreateChannel(keyboard, mouse);
        var reports = new List<(int Delta, int? Clicks, MojangScrollOutcome Outcome)>();
        var execution = new MojangPickExecution(() => true, () => channel.Object,
            sleep: slice => clock.ElapsedMilliseconds += slice,
            onScroll: (delta, clicks, outcome) => reports.Add((delta, clicks, outcome)), timeProvider: clock);

        Assert.True(MojangPickLoop.RecoverMissingPickKey(true, execution));
        Assert.Equal(50, clock.ElapsedMilliseconds);
        Assert.Equal(new[] { (240, (int?)2, MojangScrollOutcome.Submitted) }, reports);
        mouse.Verify(x => x.VerticalScroll(2), Times.Once);
        mouse.VerifyNoOtherCalls();
        keyboard.VerifyNoOtherCalls();
    }

    [Fact]
    public void MissingPickKeyRecovery_WithoutScrollHintDoesNothing()
    {
        var execution = new MojangPickExecution(
            () => throw new InvalidOperationException("无滚轮图标时不应请求输入"),
            () => throw new InvalidOperationException("不应获取输入通道"),
            sleep: _ => throw new InvalidOperationException("不应等待"));

        Assert.False(MojangPickLoop.RecoverMissingPickKey(false, execution));
    }

    [Fact]
    public void MissingPickKeyRecovery_DeniedInputDoesNotScrollOrWait()
    {
        var execution = new MojangPickExecution(() => false,
            () => throw new InvalidOperationException("被拒绝时不应获取输入通道"),
            sleep: _ => throw new InvalidOperationException("被拒绝时不应等待"));

        Assert.False(MojangPickLoop.RecoverMissingPickKey(true, execution));
    }

    [Fact]
    public void MissingPickKeyRecovery_StopAfterScrollDoesNotEnterRecoveryWait()
    {
        var allowed = true;
        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        var mouse = new Mock<IMouseInput>(MockBehavior.Strict);
        mouse.Setup(x => x.VerticalScroll(2)).Callback(() => allowed = false).Returns(mouse.Object);
        var channel = CreateChannel(keyboard, mouse);
        var execution = new MojangPickExecution(() => allowed, () => channel.Object,
            sleep: _ => throw new InvalidOperationException("停止后不应进入等待"));

        Assert.False(MojangPickLoop.RecoverMissingPickKey(true, execution));
        mouse.Verify(x => x.VerticalScroll(2), Times.Once);
        mouse.VerifyNoOtherCalls();
        keyboard.VerifyNoOtherCalls();
    }

    [Fact]
    public void ScrollSearch_RecordedBottomSequenceContinuesUpPastPenultimateItem()
    {
        var state = new MojangScrollSearchState();
        Assert.True(state.SelectUp(631, 1, 0));
        state.RecordSubmitted(true, 631, 0);
        Assert.True(state.SelectUp(559, 1, 100));
        state.RecordSubmitted(true, 559, 100);
        Assert.True(state.SelectUp(487, 1, 200));
        state.RecordSubmitted(true, 487, 200);
        Assert.False(state.SelectUp(415, 1, 300));
    }

    [Fact]
    public void ScrollSearch_ShortListReversesAfterRepeatedUnchangedSubmittedSteps()
    {
        var state = new MojangScrollSearchState();
        state.RecordSubmitted(true, 487, 0);
        state.RecordSubmitted(true, 487, 30);
        state.RecordSubmitted(true, 487, 60);
        Assert.False(state.SelectUp(487, 1, 90));
        state.RecordSubmitted(false, 487, 90);
        Assert.False(state.SelectUp(559, 1, 120));
    }

    [Fact]
    public void ScrollSearch_PlanningWithoutSubmissionDoesNotChangeDirection()
    {
        var state = new MojangScrollSearchState();
        Assert.True(state.SelectUp(631, 1, 0));
        Assert.False(state.SelectUp(559, 1, 100));
        state.RecordSubmitted(true, 631, 100);
        state.ClearProgress();
        Assert.True(state.SelectUp(559, 1, 200));
        state.Reset();
        Assert.False(state.SelectUp(559, 1, 300));
    }

    [Fact]
    public void ScrollSearch_UnchangedFastFramesDoNotImmediatelyReverse()
    {
        var state = new MojangScrollSearchState();
        state.RecordSubmitted(true, 487, 0);
        state.RecordSubmitted(true, 487, 10);
        state.RecordSubmitted(true, 487, 20);
        Assert.True(state.SelectUp(487, 1, 20));
        Assert.False(state.SelectUp(487, 1, 80));
    }

    private sealed class MillisecondTimeProvider : TimeProvider
    {
        public long ElapsedMilliseconds { get; set; }
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => ElapsedMilliseconds;
    }

    private static Mock<IInputChannel> CreateChannel(Mock<IKeyboardInput> keyboard, Mock<IMouseInput> mouse)
    {
        var channel = new Mock<IInputChannel>(MockBehavior.Strict);
        channel.SetupGet(x => x.Keyboard).Returns(keyboard.Object);
        channel.SetupGet(x => x.Mouse).Returns(mouse.Object);
        return channel;
    }
}
