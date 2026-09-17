using MultiplayerHoeingAssistant.Services;
using System.IO;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedBatchReadsTests
{
    [Fact]
    public async Task TemporaryReadLossRecoversWithoutStartingAnything()
    {
        var reads = 0;
        var result = await CoordinatedBatchReads.ReadAsync<string>(_ => ++reads switch
        {
            1 => throw new IOException(),
            2 => Task.FromResult<string?>(null),
            _ => Task.FromResult<string?>("found")
        }, default);
        Assert.Equal("found", result);
        Assert.Equal(3, reads);
    }
    [Fact]
    public async Task MissingStateHasFiniteBudget()
    {
        var reads = 0;
        Assert.Null(await CoordinatedBatchReads.ReadAsync<string>(_ =>
        { reads++; return Task.FromResult<string?>(null); }, default));
        Assert.Equal(3, reads);
    }
    [Fact]
    public async Task CancellationNeverRetries()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CoordinatedBatchReads.ReadAsync<string>(
            _ => throw new InvalidOperationException("must not read"), stop.Token));
    }
}
