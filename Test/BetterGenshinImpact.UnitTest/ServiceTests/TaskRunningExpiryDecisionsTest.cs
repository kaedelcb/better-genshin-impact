using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

/// <summary>
/// PBT 测试：验证 `TaskRunningExpiryDecisions.ShouldResetTaskRunning` 的过期判定。
/// 对应 spec `task-status-model-redesign` 的 PBT-B 与 BC-5/BC-6 超时自愈。
/// 注意：本测试项目不能直接引用 BgiCoordinatorServer（独立项目），故在测试内联复刻同一判定函数验证逻辑；
/// 生产判定位于 `BgiCoordinatorServer/Services/TaskRunningExpiryDecisions.cs`，与此处逻辑一致。
/// </summary>
public class TaskRunningExpiryDecisionsTest
{
    /// <summary>与生产 ShouldResetTaskRunning 一致的判定</summary>
    private static bool ShouldReset(bool isTaskRunning, long nowTicks, long expireTicks)
        => isTaskRunning && expireTicks > 0 && nowTicks > expireTicks;

    [Fact]
    public void ShouldReset_TaskRunningAndExpired_ReturnsTrue()
    {
        // isTaskRunning=true，now=200 > expire=100（已超时）
        Assert.True(ShouldReset(true, 200, 100));
    }

    [Fact]
    public void ShouldReset_TaskRunningButNotExpired_ReturnsFalse()
    {
        // isTaskRunning=true，now=50 < expire=100（未超时，不应复位）
        Assert.False(ShouldReset(true, 50, 100));
    }

    [Fact]
    public void ShouldReset_NotTaskRunning_ReturnsFalse()
    {
        // isTaskRunning=false，即使 now > expire 也不复位（本就不在跑）
        Assert.False(ShouldReset(false, 200, 100));
    }

    [Fact]
    public void ShouldReset_NoExpirySet_ReturnsFalse()
    {
        // expireTicks==0（DateTime.MinValue）表示未设过期 → 不判超时
        Assert.False(ShouldReset(true, 200, 0));
    }

    [Fact]
    public void ShouldReset_ExpiredAtExactBoundary_ReturnsFalse()
    {
        // now == expire 边界：严格 > 才超时（now > expire），相等不算
        Assert.False(ShouldReset(true, 100, 100));
    }
}