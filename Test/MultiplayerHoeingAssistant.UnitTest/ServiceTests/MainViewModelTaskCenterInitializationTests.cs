using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using OneDragonMigration.Core;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class MainViewModelTaskCenterInitializationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "paired-startup-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("Tools", "MultiplayerHoeingAssistant")]
    [InlineData("Tool", "MultiplayerHoeingAssistant")]
    [InlineData("Tools", "MHA")]
    public void InstalledAssistantUsesTheMarkedBgiPackageRoot(string tools, string assistant)
    {
        var installed = Path.Combine(_root, tools, assistant);
        Directory.CreateDirectory(installed);
        var image = Path.Combine(_root, "BetterGI.exe"); File.WriteAllText(image, "owned fixture");
        File.WriteAllText(Path.Combine(_root, InstallationPipeScope.MarkerFile),
            "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
        var resolved = MainViewModel.ResolveSamePackageBgiPath(installed);
        Assert.Equal(image, resolved);
        using var client = new IpcClient(Path.GetDirectoryName(resolved)!);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Assert.Equal(InstallationPipeScope.ResolveRootPipe(sid, _root), client.GetPipeName());
        Assert.Contains(".install-", client.GetPipeName());
    }

    [Fact]
    public void MissingPairedExecutableIsReportedInsteadOfUsingAnotherInstallation()
    {
        var installed = Path.Combine(_root, "Tools", "MultiplayerHoeingAssistant"); Directory.CreateDirectory(installed);
        File.WriteAllText(Path.Combine(_root, InstallationPipeScope.MarkerFile),
            "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
        Assert.Throws<FileNotFoundException>(() => MainViewModel.ResolveSamePackageBgiPath(installed));
    }

    [Fact]
    public void TaskCenterAdmissionUsesCurrentExtStatusAndRejectsStaleOrForeignFacts()
    {
        var now = DateTimeOffset.UtcNow;
        const string epoch = "20604:123456789";
        const string busy = "{\"running\":true,\"stateRevision\":17,\"bgiEpoch\":{\"processId\":20604,\"startTicksUtc\":123456789}}";
        const string idle = "{\"running\":false,\"stateRevision\":18,\"bgiEpoch\":{\"processId\":20604,\"startTicksUtc\":123456789}}";

        Assert.True(MainViewModel.ParseTaskCenterArbitrationStatus(busy, now, epoch, now)!.TaskRunning);
        Assert.False(MainViewModel.ParseTaskCenterArbitrationStatus(idle, now.AddMilliseconds(200), epoch,
            now.AddMilliseconds(200))!.TaskRunning);
        Assert.Null(MainViewModel.ParseTaskCenterArbitrationStatus(null, now, epoch, now));
        Assert.Null(MainViewModel.ParseTaskCenterArbitrationStatus(idle, null, epoch, now));
        Assert.Null(MainViewModel.ParseTaskCenterArbitrationStatus("{", now, epoch, now));
        Assert.Null(MainViewModel.ParseTaskCenterArbitrationStatus(idle, now, "999:123456789", now));
        Assert.Null(MainViewModel.ParseTaskCenterArbitrationStatus(idle, now.AddSeconds(-25), epoch, now));
        Assert.Null(MainViewModel.ParseTaskCenterArbitrationStatus(
            "{\"running\":false,\"bgiEpoch\":{\"processId\":20604,\"startTicksUtc\":123456789}}", now, epoch, now));
    }

    [Fact]
    public void TaskCenterAdmissionFallsBackOnlyToFreshSameEpochStatusDuringExtGap()
    {
        var now = DateTimeOffset.UtcNow;
        const string epoch = "20604:123456789";
        const string liveIdle = "{\"running\":false,\"stateRevision\":18,\"bgiEpoch\":{\"processId\":20604,\"startTicksUtc\":123456789}}";
        var periodicBusy = new ControlStatus
        {
            TaskRunning = true, TaskStatusAvailable = true,
            TaskStatusBgiEpoch = epoch, TaskStatusObservedAtUtc = now.AddSeconds(-3),
        };

        Assert.False(MainViewModel.SelectTaskCenterArbitrationStatus(liveIdle, now, periodicBusy, epoch, now)!.TaskRunning);
        Assert.True(MainViewModel.SelectTaskCenterArbitrationStatus(null, null, periodicBusy, epoch, now)!.TaskRunning);
        periodicBusy.TaskStatusObservedAtUtc = now.AddSeconds(-25);
        Assert.Null(MainViewModel.SelectTaskCenterArbitrationStatus(null, null, periodicBusy, epoch, now));
        periodicBusy.TaskStatusObservedAtUtc = now;
        Assert.Null(MainViewModel.SelectTaskCenterArbitrationStatus(null, null, periodicBusy, "999:123456789", now));
    }

    [Fact]
    public void TaskStopKeepsLastBusySnapshotUntilIdleRefreshArrives()
    {
        var vm = new MainViewModel();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var jsonField = typeof(MainViewModel).GetField("_latestExtStatusJson", flags)!;
        var observedField = typeof(MainViewModel).GetField("_latestExtStatusObservedAtUtc", flags)!;
        var onEvent = typeof(MainViewModel).GetMethod("OnBgiExternalEvent", flags)!;
        const string busy = "{\"running\":true,\"stateRevision\":17,\"bgiEpoch\":{\"processId\":20604,\"startTicksUtc\":123456789}}";
        jsonField.SetValue(vm, busy);
        observedField.SetValue(vm, DateTimeOffset.UtcNow);

        onEvent.Invoke(vm, [new BgiExternalEvent { Name = BgiExternalEventNames.TaskStopped, StateRevision = 18 }]);
        Assert.Equal(busy, jsonField.GetValue(vm));
        onEvent.Invoke(vm, [new BgiExternalEvent { Name = BgiExternalEventNames.TaskSlotReleased, StateRevision = 19 }]);
        Assert.Equal(busy, jsonField.GetValue(vm));
        onEvent.Invoke(vm, [new BgiExternalEvent { Name = BgiExternalEventNames.TaskStarted, StateRevision = 20 }]);
        Assert.Null(jsonField.GetValue(vm));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
