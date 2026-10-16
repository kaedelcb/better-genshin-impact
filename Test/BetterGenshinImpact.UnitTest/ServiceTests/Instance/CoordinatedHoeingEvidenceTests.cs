using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedHoeingEvidenceTests
{
    [Fact]
    public void CompletedNativeDoesNotSealGroupOrHideTheNextNative()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using (var first = scope.BeginNative())
        {
            first.SetMultiplayerMode(true);
            first.Complete();
        }
        var firstResult = scope.Snapshot;
        Assert.Equal(1, firstResult.Completed);
        Assert.Equal(0, firstResult.Active);
        Assert.False(firstResult.Sealed);
        Assert.Null(firstResult.JobResult);

        using (var second = scope.BeginNative())
        {
            Assert.Equal(2, scope.Snapshot.Started);
            Assert.Equal(1, scope.Snapshot.Active);
            second.Fail(); // The second invocation can fail before its resources/configuration load.
        }
        var final = scope.Seal(TaskRunResult.Failed);
        Assert.Equal(1, final.Completed);
        Assert.Equal(1, final.Failed);
        Assert.Equal(0, final.Active);
        Assert.Equal(TaskRunResult.Failed, final.JobResult);
        Assert.Equal(1, firstResult.Started); // Earlier snapshots are immutable.
    }

    [Fact]
    public void FailureInLaterScriptPreservesBodySuccessAndRealGroupFailure()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using (var native = scope.BeginNative())
        {
            native.SetMultiplayerMode(true);
            native.Complete();
        }
        var result = scope.Seal(TaskRunResult.Failed);
        Assert.Equal(1, result.Completed);
        Assert.Equal(0, result.Failed); // This failure belongs to the later script, not the native.
        Assert.True(result.Sealed);
        Assert.Equal(TaskRunResult.Failed, result.JobResult);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void SoloOrUnknownModeCannotProveMultiplayerCompletion(bool? mode)
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var native = scope.BeginNative();
        if (mode.HasValue) native.SetMultiplayerMode(mode.Value);
        native.Complete();
        Assert.Equal(1, scope.Snapshot.Started);
        Assert.Equal(0, scope.Snapshot.Active);
        Assert.Equal(0, scope.Snapshot.Completed);
        Assert.Equal(0, scope.Snapshot.Failed);
    }

    [Fact]
    public void ConfigurationOrResourceFailureBeforeModeIsKnownIsRecorded()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var native = scope.BeginNative();
        native.Fail();
        var result = scope.Seal(TaskRunResult.Failed);
        Assert.Equal(1, result.Started);
        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Active);
    }

    [Fact]
    public void UserCancellationCannotBecomeACompletedNative()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var native = scope.BeginNative();
        native.SetMultiplayerMode(true);
        native.Cancel();
        native.Complete();
        native.Fail();
        var result = scope.Seal(TaskRunResult.Cancelled);
        Assert.Equal(1, result.Cancelled);
        Assert.Equal(0, result.Completed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Active);
    }

    [Fact]
    public void AbandonedNativeIsNotSuccessAndTerminalCallbacksAreIdempotent()
    {
        using var scope = new CoordinatedHoeingScope(true);
        var native = scope.BeginNative();
        native.SetMultiplayerMode(true);
        native.Dispose();
        native.Dispose();
        native.Complete();
        Assert.Equal(1, scope.Snapshot.Failed);
        Assert.Equal(0, scope.Snapshot.Completed);
        Assert.Equal(0, scope.Snapshot.Active);
    }

    [Fact]
    public void SealedJobIgnoresLateNativeAndConflictingFinalResults()
    {
        using var scope = new CoordinatedHoeingScope(true);
        using var pending = scope.BeginNative();
        pending.SetMultiplayerMode(true);
        var sealedResult = scope.Seal(TaskRunResult.Cancelled);
        pending.Complete();
        pending.Fail();
        CoordinatedHoeingScope.MarkIncomplete();
        using var late = scope.BeginNative();
        late.SetMultiplayerMode(true);
        late.Complete();
        Assert.Equal(sealedResult, scope.Seal(TaskRunResult.Ran));
        Assert.Equal(sealedResult, scope.Snapshot);
        Assert.False(scope.Incomplete);
    }

    [Fact]
    public void DisposalWithoutJobResultPreservesUnknownRatherThanAssumingSuccess()
    {
        var observations = new List<CoordinatedHoeingEvidence>();
        var scope = new CoordinatedHoeingScope(true, observer: observations.Add);
        using var pending = scope.BeginNative();
        scope.Dispose();
        pending.SetMultiplayerMode(true);
        pending.Complete();
        var result = observations[^1];
        Assert.True(result.Sealed);
        Assert.Null(result.JobResult);
        Assert.Equal(0, result.Completed);
        Assert.Equal(result, scope.Snapshot);
    }

    [Fact]
    public async Task NestedAsyncInvocationsUseOneJobButDoNotLeakIntoTheNextJob()
    {
        using (var scope = new CoordinatedHoeingScope(true))
        {
            await Task.Run(async () =>
            {
                await Task.Yield();
                Assert.Same(scope, CoordinatedHoeingScope.Current);
                using var native = CoordinatedHoeingScope.Current!.BeginNative();
                native.SetMultiplayerMode(true);
                native.Complete();
            });
            using (var repeated = scope.BeginNative())
            {
                repeated.SetMultiplayerMode(true);
                repeated.Complete();
            }
            Assert.Equal(2, scope.Seal(TaskRunResult.Ran).Completed);
        }
        Assert.Null(CoordinatedHoeingScope.Current);
        using var next = new CoordinatedHoeingScope(true);
        Assert.Equal(0, next.Snapshot.Started);
        Assert.Equal(0, next.Snapshot.Completed);
        Assert.False(next.Snapshot.Sealed);
    }

    [Fact]
    public async Task ConcurrentObservationsHaveIncreasingRevisionsAndExactCounts()
    {
        var observations = new List<CoordinatedHoeingEvidence>();
        using var scope = new CoordinatedHoeingScope(true, observer: observations.Add);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() =>
        {
            using var native = scope.BeginNative();
            native.SetMultiplayerMode(i % 3 != 0);
            if (i % 3 == 1) native.Cancel();
            else native.Complete();
        })));
        var result = scope.Seal(TaskRunResult.Cancelled);
        Assert.Equal(24, result.Started);
        Assert.Equal(8, result.Completed);
        Assert.Equal(8, result.Cancelled);
        Assert.Equal(0, result.Active);
        Assert.Equal(result, observations[^1]);
        Assert.True(observations.Zip(observations.Skip(1), (a, b) => a.Revision < b.Revision).All(x => x));
    }

    [Fact]
    public void BrokenObserverCannotChangeExecutionOutcome()
    {
        using var scope = new CoordinatedHoeingScope(true,
            observer: _ => throw new IOException("observer unavailable"));
        using var native = scope.BeginNative();
        native.SetMultiplayerMode(true);
        native.Complete();
        var result = scope.Seal(TaskRunResult.Ran);
        Assert.Equal(1, result.Completed);
        Assert.Equal(TaskRunResult.Ran, result.JobResult);
    }
}
