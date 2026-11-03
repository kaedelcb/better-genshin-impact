using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[Collection("GlobalUserRoot")]
public sealed class PickupCombatScriptExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringWait_ReleasesOnlyKeysOwnedByThisNodeAndKeepsCancellationReason(bool stopRoute)
    {
        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        var channel = new Mock<IInputChannel>(MockBehavior.Strict);
        var alreadyHeld = User32.VK.VK_W;
        var pickupHeld = User32.VK.VK_R; // 例如 E 在用户配置中映射到 R。
        channel.SetupGet(x => x.Keyboard).Returns(keyboard.Object);
        channel.Setup(x => x.IsKeyDown(alreadyHeld)).Returns(true);
        channel.Setup(x => x.IsKeyDown(pickupHeld)).Returns(false);
        keyboard.Setup(x => x.KeyDown(alreadyHeld)).Returns(keyboard.Object);
        keyboard.Setup(x => x.KeyDown(pickupHeld)).Returns(keyboard.Object);
        keyboard.Setup(x => x.KeyUp(pickupHeld)).Returns(keyboard.Object);

        using var route = new CancellationTokenSource();
        using var reached = new CancellationTokenSource();
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var advanced = false;
        var running = PickupCombatScriptExecutor.RunWithOwnedInputsAsync(channel.Object, route.Token, reached.Token,
            NullLogger.Instance, async (held, stop) =>
        {
            held.Press(alreadyHeld);
            held.Press(pickupHeld);
            waitStarted.SetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), stop);
            advanced = true;
        });

        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (stopRoute) route.Cancel();
        reached.Cancel();

        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(stopRoute ? route.Token : reached.Token, canceled.CancellationToken);
        Assert.False(advanced);
        keyboard.Verify(x => x.KeyUp(pickupHeld), Times.Once);
        keyboard.Verify(x => x.KeyUp(alreadyHeld), Times.Never);
    }

    [Fact]
    public async Task ReachedWhileApproachingDoesNotStartSendingNodeCommands()
    {
        var channel = new Mock<IInputChannel>(MockBehavior.Strict);
        using var reached = new CancellationTokenSource();
        reached.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PickupCombatScriptExecutor.RunWithOwnedInputsAsync(channel.Object, CancellationToken.None, reached.Token,
                NullLogger.Instance, (_, _) => throw new InvalidOperationException("达标后不应开始策略动作")));

        Assert.Equal(reached.Token, error.CancellationToken);
        channel.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidMoveCommandDoesNotAbortRouteUnlessOwnedKeyCannotBeReleased(bool releaseFails)
    {
        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        var channel = new Mock<IInputChannel>(MockBehavior.Strict);
        var key = User32.VK.VK_E;
        channel.SetupGet(x => x.Keyboard).Returns(keyboard.Object);
        channel.Setup(x => x.IsKeyDown(key)).Returns(false);
        keyboard.Setup(x => x.KeyDown(key)).Returns(keyboard.Object);
        var keyUp = keyboard.Setup(x => x.KeyUp(key));
        var releaseError = new InvalidOperationException("松键失败");
        if (releaseFails)
            keyUp.Throws(releaseError);
        else
            keyUp.Returns(keyboard.Object);

        var badCommand = new CombatCommand("万叶", "moveby(foo,0)");
        var running = PickupCombatScriptExecutor.RunWithOwnedInputsAsync(channel.Object, CancellationToken.None,
            CancellationToken.None, NullLogger.Instance, (held, _) =>
            {
                held.Press(key);
                badCommand.Execute(avatar: null!); // 解析坐标时抛错，尚未发送移动操作。
                return Task.CompletedTask;
            });

        if (releaseFails)
        {
            var error = await Assert.ThrowsAsync<AggregateException>(() => running);
            Assert.Same(releaseError, Assert.Single(error.InnerExceptions));
        }
        else
        {
            Assert.False(await running);
        }

        keyboard.Verify(x => x.KeyUp(key), Times.Once);
    }

    [Fact]
    public async Task CommandErrorAtPickupNodeReleasesTheKeyAndRetiresObservationWithoutSkippingTheNextPoint()
    {
        Waypoint[] segment =
        [
            new() { Type = "target", Action = "combat_script", PickupTargetCount = new PickupTarget(99, 1) },
            new(),
            new()
        ];
        var plannedResume = PickupWaypointSkipDecisions.GetResumeIndex(segment, 0, 1);
        Assert.Equal(2, plannedResume); // 达标原本会跳过第一个补捡点。

        var keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
        var channel = new Mock<IInputChannel>(MockBehavior.Strict);
        var key = User32.VK.VK_A;
        channel.SetupGet(x => x.Keyboard).Returns(keyboard.Object);
        channel.Setup(x => x.IsKeyDown(key)).Returns(false);
        var order = new List<string>();
        keyboard.Setup(x => x.KeyDown(key)).Callback(() => order.Add("按下")).Returns(keyboard.Object);
        keyboard.Setup(x => x.KeyUp(key)).Callback(() => order.Add("松开")).Returns(keyboard.Object);

        using var window = new PickupWaypointApproachWindow();
        using var pickup = PickupWaypointProgress.TryBegin(99)!;
        window.Start(0, pickup, plannedResume);
        var badCommand = new CombatCommand("万叶", "moveby(foo,0)");
        var shouldSkip = await CombatScriptHandler.RunPickupCommandsAsync(CancellationToken.None, pickup,
            segment[0], "测试线路 · 第 1 点", keepObservingAfterAction: true, NullLogger.Instance,
            () => PickupCombatScriptExecutor.RunWithOwnedInputsAsync(channel.Object, CancellationToken.None,
                pickup.ReachedToken, NullLogger.Instance, (held, _) =>
                {
                    held.Press(key);
                    badCommand.Execute(avatar: null!);
                    return Task.CompletedTask;
                }));
        order.Add("动作返回");

        Assert.Equal(new[] { "按下", "松开", "动作返回" }, order);
        Assert.False(shouldSkip);
        Assert.Null(PickupWaypointProgress.Capture());
        Assert.False(window.BeginDeparture(0, plannedResume!.Value));
    }

    [Fact]
    public void SupportsSharedArtifactRouteButLeavesUnsupportedCommandsWithLegacyExecutor()
    {
        var sample = CombatScriptParser.ParseContext(
            "keypress(f);万叶 attack(0.08),keydown(E),wait(0.51),keyup(E),attack(0.2),wait(0.5);琴 attack(0.08),keydown(E),wait(0.4),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,-3500),wait(1.8),keyup(E),wait(0.3),click(middle);芙宁娜 e;",
            false);
        var legacy = CombatScriptParser.ParseContext("keypress(f);纳西妲 e(hold);", false);

        Assert.True(PickupCombatScriptExecutor.Supports(sample));
        Assert.False(PickupCombatScriptExecutor.Supports(legacy));
    }
}
