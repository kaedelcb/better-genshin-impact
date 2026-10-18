using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class ConfigTransferRuntimeGateTests
{
    [Fact]
    public void ExistingOperationPreventsImport_UntilItsReceiptCompletes()
    {
        var vm = new MainViewModel();
        Assert.True(vm.TryEnterConfigTransferOperation("start_group", out var operation));
        Assert.Throws<InvalidOperationException>(() => vm.BeginConfigTransferScope());
        operation!.Dispose();
        using var transfer = vm.BeginConfigTransferScope();
        Assert.False(vm.CanEditConfiguration);
    }

    [Fact]
    public async Task ImportBlocksActualExecutionEntry_ButLeavesStopEntryAvailable()
    {
        var vm = new MainViewModel();
        using var transfer = vm.BeginConfigTransferScope();
        var start = await Execute(vm, "start_group");
        Assert.Equal("config_transfer_in_progress", start.ErrorCode);
        var stop = await Execute(vm, "stop");
        Assert.Equal("本机执行器不可用", stop.Message);
        Assert.NotEqual("config_transfer_in_progress", stop.ErrorCode);
        var localStart = await vm.ExecuteLocalBgiCommandAsync("start_bgi", null);
        Assert.Equal("config_transfer_in_progress", localStart.ErrorCode);
    }

    [Fact]
    public void ConfirmedCompletionReleasesScope_AndRetainedRecoveryNeverReopensIt()
    {
        var vm = new MainViewModel();
        var transfer = vm.BeginConfigTransferScope();
        transfer.Dispose();
        Assert.True(vm.CanEditConfiguration);
        Assert.True(vm.TryEnterConfigTransferOperation("scheduled_online", out var operation));
        operation!.Dispose();

        transfer = vm.BeginConfigTransferScope();
        transfer.RetainForRecovery();
        transfer.Dispose();
        Assert.False(vm.CanEditConfiguration);
        Assert.Contains("退出后重新打开", vm.ConfigurationRecoveryMessage);
        Assert.False(vm.TryEnterConfigTransferOperation("coordinated_start", out _));
        Assert.False(vm.TryEnterConfigTransferOperation("task.flow.save", out _));
        Assert.True(vm.TryEnterConfigTransferOperation("stop", out var stop));
        Assert.Null(stop);
        Assert.Throws<InvalidOperationException>(() => vm.BeginConfigTransferScope());
    }

    [Fact]
    public async Task ChangedRuntimeBindingsDeferWithoutRecoveryFailure_KeepOldStopIdentityAndDisableGuardian()
    {
        var previous = new AssistConfig { PlayerUid = "local", PlayerName = "before", TeamUids = ["a", "b"],
            BgiPath = "local-bgi.exe", ObserverMode = false, GuardBgi = false };
        var imported = new AssistConfig { PlayerUid = "local", PlayerName = "display-only-change", TeamUids = ["b", "a"],
            BgiPath = "local-bgi.exe", ObserverMode = false, GuardBgi = false };
        Assert.False(MainViewModel.ConfigTransferRuntimeBindingsChanged(previous, imported));
        imported.PlayerUid = "new-identity";
        imported.ObserverMode = true;
        imported.GuardBgi = true;
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, imported);
        using var monitor = new BgiProcessMonitor("never-launch-from-tests.exe");
        typeof(MainViewModel).GetField("_processMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, monitor);
        var transfer = vm.BeginConfigTransferScope();
        Assert.True(vm.DeferChangedConfigTransferRuntimeBindings(transfer, previous));
        transfer.Dispose();

        Assert.False(vm.CanEditConfiguration);
        Assert.Empty(vm.ConfigurationRecoveryMessage);
        Assert.Contains("迁移已完成", vm.ConfigurationTransferStatusMessage);
        Assert.Equal("local", vm.Config!.PlayerUid);
        Assert.True(vm.IsExecutorMode);
        Assert.False(await monitor.RestartBgiControlledAsync(null, "崩溃守护"));
        Assert.Equal("config_transfer_deferred", (await Execute(vm, "start_group")).ErrorCode);
        Assert.Equal("本机执行器不可用", (await Execute(vm, "stop")).Message);
    }

    private static Task<CommandResult> Execute(MainViewModel vm, string command) =>
        (Task<CommandResult>)typeof(MainViewModel).GetMethod("ExecuteCommandWithHoeingStopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [new RemoteCommand { Cmd = command }])!;
}
