using System.IO;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// Physical reset has already succeeded. These checks decide whether lost server
// reset receipts can be repaired without resetting or cleaning a successor.
public sealed class ManualResetCleanupRecoveryTests
{
    private static CoordinatedBatchSnapshot Old() => new("old", 12, 0, 0, 4, "aborted", "manual_reset");

    [Fact]
    public async Task AlreadyBlockedUserCancellationCannotLockOutManualResetRetirement()
    {
        var batch = new OnlineHoeingBatch(Old() with
        {
            Protocol = "hoeing.start.v2", ParticipantUids = ["member"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["member"] = [new("group", "group")] }
        }, "member", new MultiplayerHoeingAssistant.Models.TaskConflictPolicySettings());
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = batch.Cts.Token.Register(() => { entered.TrySetResult(); release.Wait(); });
        var stop = Task.Run(() => batch.Cancel());
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Run(() => batch.MarkSettled(waitForCallbacks: false)).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(batch.IsAlive);
            Assert.False(stop.IsCompleted);
        }
        finally { release.Set(); await stop.WaitAsync(TimeSpan.FromSeconds(2)); }
    }

    [Fact]
    public async Task MissingReceiptReauthorizesOnlyOriginalBatchAndRetriesCleanup()
    {
        var confirmed = new List<string>();
        await MainViewModel.ConfirmManualResetCleanupAsync("first", "old", "member",
            (id, batch) =>
            {
                Assert.Equal("old", batch);
                confirmed.Add(id);
                return id == "first" ? Task.FromException(new IOException("receipt lost")) : Task.CompletedTask;
            },
            () => Task.FromResult<CoordinatedBatchSnapshot?>(Old()),
            (id, generation, batch, uid) =>
            {
                Assert.Equal(12, generation);
                Assert.Equal("old", batch);
                Assert.Equal("member", uid);
                Assert.NotEqual("first", id);
                return Task.FromResult(new CoordinatedBatchResetResult { Found = true, BatchId = batch });
            });
        Assert.Equal(2, confirmed.Count);
        Assert.StartsWith("reset_cleanup_", confirmed[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOldBatchOrSuccessorNeedsNoFurtherReset(bool successor)
    {
        await MainViewModel.ConfirmManualResetCleanupAsync("first", "old", "member",
            (_, _) => Task.FromException(new IOException("old identity no longer current")),
            () => Task.FromResult<CoordinatedBatchSnapshot?>(successor ? Old() with { BatchId = "next" } : null),
            (_, _, _, _) => throw new Exception("must not reset successor"));
    }

    [Fact]
    public async Task SuccessorAppearingDuringRepairIsLeftUntouched()
    {
        var reads = 0;
        var confirmations = 0;
        await MainViewModel.ConfirmManualResetCleanupAsync("first", "old", "member",
            (_, _) => { confirmations++; return Task.FromException(new IOException("lost")); },
            () => Task.FromResult<CoordinatedBatchSnapshot?>(++reads == 1 ? Old() : Old() with { BatchId = "next" }),
            (_, _, _, _) => Task.FromResult(new CoordinatedBatchResetResult { Found = true, BatchId = "next", Reason = "stale_reset" }));
        Assert.Equal(1, confirmations);
    }

    [Fact]
    public async Task UnreachableServerIsNotReportedAsCleaned()
    {
        await Assert.ThrowsAsync<IOException>(() => MainViewModel.ConfirmManualResetCleanupAsync("first", "old", "member",
            (_, _) => Task.FromException(new IOException("offline")),
            () => Task.FromException<CoordinatedBatchSnapshot?>(new IOException("offline")),
            (_, _, _, _) => throw new Exception("cannot authorize offline")));
    }
}
