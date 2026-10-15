using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("UnifiedHoeingStartLifecycle")]
public sealed class CoordinatedStopTargetTests
{
    private static OnlineHoeingBatch Batch(string id) => new(new CoordinatedBatchSnapshot(id, 1, 0, 0, 0, "running", "")
    {
        Protocol = "hoeing.start.v2",
        ParticipantUids = ["self"],
        Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
        {
            ["self"] = new[] { new CoordinatedHoeingBinding("锄地", "group") }
        }
    }, "self", new TaskConflictPolicySettings());

    private static MainViewModel Vm(OnlineHoeingBatch batch)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "停止入口测试必须使用隔离 NEXUSBGI_DATA_ROOT");
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, batch);
        return vm;
    }

    private static Task<CommandResult> Stop(MainViewModel vm, string? id, bool marked = true)
    {
        var command = new RemoteCommand
        {
            Cmd = "stop", Target = ["self"],
            Params = marked ? new() { ["hoeingBatchId"] = id ?? "" } : null
        };
        return (Task<CommandResult>)typeof(MainViewModel)
            .GetMethod("ExecuteCommandWithHoeingStopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object[] { command })!;
    }

    [Fact]
    public async Task MatchingStopCancelsOwnedBatchAndReportsPendingCleanup()
    {
        var batch = Batch("owned");
        var result = await Stop(Vm(batch), "owned");
        Assert.Equal("success", result.Status);
        Assert.Equal(ResponsibilityState.Pending, result.ResponsibilityState);
        Assert.False(result.IsTerminal);
        Assert.True(batch.IsCancellationRequested);
        Assert.True(batch.IsAlive);
    }

    [Theory]
    [InlineData("previous", true)]
    [InlineData("", true)]
    [InlineData(null, false)]
    public async Task DelayedOrUnidentifiedStopCannotCancelCurrentBatch(string? expected, bool marked)
    {
        var batch = Batch("successor");
        var result = await Stop(Vm(batch), expected, marked);
        Assert.Equal("failed", result.Status);
        Assert.Equal("hoeing_stop_target_changed", result.ErrorCode);
        Assert.False(batch.IsCancellationRequested);
    }

    [Fact]
    public async Task OldBatchStopWithNoActiveBatchNeverFallsBackToGenericExecutor()
    {
        var vm = Vm(Batch("old"));
        typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, null);
        var result = await Stop(vm, "old");
        Assert.Equal("hoeing_stop_target_changed", result.ErrorCode);
        Assert.Contains("未停止其他任务", result.Message);
    }

    [Fact]
    public void SelectingAnotherMemberFreezesOnlyTheirObservedTargetWithoutCancellingSelf()
    {
        var self = Batch("self-batch");
        var vm = Vm(self);
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new AssistConfig { PlayerUid = "self" });
        vm.Members.Add(new MemberViewModel
        {
            PlayerUid = "peer", HoeingBatchId = "peer-batch", HoeingStartPhase = "running"
        });
        var frozen = (Dictionary<string, object>)typeof(MainViewModel)
            .GetMethod("FreezeStopTargets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object?[] { new[] { "peer" }, null })!;
        var targets = Assert.IsType<Dictionary<string, string>>(frozen["hoeingBatchIds"]);
        Assert.Equal("peer-batch", targets["peer"]);
        Assert.False(targets.ContainsKey("self"));
        Assert.False(self.IsCancellationRequested);
        vm.Members[0].HoeingBatchId = "peer-successor";
        Assert.Equal("peer-batch", targets["peer"]);
    }
    [Fact]
    public void ObserverUsingTheSameUidFreezesTheRemoteExecutorBatch()
    {
        var vm = Vm(Batch("unrelated-local-record"));
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(vm, new AssistConfig { PlayerUid = "self", ObserverMode = true });
        vm.Members.Add(new MemberViewModel
        {
            PlayerUid = "self", HoeingBatchId = "remote-same-uid", HoeingStartPhase = "running"
        });
        var frozen = (Dictionary<string, object>)typeof(MainViewModel)
            .GetMethod("FreezeStopTargets", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, new object?[] { new[] { "self" }, null })!;
        var targets = Assert.IsType<Dictionary<string, string>>(frozen["hoeingBatchIds"]);
        Assert.Equal("remote-same-uid", targets["self"]);
    }

}
