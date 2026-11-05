using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.Service;
using System.Reflection;
using System.Runtime.CompilerServices;
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
    public void SupportsTheUnmodifiedLatestArtifactRoute()
    {
        var script = CombatScriptParser.ParseContext(
            "keypress(x),wait(0.2),attack(0.03);万叶 attack(0.08),keydown(E),wait(0.7),keyup(E),attack(0.2);琴 attack(0.08),keydown(E),wait(0.4),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,0),wait(0.2),moveby(1000,-3500),wait(1.8),keyup(E),wait(0.3),click(middle);",
            false);

        Assert.Equal(CombatScriptParser.CurrentAvatarName, script.CombatCommands[2].Name);
        Assert.True(PickupCombatScriptExecutor.Supports(script, out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("attack")]
    [InlineData("attack(0.03)")]
    [InlineData("keypress(X)")]
    [InlineData("keypress(VK_X)")]
    public void AcceptsTheNewlySupportedForms(string text)
    {
        Assert.True(PickupCombatScriptExecutor.Supports(CombatScriptParser.ParseContext(text, false), out var reason));
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("keypress(q)", "第 1 条", "keypress(q)")]
    [InlineData("keypress(x);纳西妲 e(hold)", "第 2 条", "hold")]
    [InlineData("阿蕾奇诺 attack(0.2)", "第 1 条", "阿蕾奇诺")]
    [InlineData("wait(-1)", "第 1 条", "wait(-1)")]
    public void UnsupportedCommandReportsItsPositionAndContent(string text, string position, string content)
    {
        Assert.False(PickupCombatScriptExecutor.Supports(CombatScriptParser.ParseContext(text, false), out var reason));
        Assert.Contains(position, reason);
        Assert.Contains(content, reason);
    }

    [Fact]
    public void CurrentAvatarAttackKeepsTheExistingSpecialComboSafetyFallback()
    {
        using var runtime = new CommandRuntime("阿蕾奇诺");
        runtime.Avatar.ArlecchinoAutoEnabled = true;
        var script = CombatScriptParser.ParseContext("keypress(x);attack(0.03)", false);

        Assert.False(PickupCombatScriptExecutor.Supports(script, out var reason, runtime.Scenes));
        Assert.Contains("第 2 条", reason);
        Assert.Contains("特殊普攻", reason);
        runtime.Avatar.ArlecchinoAutoEnabled = false;
        Assert.True(PickupCombatScriptExecutor.Supports(script, out _, runtime.Scenes));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KeypressXInTheRealCommandLoopStopsBeforeFollowingCommandsAndReleasesHeldKeys(bool stopRoute)
    {
        using var runtime = new CommandRuntime();
        using var route = new CancellationTokenSource();
        using var reached = new CancellationTokenSource();
        runtime.Channel.Setup(x => x.IsKeyDown(User32.VK.VK_E)).Returns(false);
        runtime.Keyboard.Setup(x => x.KeyDown(User32.VK.VK_E)).Returns(runtime.Keyboard.Object);
        runtime.Keyboard.Setup(x => x.KeyUp(User32.VK.VK_E)).Returns(runtime.Keyboard.Object);
        runtime.Keyboard.Setup(x => x.KeyPress(User32.VK.VK_X)).Callback(() =>
        {
            if (stopRoute) route.Cancel();
            reached.Cancel();
        }).Returns(runtime.Keyboard.Object);
        var script = CombatScriptParser.ParseContext("万叶 keydown(E),keypress(x),keydown(W);", false);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PickupCombatScriptExecutor.ExecuteAsync(script, runtime.Scenes, route.Token, reached.Token, NullLogger.Instance)
                .WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(stopRoute ? route.Token : reached.Token, error.CancellationToken);
        runtime.Keyboard.Verify(x => x.KeyPress(User32.VK.VK_X), Times.Once);
        runtime.Keyboard.Verify(x => x.KeyUp(User32.VK.VK_E), Times.Once);
        runtime.Keyboard.Verify(x => x.KeyDown(User32.VK.VK_W), Times.Never);
    }

    [Fact]
    public async Task ResolvedCurrentAvatarAttackCancelsItsLongWaitAndReleasesOnlyOwnedKeys()
    {
        using var runtime = new CommandRuntime();
        using var reached = new CancellationTokenSource();
        var owned = User32.VK.VK_E;
        var foreign = User32.VK.VK_A;
        runtime.Channel.Setup(x => x.IsKeyDown(owned)).Returns(false);
        runtime.Channel.Setup(x => x.IsKeyDown(foreign)).Returns(true);
        runtime.Keyboard.Setup(x => x.KeyDown(owned)).Returns(runtime.Keyboard.Object);
        runtime.Keyboard.Setup(x => x.KeyDown(foreign)).Returns(runtime.Keyboard.Object);
        runtime.Keyboard.Setup(x => x.KeyUp(owned)).Returns(runtime.Keyboard.Object);
        runtime.Channel.Setup(x => x.SimulateAction(GIActions.NormalAttack, KeyType.KeyPress))
            .Callback(() => reached.Cancel()).Returns(runtime.Channel.Object);
        var command = Assert.Single(CombatScriptParser.ParseContext("attack(30)", false).CombatCommands);
        Assert.Equal(CombatScriptParser.CurrentAvatarName, command.Name);

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PickupCombatScriptExecutor.RunWithOwnedInputsAsync(runtime.Channel.Object, CancellationToken.None,
                reached.Token, NullLogger.Instance, async (held, stop) =>
                {
                    held.Press(owned);
                    held.Press(foreign);
                    await PickupCombatScriptExecutor.ExecuteCommandAsync(command, runtime.Avatar, held, stop);
                }).WaitAsync(TimeSpan.FromSeconds(3)));

        Assert.Equal(reached.Token, error.CancellationToken);
        runtime.Channel.Verify(x => x.SimulateAction(GIActions.NormalAttack, KeyType.KeyPress), Times.Once);
        runtime.Keyboard.Verify(x => x.KeyUp(owned), Times.Once);
        runtime.Keyboard.Verify(x => x.KeyUp(foreign), Times.Never);
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

    private sealed class CommandRuntime : IDisposable
    {
        private static readonly PropertyInfo ConfigProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;
        private static readonly FieldInfo BackendField = typeof(InputHub).GetField("_backend", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly object? _originalConfig;
        private readonly object? _originalBackend;

        internal CommandRuntime(string name = "枫原万叶")
        {
            _originalConfig = ConfigProperty.GetValue(null);
            ConfigProperty.SetValue(null, new AllConfig());
            Keyboard = new Mock<IKeyboardInput>(MockBehavior.Strict);
            Channel = new Mock<IInputChannel>(MockBehavior.Strict);
            Channel.SetupGet(x => x.Keyboard).Returns(Keyboard.Object);
            var backend = new Mock<IInputBackend>(MockBehavior.Strict);
            backend.SetupGet(x => x.Foreground).Returns(Channel.Object);
            _originalBackend = BackendField.GetValue(null);
            BackendField.SetValue(null, backend.Object);
            Avatar = (Avatar)RuntimeHelpers.GetUninitializedObject(typeof(Avatar));
            Avatar.Name = name;
            Scenes = (CombatScenes)RuntimeHelpers.GetUninitializedObject(typeof(CombatScenes));
            Scenes.Avatars = [Avatar];
            Avatar.CombatScenes = Scenes;
        }

        internal Avatar Avatar { get; }
        internal CombatScenes Scenes { get; }
        internal Mock<IKeyboardInput> Keyboard { get; }
        internal Mock<IInputChannel> Channel { get; }

        public void Dispose()
        {
            BackendField.SetValue(null, _originalBackend);
            ConfigProperty.SetValue(null, _originalConfig);
        }
    }

}
