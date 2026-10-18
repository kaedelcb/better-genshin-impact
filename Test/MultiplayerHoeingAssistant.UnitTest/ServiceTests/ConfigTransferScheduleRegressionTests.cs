using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class ConfigTransferScheduleRegressionTests
{
    private static AssistConfig Configuration(string time = "12:00") => new()
    {
        ScheduledOnlineTime = time, PlayerUid = "local", TeamUids = ["local", "friend"],
        OnlineHoeingGroupNames = ["日常"], OnlineHoeingGroupTypes = ["group"],
    };

    [Fact]
    public void ChangedImportedSchedulePersistsPendingAuthorizationAndBlocksActualSchedulerAdmissionAfterReload()
    {
        var imported = Configuration("12:01");
        imported.ScheduledOnlineRequiresConfirmation = ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(Configuration(), imported);
        var reloaded = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(imported))!;
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, reloaded);

        Assert.True(reloaded.ScheduledOnlineRequiresConfirmation);
        Assert.False(vm.TryEnterConfigTransferOperation("scheduled_online", out _));
        Assert.True(vm.CanEditConfiguration);
        Assert.True(vm.TryEnterConfigTransferOperation("quick_start", out var ordinary));
        ordinary!.Dispose();
        Assert.True(vm.TryEnterConfigTransferOperation("stop", out _));
        Assert.Contains("定时上线", vm.ConfigurationTransferStatusMessage);
        Assert.Contains("确认时间", vm.ConfigurationTransferStatusMessage);

        vm.SetConfirmedScheduledOnlineTime("12:02");

        Assert.False(reloaded.ScheduledOnlineRequiresConfirmation);
        Assert.Equal("12:02", reloaded.ScheduledOnlineTime);
        Assert.Empty(vm.ConfigurationTransferStatusMessage);
        Assert.True(vm.TryEnterConfigTransferOperation("scheduled_online", out var confirmed));
        confirmed!.Dispose();
    }

    [Fact]
    public void UnchangedLocallyAuthorizedScheduleRemainsUsableButIncomingFalseCannotLaunderPendingLocalSchedule()
    {
        var local = Configuration();
        var imported = Configuration();
        imported.TeamUids.Reverse();
        Assert.False(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        local.ScheduledOnlineRequiresConfirmation = true;
        imported.ScheduledOnlineRequiresConfirmation = false;
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(null, imported));
        imported.ScheduledOnlineTime = "";
        Assert.False(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
    }

    [Fact]
    public void SameTimeWithDifferentTaskOrExecutionIdentityRequiresConfirmationButDisplayOnlyChangesDoNot()
    {
        var local = Configuration();
        var imported = Configuration();
        imported.PlayerName = "new display name";
        imported.GuardBgi = true;
        Assert.False(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        imported.OnlineHoeingGroupNames = ["新迁入任务"];
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        imported = Configuration();
        imported.PlayerUid = "different identity";
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
        imported = Configuration();
        imported.OnlineHoeingCompletionPolicy = "runSpecified";
        imported.OnlineHoeingSpecifiedTaskName = "another task";
        Assert.True(ConfigTransferSchedulePolicy.RequiresConfirmationAfterImport(local, imported));
    }
}
