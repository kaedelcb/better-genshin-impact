using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// [手动停止冷却 2026-09-14] CancellationContext.IsInManualStopCooldown：
/// ManualCancel 置位时间戳、Set() 清零、窗口内/外判定、普通 Cancel 不武装。
/// 注意：CancellationContext 是单例，每个用例开头 Set() 自清，不依赖执行顺序。
/// </summary>
public class ManualStopCooldownTests
{
    [Fact]
    public void Cooldown_ShouldBeInactive_WhenNeverManualCancelled()
    {
        CancellationContext.Instance.Set();

        Assert.False(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out var remaining));
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void Cooldown_ShouldBeActive_AfterManualCancel_AndClearedBySet()
    {
        CancellationContext.Instance.Set();
        CancellationContext.Instance.ManualCancel();

        Assert.True(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out var remaining));
        Assert.True(remaining is > 0 and <= 30);

        // 任务起步（Set）= 冷却解除：新任务在干净上下文中执行
        CancellationContext.Instance.Set();
        Assert.False(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void Cooldown_ShouldNotBeArmed_ByPlainCancelOrCancelTokenOnly()
    {
        CancellationContext.Instance.Set();
        CancellationContext.Instance.Cancel();
        Assert.False(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));

        CancellationContext.Instance.Set();
        CancellationContext.Instance.CancelTokenOnly();
        Assert.False(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void Cooldown_ShouldExpire_OutsideWindow()
    {
        CancellationContext.Instance.Set();
        CancellationContext.Instance.ManualCancel();

        // 零宽窗口：任何非负 elapsed 都视为已过期
        Assert.False(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.Zero, out _));
    }
}
