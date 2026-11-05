using BetterGenshinImpact.Core.Input;
using Moq;
using Vanara.PInvoke;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[Collection("GlobalUserRoot")]
public sealed class PickupWaypointInputScopeTests
{
    [Fact]
    public void CancellationImmediatelyReleasesOwnedKeyboardAndMouseWithoutReleasingForeignKeys()
    {
        var input = new FakeInput();
        input.Pressed.Add(User32.VK.VK_W);
        using var stop = new CancellationTokenSource();
        var original = InputHub.Foreground;
        using (InputHub.BeginOperation(stop.Token, input.Channel.Object))
        {
            InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_W);
            InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_A);
            InputHub.Foreground.Mouse.LeftButtonDown();

            stop.Cancel();

            Assert.Contains(User32.VK.VK_W, input.Pressed);
            Assert.DoesNotContain(User32.VK.VK_A, input.Pressed);
            Assert.DoesNotContain(User32.VK.VK_LBUTTON, input.Pressed);
            Assert.Equal(1, input.UpCount(User32.VK.VK_A));
            Assert.Equal(1, input.UpCount(User32.VK.VK_LBUTTON));
            Assert.Equal(0, input.UpCount(User32.VK.VK_W));
            var error = Assert.Throws<OperationCanceledException>(
                () => InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_D));
            Assert.Equal(stop.Token, error.CancellationToken);
        }

        Assert.Same(original, InputHub.Foreground);
        Assert.Null(InputHub.CurrentOperationToken);
    }

    [Fact]
    public async Task SynchronousWaitIsInterruptedByTheNodeToken()
    {
        var input = new FakeInput();
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(() =>
        {
            using var scope = InputHub.BeginOperation(stop.Token, input.Channel.Object);
            started.SetResult();
            InputHub.WaitInOperation(30_000);
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => worker.WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.Empty(input.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsynchronousWaitPreservesBothNodeAndIndependentCancellation(bool cancelNode)
    {
        var input = new FakeInput();
        using var node = new CancellationTokenSource();
        using var independent = new CancellationTokenSource();
        using var scope = InputHub.BeginOperation(node.Token, input.Channel.Object);
        var waiting = InputHub.DelayInOperation(30_000, independent.Token);

        if (cancelNode) node.Cancel();
        else independent.Cancel();

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(cancelNode ? node.Token : independent.Token, error.CancellationToken);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedScopesDoNotDoubleOwnInputsAndKeepParentCancellation(bool cancelParent)
    {
        var input = new FakeInput();
        using var parentStop = new CancellationTokenSource();
        using var childStop = new CancellationTokenSource();
        using var parent = InputHub.BeginOperation(parentStop.Token, input.Channel.Object);
        InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_E);
        using (InputHub.BeginOperation(childStop.Token, InputHub.Foreground))
        {
            InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_A);
            if (cancelParent) parentStop.Cancel();
            else childStop.Cancel();

            var error = Assert.Throws<OperationCanceledException>(() => InputHub.WaitInOperation(30_000));
            Assert.Equal(cancelParent ? parentStop.Token : childStop.Token, error.CancellationToken);
            Assert.DoesNotContain(User32.VK.VK_A, input.Pressed);
            Assert.Equal(1, input.UpCount(User32.VK.VK_A));
            Assert.Equal(cancelParent ? 1 : 0, input.UpCount(User32.VK.VK_E));
        }

        if (!cancelParent)
        {
            Assert.Equal(parentStop.Token, InputHub.CurrentOperationToken);
            Assert.Contains(User32.VK.VK_E, input.Pressed);
            parentStop.Cancel();
        }
        Assert.Equal(1, input.UpCount(User32.VK.VK_E));
    }

    [Fact]
    public async Task AClosedScopeStopsDetachedInputsButKeepsReadOnlyBackgroundWaiting()
    {
        var input = new FakeInput();
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task background;
        using (InputHub.BeginOperation(CancellationToken.None, input.Channel.Object))
        {
            background = Task.Run(async () =>
            {
                await resume.Task;
                Assert.Null(InputHub.CurrentOperationToken);
                await InputHub.DelayInOperation(1);
                Assert.Throws<OperationCanceledException>(() =>
                    InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_A));
            });
        }

        resume.SetResult();
        await background.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(input.Events);
    }

    [Fact]
    public void CancellationInsideAnAtomicPressDoesNotSendADuplicateKeyUp()
    {
        var input = new FakeInput();
        using var stop = new CancellationTokenSource();
        input.Keyboard.Setup(x => x.KeyPress(User32.VK.VK_X))
            .Callback(() => stop.Cancel()).Returns(input.Keyboard.Object);
        using var scope = InputHub.BeginOperation(stop.Token, input.Channel.Object);
        InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_E);

        Assert.Throws<OperationCanceledException>(() => InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_X));

        Assert.Equal(1, input.UpCount(User32.VK.VK_E));
        Assert.Equal(0, input.UpCount(User32.VK.VK_X));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeTheUnderlyingDownTakesEffectStillReleasesItAfterSending(bool mouseButton)
    {
        var input = new FakeInput();
        using var stop = new CancellationTokenSource();
        var key = mouseButton ? User32.VK.VK_LBUTTON : User32.VK.VK_A;
        var foreign = User32.VK.VK_W;
        input.Pressed.Add(foreign);
        Action sendDown = () =>
        {
            // 特意在模拟的按下生效之前取消，确定性覆盖同步重入顺序。
            stop.Cancel();
            input.Events.Add(("down", key));
            input.Pressed.Add(key);
        };
        if (mouseButton)
            input.Mouse.Setup(x => x.LeftButtonDown()).Callback(sendDown).Returns(input.Mouse.Object);
        else
            input.Keyboard.Setup(x => x.KeyDown(key)).Callback(sendDown).Returns(input.Keyboard.Object);

        using var scope = InputHub.BeginOperation(stop.Token, input.Channel.Object);
        var error = Assert.Throws<OperationCanceledException>(() =>
        {
            if (mouseButton) InputHub.Foreground.Mouse.LeftButtonDown();
            else InputHub.Foreground.Keyboard.KeyDown(key);
        });

        Assert.Equal(stop.Token, error.CancellationToken);
        Assert.DoesNotContain(key, input.Pressed);
        Assert.Contains(foreign, input.Pressed);
        Assert.Equal(new[] { ("down", key), ("up", key) }, input.Events);
        Assert.Equal(0, input.UpCount(foreign));
    }

    [Fact]
    public async Task AClosedScopeCannotRetryFailedReleaseThroughEitherPublicReleaseAllEntry()
    {
        var input = new FakeInput();
        var releaseError = new InvalidOperationException("测试首次松键失败");
        var attempts = 0;
        input.Keyboard.Setup(x => x.KeyUp(User32.VK.VK_E))
            .Callback(() => attempts++).Throws(releaseError);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = InputHub.BeginOperation(CancellationToken.None, input.Channel.Object);
        var oldChannel = InputHub.Foreground;
        oldChannel.Keyboard.KeyDown(User32.VK.VK_E);
        var background = Task.Run(async () =>
        {
            await resume.Task;
            Assert.Throws<OperationCanceledException>(() => InputHub.ReleaseAll());
        });

        Assert.Throws<AggregateException>(() => scope.Dispose());
        Assert.Equal(1, attempts);
        // 现在底层可以松键了，但旧作用域不得在下一节点运行时重试旧输入。
        input.Keyboard.Setup(x => x.KeyUp(User32.VK.VK_E))
            .Callback(() => attempts++).Returns(input.Keyboard.Object);
        Assert.Throws<OperationCanceledException>(() => oldChannel.ReleaseAll());
        resume.SetResult();
        await background.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void ReleaseFailureDoesNotEscapeCancellationCallbackAndIsReportedByDispose()
    {
        var input = new FakeInput();
        using var stop = new CancellationTokenSource();
        var releaseError = new InvalidOperationException("测试松键失败");
        input.Keyboard.Setup(x => x.KeyUp(User32.VK.VK_E)).Throws(releaseError);
        var original = InputHub.Foreground;
        var scope = InputHub.BeginOperation(stop.Token, input.Channel.Object);
        InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_E);

        Assert.Null(Record.Exception(() => stop.Cancel()));
        var error = Assert.Throws<AggregateException>(() => scope.Dispose());

        Assert.All(error.InnerExceptions, value => Assert.Same(releaseError, value));
        Assert.Same(original, InputHub.Foreground);
        Assert.Null(InputHub.CurrentOperationToken);
    }

    private sealed class FakeInput
    {
        internal FakeInput()
        {
            Channel.SetupGet(x => x.Keyboard).Returns(Keyboard.Object);
            Channel.SetupGet(x => x.Mouse).Returns(Mouse.Object);
            Channel.Setup(x => x.IsKeyDown(It.IsAny<User32.VK>())).Returns((User32.VK key) => Pressed.Contains(key));
            Keyboard.Setup(x => x.KeyDown(It.IsAny<User32.VK>())).Callback((User32.VK key) =>
            {
                Events.Add(("down", key));
                Pressed.Add(key);
            }).Returns(Keyboard.Object);
            Keyboard.Setup(x => x.KeyUp(It.IsAny<User32.VK>())).Callback((User32.VK key) =>
            {
                Events.Add(("up", key));
                Pressed.Remove(key);
            }).Returns(Keyboard.Object);
            Keyboard.Setup(x => x.KeyPress(It.IsAny<User32.VK>()))
                .Returns(Keyboard.Object);
            Mouse.Setup(x => x.LeftButtonDown()).Callback(() =>
            {
                Events.Add(("down", User32.VK.VK_LBUTTON));
                Pressed.Add(User32.VK.VK_LBUTTON);
            }).Returns(Mouse.Object);
        }

        internal Mock<IInputChannel> Channel { get; } = new(MockBehavior.Strict);
        internal Mock<IKeyboardInput> Keyboard { get; } = new(MockBehavior.Strict);
        internal Mock<IMouseInput> Mouse { get; } = new(MockBehavior.Strict);
        internal HashSet<User32.VK> Pressed { get; } = [];
        internal List<(string Action, User32.VK Key)> Events { get; } = [];
        internal int UpCount(User32.VK key) => Events.Count(value => value == ("up", key));
    }
}
