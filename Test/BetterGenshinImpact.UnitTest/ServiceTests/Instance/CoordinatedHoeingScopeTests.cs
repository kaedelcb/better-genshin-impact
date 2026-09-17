using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedHoeingScopeTests
{
    [Fact]
    public async Task FailureFlowsFromNestedAsyncWorkAndDoesNotLeakToNextJob()
    {
        Assert.False(CoordinatedHoeingScope.IsActive);
        using (var scope = new CoordinatedHoeingScope(true))
        {
            await Task.Run(async () => { await Task.Yield(); CoordinatedHoeingScope.MarkIncomplete(); });
            Assert.True(scope.Incomplete);
        }
        Assert.False(CoordinatedHoeingScope.IsActive);
        using var next = new CoordinatedHoeingScope(true);
        Assert.False(next.Incomplete);
    }

    [Fact]
    public async Task IndependentJobsCannotMarkEachOther()
    {
        using var entered = new SemaphoreSlim(0);
        using var release = new SemaphoreSlim(0);
        var failing = Task.Run(async () =>
        {
            using var scope = new CoordinatedHoeingScope(true);
            entered.Release();
            await release.WaitAsync();
            CoordinatedHoeingScope.MarkIncomplete();
            Assert.True(scope.Incomplete);
        });
        await entered.WaitAsync();
        using (var other = new CoordinatedHoeingScope(false))
        {
            Assert.False(CoordinatedHoeingScope.IsActive);
            release.Release();
            await failing;
            Assert.False(other.Incomplete);
        }
    }
}
