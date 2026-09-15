#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.Shared.CooperativeRerun;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 共同重跑：客户端会话 ↔ **真实服务端状态机**的契约集成测试。
///
/// 为什么需要它：只注入固定快照的"单侧假服务端"测试无法发现两端约定不一致——第5轮独立审查发现的
/// 四个阻断全部属于这一类。这里让客户端 `CooperativeRerunSession` 直接对真实的 `RerunExecutionState`
/// 发请求，阶段推进、计划哈希冻结、终态校验都走真实实现；任何一端改了约定都会在这里失败。
///
/// 覆盖：双成员完整成功流程（Enroll→NormalDone→Prepare→逐线路到达/结算→Finish）、
/// 无标记时空计划直接完成、未真实到达就申报完成必须被拒。
/// </summary>
public sealed class CooperativeRerunServerContractTests
{
    static CooperativeRerunServerContractTests()
    {
        var field = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, new BetterGenshinImpact.Core.Config.AllConfig());
    }

    private const string Scope = "1";
    private const string RouteId = "0:route";
    private const string SyncPoint = "s:0:tp";
    private const string FightPoint = "s:0:fight:0";

    private static CooperativeRoutePlan Plan()
    {
        var task = new PathingTask { FileName = "route.json", FullPath = "route.json", Positions = new() };
        var plan = CooperativeRoutePlan.FromTask(
            new RouteInfo { FileName = "route.json", FullPath = "route.json" }, task, 0, 30);
        plan.Manifest.RouteId = RouteId;
        plan.Manifest.Eligible = true;
        plan.Manifest.Checkpoints.Add(new() { Id = SyncPoint, Segment = 0, Kind = "teleport" });
        plan.Manifest.Checkpoints.Add(new() { Id = FightPoint, Segment = 0, Kind = "fight" });
        plan.SyncPointIds[0] = SyncPoint;
        plan.FightPointIds[0] = FightPoint;
        return plan;
    }

    /// <summary>
    /// 构造真实状态机。必须用**当前时间**作起点：状态机按"阶段起点 + 120s"评估准备超时，
    /// 用固定过去时间会让第一个请求就 Abort(PreparationTimeout)。
    /// </summary>
    private static RerunExecutionState State(params string[] uids)
        => new(Scope, worldRound: 0,
            uids.ToDictionary(u => u, u => "conn-" + u, StringComparer.Ordinal), DateTime.UtcNow);

    /// <summary>把网关调用直接转成真实状态机的 Apply；协议拒绝按服务端网关同样形状回错误码。</summary>
    private static void WireToServer(CoordinatorClient client, RerunExecutionState state,
        string uid, List<RerunRequest>? seen = null, Func<DateTime>? clock = null)
    {
        client._testIsConnectedOverride = true;
        typeof(CoordinatorClient).GetField("_isInRoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, true);
        var gateway = client.GetOrCreateGatewayForTest();
        typeof(BgiGatewayClient).GetProperty(nameof(BgiGatewayClient.ServerCapabilities),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(gateway, new[] { RerunProtocol.Capability });
        gateway._testSendOverride = (_, _, _) => Task.CompletedTask;
        gateway._testInvokeOverride = (_, envelope, _) =>
        {
            var request = JsonSerializer.Deserialize<RerunRequest>(
                envelope.Payload!.ToJsonString(), GatewayJson.Options)!;
            seen?.Add(request);
            object payload;
            try
            {
                payload = state.Apply(uid, "conn-" + uid, request, clock?.Invoke() ?? DateTime.UtcNow);
            }
            catch (RerunProtocolException ex)
            {
                payload = new { error = new { code = ex.Code, message = ex.Message } };
            }
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = envelope.Name,
                Payload = GatewayEnvelope.ToPayload(payload),
            });
        };
    }

    /// <summary>等待条件成立（客户端快照由后台轮询更新，断言前必须等它追上服务端）。</summary>
    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("等待条件未成立: " + what);
    }

    /// <summary>两名固定成员从 Enroll 走到 Finish 的完整流程必须真的到达服务端 Completed。</summary>
    [Fact]
    public async Task TwoMembersCompleteFullCooperativeRerunAgainstRealServerState()
    {
        var plan = Plan();
        var state = State("uid-a", "uid-b");
        var clientA = new CoordinatorClient();
        var clientB = new CoordinatorClient();
        var seenA = new List<RerunRequest>();
        WireToServer(clientA, state, "uid-a", seenA);
        WireToServer(clientB, state, "uid-b");

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var a = await CooperativeRerunSession.StartAsync(clientA, new[] { plan }, Scope, bound.Token);
        await using var b = await CooperativeRerunSession.StartAsync(clientB, new[] { plan }, Scope, bound.Token);

        // 正常轮：A 在该战斗点复苏 → 必须可靠登记为该线路需重跑。
        // 死亡标记传的是**规范检查点 ID**（不是共享战斗用的 RouteId/point 复合键）。
        a.CreateContext(plan, isReplay: false).ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => a.Snapshot.Marks.Any(m => m.RouteId == RouteId && !m.IsReplay),
            "正常轮死亡标记进入快照");

        // 阶段推进需要固定全员参与，两端必须并发推进。
        await Task.WhenAll(
            a.CompleteNormalAsync(bound.Token),
            b.CompleteNormalAsync(bound.Token));

        Assert.Equal(RerunStage.Running, a.Snapshot.Stage);
        Assert.Equal(RerunStage.Running, b.Snapshot.Stage);
        Assert.Equal(new[] { RouteId }, a.Snapshot.Plan.ToArray());
        Assert.Equal(a.Snapshot.PlanHash, b.Snapshot.PlanHash);
        Assert.False(string.IsNullOrEmpty(a.Snapshot.PlanHash));

        // Prepare 必须携带服务端冻结的哈希（否则真实服务端会以 rerun_plan_hash 拒绝）。
        var prepareA = seenA.Single(r => r.Operation == RerunProtocol.Prepare);
        Assert.Equal(a.Snapshot.PlanHash, prepareA.PlanHash);

        // 重跑：两端都真实到达规范同步点后，才可能被服务端解析。
        var contextA = a.CreateContext(plan, isReplay: true);
        var contextB = b.CreateContext(plan, isReplay: true);
        await Task.WhenAll(
            contextA.WaitAsync(SyncPoint, bound.Token),
            contextB.WaitAsync(SyncPoint, bound.Token));

        await Task.WhenAll(
            a.CompleteRouteAsync(contextA, RerunRouteOutcome.Completed, bound.Token),
            b.CompleteRouteAsync(contextB, RerunRouteOutcome.Completed, bound.Token));
        await WaitUntilAsync(() => a.Snapshot.Stage == RerunStage.Finishing, "进入收尾阶段");

        // 收尾：神像后固定全员确认收尾，服务端才 Completed。
        await a.FinishAsync(bound.Token);
        await b.FinishAsync(bound.Token);
        await WaitUntilAsync(() => a.Snapshot.Stage == RerunStage.Completed, "阶段完成");
        Assert.Equal(RerunStage.Completed, state.Stage);
    }

    /// <summary>
    /// 仅登记、无任何标记时计划为空：真实服务端直接 Completed，不得进入重跑、也不该去神像。
    /// </summary>
    [Fact]
    public async Task NoMarksProducesEmptyPlanAndCompletesWithoutReplay()
    {
        var plan = Plan();
        var state = State("uid-a");
        var client = new CoordinatorClient();
        WireToServer(client, state, "uid-a");

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, bound.Token);
        var snapshot = await session.CompleteNormalAsync(bound.Token);

        Assert.Equal(RerunStage.Completed, snapshot.Stage);
        Assert.Empty(snapshot.Plan);
        Assert.False(CooperativeRerunTaskDecisions.ShouldStatue(snapshot.Stage, snapshot.Plan.Count));
    }

    /// <summary>
    /// 未真实到达规范同步点就申报 Completed，真实服务端必须拒绝（防止把跳过误报成成功）。
    /// </summary>
    [Fact]
    public async Task CompletedWithoutArrivalIsRejectedByRealServer()
    {
        var plan = Plan();
        var state = State("uid-a");
        var client = new CoordinatorClient();
        WireToServer(client, state, "uid-a");

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, bound.Token);
        var normal = session.CreateContext(plan, isReplay: false);
        normal.ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => session.Snapshot.Marks.Any(m => m.RouteId == RouteId && !m.IsReplay),
            "正常轮死亡标记进入快照");
        await session.CompleteNormalAsync(bound.Token);
        Assert.Equal(RerunStage.Running, session.Snapshot.Stage);

        var replay = session.CreateContext(plan, isReplay: true);
        var ex = await Assert.ThrowsAsync<GatewayErrorException>(
            () => session.CompleteRouteAsync(replay, RerunRouteOutcome.Completed, bound.Token));
        Assert.Equal("rerun_unresolved", ex.Code);
    }

    /// <summary>
    /// 计划里的**每一个**非战斗规范检查点都必须被真实到达（或被明确豁免），
    /// 少一个就不得算作完成——这是"重跑走完整正常线路"在协议层的最小保证。
    /// </summary>
    [Fact]
    public async Task EveryCanonicalCheckpointMustBeArrivedBeforeCompleted()
    {
        // 两个规范同步点的计划（分属两个段）。
        var task = new PathingTask { FileName = "route2.json", FullPath = "route2.json", Positions = new() };
        var plan = CooperativeRoutePlan.FromTask(
            new RouteInfo { FileName = "route2.json", FullPath = "route2.json" }, task, 0, 30);
        plan.Manifest.RouteId = RouteId;
        plan.Manifest.Eligible = true;
        plan.Manifest.Checkpoints.Add(new() { Id = SyncPoint, Segment = 0, Kind = "teleport" });
        plan.Manifest.Checkpoints.Add(new() { Id = "s:1:tp", Segment = 1, Kind = "teleport" });
        plan.Manifest.Checkpoints.Add(new() { Id = FightPoint, Segment = 1, Kind = "fight" });
        plan.SyncPointIds[0] = SyncPoint;
        plan.SyncPointIds[10000] = "s:1:tp";
        plan.FightPointIds[10000] = FightPoint;

        var state = State("uid-a");
        var client = new CoordinatorClient();
        WireToServer(client, state, "uid-a");
        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, bound.Token);

        var normal = session.CreateContext(plan, isReplay: false);
        normal.ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => session.Snapshot.Marks.Any(m => m.RouteId == RouteId && !m.IsReplay), "正常轮标记进入快照");
        await session.CompleteNormalAsync(bound.Token);
        Assert.Equal(RerunStage.Running, session.Snapshot.Stage);

        // 只到达第一个规范点就申报完成 → 必须被真实服务端拒绝。
        var replay = session.CreateContext(plan, isReplay: true);
        await replay.WaitAsync(SyncPoint, bound.Token);
        var rejected = await Assert.ThrowsAsync<GatewayErrorException>(
            () => session.CompleteRouteAsync(replay, RerunRouteOutcome.Completed, bound.Token));
        Assert.Equal("rerun_unresolved", rejected.Code);

        // 补齐第二个规范点后，同一个上下文就能正常完成。
        await replay.WaitAsync("s:1:tp", bound.Token);
        await session.CompleteRouteAsync(replay, RerunRouteOutcome.Completed, bound.Token);
        await WaitUntilAsync(() => session.Snapshot.Stage == RerunStage.Finishing, "补齐后进入收尾");
        Assert.Equal(RerunRouteOutcome.Completed, session.Snapshot.Outcomes[RouteId + "/uid-a"]);
    }

    /// <summary>
    /// 契约要求"可恢复异常记不完整，队伍仍可协调时继续其他计划项"。
    /// 线路提前结束时本机没有逐点豁免记录，若直接提交 Incomplete 会被服务端 rerun_unresolved 拒绝
    /// 并把整轮拖停；客户端必须先补覆盖性豁免，使该线路如实记为不完整、且会话继续推进。
    /// </summary>
    [Fact]
    public async Task IncompleteRouteAutoBypassesUnreachedPointsAndSessionContinues()
    {
        var plan = Plan();
        var state = State("uid-a", "uid-b");
        var clientA = new CoordinatorClient();
        var clientB = new CoordinatorClient();
        WireToServer(clientA, state, "uid-a");
        WireToServer(clientB, state, "uid-b");

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var a = await CooperativeRerunSession.StartAsync(clientA, new[] { plan }, Scope, bound.Token);
        await using var b = await CooperativeRerunSession.StartAsync(clientB, new[] { plan }, Scope, bound.Token);

        a.CreateContext(plan, isReplay: false).ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => a.Snapshot.Marks.Any(m => m.RouteId == RouteId && !m.IsReplay), "正常轮标记进入快照");
        await Task.WhenAll(a.CompleteNormalAsync(bound.Token), b.CompleteNormalAsync(bound.Token));
        Assert.Equal(RerunStage.Running, a.Snapshot.Stage);

        // A 因异常提前结束：从未到达任何规范点，直接申报不完整。
        // 注意：线路推进需要固定全员都提交终态，因此 A 的提交会一直等到 B 提交为止——
        // 必须先起 A 的提交（不 await），再推进 B，最后等 A 返回。
        var contextA = a.CreateContext(plan, isReplay: true);
        var completeA = a.CompleteRouteAsync(contextA, RerunRouteOutcome.Incomplete, bound.Token);

        // B 正常跑完本线路：A 的覆盖性豁免使该同步点对双方都算解决，B 才能继续。
        var contextB = b.CreateContext(plan, isReplay: true);
        await contextB.WaitAsync(SyncPoint, bound.Token);
        await b.CompleteRouteAsync(contextB, RerunRouteOutcome.Completed, bound.Token);
        await completeA;

        // 会话必须继续（进入收尾）而不是被中止；A 的终态如实记为不完整。
        await WaitUntilAsync(() => a.Snapshot.Stage == RerunStage.Finishing, "不完整线路后会话继续推进");
        Assert.True(state.Stage != RerunStage.Aborted);
        var outcomes = a.Snapshot.Outcomes;
        Assert.Equal(RerunRouteOutcome.Incomplete, outcomes[RouteId + "/uid-a"]);
        Assert.Equal(RerunRouteOutcome.Completed, outcomes[RouteId + "/uid-b"]);
    }

    /// <summary>
    /// 已确认需求⑦：重跑期间再次复苏**不得**追加计划项（只重跑一次）。
    /// 用真实服务端验证：重跑期标记只影响本场三态，不进入待重跑集合、不改变已冻结计划。
    /// </summary>
    [Fact]
    public async Task ReplayDeathDoesNotExtendTheFrozenPlan()
    {
        var plan = Plan();
        var state = State("uid-a", "uid-b");
        var clientA = new CoordinatorClient();
        var clientB = new CoordinatorClient();
        WireToServer(clientA, state, "uid-a");
        WireToServer(clientB, state, "uid-b");

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var a = await CooperativeRerunSession.StartAsync(clientA, new[] { plan }, Scope, bound.Token);
        await using var b = await CooperativeRerunSession.StartAsync(clientB, new[] { plan }, Scope, bound.Token);

        a.CreateContext(plan, isReplay: false).ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => a.Snapshot.Marks.Any(m => m.RouteId == RouteId && !m.IsReplay), "正常轮标记进入快照");
        await Task.WhenAll(a.CompleteNormalAsync(bound.Token), b.CompleteNormalAsync(bound.Token));
        Assert.Equal(RerunStage.Running, a.Snapshot.Stage);
        var planBefore = a.Snapshot.Plan.ToArray();
        Assert.Equal(new[] { RouteId }, planBefore);

        // 重跑中 A 再次复苏 → 只产生一个重跑期标记。
        var contextA = a.CreateContext(plan, isReplay: true);
        contextA.ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => a.Snapshot.Marks.Any(m => m.RouteId == RouteId && m.IsReplay), "重跑期标记进入快照");

        // 计划必须保持冻结不变（不追加、不重复），阶段仍是同一轮重跑的 Running。
        Assert.Equal(RouteId, a.Snapshot.CurrentRouteId);
        Assert.Equal(planBefore, a.Snapshot.Plan.ToArray());
        Assert.Equal(0, a.Snapshot.CurrentPlanIndex);
    }

    /// <summary>
    /// 已确认需求：成员失联后不得静默缩人数继续，也不得让其余成员无限等待——
    /// 固定名册的租约到期必须把阶段统一置为中止，使其余成员能在游戏循环里立刻看到并停止动作。
    /// </summary>
    [Fact]
    public async Task SilentMemberLeaseExpiryAbortsStageAndIsVisibleToOthers()
    {
        var plan = Plan();
        var clock = DateTime.UtcNow;
        var state = new RerunExecutionState(Scope, worldRound: 0,
            new Dictionary<string, string> { ["uid-a"] = "conn-uid-a", ["uid-b"] = "conn-uid-b" }, clock);
        var clientA = new CoordinatorClient();
        var clientB = new CoordinatorClient();
        WireToServer(clientA, state, "uid-a", clock: () => clock);
        WireToServer(clientB, state, "uid-b", clock: () => clock);

        using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var a = await CooperativeRerunSession.StartAsync(clientA, new[] { plan }, Scope, bound.Token);
        await using var b = await CooperativeRerunSession.StartAsync(clientB, new[] { plan }, Scope, bound.Token);

        a.CreateContext(plan, isReplay: false).ReportDeath(FightPoint);
        await WaitUntilAsync(
            () => a.Snapshot.Marks.Any(m => m.RouteId == RouteId && !m.IsReplay), "正常轮标记进入快照");
        await Task.WhenAll(a.CompleteNormalAsync(bound.Token), b.CompleteNormalAsync(bound.Token));
        Assert.Equal(RerunStage.Running, a.Snapshot.Stage);
        Assert.False(a.CreateContext(plan, isReplay: true).IsAborted);

        // B 从此不再发任何请求（进程卡死/断线），时间推进超过成员租约上界。
        clock = clock.AddSeconds(60);
        await a.SendAsync(RerunProtocol.Poll, ct: bound.Token);
        await WaitUntilAsync(() => a.Snapshot.Stage == RerunStage.Aborted, "租约到期后阶段中止");

        var context = a.CreateContext(plan, isReplay: true);
        Assert.True(context.IsAborted, "中止必须对仍在执行的成员可见，从而立即停止游戏动作");
        // 中止是终态：迟到的完成尝试既不会生效，也不会被静默当成成功——
        // 服务端在 Aborted 阶段直接返回快照（不执行该操作），客户端随即暴露中止原因。
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => a.CompleteRouteAsync(context, RerunRouteOutcome.Completed, bound.Token));
        Assert.Contains("ParticipantLeaseExpired", ex.Message, StringComparison.Ordinal);
        Assert.Equal(RerunStage.Aborted, a.Snapshot.Stage);
    }
}
