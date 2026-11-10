using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 启动流程「跳转」节点：把空分支接到别处已有的节点（典型场景：右侧「是」分支跳到左侧定时触发器）。
/// 验证约定行为：跳转是链尾节点——执行目标及其所在链的剩余节点后，跳转所在的链结束，外层链继续；
/// 旧配置里跳转后面残留的节点不执行并标为跳过；目标未选/已删除时节点失败、外层链继续；
/// 会再次走到本跳转的目标拒绝执行；同一次执行里同一触发器不重复挂载；触发链单独执行时可跳到整棵树里的节点；
/// 编号按先序且与网页同口径；新字段序列化往返不丢、旧配置缺字段默认空。假执行器与假挂载委托，无真实进程。
/// </summary>
public class StartupFlowJumpTests
{
    private readonly List<string> _logs = new();
    private readonly List<(string StepId, NodeRunState State, string? Note)> _reports = new();
    private readonly List<string> _armedTimers = new();
    private readonly List<string> _armedDogs = new();
    private readonly List<string> _ran = new();

    private StartupFlowRunner MakeRunner(Func<IReadOnlyList<StartupStep>>? root = null, Func<string, bool>? isArmed = null)
    {
        var runner = new StartupFlowRunner(
            (_, _) => Task.FromResult(new CommandResult { Status = "success", Message = "ok" }),
            (_, _) => Task.FromResult(StartupHandoffResult.Accepted("run", null)),
            step => _armedTimers.Add(step.Id),
            (_, _) => Task.FromResult((true, "测试确认")),
            _logs.Add,
            () => null,
            step => _armedDogs.Add(step.Id),
            jumpRoot: root,
            isTriggerArmed: isArmed);
        runner.NodeStateSink = (step, state, note) =>
        {
            _reports.Add((step.Id, state, note));
            if (state == NodeRunState.Running) _ran.Add(step.Id);
        };
        return runner;
    }

    private static StartupStep Wait0(string id) => new() { Id = id, Kind = StartupStepKinds.Wait, WaitSeconds = 0 };
    private static StartupStep Jump(string id, string target) => new() { Id = id, Kind = StartupStepKinds.JumpTo, JumpTargetId = target };
    private static StartupStep Cond(string id, bool pass, List<StartupStep> yes, List<StartupStep> no) => new()
    {
        Id = id, NodeType = "condition", Kind = StartupStepKinds.Weekday,
        Weekdays = pass ? [1, 2, 3, 4, 5, 6, 7] : [],
        TrueSteps = yes, FalseSteps = no,
    };

    private NodeRunState? Last(string id) => _reports.Where(r => r.StepId == id).Select(r => (NodeRunState?)r.State).LastOrDefault();

    [Fact]
    public async Task Jump_RunsTargetTail_ThenReturnsAndContinues()
    {
        // 左支 [a, b]，右支 [跳转→a]，条件后有 tail；走右支：跳转 → a → b → 右支结束 → 外层 tail
        var steps = new List<StartupStep>
        {
            Cond("c", pass: false, yes: [Wait0("a"), Wait0("b")], no: [Jump("j", "a")]),
            Wait0("tail"),
        };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.Equal(["c", "j", "a", "b", "tail"], _ran);
        Assert.Equal(NodeRunState.Success, Last("j"));
        Assert.Equal(NodeRunState.Success, Last("tail"));
    }

    [Fact]
    public async Task StaleNodesAfterJump_AreSkipped_ButOuterChainContinues()
    {
        // 截图场景：右支 [跳转→定时器, 电子狗]（旧配置，电子狗在跳转之后），主链末尾结束流程
        var timer = new StartupStep { Id = "t", Kind = StartupStepKinds.TimerTrigger, TriggerTime = "04:01", FireSteps = [Wait0("f")] };
        var dog = new StartupStep { Id = "d", Kind = StartupStepKinds.Watchdog, WatchKind = StartupStepKinds.BgiTaskRunning };
        var steps = new List<StartupStep>
        {
            Cond("c", pass: false, yes: [timer], no: [Jump("j", "t"), dog]),
            Wait0("tail"),
        };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.Single(_armedTimers);
        Assert.Empty(_armedDogs);
        Assert.Equal(NodeRunState.Skipped, Last("d"));
        Assert.Equal(NodeRunState.Success, Last("tail"));
    }

    [Fact]
    public void JumpAndEndFlow_AreChainTerminals()
    {
        Assert.True(StartupStepKinds.IsChainTerminal(StartupStepKinds.JumpTo));
        Assert.True(StartupStepKinds.IsChainTerminal(StartupStepKinds.EndFlow));
        Assert.False(StartupStepKinds.IsChainTerminal(StartupStepKinds.TimerTrigger));
    }

    [Fact]
    public async Task Jump_ToTimer_ArmsOnce_EvenIfReachedTwice()
    {
        var timer = new StartupStep { Id = "t", Kind = StartupStepKinds.TimerTrigger, TriggerTime = "04:01", FireSteps = [Wait0("f")] };
        // 分支里先挂载定时器，主链再经跳转第二次走到它：只挂一份
        var steps = new List<StartupStep> { Cond("c", pass: true, yes: [timer], no: []), Jump("j", "t") };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.Single(_armedTimers);
        Assert.Contains(_logs, l => l.Contains("已挂载过"));
    }

    [Fact]
    public async Task Jump_ToAlreadyArmedTrigger_FromAnotherRun_DoesNotStackSecondInstance()
    {
        var dog = new StartupStep { Id = "d", Kind = StartupStepKinds.Watchdog, WatchKind = StartupStepKinds.GameRunning };
        var tree = new List<StartupStep> { dog, Wait0("x") };
        var fire = new List<StartupStep> { Jump("j", "d") };
        // 模拟：电子狗已挂着（另一次执行挂的），其触发链跳回挂载它的节点
        await MakeRunner(root: () => tree, isArmed: id => id == "d").RunAsync(fire, CancellationToken.None);

        Assert.Empty(_armedDogs);
        Assert.Contains("x", _ran);
    }

    [Fact]
    public async Task NormalRun_WithoutJump_KeepsExistingMountBehaviour()
    {
        // 未经跳转时不查询宿主是否已挂载（原有「流程重跑会再挂一份」的行为不变）
        var timer = new StartupStep { Id = "t", Kind = StartupStepKinds.TimerTrigger, TriggerTime = "04:01", FireSteps = [Wait0("f")] };
        var queried = false;
        await MakeRunner(isArmed: _ => queried = true).RunAsync([timer], CancellationToken.None);

        Assert.Single(_armedTimers);
        Assert.False(queried);
    }

    [Fact]
    public async Task Jump_FromFireChain_FindsTargetInWholeTree()
    {
        var tree = new List<StartupStep> { Wait0("main1"), Wait0("main2") };
        var fire = new List<StartupStep> { Jump("j", "main2") };
        await MakeRunner(root: () => tree).RunAsync(fire, CancellationToken.None);

        Assert.Equal(["j", "main2"], _ran);
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    public async Task Jump_WithoutValidTarget_FailsNodeAndOuterChainContinues(string target)
    {
        var steps = new List<StartupStep> { Cond("c", pass: true, yes: [Jump("j", target)], no: []), Wait0("tail") };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.Equal(NodeRunState.Failed, Last("j"));
        Assert.Equal(NodeRunState.Success, Last("tail"));
    }

    [Fact]
    public async Task Jump_BackwardsIntoOwnChain_IsRefusedAsLoop()
    {
        // a → 跳转→a 会无限重复 a；拒绝执行，外层链继续
        var steps = new List<StartupStep> { Cond("c", pass: true, yes: [Wait0("a"), Jump("j", "a")], no: []), Wait0("tail") };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.Equal(NodeRunState.Failed, Last("j"));
        Assert.Equal(1, _ran.Count(id => id == "a"));
        Assert.Equal(NodeRunState.Success, Last("tail"));
    }

    [Fact]
    public async Task MutualJumps_AreRefusedWithoutRunaway()
    {
        // 两条分支互相跳：j1→b 的目标链含 j2，j2→a 又会回到 j1 → 在第二跳处拒绝
        var steps = new List<StartupStep>
        {
            Cond("c1", pass: true, yes: [Wait0("a"), Jump("j1", "b")], no: []),
            Cond("c2", pass: false, yes: [], no: [Wait0("b"), Jump("j2", "a")]),
        };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.True(_ran.Count < 20, "跳转不应失控展开");
        Assert.Contains(_reports, r => r.StepId == "j2" && r.State == NodeRunState.Failed);
    }

    [Fact]
    public async Task DisabledJump_IsSkipped()
    {
        var steps = new List<StartupStep> { new() { Id = "j", Kind = StartupStepKinds.JumpTo, JumpTargetId = "x", Enabled = false }, Wait0("x") };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.Equal(NodeRunState.Skipped, Last("j"));
        Assert.Equal(1, _ran.Count(id => id == "x"));
    }

    [Fact]
    public async Task Jump_TargetChainEndFlow_StopsWholeFlow()
    {
        var steps = new List<StartupStep>
        {
            Cond("c", pass: false, yes: [Wait0("a"), new StartupStep { Id = "end", Kind = StartupStepKinds.EndFlow }], no: [Jump("j", "a")]),
            Wait0("tail"),
        };
        await MakeRunner().RunAsync(steps, CancellationToken.None);

        Assert.DoesNotContain("tail", _ran);
    }

    [Fact]
    public void Numbering_IsPreorder_AndIgnoresInactiveChildLists()
    {
        var steps = new List<StartupStep>
        {
            Cond("c", pass: true, yes: [Wait0("y")], no: [Wait0("n")]),
            // 普通动作节点的 FireSteps 不生效，不参与编号（与执行器、网页同口径）
            new() { Id = "w", Kind = StartupStepKinds.Wait, FireSteps = [Wait0("ghost")] },
            new() { Id = "t", Kind = StartupStepKinds.TimerTrigger, FireSteps = [Wait0("f")] },
        };
        var map = StartupStepNumbering.Build(steps);

        Assert.Equal(new[] { "c", "y", "n", "w", "t", "f" }, map.OrderBy(kv => kv.Value).Select(kv => kv.Key));
        Assert.False(map.ContainsKey("ghost"));
        Assert.True(StartupStepNumbering.JumpWouldLoop([Wait0("a"), Jump("j", "a")], "j", "a"));
        Assert.False(StartupStepNumbering.JumpWouldLoop(steps, "j", "y"));
    }

    [Fact]
    public void JumpTargetId_RoundTrips_AndOldConfigDefaultsEmpty()
    {
        var json = JsonSerializer.Serialize(new StartupStep { Kind = StartupStepKinds.JumpTo, JumpTargetId = "abc" });
        Assert.Contains("\"jumpTargetId\":\"abc\"", json);
        Assert.Equal("abc", JsonSerializer.Deserialize<StartupStep>(json)!.JumpTargetId);

        var old = JsonSerializer.Deserialize<StartupStep>("{\"kind\":\"wait\"}")!;
        Assert.Equal("", old.JumpTargetId);
        Assert.NotNull(StartupStepKinds.Find(StartupStepKinds.JumpTo));
    }
}
