using BetterGenshinImpact.Core.Script;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>
/// [R5「task.stop 定向身份绑定」批次，测试基础设施修复] <see cref="ManualStopCooldownTests"/> 的
/// <c>ManualCancel()</c> 会经 <c>CancellationContext.CancelCore</c> 调用**进程级静态**
/// <c>ExecutionScope.StopActive(manual: true)</c>，推进全局停止水位。默认并行调度下，
/// 它可与任何其他集合里正在进行的 <c>ExecutionScope.Start</c> 竞争：新建的根在
/// <c>ThrowIfStopped</c> 处看到水位已变，误报「根流程已停止或已让位」。
/// 实测复现（仅这两个类同批运行，未触及本轮产品改动）：6/6 次 <c>ExecutionScopeSkippedTests</c>
/// 的 <c>Observe_SkippedThenFailed_FailureNotCoveredBySkip</c> 失败；单独运行本类 4/4、对方类 5/5 通过。
/// 因此把本类放入不可并行集合（与 <c>TaskTakeoverIncident</c> 同纪律），断言与用例一字未改。
/// </summary>
[CollectionDefinition("GlobalExecutionStopWatermark", DisableParallelization = true)]
public sealed class GlobalExecutionStopWatermarkCollection;

/// <summary>
/// [手动停止冷却 2026-09-14] CancellationContext.IsInManualStopCooldown：
/// ManualCancel 置位时间戳、Set() 保留、窗口内/外判定、普通 Cancel 不武装。
/// 使用独立上下文隔离历史，不依赖执行顺序。
/// </summary>
[Collection("GlobalExecutionStopWatermark")]
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
