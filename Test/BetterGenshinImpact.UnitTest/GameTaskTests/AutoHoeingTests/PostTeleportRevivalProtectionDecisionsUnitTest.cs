#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// PostTeleportRevivalProtectionDecisions 的单元测试（普通 xunit Fact，非 PBT）。
/// 覆盖 design.md §Unit Tests 与 requirements.md 2.5, 4.1, 6.1：
///   - IsEligible：命中组合（联机 + teleport + 严格等待 + 未消费 + 0<=elapsed<=10，含 0/10 边界）
///     与不命中组合（负时间差、>10s、单机、非 teleport、未完成、已消费）。
///   - ComputeProgress：目标为当前段起点，不推进到下一段。
///   - TryConsume：一次性消费（首个 true、第二个 false、最终 consumed==1）。
/// </summary>
public class PostTeleportRevivalProtectionDecisionsUnitTest
{
    private const double WindowSeconds = PostTeleportRevivalProtectionDecisions.WindowSeconds;

    private static readonly DateTime CompletionTime = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static bool Eligible(
        bool isMultiplayerHoeing = true,
        string? syncPointType = "teleport",
        bool strictWaitCompleted = true,
        double elapsedSeconds = 2,
        bool consumed = false)
    {
        var revivalTime = CompletionTime.AddSeconds(elapsedSeconds);
        return PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing, syncPointType, strictWaitCompleted,
            CompletionTime, revivalTime, consumed);
    }

    // =========================================================================
    // IsEligible：命中组合
    // =========================================================================

    [Fact]
    public void IsEligible_AllConditionsMet_ReturnsTrue()
    {
        // 联机 + teleport + 严格等待 + 未消费 + 窗口内（2 秒）
        Assert.True(Eligible(elapsedSeconds: 2));
    }

    [Fact]
    public void IsEligible_ExactlyZeroSeconds_ReturnsTrue()
    {
        // 边界：恰好 0 秒命中
        Assert.True(Eligible(elapsedSeconds: 0));
    }

    [Fact]
    public void IsEligible_ExactlyWindowSeconds_ReturnsTrue()
    {
        // 边界：恰好 10 秒命中
        Assert.True(Eligible(elapsedSeconds: WindowSeconds));
    }

    [Fact]
    public void IsEligible_JustInsideWindow_ReturnsTrue()
    {
        // 边界：9.999 秒仍在窗口内命中
        Assert.True(Eligible(elapsedSeconds: WindowSeconds - 0.001));
    }

    // =========================================================================
    // IsEligible：不命中组合
    // =========================================================================

    [Fact]
    public void IsEligible_NegativeElapsed_ReturnsFalse()
    {
        // 复苏时刻早于完成时刻 → 负时间差不命中
        Assert.False(Eligible(elapsedSeconds: -1));
    }

    [Fact]
    public void IsEligible_JustBeyondWindow_ReturnsFalse()
    {
        // 10.001 秒超过窗口 → 不命中
        Assert.False(Eligible(elapsedSeconds: WindowSeconds + 0.001));
    }

    [Fact]
    public void IsEligible_BeyondWindow_ReturnsFalse()
    {
        // 11 秒超过窗口 → 不命中
        Assert.False(Eligible(elapsedSeconds: 11));
    }

    [Fact]
    public void IsEligible_SinglePlayer_ReturnsFalse()
    {
        // 单机不命中
        Assert.False(Eligible(isMultiplayerHoeing: false));
    }

    [Fact]
    public void IsEligible_AutoSyncPointType_ReturnsFalse()
    {
        // 自动生成同步点（"auto"）不命中
        Assert.False(Eligible(syncPointType: "auto"));
    }

    [Fact]
    public void IsEligible_ManualSyncPointType_ReturnsFalse()
    {
        // 手动设置同步点（"manual"）不命中
        Assert.False(Eligible(syncPointType: "manual"));
    }

    [Fact]
    public void IsEligible_OtherSyncPointType_ReturnsFalse()
    {
        // 其他非 teleport 类型不命中
        Assert.False(Eligible(syncPointType: "fight"));
    }

    [Fact]
    public void IsEligible_NullSyncPointType_ReturnsFalse()
    {
        // 空类型不命中
        Assert.False(Eligible(syncPointType: null));
    }

    [Fact]
    public void IsEligible_StrictWaitNotCompleted_ReturnsFalse()
    {
        // 严格等待未完成不命中
        Assert.False(Eligible(strictWaitCompleted: false));
    }

    [Fact]
    public void IsEligible_OpportunityConsumed_ReturnsFalse()
    {
        // 机会已消费不命中
        Assert.False(Eligible(consumed: true));
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

    [Fact]
    public void TryConsume_AfterAlreadyConsumed_ReturnsFalse()
    {
        // 预置已消费状态（1）：任何调用都失败
        var consumed = 1;
        Assert.False(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed));
        Assert.Equal(1, consumed);
    }
}
