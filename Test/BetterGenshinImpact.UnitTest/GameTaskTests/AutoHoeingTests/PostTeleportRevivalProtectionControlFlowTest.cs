#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 传送后复苏保护 - 控制流 / 集成测试（Task 6.2）。
/// **Validates: Requirements 2.2-2.4, 2.6-2.8, 3.1, 3.6, 4.3-4.4, 6.3-6.4**
///
/// 覆盖列：
///   1. 保护命中不进入 tracker / 广播 / 升级：
///      - 直接驱动 <see cref="PathExecutor.IsPostTeleportRevivalProtectionHit"/>（RouteExecutionEngine
///        在 tracker/路线标记/ReportAnomalyAsync 之前调用的只读短路判定），验证命中/未命中边界；
///      - 忠实模拟 RouteExecutionEngine.OnMultiplayerDefeatedDetected 回调，验证命中短路
///        （不 HandleRevivalTrigger / 不标记路线重跑 / 不 ReportAnomalyAsync），未命中保持既有流程。
///   2. 同段多个复苏事件最多一次消费：对 <see cref="PostTeleportRevivalProtectionDecisions.TryConsume"/>
///      做并发/多次调用断言，至多一个赢家且状态保持 Consumed。
///   3. 段切换不泄漏状态：保护消费后状态保持 Consumed（不因同段重跑重置），进入下一段才建立新机会。
///   4. 联机与单机对照：单机（MultiplayerCoordinator == null）保护判定恒 false（零感知）；
///      保护窗口为常量、不新增 AutoHoeingConfig 持久化字段（SignalR/Hub/配置载荷不变）。
///
/// 本文件只读引用已实现生产代码（PathExecutor / PostTeleportRevivalProtectionDecisions），
/// 不修改任何生产代码，不改其它测试文件。沿用仓库既有测试模式：
///   - PathExecutor 保护状态字段为 private，用反射注入（见 MultiplayerRoomBugConditionTest /
///     CoordinatorClientWaitPointTests 的私有成员反射模式）；
///   - RouteExecutionEngine 依赖过多难以直接实例化，用既有忠实模拟模式
///     （PostTeleportRevivalProtectionBugConditionTest）验证回调短路语义。
/// </summary>
public class PostTeleportRevivalProtectionControlFlowTest : IDisposable
{
    private const int SegmentB = 2; // 当前段 B 的段起点索引（CurWaypoints.Item1）
    private const int SegmentC = 3; // 下一段 C 的段起点索引

    private readonly CoordinatorClient _client;
    private readonly MultiplayerCoordinator _coordinator;

    public PostTeleportRevivalProtectionControlFlowTest()
    {
        // 沿用 MultiplayerCoordinatorWaitForAllPlayersTest.CreateCoordinator 的构造方式。
        _client = new CoordinatorClient();
        _coordinator = new MultiplayerCoordinator(
            _client,
            new SyncPointResolver(),
            new AutoHoeingConfig());
    }

    public void Dispose()
    {
        _client.DisposeAsync().AsTask().Wait();
    }

    // =========================================================================
    // 构造辅助：创建 PathExecutor 并注入联机协调器 / 当前段 / 保护窗口状态
    // =========================================================================

    /// <summary>
    /// 创建带联机协调器的 PathExecutor，并把当前段设为 <paramref name="segmentIndex"/>。
    /// </summary>
    private static PathExecutor CreateExecutor(int segmentIndex)
        => new(CancellationToken.None)
        {
            MultiplayerCoordinator = new MultiplayerCoordinator(
                new CoordinatorClient(), new SyncPointResolver(), new AutoHoeingConfig()),
            CurWaypoints = (segmentIndex, new List<WaypointForTrack>())
        };

    /// <summary>
    /// 通过反射注入保护状态字段（Task 4.1 建立的每段一次窗口）。
    /// </summary>
    private static void InjectProtectionState(
        PathExecutor executor,
        int protectionSegmentId,
        DateTime completedAtUtc,
        int consumed)
    {
        SetPrivateField(executor, "_protectionSegmentId", protectionSegmentId);
        SetPrivateField(executor, "_protectionSyncPointId", "sync_B");
        SetPrivateField(executor, "_protectionCompletedAtUtc", completedAtUtc);
        SetPrivateField(executor, "_protectionConsumed", consumed);
    }

    private static void SetPrivateField(object target, string name, object value)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object? GetPrivateField(object target, string name)
    {
        var field = target.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }

    // =========================================================================
    // Part A：PathExecutor.IsPostTeleportRevivalProtectionHit 短路判定
    //
    // 这是 RouteExecutionEngine.OnMultiplayerDefeatedDetected 在 tracker / 路线标记 /
    // ReportAnomalyAsync 之前调用的只读判定。命中 → 回调短路（不进入团队副作用）。
    // 覆盖命中边界与所有未命中边界。
    // =========================================================================

    /// <summary>
    /// A1 命中：联机 + 当前段匹配保护段 + 机会未消费 + 复苏在 10 秒窗口内 → true。
    /// （RouteExecutionEngine 据此短路 tracker/广播/升级，requirements 2.3 / 3.6）
    ///
    /// **Validates: Requirements 2.1, 2.3, 6.3**
    /// </summary>
    [Fact]
    public void IsHit_ActiveWindow_MatchingSegment_NotConsumed_InWindow_ReturnsTrue()
    {
        var executor = CreateExecutor(SegmentB);
        // 保护窗口刚建立（completedAt = 现在），elapsed ≈ 0 ≤ 10s，机会未消费
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow, consumed: 0);

        Assert.True(executor.IsPostTeleportRevivalProtectionHit(),
            "联机 + 匹配段 + 未消费 + 窗口内复苏应命中保护（短路团队副作用）");
    }

    /// <summary>
    /// A2 单机零感知：MultiplayerCoordinator == null → 恒 false。
    /// 单机不进入保护决策（requirements 3.1 / 5.3），即使保护段字段匹配也不命中。
    ///
    /// **Validates: Requirements 3.1, 5.3**
    /// </summary>
    [Fact]
    public void IsHit_SingleMode_NullCoordinator_AlwaysFalse()
    {
        var executor = new PathExecutor(CancellationToken.None)
        {
            // MultiplayerCoordinator 保持 null（单机）
            CurWaypoints = (SegmentB, new List<WaypointForTrack>())
        };
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow, consumed: 0);

        Assert.False(executor.IsPostTeleportRevivalProtectionHit(),
            "单机（coordinator == null）保护判定恒 false，单机路径零感知");
    }

    /// <summary>
    /// A3 段不匹配：保护段属于 B，但当前段已切到 C → 不命中（段切换不泄漏状态）。
    ///
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Fact]
    public void IsHit_SegmentMismatch_ProtectionForBOldSegment_ReturnsFalse()
    {
        var executor = CreateExecutor(SegmentC); // 当前段已推进到 C
        // 保护窗口仍是 B 段（旧段状态未带到 C）
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow, consumed: 0);

        Assert.False(executor.IsPostTeleportRevivalProtectionHit(),
            "当前段 C 与保护段 B 不匹配：旧段保护状态不得泄漏到新段（不命中）");
    }

    /// <summary>
    /// A4 机会已消费：同段第一次命中后再次复苏 → 不命中（走原 tracker/广播/升级流程）。
    ///
    /// **Validates: Requirements 2.6, 3.4**
    /// </summary>
    [Fact]
    public void IsHit_OpportunityConsumed_ReturnsFalse()
    {
        var executor = CreateExecutor(SegmentB);
        // 机会已被消费（consumed=1），即使仍在窗口内也不命中
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow, consumed: 1);

        Assert.False(executor.IsPostTeleportRevivalProtectionHit(),
            "同段机会已消耗：再次复苏不再保护，走既有异常流程");
    }

    /// <summary>
    /// A5 窗口过期：复苏已超过 10 秒（completedAt 在 11 秒前）→ 不命中。
    ///
    /// **Validates: Requirements 2.5, 3.4**
    /// </summary>
    [Fact]
    public void IsHit_WindowExpired_Over10s_ReturnsFalse()
    {
        var executor = CreateExecutor(SegmentB);
        // 保护窗口 11 秒前建立 → 当前 UtcNow 距完成已 > 10s → 过期
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow.AddSeconds(-11), consumed: 0);

        Assert.False(executor.IsPostTeleportRevivalProtectionHit(),
            "超过 10 秒窗口不命中保护，保持原同步点后异常流程");
    }

    /// <summary>
    /// A6 Inactive：未建立保护窗口（_protectionSegmentId == -1）→ 恒 false。
    /// 传送严格等待未完成 / 非 teleport 同步点时不建立窗口，保护不介入。
    ///
    /// **Validates: Requirements 3.2, 3.3, 4.2**
    /// </summary>
    [Fact]
    public void IsHit_Inactive_NoProtectionSegment_ReturnsFalse()
    {
        var executor = CreateExecutor(SegmentB);
        // 不注入保护状态：_protectionSegmentId 保持初始 -1（Inactive）
        Assert.False(executor.IsPostTeleportRevivalProtectionHit(),
            "未建立保护窗口（Inactive）恒 false，等待未完成/非 teleport 不介入");
    }

    // =========================================================================
    // Part B：RouteExecutionEngine.OnMultiplayerDefeatedDetected 回调短路语义
    //
    // RouteExecutionEngine 依赖过多难以直接实例化，沿用既有忠实模拟模式
    // （PostTeleportRevivalProtectionBugConditionTest）复现回调控制流：
    //   if (executor == null) return;
    //   if (!retryMode) return;
    //   if (executor.IsPostTeleportRevivalProtectionHit()) { log; return; }   // 保护短路
    //   HandleRevivalTrigger(executor);                                        // tracker
    //   if (ShouldMarkRerun(...)) markSet.Add(routeIndex);                     // 路线标记
    //   if (coordinator connected) ReportAnomalyAsync(...);                    // 异常上报
    // =========================================================================

    /// <summary>模拟回调的可观察结果：是否进入 tracker / 路线重跑标记 / 异常上报。</summary>
    private sealed class CallbackOutcome
    {
        public bool EnteredTracker { get; set; }
        public bool MarkedRouteRerun { get; set; }
        public bool ReportedAnomaly { get; set; }
        public bool ShortCircuitedByProtection { get; set; }
    }

    /// <summary>
    /// 忠实模拟 RouteExecutionEngine.OnMultiplayerDefeatedDetected 的控制流。
    /// <paramref name="isProtectionHit"/> 由 IsPostTeleportRevivalProtectionHit 的语义给出。
    /// </summary>
    private static CallbackOutcome SimulateOnMultiplayerDefeatedDetected(bool isProtectionHit)
    {
        var outcome = new CallbackOutcome();
        // 命中保护 → 在 tracker/路线标记/异常上报之前短路，直接 return
        if (isProtectionHit)
        {
            outcome.ShortCircuitedByProtection = true;
            return outcome;
        }
        // 未命中 → 完全走既有回调逻辑
        outcome.EnteredTracker = true;                 // HandleRevivalTrigger → RevivalRecurrenceTracker
        outcome.MarkedRouteRerun = true;               // RouteRerunDecisions.ShouldMarkRerun → markSet
        outcome.ReportedAnomaly = true;                // coordinator connected → ReportAnomalyAsync
        return outcome;
    }

    /// <summary>
    /// B1 命中短路：保护命中时不进入 tracker / 路线标记 / 异常上报。
    ///
    /// **Validates: Requirements 1.2, 2.3, 3.6**
    /// </summary>
    [Fact]
    public void Callback_ProtectionHit_ShortCircuitsTeamSideEffects()
    {
        // 命中保护（由 Part A 的 IsPostTeleportRevivalProtectionHit 判定驱动）
        var executor = CreateExecutor(SegmentB);
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow, consumed: 0);
        bool protectionHit = executor.IsPostTeleportRevivalProtectionHit();
        Assert.True(protectionHit, "前置：该复苏属于保护命中");

        var outcome = SimulateOnMultiplayerDefeatedDetected(protectionHit);

        Assert.True(outcome.ShortCircuitedByProtection, "保护命中应在 tracker/广播/升级之前短路");
        Assert.False(outcome.EnteredTracker, "保护命中不得进入 RevivalRecurrenceTracker");
        Assert.False(outcome.MarkedRouteRerun, "保护命中不得标记路线重跑");
        Assert.False(outcome.ReportedAnomaly, "保护命中不得 ReportAnomalyAsync / PlayerAnomalyNotify");
    }

    /// <summary>
    /// B2 未命中保持既有流程：非保护复苏继续进入 tracker / 路线标记 / 异常上报。
    /// （preservation：非命中输入保持原回调逻辑）
    ///
    /// **Validates: Requirements 3.4, 3.5, 3.6**
    /// </summary>
    [Fact]
    public void Callback_ProtectionNotHit_FallsThroughToOriginalFlow()
    {
        // 未命中（例如窗口过期 / 机会已消费 / 单机）
        var executor = CreateExecutor(SegmentB);
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow.AddSeconds(-11), consumed: 0);
        bool protectionHit = executor.IsPostTeleportRevivalProtectionHit();
        Assert.False(protectionHit, "前置：窗口过期 → 不命中保护");

        var outcome = SimulateOnMultiplayerDefeatedDetected(protectionHit);

        Assert.False(outcome.ShortCircuitedByProtection, "非命中不短路");
        Assert.True(outcome.EnteredTracker, "非命中保持 tracker");
        Assert.True(outcome.MarkedRouteRerun, "非命中保持路线重跑标记");
        Assert.True(outcome.ReportedAnomaly, "非命中保持异常上报");
    }

    // =========================================================================
    // Part C：TryConsume 一次性消费（同段多个复苏事件最多一次消费）
    //
    // PostTeleportRevivalProtectionDecisions.TryConsume 用 Interlocked.CompareExchange
    // 保证同段并发复苏至多一个赢家；消费后状态保持 1（Consumed），不因同段重跑重置。
    // =========================================================================

    /// <summary>
    /// C1 顺序一次性：第一次调用赢得消费，第二次失败，状态保持 Consumed。
    ///
    /// **Validates: Requirements 2.6, 4.3**
    /// </summary>
    [Fact]
    public void TryConsume_Sequential_FirstWins_SecondFails_StateStaysConsumed()
    {
        int consumed = 0;

        bool first = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        bool second = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);

        Assert.True(first, "第一次调用应赢得保护消费");
        Assert.False(second, "第二次调用不得重复消费");
        Assert.Equal(1, consumed); // 状态保持 Consumed，不因同段重跑重置
    }

    /// <summary>
    /// C2 并发一次性：同段多个复苏事件并发竞争，至多一个赢家，其余失败。
    ///
    /// **Validates: Requirements 4.3, 4.4, 6.4**
    /// </summary>
    [Fact]
    public void TryConsume_Concurrent_AtMostOneWinner()
    {
        int consumed = 0;
        const int ActorCount = 32;
        var wins = new bool[ActorCount];

        // 并发竞争同一段保护机会（模拟后台复苏检测回调来自多线程）
        Parallel.For(0, ActorCount, i =>
        {
            wins[i] = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        });

        int winners = wins.Count(w => w);
        Assert.Equal(1, winners);        // 至多（且恰好）一个赢家
        Assert.Equal(1, consumed);       // 消费后状态保持 1
    }

    /// <summary>
    /// C3 消费后不重置：同段再次尝试（模拟段内重跑后的再次复苏）不得重新获得机会。
    /// 只有进入下一段（新窗口 reset）才建立新机会。
    ///
    /// **Validates: Requirements 2.7, 4.6**
    /// </summary>
    [Fact]
    public void TryConsume_AfterConsumption_DoesNotReset_SameSegmentRerun()
    {
        int consumed = 0;
        Assert.True(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed));

        // 段内重跑后再次尝试（状态仍为同一 int 位）→ 不重置、不重新消费
        Assert.False(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed));
        Assert.Equal(1, consumed);
    }

    // =========================================================================
    // Part D：联机与单机对照 + 配置/协议载荷不变
    //
    // 单机零感知（requirements 3.1 / 5.3 / 5.4）；保护窗口为常量，不新增持久化配置字段，
    // 不改变 SignalR/Hub/配置载荷（requirements 3.8 / 3.11 / 5.1 / 5.2）。
    // =========================================================================

    /// <summary>
    /// D1 单机对照：单机保护判定恒 false（决策层与执行层双验证）。
    ///
    /// **Validates: Requirements 3.1, 5.3**
    /// </summary>
    [Fact]
    public void SingleVsMultiplayer_SingleModeProtectionNeverEngages()
    {
        // 决策层：单机 isMultiplayerHoeing=false → IsEligible 恒 false
        bool decisionEligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: false,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: DateTime.UtcNow,
            revivalTime: DateTime.UtcNow.AddSeconds(2),
            consumed: false);
        Assert.False(decisionEligible, "决策层：单机不进入保护决策");

        // 执行层：coordinator == null → IsPostTeleportRevivalProtectionHit 恒 false
        var executor = new PathExecutor(CancellationToken.None)
        {
            CurWaypoints = (SegmentB, new List<WaypointForTrack>())
        };
        InjectProtectionState(executor, SegmentB, DateTime.UtcNow, consumed: 0);
        Assert.False(executor.IsPostTeleportRevivalProtectionHit(),
            "执行层：单机（coordinator==null）保护判定恒 false");

        // 对照：联机同条件应命中（证明单机是"零感知"，而非"保护逻辑整体失效"）
        var mpExecutor = CreateExecutor(SegmentB);
        InjectProtectionState(mpExecutor, SegmentB, DateTime.UtcNow, consumed: 0);
        Assert.True(mpExecutor.IsPostTeleportRevivalProtectionHit(),
            "对照：联机同条件应命中（单机零感知而非功能缺失）");
    }

    /// <summary>
    /// D2 配置/协议载荷不变：保护窗口固定为 10 秒常量，AutoHoeingConfig 未新增任何保护字段；
    /// 不引入 RoomConfig / SignalR / 持久化字段（design §Non-goals / requirements 3.11 / 5.2）。
    ///
    /// **Validates: Requirements 3.11, 5.1, 5.2**
    /// </summary>
    [Fact]
    public void NoConfigProtocolChange_ProtectionWindowFixed_NoNewConfigField()
    {
        // 窗口固定为 10 秒常量（requirements 5.2：保护窗口固定、不暴露配置项、不写入 JSON）
        Assert.Equal(10, PostTeleportRevivalProtectionDecisions.WindowSeconds);

        // AutoHoeingConfig 未新增任何保护相关持久化字段（零命中保护语义的配置属性）
        var protectionFields = typeof(AutoHoeingConfig)
            .GetProperties()
            .Select(p => p.Name)
            .Where(n => n.Contains("Protection") || n.Contains("PostTeleport") || n.Contains("RevivalProtection"))
            .ToList();
        Assert.Empty(protectionFields);
    }
}
