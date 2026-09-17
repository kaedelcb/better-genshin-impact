using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// [手动停止冷却 2026-09-14] CancellationContext.IsInManualStopCooldown：
/// ManualCancel 置位时间戳、Set() 保留、窗口内/外判定、普通 Cancel 不武装。
/// 使用独立上下文隔离历史，不依赖执行顺序。
/// </summary>
public class ManualStopCooldownTests
{
    // Each test owns its token context; singleton history must not leak between tests.
    private readonly CancellationContext context = new();
    [Fact]
    public void Cooldown_ShouldBeInactive_WhenNeverManualCancelled()
    {
        context.Set();

        Assert.False(context.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out var remaining));
        Assert.Equal(0, remaining);
    }

    [Fact]
    public void Cooldown_ShouldRemainActive_AfterManualCancel_AndSet()
    {
        context.Set();
        context.ManualCancel();

        Assert.True(context.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out var remaining));
        Assert.True(remaining is > 0 and <= 30);

        // 子任务起步不能撤销用户停止事实。
        context.Set();
        Assert.True(context.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void Cooldown_ShouldNotBeArmed_ByPlainCancelOrCancelTokenOnly()
    {
        context.Set();
        context.Cancel();
        Assert.False(context.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));

        context.Set();
        context.CancelTokenOnly();
        Assert.False(context.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public void Cooldown_ShouldExpire_OutsideWindow()
    {
        context.Set();
        context.ManualCancel();

        // 零宽窗口：任何非负 elapsed 都视为已过期
        Assert.False(context.IsInManualStopCooldown(TimeSpan.Zero, out _));
    }
}
