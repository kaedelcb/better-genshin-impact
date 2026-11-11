using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using Xunit;

// 运行生产恢复编排，以模拟画面/键鼠替代游戏；判据是完成开关图之前不发布恢复。
public sealed class NetworkRecoveryStagesTests : IDisposable
{
    public NetworkRecoveryStagesTests()
    {
        TaskContext.Instance().Config.TpConfig.UseOfficialTeleport = false;
        TaskControl.IsSuspendedByNetwork = true;
        NetworkRecovery.RecoveryNetworkDone = false;
        RunnerContext.Instance.IsSuspend = false;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MainUiOrExistingMapSkipsLoginAndOnlyResumesAfterStableClose(bool alreadyOnMap)
    {
        var onMap = alreadyOnMap;
        var elapsed = 0;
        var closes = 0;
        var probes = 0;
        var version = TaskControl.NetworkRecoveryVersion;
        Screen.IsMapForTest = onMap;
        Bv.MainUiForTest = () => !onMap;
        NetworkRecovery.StartForTest = _ => throw new Exception("主界面/地图不应重新处理登录页");
        TpTaskFastDrag.ProbeForTest = _ =>
        {
            probes++;
            Assert.True(TaskControl.IsSuspendedByNetwork);
            onMap = Screen.IsMapForTest = true;
            return Task.FromResult(true);
        };
        InputHub.Keyboard.PressForTest = () => closes++;
        TaskControl.DelayForTest = (ms, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            Assert.True(TaskControl.IsSuspendedByNetwork);
            Assert.False(NetworkRecovery.RecoveryNetworkDone);
            elapsed += ms;
            if (elapsed >= 1700) onMap = Screen.IsMapForTest = false;
            return Task.CompletedTask;
        };

        await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);

        Assert.Equal(1, probes);
        Assert.Equal(1, closes);
        Assert.Equal(1800, elapsed);
        Assert.False(TaskControl.IsSuspendedByNetwork);
        Assert.True(NetworkRecovery.RecoveryNetworkDone);
        Assert.Equal(version + 1, TaskControl.NetworkRecoveryVersion);
    }

    [Fact]
    public async Task LoginPageStillUsesExistingRecoveryBeforeMapVerification()
    {
        var mainUi = false;
        var logins = 0;
        var probes = 0;
        Bv.MainUiForTest = () => mainUi;
        NetworkRecovery.StartForTest = _ => { logins++; mainUi = true; return Task.CompletedTask; };
        TpTaskFastDrag.ProbeForTest = _ =>
        {
            Assert.Equal(1, logins);
            Assert.True(mainUi);
            probes++;
            mainUi = false;
            return Task.FromResult(true);
        };
        InputHub.Keyboard.PressForTest = () => mainUi = true;
        TaskControl.DelayForTest = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
        Assert.Equal(1, logins);
        Assert.Equal(1, probes);
        Assert.False(TaskControl.IsSuspendedByNetwork);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingMapNeverSendsEscapeToAnotherForegroundApplication(bool requiresForeground)
    {
        Screen.IsMapForTest = true;
        var mainUi = false;
        var focuses = 0;
        var closes = 0;
        TaskContext.Instance().Runtime = new TaskContext.RuntimeData();
        TaskContext.Instance().Runtime!.Window.RequiresForeground = requiresForeground;
        SystemControl.ActiveForTest = false;
        Bv.MainUiForTest = () => mainUi;
        NetworkRecovery.StartForTest = _ => throw new Exception("已有地图不能重走登录");
        TpTaskFastDrag.ProbeForTest = _ => Task.FromResult(true);
        SystemControl.FocusForTest = () =>
        {
            Assert.Equal(0, closes);
            Assert.True(TaskControl.IsSuspendedByNetwork);
            focuses++;
            SystemControl.ActiveForTest = true;
        };
        InputHub.Keyboard.PressForTest = () =>
        {
            Assert.True(!requiresForeground || SystemControl.ActiveForTest);
            closes++;
            mainUi = true;
            Screen.IsMapForTest = false;
        };
        TaskControl.DelayForTest = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(requiresForeground ? 1 : 0, focuses);
        Assert.Equal(1, closes);
        Assert.False(TaskControl.IsSuspendedByNetwork);
    }

    [Fact]
    public async Task StopDuringClosingDoesNotPublishRecoveryOrRepeatInput()
    {
        using var stop = new CancellationTokenSource();
        var mainUi = true;
        var closes = 0;
        var version = TaskControl.NetworkRecoveryVersion;
        Bv.MainUiForTest = () => mainUi;
        TpTaskFastDrag.ProbeForTest = _ => { mainUi = false; return Task.FromResult(true); };
        InputHub.Keyboard.PressForTest = () => closes++;
        TaskControl.DelayForTest = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var recovery = TaskControl.RunNetworkRecoveryForTest(stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        Assert.Equal(1, closes);
        Assert.True(TaskControl.IsSuspendedByNetwork);
        Assert.False(NetworkRecovery.RecoveryNetworkDone);
        Assert.Equal(version, TaskControl.NetworkRecoveryVersion);
    }

    public void Dispose()
    {
        Screen.IsMapForTest = false;
        SystemControl.ActiveForTest = true;
        SystemControl.FocusForTest = null;
        TaskContext.Instance().Runtime = null;
        Bv.MainUiForTest = () => true;
        NetworkRecovery.StartForTest = null;
        TpTaskFastDrag.ProbeForTest = null;
        InputHub.Keyboard.PressForTest = null;
        TaskControl.DelayForTest = null;
        TaskControl.IsSuspendedByNetwork = false;
        TaskControl.IsSuspendedByWindow = false;
        NetworkRecovery.RecoveryNetworkDone = false;
        RunnerContext.Instance.IsSuspend = false;
    }
}
