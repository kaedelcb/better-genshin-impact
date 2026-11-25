using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoOnline;
using BetterGenshinImpact.Service.Execution;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

[Collection("TaskTakeoverIncident")]
public sealed class RoomActionMailboxTests
{
    [Fact]
    public async Task ConfiguredOnlineTaskSendsTargetAndNeverFallsBackWhenRejected()
    {
        using var peerLifetime = new CancellationTokenSource();
        var peer = Guid.NewGuid();
        try
        {
            using var scope = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "targeted-online", JobSource.Ui));
            using var actions = ConfigurationGroupRoomActions.Begin(scope, "targeted-online", 0, false);
            using var step = actions.EnterStep(NotifyOnlineTask.TaskName, 0, 0);
            var settings = new System.Collections.Generic.Dictionary<string, object?> { [SwitchAssistantRoomTask.TargetRoomName] = "房间 B" };
            var task = BetterGenshinImpact.GameTask.SoloTaskRegistry.CreateTask(NotifyOnlineTask.TaskName, null, settings)!;
            var generation = NotifyOnlineTask.CurrentGeneration;
            Assert.True(RoomActionMailbox.TryPeek(peer, peerLifetime.Token, out _));

            var execution = task.Start(CancellationToken.None);
            Assert.True(RoomActionMailbox.TryPeek(peer, peerLifetime.Token, out var signal));
            Assert.NotNull(signal);
            Assert.Equal("switchOnline", signal.Kind);
            Assert.Equal("房间 B", signal.TargetRoomName);
            Assert.True(RoomActionMailbox.Acknowledge(peer, signal.Id, false));
            await Assert.ThrowsAsync<RoomActionHandoffException>(() => execution);
            Assert.Equal(generation, NotifyOnlineTask.CurrentGeneration);
        }
        finally
        {
            peerLifetime.Cancel();
        }
    }

    [Fact]
    public async Task OnlyOriginalIpcPeerCanReceiveAndContinueTheChain()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var run = Guid.NewGuid();
        var stop = ExecutionScope.StopVersionNow;
        Assert.True(RoomActionMailbox.TryPeek(a, first.Token, out var empty));
        Assert.Null(empty);
        var change = new RoomActionMailbox.Signal(Guid.NewGuid(), run, "switch", "房间 B", stop);
        var pending = RoomActionMailbox.Post(change, null, CancellationToken.None);
        Assert.False(RoomActionMailbox.TryPeek(b, second.Token, out _));
        Assert.False(RoomActionMailbox.Acknowledge(b, change.Id, true));
        Assert.True(RoomActionMailbox.TryPeek(a, first.Token, out var offered));
        Assert.Equal(change, offered);
        Assert.True(RoomActionMailbox.Acknowledge(a, change.Id, true));
        Assert.True(RoomActionMailbox.Acknowledge(a, change.Id, true));
        Assert.Equal(a, (await RoomActionMailbox.WaitAsync(pending)).PeerId);
        first.Cancel();
        Assert.True(RoomActionMailbox.TryPeek(b, second.Token, out var next));
        Assert.Null(next);
        var online = new RoomActionMailbox.Signal(Guid.NewGuid(), run, "online", null, stop);
        var rejected = RoomActionMailbox.Post(online, a, CancellationToken.None);
        Assert.False((await RoomActionMailbox.WaitAsync(rejected)).Accepted);
        Assert.True(RoomActionMailbox.TryPeek(b, second.Token, out next));
        Assert.Null(next);
        second.Cancel();
    }
}
