using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskCenterOnlineSendGateTests
{
    [Fact]
    public async Task PhysicalResetInvalidatesLostLeaseWaiters_AndOldDisposeCannotReleaseSuccessor()
    {
        var gate = new TaskCenterOnlineSendGate();
        var old = await gate.EnterAsync(default);
        var waiter = gate.EnterAsync(default).AsTask();
        gate.CloseForPriority();
        gate.ResetAfterPhysicalBgiReset();
        await Assert.ThrowsAsync<RunRecordConflictException>(() => waiter).WaitAsync(TimeSpan.FromSeconds(2));
        await using var current = await gate.EnterAsync(default).AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        await old.DisposeAsync();
        using var budget = new CancellationTokenSource();
        var blocked = gate.EnterAsync(budget.Token).AsTask();
        Assert.False(blocked.IsCompleted);
        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
    }

    [Fact]
    public async Task RevokedScopeCannotOpenCloseOrPublishPortPrefix_AfterPhysicalReset()
    {
        var gate = new TaskCenterOnlineSendGate();
        var authority = new TaskCenterDriveAuthority();
        var invoked = false;
        using (authority.Enter())
        {
            authority.Revoke();
            gate.ResetAfterPhysicalBgiReset();
            Assert.Throws<RunRecordConflictException>(() => gate.CloseForPriority());
            await Assert.ThrowsAsync<RunRecordConflictException>(() => gate.OpenAsync());
            await Assert.ThrowsAsync<RunRecordConflictException>(() => TaskCenterDriveAuthority.StartPortCall(() =>
            { invoked = true; return Task.CompletedTask; }));
            await Assert.ThrowsAsync<RunRecordConflictException>(() => gate.EnterAsync(default).AsTask());
        }
        Assert.False(invoked);
        Assert.False(gate.IsClosed);
        await using var next = await gate.EnterAsync(default);
    }

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
