using System.Reflection;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class OnlineRestartEpochTests
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;

    // Real SDK handshakes and snapshot callback, with only BGI transport replaced.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RestartWithoutTakeoverTicket_ResetsOldFloor_ConsumesFreshOnlineExactlyOnce(bool manuallyCleared, bool stale)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        await using var pipe = new BgiInstancePipeDouble();
        pipe.BodyForTest = (op, _) => op == "ext.task.status"
            ? (true, null, null, new { stateRevision = 342L, onlineGeneration = 82, onlineTriggeredAt = DateTime.UtcNow.AddMinutes(stale ? -25 : 0) })
            : null;
        var previous = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var client = new BgiExternalClient(AssistantDataDirectory.Root);
        var vm = new MainViewModel();
        var lifecycle = (OnlineIntentLifecycle)Get(vm, "_intentLifecycle")!;
        if (manuallyCleared) lifecycle.OnManualCleared();
        var count = 0;
        client.EpochChanged += epoch =>
        {
            count++;
            Invoke(vm, "OnBgiExternalEpochChanged", epoch);
        };
        client.StatusSnapshotUpdated += snapshot => Invoke(vm, "OnBgiStatusSnapshotUpdated", snapshot);
        pipe.Start();
        try
        {
            Assert.True(await Connect(client));
            Assert.Equal(0, count);
            // Represent the previous process identity; reconnect validates the real replacement.
            typeof(BgiEpoch).GetProperty(nameof(BgiEpoch.StartTicksUtc))!.SetValue(client.ServerEpoch,
                client.ServerEpoch!.StartTicksUtc - 1);
            Set(vm, "_taskCenterRequiredStatusRevision", 900L);
            // An unsuccessful connection attempt must not lose the last verified identity.
            Invoke(client, "DisposePipe");
            Assert.Null(client.ServerEpoch);
            pipe.ReportedProcessIdOverride = int.MaxValue;
            Assert.False(await Connect(client));
            Assert.Equal(0, count);
            pipe.ReportedProcessIdOverride = null;
            Assert.True(await Connect(client));
            Assert.Equal(1, count);
            Assert.Equal(0L, Get(vm, "_taskCenterRequiredStatusRevision"));
            await client.RefreshStatusSnapshotAsync("restart-regression");
            if (stale)
            {
                Assert.False(lifecycle.IsOnlineReady);
                Assert.False(lifecycle.TryBeginAllReady(82));
                return;
            }
            Assert.True(lifecycle.IsOnlineReady);
            Assert.Equal(82, lifecycle.CurrentOnlineGeneration);
            Assert.True(lifecycle.TryBeginAllReady(82));
            await client.RefreshStatusSnapshotAsync("duplicate-snapshot");
            Assert.False(lifecycle.TryBeginAllReady(82));
            lifecycle.OnManualCleared();
            await client.RefreshStatusSnapshotAsync("late-after-stop");
            Assert.False(lifecycle.IsOnlineReady);
            Assert.False(lifecycle.TryBeginAllReady(82));
            Assert.True(lifecycle.ManuallyClearedOnline);
            lifecycle.NoteScheduledOnlineConfigured();
            var scheduled = lifecycle.RaiseLocalIntent("scheduled");
            Assert.True(scheduled > 82);
            Assert.True(lifecycle.TryBeginAllReady(scheduled));
            Assert.False(lifecycle.TryBeginAllReady(scheduled));
        }
        finally { BgiExternalClient.PipeNameOverrideForTest = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameProcessReconnect_PreservesStatusFloor_AndDoesNotAnnounceRestart(bool hasTicket)
    {
        await using var pipe = new BgiInstancePipeDouble();
        var previous = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var client = new BgiExternalClient(AssistantDataDirectory.Root);
        var vm = new MainViewModel();
        Set(vm, "_taskCenterRequiredStatusRevision", 900L);
        var count = 0;
        client.EpochChanged += epoch => { count++; Invoke(vm, "OnBgiExternalEpochChanged", epoch); };
        pipe.Start();
        try
        {
            Assert.True(await Connect(client));
            client.TakeoverTicket = hasTicket ? "retained-owner-ticket" : null;
            Invoke(client, "DisposePipe");
            Assert.Null(client.ServerEpoch);
            Assert.True(await Connect(client));
            Assert.Equal(0, count);
            Assert.Equal(900L, Get(vm, "_taskCenterRequiredStatusRevision"));
            Assert.Equal(hasTicket ? "retained-owner-ticket" : null, client.TakeoverTicket);
        }
        finally { BgiExternalClient.PipeNameOverrideForTest = previous; }
    }

    private static async Task<bool> Connect(BgiExternalClient client)
    {
        if (client.ServerEpoch is null && Get(client, "_readerLoop") is Task reader)
            await reader.WaitAsync(TimeSpan.FromSeconds(10));
        return await ((Task<bool>)Invoke(client, "TryEstablishOnceAsync", CancellationToken.None)!).WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static object? Invoke(object target, string name, params object[] args)
        => target.GetType().GetMethod(name, Flags)!.Invoke(target, args);
    private static object? Get(object target, string name) => target.GetType().GetField(name, Flags)!.GetValue(target);
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, Flags)!.SetValue(target, value);
}
