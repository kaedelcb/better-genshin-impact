using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskCenterOnlineSendGateTests
{
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
