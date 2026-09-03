#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// PostTeleportRevivalProtectionDecisions 扩展后（triggerType 参数化）的单元测试（普通 xunit Fact，非 PBT）。
/// 覆盖 design.md §Unit Tests 与 requirements.md 2.1, 2.6, 4.1, 6.1：
///   - GetWindowSeconds："stuck"=20、"revival"=10、其他值回退 10。
///   - 7 参 IsEligible：stuck 命中组合（联机 teleport + 严格等待 + 未消费 + 0<=elapsed<=20，含恰好 0/20）
///     与不命中组合（负时间差、>20s、单机、非 teleport、未完成、已消费）；
///     revival 组合（0<=elapsed<=10，含恰好 0/10，>10 不命中，公开窗口区分）。
///   - 6 参复苏入口：委托到 revival，行为与既有一致（回归防护）。
///   - ComputeProgress：目标为当前段起点，不推进到下一段。
///   - TryConsume：一次性消费。
/// </summary>
public class PostTeleportStuckProtectionDecisionsUnitTest
{
    private const double RevivalWindowSeconds = PostTeleportRevivalProtectionDecisions.WindowSeconds;        // 10
    private const double StuckWindowSeconds = PostTeleportRevivalProtectionDecisions.StuckWindowSeconds;     // 20

    private static readonly DateTime CompletionTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    // =========================================================================
    // GetWindowSeconds：按触发类型取窗口
    // =========================================================================

    [Fact]
    public void GetWindowSeconds_Stuck_ReturnsStuckWindow()
    {
        Assert.Equal(StuckWindowSeconds, PostTeleportRevivalProtectionDecisions.GetWindowSeconds("stuck"));
    }

    [Fact]
    public void GetWindowSeconds_Revival_ReturnsRevivalWindow()
    {
        Assert.Equal(RevivalWindowSeconds, PostTeleportRevivalProtectionDecisions.GetWindowSeconds("revival"));
    }

    [Fact]
    public void GetWindowSeconds_UnknownType_FallsBackToRevivalWindow()
    {
        // 未知/其他触发类型回退到复苏窗口
        Assert.Equal(RevivalWindowSeconds, PostTeleportRevivalProtectionDecisions.GetWindowSeconds("other"));
        Assert.Equal(RevivalWindowSeconds, PostTeleportRevivalProtectionDecisions.GetWindowSeconds(""));
        Assert.Equal(RevivalWindowSeconds, PostTeleportRevivalProtectionDecisions.GetWindowSeconds("stuck "));
    }

    // =========================================================================
    // 7 参 IsEligible：stuck 命中组合
    // =========================================================================

    [Fact]
    public void IsEligible_Stuck_AllConditionsMet_ReturnsTrue()
    {
        // 联机 + teleport + 严格等待 + 未消费 + 窗口内（5 秒）
        Assert.True(EligibleStuck(elapsedSeconds: 5));
    }

    [Fact]
    public void IsEligible_Stuck_ExactlyZeroSeconds_ReturnsTrue()
    {
        // 边界：恰好 0 秒命中
        Assert.True(EligibleStuck(elapsedSeconds: 0));
    }

    [Fact]
    public void IsEligible_Stuck_ExactlyWindowSeconds_ReturnsTrue()
    {
        // 边界：恰好 20 秒命中
        Assert.True(EligibleStuck(elapsedSeconds: StuckWindowSeconds));
    }

    [Fact]
    public void IsEligible_Stuck_JustInsideWindow_ReturnsTrue()
    {
        // 边界：19.999 秒仍在窗口内命中
        Assert.True(EligibleStuck(elapsedSeconds: StuckWindowSeconds - 0.001));
    }

    // =========================================================================
    // 7 参 IsEligible：stuck 不命中组合
    // =========================================================================

    [Fact]
    public void IsEligible_Stuck_NegativeElapsed_ReturnsFalse()
    {
        // 卡死时刻早于完成时刻 → 负时间差不命中
        Assert.False(EligibleStuck(elapsedSeconds: -1));
    }

    [Fact]
    public void IsEligible_Stuck_JustBeyondWindow_ReturnsFalse()
    {
        // 20.001 秒超过窗口 → 不命中
        Assert.False(EligibleStuck(elapsedSeconds: StuckWindowSeconds + 0.001));
    }

    [Fact]
    public void IsEligible_Stuck_BeyondWindow_ReturnsFalse()
    {
        // 21 秒超过窗口 → 不命中
        Assert.False(EligibleStuck(elapsedSeconds: 21));
    }

    [Fact]
    public void IsEligible_Stuck_SinglePlayer_ReturnsFalse()
    {
        // 单机不命中
        Assert.False(EligibleStuck(isMultiplayerHoeing: false));
    }

    [Fact]
    public void IsEligible_Stuck_AutoSyncPointType_ReturnsFalse()
    {
        // 自动生成同步点（"auto"）不命中
        Assert.False(EligibleStuck(syncPointType: "auto"));
    }

    [Fact]
    public void IsEligible_Stuck_ManualSyncPointType_ReturnsFalse()
    {
        // 手动设置同步点（"manual"）不命中
        Assert.False(EligibleStuck(syncPointType: "manual"));
    }

    [Fact]
    public void IsEligible_Stuck_OtherSyncPointType_ReturnsFalse()
    {
        // 其他非 teleport 类型不命中
        Assert.False(EligibleStuck(syncPointType: "fight"));
    }

    [Fact]
    public void IsEligible_Stuck_NullSyncPointType_ReturnsFalse()
    {
        // 空类型不命中
        Assert.False(EligibleStuck(syncPointType: null));
    }

    [Fact]
    public void IsEligible_Stuck_StrictWaitNotCompleted_ReturnsFalse()
    {
        // 严格等待未完成不命中
        Assert.False(EligibleStuck(strictWaitCompleted: false));
    }

    [Fact]
    public void IsEligible_Stuck_OpportunityConsumed_ReturnsFalse()
    {
        // 机会已消费不命中
        Assert.False(EligibleStuck(consumed: true));
    }

    // =========================================================================
    // 7 参 IsEligible：revival 组合（窗口区分回归）
    // =========================================================================

    [Fact]
    public void IsEligible_Revival_AllConditionsMet_ReturnsTrue()
    {
        // 联机 + teleport + 严格等待 + 未消费 + 复苏窗口内（5 秒）
        Assert.True(EligibleRevival(elapsedSeconds: 5));
    }

    [Fact]
    public void IsEligible_Revival_ExactlyZeroSeconds_ReturnsTrue()
    {
        // 边界：恰好 0 秒命中
        Assert.True(EligibleRevival(elapsedSeconds: 0));
    }

    [Fact]
    public void IsEligible_Revival_ExactlyWindowSeconds_ReturnsTrue()
    {
        // 边界：恰好 10 秒命中
        Assert.True(EligibleRevival(elapsedSeconds: RevivalWindowSeconds));
    }

    [Fact]
    public void IsEligible_Revival_JustInsideWindow_ReturnsTrue()
    {
        // 边界：9.999 秒仍在窗口内命中
        Assert.True(EligibleRevival(elapsedSeconds: RevivalWindowSeconds - 0.001));
    }

    [Fact]
    public void IsEligible_Revival_BeyondWindow_ReturnsFalse()
    {
        // 10.001 秒超过复苏窗口 → 不命中（即便仍落在 stuck 20s 窗口内）
        Assert.False(EligibleRevival(elapsedSeconds: RevivalWindowSeconds + 0.001));
    }

    [Fact]
    public void IsEligible_Revival_ExceedsWindowButWithinStuckWindow_ReturnsFalse()
    {
        // 关键窗口区分：15 秒 > 复苏 10s 窗口 → 复苏不命中，
        // 但 <= stuck 20s 窗口；证明两种 triggerType 使用各自窗口时长。
        Assert.False(EligibleRevival(elapsedSeconds: 15));
        Assert.True(EligibleStuck(elapsedSeconds: 15));
    }

    [Fact]
    public void IsEligible_Revival_SinglePlayer_ReturnsFalse()
    {
        // 单机不命中
        Assert.False(EligibleRevival(isMultiplayerHoeing: false));
    }

    [Fact]
    public void IsEligible_Revival_NonTeleportSyncPoint_ReturnsFalse()
    {
        // 非 teleport 同步点不命中
        Assert.False(EligibleRevival(syncPointType: "auto"));
    }

    [Fact]
    public void IsEligible_Revival_StrictWaitNotCompleted_ReturnsFalse()
    {
        // 严格等待未完成不命中
        Assert.False(EligibleRevival(strictWaitCompleted: false));
    }

    [Fact]
    public void IsEligible_Revival_OpportunityConsumed_ReturnsFalse()
    {
        // 机会已消费不命中
        Assert.False(EligibleRevival(consumed: true));
    }

    // =========================================================================
    // 6 参 IsEligible：复苏入口委托到 revival（回归防护）
    // =========================================================================

    [Fact]
    public void IsEligible_SixParam_DelegatesToRevival_Hit()
    {
        // 6 参复苏入口 = 7 参 "revival" 入口；窗口内命中
        var revivalTime = CompletionTime.AddSeconds(5);
        var six = PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, CompletionTime, revivalTime, false);
        var seven = PostTeleportRevivalProtectionDecisions.IsEligible(
            "revival", true, "teleport", true, CompletionTime, revivalTime, false);
        Assert.True(six);
        Assert.Equal(seven, six);
    }

    [Fact]
    public void IsEligible_SixParam_ExactlyWindowSeconds_ReturnsTrue()
    {
        // 复苏 10s 边界命中
        var revivalTime = CompletionTime.AddSeconds(RevivalWindowSeconds);
        Assert.True(PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, CompletionTime, revivalTime, false));
    }

    [Fact]
    public void IsEligible_SixParam_BeyondWindow_ReturnsFalse()
    {
        // 复苏 10.001s 不命中（与既有一致）
        var revivalTime = CompletionTime.AddSeconds(RevivalWindowSeconds + 0.001);
        Assert.False(PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, CompletionTime, revivalTime, false));
    }

    [Fact]
    public void IsEligible_SixParam_NegativeElapsed_ReturnsFalse()
    {
        // 负时间差不命中（回归防护）
        var revivalTime = CompletionTime.AddSeconds(-1);
        Assert.False(PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, CompletionTime, revivalTime, false));
    }

    [Fact]
    public void IsEligible_SixParam_Consumed_ReturnsFalse()
    {
        // 机会已消费不命中（回归防护）
        var revivalTime = CompletionTime.AddSeconds(2);
        Assert.False(PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, CompletionTime, revivalTime, true));
    }

    // =========================================================================
    // ComputeProgress：目标为当前段起点，不推进到下一段
    // =========================================================================

    [Fact]
    public void ComputeProgress_OffsetZero_ReturnsSegmentStart()
    {
        // 段起点偏移 0 → 目标即当前段起点（不推进索引）
        Assert.Equal(5, PostTeleportRevivalProtectionDecisions.ComputeProgress(5, 0));
    }

    [Fact]
    public void ComputeProgress_NonZeroOffset_IsWithinSameSegment()
    {
        // 段内偏移不推进到下一段，仍落在当前段
        Assert.Equal(7, PostTeleportRevivalProtectionDecisions.ComputeProgress(5, 2));
    }

    [Fact]
    public void ComputeProgress_LastSegment_DoesNotAdvance()
    {
        // 最后一段保护命中仍回当前段起点
        Assert.Equal(9, PostTeleportRevivalProtectionDecisions.ComputeProgress(9, 0));
    }

    // =========================================================================
    // TryConsume：一次性消费
    // =========================================================================

    [Fact]
    public void TryConsume_FirstCall_ReturnsTrue()
    {
        var consumed = 0;
        Assert.True(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed));
        Assert.Equal(1, consumed);
    }

    [Fact]
    public void TryConsume_SecondCall_ReturnsFalse()
    {
        var consumed = 0;
        PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        // 同段第二次消费失败，状态保持 1
        Assert.False(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed));
        Assert.Equal(1, consumed);
    }

    // =========================================================================
    // 私有辅助：7 参入口调用
    // =========================================================================

    private static bool EligibleStuck(
        bool isMultiplayerHoeing = true,
        string? syncPointType = "teleport",
        bool strictWaitCompleted = true,
        double elapsedSeconds = 5,
        bool consumed = false)
    {
        // 卡死窗口 20s 内（默认 5 秒），触发类型 stuck
        return Eligible("stuck", isMultiplayerHoeing, syncPointType,
            strictWaitCompleted, elapsedSeconds, consumed);
    }

    private static bool EligibleRevival(
        bool isMultiplayerHoeing = true,
        string? syncPointType = "teleport",
        bool strictWaitCompleted = true,
        double elapsedSeconds = 5,
        bool consumed = false)
    {
        // 复苏窗口 10s 内（默认 5 秒），触发类型 revival
        return Eligible("revival", isMultiplayerHoeing, syncPointType,
            strictWaitCompleted, elapsedSeconds, consumed);
    }

    private static bool Eligible(
        string triggerType,
        bool isMultiplayerHoeing,
        string? syncPointType,
        bool strictWaitCompleted,
        double elapsedSeconds,
        bool consumed)
    {
        var triggerTime = CompletionTime.AddSeconds(elapsedSeconds);
        return PostTeleportRevivalProtectionDecisions.IsEligible(
            triggerType, isMultiplayerHoeing, syncPointType,
            strictWaitCompleted, CompletionTime, triggerTime, consumed);
    }
}
