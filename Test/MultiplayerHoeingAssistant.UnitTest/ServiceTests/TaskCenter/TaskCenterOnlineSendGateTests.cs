using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskCenterOnlineSendGateTests
{
    [Fact]
    public async Task PriorityCloseDoesNotWaitForLostReply_AndStillBlocksSuccessors()
    {
        var gate = new TaskCenterOnlineSendGate();
        var inFlight = await gate.EnterAsync(CancellationToken.None);
        gate.CloseForPriority();
        Assert.True(gate.IsClosed);
        var next = gate.EnterAsync(CancellationToken.None).AsTask();
        Assert.False(next.IsCompleted);
        await inFlight.DisposeAsync();
        Assert.False(next.IsCompleted);
        await gate.OpenAsync();
        await using var accepted = await next.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task CancelledOrdinaryCloseDoesNotLeaveAnUnownedClosedGate()
    {
        var gate = new TaskCenterOnlineSendGate();
        var held = await gate.EnterAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var close = gate.CloseAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => close);
        Assert.False(gate.IsClosed);
        await held.DisposeAsync();
        await using var next = await gate.EnterAsync(CancellationToken.None);
    }

    [Fact]
    public async Task CloseWaitsForExistingSend_AndHoldsSuccessorUntilOpen()
    {
        var gate = new TaskCenterOnlineSendGate();
        var first = await gate.EnterAsync(CancellationToken.None);
        var close = gate.CloseAsync(CancellationToken.None);
        Assert.False(close.IsCompleted);
        await first.DisposeAsync();
        await close.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(gate.IsClosed);

        var successor = gate.EnterAsync(CancellationToken.None).AsTask();
        Assert.False(successor.IsCompleted);
        await gate.OpenAsync();
        await using var second = await successor.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(gate.IsClosed);
    }
}
