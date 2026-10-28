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

    private static Mock<IInputChannel> CreateChannel(Mock<IKeyboardInput> keyboard, Mock<IMouseInput> mouse)
    {
        var channel = new Mock<IInputChannel>(MockBehavior.Strict);
        channel.SetupGet(x => x.Keyboard).Returns(keyboard.Object);
        channel.SetupGet(x => x.Mouse).Returns(mouse.Object);
        return channel;
    }
}
