using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedTaskQueueTests
{
    private static BgiTaskCoordinator Create() => new(() => true, (_, _) => { }, NullLogger.Instance,
        slotPollInterval: TimeSpan.FromMilliseconds(10));

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task HoeingFailureRetainsItsCodeForAssistantRecovery()
    {
        using var queue = Create();
        var submitted = queue.Submit(new(1, "elite", null, 0,
            (_, _) => Task.FromException<bool>(new HoeingIncompleteException())));
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        Assert.Equal("hoeing_incomplete", queue.QueryItemStatus(submitted.TaskHandle).ErrorCode);
    }

    [Fact]
    public async Task CancellationIsNotTerminalUntilExecutorCleanupReturns()
    {
        using var queue = Create();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleaning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = queue.Submit(new(1, "elite", null, 0, async (_, ct) =>
        {
            started.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { cleaning.SetResult(); await allowCleanup.Task; }
            return false;
        }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queue.CancelByHandle(submitted.TaskHandle, ownedOnly: true);
        await cleaning.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { Assert.Equal("running", queue.QueryItemStatus(submitted.TaskHandle).Status); }
        finally { allowCleanup.TrySetResult(); }
        await Until(() => queue.QueryItemStatus(submitted.TaskHandle).Status == "failed");
        Assert.Equal("task_cancelled", queue.QueryItemStatus(submitted.TaskHandle).ErrorCode);
    }

    [Fact]
    public async Task StaleOwnedCancelCannotCancelNextJob()
    {
        using var queue = Create();
        var first = queue.Submit(new(1, "elite", null, 0, (_, _) => Task.FromResult(false)));
        await Until(() => queue.QueryItemStatus(first.TaskHandle).Status == "completed");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken nextToken = default;
        var next = queue.Submit(new(2, "other", null, 0, async (_, ct) =>
        { nextToken = ct; started.SetResult(); await release.Task; return false; }));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Equal(BgiTaskCoordinator.CancelOutcome.NotFound, queue.CancelByHandle(first.TaskHandle, true));
            Assert.False(nextToken.IsCancellationRequested);
        }
        finally { release.TrySetResult(); }
        await Until(() => queue.QueryItemStatus(next.TaskHandle).Status == "completed");
    }
}
