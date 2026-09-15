#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.Shared.CooperativeRerun;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 联机锄地共同重跑：客户端契约与故障路径测试（只读验证生产行为，不改生产代码）。
///
/// 覆盖上一批未覆盖的四类缺口：
/// 1) 真实 room.closed 事件导致的严格等待（修复前会把取消误判为“同步成功”）；
/// 2) Recover / Bypass 三态在请求线上的形状与“Recover 不豁免”语义；
/// 3) 快照身份/修订校验（旧 revision、他人 session、他人 scope 不得污染本地状态）；
/// 4) 按点停战开关的阶段匹配与一次性完成，以及 Finish 的阶段收口。
/// </summary>
public sealed class CooperativeRerunContractTests
{
    /// <summary>
    /// PathingTaskInfo 构造会读 TaskContext.Config（运行期配置服务），单测环境没有它。
    /// 注入一个默认 AllConfig，使冻结计划可在纯单测中构造（与 CooperativeRoutePlanTests 同法）。
    /// </summary>
    static CooperativeRerunContractTests()
    {
        var field = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        field?.SetValue(null, new BetterGenshinImpact.Core.Config.AllConfig());
    }

    private const string Scope = "round-1";
    private const string FightPoint = "s:0:fight:0";
    private const string SyncPoint = "s:0:tp";

    private static CooperativeRoutePlan Plan()
    {
        var task = new PathingTask { FileName = "route.json", FullPath = "route.json", Positions = new() };
        var plan = CooperativeRoutePlan.FromTask(new RouteInfo(), task, 0, 30);
        plan.Manifest.RouteId = "0:route";
        plan.Manifest.Eligible = true;
        plan.Manifest.Checkpoints.Add(new() { Id = FightPoint, Segment = 0, Kind = "fight" });
        plan.Manifest.Checkpoints.Add(new() { Id = SyncPoint, Segment = 0, Kind = "teleport" });
        plan.FightPointIds[0] = FightPoint;
        return plan;
    }

    private static RerunSnapshot Snap(RerunRequest request, long revision, RerunStage stage,
        string? sessionId = "server", string? scope = null, string routeId = "", int planIndex = 0,
        IEnumerable<string>? resolved = null, IEnumerable<RerunMarkEvent>? marks = null) => new()
    {
        SessionId = sessionId ?? "server",
        Scope = scope ?? request.Scope,
        Stage = stage,
        Revision = revision,
        CurrentRouteId = routeId,
        CurrentPlanIndex = planIndex,
        PlanHash = "hash",
        ResolvedPoints = resolved?.ToList() ?? new(),
        Marks = marks?.ToList() ?? new(),
    };

    private static CoordinatorClient Client(Func<RerunRequest, RerunSnapshot> respond, bool capability = true,
        List<RerunRequest>? seen = null)
    {
        var client = new CoordinatorClient();
        client._testIsConnectedOverride = true;
        typeof(CoordinatorClient).GetField("_isInRoom", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(client, true);
        var gateway = client.GetOrCreateGatewayForTest();
        typeof(BgiGatewayClient).GetProperty(nameof(BgiGatewayClient.ServerCapabilities),
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
            .SetValue(gateway, capability ? new[] { RerunProtocol.Capability } : Array.Empty<string>());
        gateway._testSendOverride = (_, _, _) => Task.CompletedTask;
        gateway._testInvokeOverride = (_, envelope, _) =>
        {
            var request = JsonSerializer.Deserialize<RerunRequest>(envelope.Payload!.ToJsonString(), GatewayJson.Options)!;
            seen?.Add(request);
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = envelope.Name,
                Payload = GatewayEnvelope.ToPayload(respond(request)),
            });
        };
        return client;
    }

    private static GatewayEnvelope Evt(string name, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Event,
        Name = name,
        Payload = GatewayEnvelope.ToPayload(payload),
    };

    // =========================================================================
    // 1) 真实 room.closed 事件：严格等待必须判定为“未确认完成”
    // =========================================================================

    /// <summary>
    /// 修复前：等待循环只看“任务已完成”，RoomClosed 触发的 tcs 取消会让它 break 后返回 true，
    /// 调用方据此误认为“全员已到”（并建立传送后保护窗口）。本测试走真实事件分发路径，
    /// 断言门面返回 false（未确认），而不是只 mock 一个抛异常的发送。
    /// </summary>
    [Fact]
    public async Task RoomClosedDuringStrictWait_IsNotReportedAsSuccess()
    {
        var client = Client(_ => Snap(new RerunRequest(), 1, RerunStage.Normal));
        var coordinator = new MultiplayerCoordinator(client, new SyncPointResolver(), new AutoHoeingConfig());

        var wait = coordinator.WaitForAllPlayers("sp-never-arrives", CancellationToken.None);
        // 让等待先完成订阅并进入 5s 重试等待（200ms 探测窗口之后）。
        await Task.Delay(400);
        client.DispatchEvt(Evt("room.closed", new { reason = "房主已关闭房间" }));

        var completed = await Task.WhenAny(wait, Task.Delay(5000));
        Assert.True(ReferenceEquals(completed, wait), "关房后严格等待应立即结束，而不是继续空等");
        Assert.False(await wait, "关房导致的等待取消不能被当成“全员已到”");
    }

    // =========================================================================
    // 2) Recover / Bypass 三态
    // =========================================================================

    [Fact]
    public async Task RecoverIsSentForCurrentRouteAndDoesNotResolvePoints()
    {
        var plan = Plan();
        var seen = new List<RerunRequest>();
        var client = Client(r => Snap(r, 7, RerunStage.Running, routeId: plan.Manifest.RouteId,
            resolved: Array.Empty<string>()), seen: seen);
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        var context = session.CreateContext(plan, isReplay: true);

        await context.ReportRecoveryAsync("联机：战斗后复苏", CancellationToken.None);

        var recover = Assert.Single(seen.Where(r => r.Operation == RerunProtocol.Recover));
        Assert.Equal(plan.Manifest.RouteId, recover.RouteId);
        Assert.True(recover.IsReplay);
        // Recover 不豁免、不产生任何已解决点：本地能看到的最新快照仍无 ResolvedPoints。
        Assert.Empty(session.Snapshot.ResolvedPoints);
    }

    [Fact]
    public async Task BypassCarriesAcknowledgedRangeAndScopeFlag()
    {
        var plan = Plan();
        var seen = new List<RerunRequest>();
        var client = Client(r => Snap(r, 9, RerunStage.Running, routeId: plan.Manifest.RouteId), seen: seen);
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        var context = session.CreateContext(plan, isReplay: true);

        await context.ReportBypassAsync(throughSegment: 1, skipRoute: false, CancellationToken.None);
        await context.ReportBypassAsync(throughSegment: -1, skipRoute: true, CancellationToken.None);

        var bypasses = seen.Where(r => r.Operation == RerunProtocol.Bypass).ToList();
        Assert.Equal(2, bypasses.Count);
        Assert.Equal(1, bypasses[0].ThroughSegment);
        Assert.False(bypasses[0].SkipRoute);
        Assert.True(bypasses[1].SkipRoute);
        Assert.All(bypasses, b => Assert.Equal(plan.Manifest.RouteId, b.RouteId));
    }

    // =========================================================================
    // 3) 快照身份与修订校验
    // =========================================================================

    [Fact]
    public async Task StaleRevisionForeignSessionAndForeignScopeAreIgnored()
    {
        var plan = Plan();
        // 按请求的 reason 决定返回哪一帧：后台 poll 会并发取快照，用全局计数器会被 poll 抢走脚本，
        // 因此这里用"请求自身携带的指纹"驱动，保证与线程/时序无关的确定性。
        var client = Client(r => r.Reason switch
        {
            "stale" => Snap(r, 4, RerunStage.Running, routeId: plan.Manifest.RouteId, planIndex: 0),
            "foreign-session" => Snap(r, 11, RerunStage.Running, sessionId: "someone-else",
                routeId: plan.Manifest.RouteId, planIndex: 0),
            "foreign-scope" => Snap(r, 12, RerunStage.Running, scope: "another-round",
                routeId: plan.Manifest.RouteId, planIndex: 0),
            _ => Snap(r, 10, RerunStage.Running, routeId: plan.Manifest.RouteId, planIndex: 3),
        });
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        var context = session.CreateContext(plan, isReplay: true);

        await context.ReportRecoveryAsync("baseline", CancellationToken.None);
        Assert.Equal(10, session.Snapshot.Revision);
        Assert.Equal(3, session.Snapshot.CurrentPlanIndex);

        await context.ReportRecoveryAsync("stale", CancellationToken.None);
        await context.ReportRecoveryAsync("foreign-session", CancellationToken.None);
        await context.ReportRecoveryAsync("foreign-scope", CancellationToken.None);

        Assert.Equal(10, session.Snapshot.Revision);
        Assert.Equal(3, session.Snapshot.CurrentPlanIndex);
        Assert.Equal("server", session.Snapshot.SessionId);
        Assert.Equal(Scope, session.Snapshot.Scope);
    }

    // =========================================================================
    // 4) 按点停战开关 + Finish 收口
    // =========================================================================

    [Fact]
    public async Task ShouldSkipFightMatchesPhaseAndStopsAfterCompletion()
    {
        var plan = Plan();
        var marks = new[]
        {
            new RerunMarkEvent { RouteId = plan.Manifest.RouteId, PointId = FightPoint, IsReplay = true },
            new RerunMarkEvent { RouteId = plan.Manifest.RouteId, PointId = FightPoint, IsReplay = false },
        };
        var replayStage = RerunStage.Running;
        var client = Client(r => Snap(r, 5, replayStage, routeId: plan.Manifest.RouteId, marks: marks));
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);

        var replay = session.CreateContext(plan, isReplay: true);
        Assert.True(replay.ShouldSkipFight(FightPoint), "重跑阶段应命中重跑期标记");
        Assert.False(replay.ShouldSkipFight(SyncPoint), "未标记的点不应被跳过");
        replay.CompleteFight(FightPoint);
        Assert.False(replay.ShouldSkipFight(FightPoint), "本场已完成后不得再跳过同一点（防迟消息重复停战）");

        // 正常轮上下文只认正常轮标记；当前快照仍是 Running，故不命中。
        var normal = session.CreateContext(plan, isReplay: false);
        Assert.False(normal.ShouldSkipFight(FightPoint), "重跑标记不得驱动正常轮停战");

        // 切到正常轮阶段后，必须先让会话取回新快照（阶段由服务端快照驱动，不是本地推断）。
        // 注意：正常轮不再发送 Recover（服务端只接受 Running），故这里用 Poll 刷新而不是 Recover。
        replayStage = RerunStage.Normal;
        await session.SendAsync(RerunProtocol.Poll, ct: CancellationToken.None);
        Assert.Equal(RerunStage.Normal, session.Snapshot.Stage);
        Assert.True(normal.ShouldSkipFight(FightPoint), "正常轮阶段应命中正常轮标记");
    }

    [Fact]
    public async Task FinishWaitsForFinishingStageAndIsIdempotentOnCompleted()
    {
        var plan = Plan();
        var seen = new List<RerunRequest>();
        var polls = 0;
        var client = Client(r =>
        {
            if (r.Operation == RerunProtocol.Poll) polls++;
            return r.Operation switch
            {
                RerunProtocol.Finish => Snap(r, 30, RerunStage.Completed),
                _ => Snap(r, 20 + polls, polls < 2 ? RerunStage.Running : RerunStage.Finishing,
                    routeId: plan.Manifest.RouteId, planIndex: 1),
            };
        }, seen: seen);
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);

        await session.FinishAsync(CancellationToken.None);
        Assert.Contains(seen, r => r.Operation == RerunProtocol.Finish);
    }

    [Fact]
    public async Task IsAbortedReflectsServerAbortSoGameplayCanStop()
    {
        var plan = Plan();
        var stage = RerunStage.Running;
        var client = Client(r => Snap(r, 20, stage, routeId: plan.Manifest.RouteId));
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        var context = session.CreateContext(plan, isReplay: true);
        Assert.False(context.IsAborted);

        // 服务端中止（成员掉线租约到期 / 任一方失败或取消）后，执行器必须能在游戏动作循环里同步看到，
        // 从而立即停战并停止剩余路点，而不是跑完整条线路才在下个 RPC 发现。
        stage = RerunStage.Aborted;
        await session.SendAsync(RerunProtocol.Poll, ct: CancellationToken.None);
        Assert.True(context.IsAborted);
    }

    [Fact]
    public async Task AbortedStageSurfacesFailureInsteadOfSuccess()
    {
        var plan = Plan();
        var client = Client(r => Snap(r, 15, RerunStage.Aborted, routeId: plan.Manifest.RouteId));
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        var context = session.CreateContext(plan, isReplay: true);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.FinishAsync(CancellationToken.None));
        // Abort 终态下等待中的点也必须显式失败，不能静默返回“已完成”。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => context.WaitAsync(SyncPoint, CancellationToken.None));
    }

    // =========================================================================
    // 5) 真实握手必须宣告客户端能力（否则服务端按“缺省即不支持”拒绝 Enroll）
    // =========================================================================

    [Fact]
    public async Task HelloAdvertisesCooperativeRerunCapability()
    {
        var client = new CoordinatorClient();
        var gateway = client.GetOrCreateGatewayForTest();
        string? sentCapabilities = null;
        gateway._testInvokeOverride = (_, envelope, _) =>
        {
            sentCapabilities = envelope.Payload!.ToJsonString();
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = envelope.Name,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    protocolVersion = GatewayProtocol.ProtocolVersion,
                    serverVersion = "test",
                    capabilities = new[] { RerunProtocol.Capability },
                }),
            });
        };

        await gateway.HelloAsync(CancellationToken.None);

        Assert.NotNull(sentCapabilities);
        Assert.Contains(RerunProtocol.Capability, sentCapabilities!,
            StringComparison.Ordinal);
    }

    // =========================================================================
    // 6) Prepare 必须等计划哈希冻结后再发（非末位成员旧实现必然被 rerun_plan_hash 拒绝）
    // =========================================================================

    [Fact]
    public async Task PrepareWaitsForFrozenPlanHash()
    {
        var plan = Plan();
        var seen = new List<RerunRequest>();
        const string frozenHash = "frozen-hash-1";
        var client = Client(r =>
        {
            var snap = r.Operation switch
            {
                // 末位提交前：仍处于 Normal，且 hash 尚未冻结（服务端此刻不返回 hash）。
                RerunProtocol.NormalDone => Snap(r, 2, RerunStage.Normal),
                // 全员提交后才进入 Preparing 并带回冻结 hash。
                RerunProtocol.Prepare => Snap(r, 4, RerunStage.Running),
                _ => r.SessionId.Length == 0
                    ? Snap(r, 1, RerunStage.Normal)
                    : Snap(r, 3, RerunStage.Preparing),
            };
            if (snap.Stage == RerunStage.Preparing) snap.PlanHash = frozenHash;
            return snap;
        }, seen: seen);
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);

        var final = await session.CompleteNormalAsync(CancellationToken.None);

        Assert.Equal(RerunStage.Running, final.Stage);
        var prepare = Assert.Single(seen.Where(r => r.Operation == RerunProtocol.Prepare));
        Assert.Equal(frozenHash, prepare.PlanHash);
        Assert.False(string.IsNullOrEmpty(prepare.PlanHash), "Prepare 不得携带空 hash（否则服务端必然拒绝）");
    }

    // =========================================================================
    // 7) 正常轮不得发送只接受于重跑阶段的操作；豁免必须导致“不完整”终态
    // =========================================================================

    [Fact]
    public async Task NormalRoundDoesNotSendRecoverOrBypass()
    {
        var plan = Plan();
        var seen = new List<RerunRequest>();
        var client = Client(r => Snap(r, 3, RerunStage.Normal, routeId: plan.Manifest.RouteId), seen: seen);
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        var normal = session.CreateContext(plan, isReplay: false);

        await normal.ReportRecoveryAsync("正常轮复苏", CancellationToken.None);
        await normal.ReportBypassAsync(1, false, CancellationToken.None);

        Assert.DoesNotContain(seen, r => r.Operation == RerunProtocol.Recover);
        Assert.DoesNotContain(seen, r => r.Operation == RerunProtocol.Bypass);
        // 豁免即使不发协议，也必须在本地登记为“不完整 + 有豁免”，供终态自检。
        Assert.True(normal.HadIncompleteExecution);
        Assert.True(normal.HadBypass);
    }

    [Fact]
    public async Task IncompleteRouteRequiresReasonAndBypassBlocksCompleted()
    {
        var plan = Plan();
        var seen = new List<RerunRequest>();
        var routeDone = false;
        var client = Client(r =>
        {
            if (r.Operation == RerunProtocol.RouteDone) routeDone = true;
            // 提交终态后把当前线路让出（模拟服务端在全员终态后推进），否则等待会一直挂在同一线路。
            return Snap(r, 6, routeDone ? RerunStage.Finishing : RerunStage.Running,
                routeId: routeDone ? "" : plan.Manifest.RouteId, planIndex: routeDone ? 1 : 0);
        }, seen: seen);
        await using var session = await CooperativeRerunSession.StartAsync(client, new[] { plan }, Scope, CancellationToken.None);
        using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        // 未提供任何原因时，客户端必须补一个非空原因（服务端强制 Incomplete 带原因）。
        var context = session.CreateContext(plan, isReplay: true);
        await session.CompleteRouteAsync(context, RerunRouteOutcome.Incomplete, bounded.Token);
        var done = Assert.Single(seen.Where(r => r.Operation == RerunProtocol.RouteDone));
        Assert.False(string.IsNullOrWhiteSpace(done.Reason));

        // 有豁免时不允许提交 Completed（服务端要求所有非战斗点真实到达）。
        var bypassed = session.CreateContext(plan, isReplay: true);
        await bypassed.ReportBypassAsync(-1, true, bounded.Token);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.CompleteRouteAsync(bypassed, RerunRouteOutcome.Completed, bounded.Token));
    }
}
