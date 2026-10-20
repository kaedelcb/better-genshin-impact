using System.IO;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CoordinatedBeginContinuationTests
{
    private static CoordinatedBatchSnapshot Accepted(string request) => new("original-batch", 7, 0, 0, 0, "registering", "")
        { RequestId = request, Protocol = "hoeing.start.v2", StateContinuation = true };

    [Fact]
    public async Task NeverDeliveredRequestIsRetriedWithItsOriginalIdAfterConnectionRecovers()
    {
        const string request = "same-click"; var sends = 0; var queries = 0;
        var result = await MainViewModel.ContinueCoordinatedBeginAsync(request,
            _ => ++sends == 1 ? throw new IOException("not delivered") : Task.FromResult(Accepted(request)),
            _ => { queries++; return Task.FromResult<CoordinatedBatchSnapshot?>(null); },
            (_, _) => throw new InvalidOperationException("not stopped"), () => { }, _ => { }, CancellationToken.None,
            _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(request, result.RequestId); Assert.Equal(2, sends); Assert.Equal(1, queries);
    }

    [Fact]
    public async Task LostAcceptanceReplyIsAdoptedWithoutCreatingAnotherBatch()
    {
        const string request = "lost-reply"; var sends = 0; var queries = 0;
        var result = await MainViewModel.ContinueCoordinatedBeginAsync(request,
            _ => { sends++; throw new IOException("created but reply lost"); },
            _ => ++queries == 1 ? throw new IOException("query temporarily unavailable")
                : Task.FromResult<CoordinatedBatchSnapshot?>(Accepted(request)),
            (_, _) => throw new InvalidOperationException("not stopped"), () => { }, _ => { }, CancellationToken.None,
            _ => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("original-batch", result.BatchId); Assert.Equal(1, sends); Assert.Equal(2, queries);
    }

    [Fact]
    public async Task StopDuringSendRevokesTheLateOriginalAcceptanceWithoutResending()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<CoordinatedBatchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0; var cancels = 0;
        var pending = MainViewModel.ContinueCoordinatedBeginAsync("late",
            _ => { sends++; entered.SetResult(); return reply.Task; },
            _ => throw new InvalidOperationException("receipt is available"),
            (batch, _) => { Assert.Equal("original-batch", batch.BatchId); cancels++; return Task.CompletedTask; },
            () => { }, _ => { }, stop.Token, _ => Task.CompletedTask);
        await entered.Task; stop.Cancel(); reply.SetResult(Accepted("late"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sends); Assert.Equal(1, cancels);
    }

    [Fact]
    public async Task StopWhileWaitingStillResolvesTheLostReplyAndCancelsOnlyThatBatch()
    {
        using var stop = new CancellationTokenSource(); var sends = 0; var cancels = 0;
        var pending = MainViewModel.ContinueCoordinatedBeginAsync("stopped-pending",
            _ => { sends++; throw new IOException("lost reply"); },
            _ => Task.FromResult<CoordinatedBatchSnapshot?>(Accepted("stopped-pending")),
            (batch, _) => { Assert.Equal("stopped-pending", batch.RequestId); cancels++; return Task.CompletedTask; },
            () => { }, _ => { }, stop.Token,
            _ => { stop.Cancel(); throw new OperationCanceledException(stop.Token); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sends); Assert.Equal(1, cancels);
    }

    [Fact]
    public async Task StopBeforeDeliveryIsKnownDoesNotCreateTheRequestAfterwards()
    {
        using var stop = new CancellationTokenSource(); var sends = 0; var queries = 0;
        var pending = MainViewModel.ContinueCoordinatedBeginAsync("not-sent",
            _ => { sends++; throw new IOException("before delivery"); },
            _ => { queries++; return Task.FromResult<CoordinatedBatchSnapshot?>(null); },
            (_, _) => throw new InvalidOperationException("nothing accepted"), () => { }, _ => { }, stop.Token,
            _ => { stop.Cancel(); return Task.CompletedTask; });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sends); Assert.Equal(1, queries);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedRoomRevokesTheOriginalLateAcceptanceWithoutSendingInTheNewRoom(bool replyLost)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reply = new TaskCompletionSource<CoordinatedBatchSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = false; var sends = 0; var originalQueries = 0; var originalResets = 0;
        var pending = MainViewModel.ContinueCoordinatedBeginAsync("old-room-request",
            _ => { Assert.False(changed); sends++; entered.SetResult(); return reply.Task; },
            _ => { Assert.True(changed); originalQueries++; return Task.FromResult<CoordinatedBatchSnapshot?>(Accepted("old-room-request")); },
            (batch, _) => { Assert.True(changed); Assert.Equal("old-room-request", batch.RequestId); originalResets++; return Task.CompletedTask; },
            () => { if (changed) throw new ControlContextRevokedException("changed room", CancellationToken.None); },
            _ => { }, CancellationToken.None, _ => Task.CompletedTask);
        await entered.Task;
        changed = true;
        if (replyLost) reply.SetException(new IOException("original server accepted before room switch"));
        else reply.SetResult(Accepted("old-room-request"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, sends); Assert.Equal(replyLost ? 1 : 0, originalQueries); Assert.Equal(1, originalResets);
    }

}
