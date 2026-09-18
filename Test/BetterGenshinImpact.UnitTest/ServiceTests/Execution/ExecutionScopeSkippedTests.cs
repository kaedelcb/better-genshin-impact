using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service.Execution;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

/// <summary>
/// ExecutionScope.Observe 的 Skipped 聚合规则夹具（R4.7 D6 + ASTRA 二轮 B6）：
/// 正常跳过分别表达（不算成功执行也不算失败、不计 FailureCount）；
/// 失败不被跳过覆盖；Cancelled/Preempted 始终覆盖。
/// </summary>
public class ExecutionScopeSkippedTests
{
    private static ExecutionScope StartScope()
        => ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "agg-test", JobSource.OneDragonInternal));

    [Fact]
    public void Observe_Skipped_DoesNotCountFailure_ExpressesSkipped()
    {
        using var scope = StartScope();
        scope.Observe(TaskRunResult.Skipped);

        Assert.Equal(TaskRunResult.Skipped, scope.Result); // 分别表达
        Assert.Equal(0, scope.FailureCount); // 不算失败
    }

    [Fact]
    public void Observe_SkippedThenFailed_FailureNotCoveredBySkip()
    {
        using var scope = StartScope();
        scope.Observe(TaskRunResult.Skipped);
        scope.Observe(TaskRunResult.Failed);

        Assert.Equal(TaskRunResult.Failed, scope.Result); // 失败覆盖跳过
        Assert.Equal(1, scope.FailureCount);
    }

    [Fact]
    public void Observe_FailedThenSkipped_FailureSticks()
    {
        using var scope = StartScope();
        scope.Observe(TaskRunResult.Failed);
        scope.Observe(TaskRunResult.Skipped);

        Assert.Equal(TaskRunResult.Failed, scope.Result); // 失败不被后续跳过覆盖
        Assert.Equal(1, scope.FailureCount);
    }

    [Fact]
    public void Observe_SkippedThenCancelled_CancelledWins()
    {
        using var scope = StartScope();
        scope.Observe(TaskRunResult.Skipped);
        scope.Observe(TaskRunResult.Cancelled);

        Assert.Equal(TaskRunResult.Cancelled, scope.Result);
        Assert.Equal(0, scope.FailureCount);
    }

    [Fact]
    public void Observe_RanThenSkipped_SkippedExpressesNotFullyRan()
    {
        using var scope = StartScope();
        scope.Observe(TaskRunResult.Ran);
        scope.Observe(TaskRunResult.Skipped);

        Assert.Equal(TaskRunResult.Skipped, scope.Result); // Ran → Skipped（有项被跳过 ≠ 全部成功执行）
        Assert.Equal(0, scope.FailureCount);
    }
}