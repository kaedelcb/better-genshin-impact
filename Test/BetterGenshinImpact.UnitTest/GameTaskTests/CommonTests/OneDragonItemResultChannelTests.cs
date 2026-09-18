using BetterGenshinImpact.GameTask.Common;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonTests;

/// <summary>
/// OneDragonItemResultChannel（R4.7 结果传播显式通道）夹具：
/// 上报优先级（失败不被后续覆盖）、AsyncLocal 随 ExecutionContext 流入 Task.Run、
/// 嵌套作用域退出恢复、无通道静默丢弃、Dispose 后上报忽略。
/// </summary>
public class OneDragonItemResultChannelTests
{
    [Fact]
    public void Report_FailureSticks_NotCoveredByLaterReports()
    {
        using var channel = OneDragonItemResultChannel.OpenScoped();
        channel.Report(OneDragonItemOutcome.Succeeded);
        channel.Report(OneDragonItemOutcome.Failed, "进壶失败");
        channel.Report(OneDragonItemOutcome.SkippedNormal, "活动结束");
        channel.Report(OneDragonItemOutcome.Succeeded);

        Assert.Equal(OneDragonItemOutcome.Failed, channel.Outcome);
        Assert.Equal("进壶失败", channel.Reason); // 失败原因不被覆盖
    }

    [Fact]
    public void Report_SkippedNormal_DistinguishedFromFailure()
    {
        using var channel = OneDragonItemResultChannel.OpenScoped();
        channel.Report(OneDragonItemOutcome.SkippedNormal, "未配置战斗策略");

        Assert.Equal(OneDragonItemOutcome.SkippedNormal, channel.Outcome);
        Assert.NotEqual(OneDragonItemOutcome.Failed, channel.Outcome); // 正常跳过 ≠ 失败
    }

    [Fact]
    public async Task Ambient_FlowsIntoTaskRun_ExecutionContext()
    {
        using var channel = OneDragonItemResultChannel.OpenScoped();
        // 模拟执行包装：子项在 Task.Run 内吞异常后显式上报
        await Task.Run(() =>
        {
            OneDragonItemResultChannel.ReportCurrent(OneDragonItemOutcome.Failed, "吞异常点上报");
        });

        Assert.Equal(OneDragonItemOutcome.Failed, channel.Outcome);
        Assert.Equal("吞异常点上报", channel.Reason);
    }

    [Fact]
    public void NestedScope_DisposeRestoresPrevious()
    {
        using var outer = OneDragonItemResultChannel.OpenScoped();
        outer.Report(OneDragonItemOutcome.SkippedNormal, "外层");
        using (var inner = OneDragonItemResultChannel.OpenScoped())
        {
            Assert.NotSame(outer, OneDragonItemResultChannel.Current);
            inner.Report(OneDragonItemOutcome.Failed, "内层失败");
            Assert.Equal(OneDragonItemOutcome.Failed, inner.Outcome);
        }
        Assert.Same(outer, OneDragonItemResultChannel.Current); // 退出恢复上一层
        Assert.Equal(OneDragonItemOutcome.SkippedNormal, outer.Outcome); // 外层不受内层污染
    }

    [Fact]
    public void NoChannel_ReportCurrent_SilentNoOp()
    {
        Assert.Null(OneDragonItemResultChannel.Current);
        OneDragonItemResultChannel.ReportCurrent(OneDragonItemOutcome.Failed, "独立运行无通道");
        Assert.Null(OneDragonItemResultChannel.Current); // 静默丢弃，保持公版独立运行行为
    }

    [Fact]
    public void DisposedChannel_IgnoresFurtherReports()
    {
        var channel = OneDragonItemResultChannel.OpenScoped();
        channel.Report(OneDragonItemOutcome.SkippedNormal, "活动结束");
        channel.Dispose();
        channel.Report(OneDragonItemOutcome.Failed, "迟到的失败");

        Assert.Equal(OneDragonItemOutcome.SkippedNormal, channel.Outcome);
        Assert.Null(OneDragonItemResultChannel.Current); // Dispose 后环境已清理
    }

    [Fact]
    public void FailedException_CarriesReason()
    {
        var ex = new OneDragonItemFailedException("无法进入尘歌壶");
        Assert.Equal("无法进入尘歌壶", ex.Message);
    }
}
