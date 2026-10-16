using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>模式直接读取当前任务的实际配置，并与同一执行根身份一起上报。</summary>
[Collection("TaskTakeoverIncident")]
public sealed class HoeingModeStatusTests : IDisposable
{
    private readonly AllConfig? _previousConfig = ConfigService.Config;
    private static readonly PropertyInfo ConfigProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;
    private readonly InstanceRequestHandler _handler = new(new InstanceContext(BetterGiInstanceType.Primary, "hoeing-mode", null),
        null!, null!, _ => { }, _ => { }, NullLogger.Instance);

    public HoeingModeStatusTests()
    {
        ConfigProperty.SetValue(null, new AllConfig());
        AutoHoeingProgress.Clear();
        PreemptionGate.Disarm();
    }

    public void Dispose()
    {
        AutoHoeingProgress.Clear();
        PreemptionGate.Disarm();
        ConfigProperty.SetValue(null, _previousConfig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothStatusChannelsReadEffectiveTaskConfigWithItsExecutionIdentity(bool multiplayer)
    {
        ConfigService.Config!.AutoHoeingConfig.MultiplayerEnabled = !multiplayer;
        using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "当前配置组", JobSource.Ui));
        var effectiveConfig = new AutoHoeingConfig { MultiplayerEnabled = multiplayer };
        using (root.UseHoeingConfig(effectiveConfig))
        {
            foreach (var progressRunning in new[] { false, true })
            {
                AutoHoeingProgress.IsRunning = progressRunning;
                AssertStatus(root.ExecutionInstanceId, multiplayer, progressRunning);
            }
            // 收尾时进度已清空，实际任务还没有退出，仍能读取本任务的模式。
            AutoHoeingProgress.Clear();
            AssertStatus(root.ExecutionInstanceId, multiplayer, false);
        }
        AssertStatus(root.ExecutionInstanceId, null, false);
    }

    [Fact]
    public void ReadsCurrentTaskSwitchDirectlyInsteadOfRememberingACopiedFlag()
    {
        using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "当前锄地", JobSource.Ui));
        var config = new AutoHoeingConfig { MultiplayerEnabled = false };
        using var task = root.UseHoeingConfig(config);
        AssertStatus(root.ExecutionInstanceId, false, false);
        config.MultiplayerEnabled = true;
        AssertStatus(root.ExecutionInstanceId, true, false);
    }

    [Fact]
    public void TaskChangesAdvanceExistingRevisionAndReleaseDoesNotLeakToNextTask()
    {
        using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "同组先后任务", JobSource.Ui));
        var before = ExecutionScope.GetActiveSnapshot()!;
        var binding = root.UseHoeingConfig(new AutoHoeingConfig { MultiplayerEnabled = false });
        var running = ExecutionScope.GetActiveSnapshot()!;
        Assert.True(running.StateRevision > before.StateRevision);
        AssertStatus(root.ExecutionInstanceId, false, false);
        binding.Dispose();
        var released = ExecutionScope.GetActiveSnapshot()!;
        Assert.True(released.StateRevision > running.StateRevision);
        binding.Dispose();
        Assert.Equal(released, ExecutionScope.GetActiveSnapshot());
        AssertStatus(root.ExecutionInstanceId, null, false);
    }

    [Fact]
    public void NestedHoeingTaskRestoresOuterConfigWithoutLeakingToFollowingWork()
    {
        using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "锄地内部有限重开", JobSource.Ui));
        using (root.UseHoeingConfig(new AutoHoeingConfig { MultiplayerEnabled = false }))
        {
            using (root.UseHoeingConfig(new AutoHoeingConfig { MultiplayerEnabled = true }))
                AssertStatus(root.ExecutionInstanceId, true, false);
            AssertStatus(root.ExecutionInstanceId, false, false);
        }
        AssertStatus(root.ExecutionInstanceId, null, false);
    }

    [Fact]
    public void LateOldTaskReleaseCannotClearNewExecutionMode()
    {
        using var oldRoot = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "旧单机任务", JobSource.Ui));
        using var oldTask = oldRoot.UseHoeingConfig(new AutoHoeingConfig { MultiplayerEnabled = false });
        oldRoot.Dispose();
        using var successor = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "新联机任务", JobSource.Ui));
        using var newTask = successor.UseHoeingConfig(new AutoHoeingConfig { MultiplayerEnabled = true });
        var snapshot = ExecutionScope.GetActiveSnapshot();
        oldTask.Dispose();
        Assert.Equal(snapshot, ExecutionScope.GetActiveSnapshot());
        AssertStatus(successor.ExecutionInstanceId, true, false);
    }

    [Fact]
    public void RunningProgressWithoutCurrentTaskConfigDoesNotInventAMode()
    {
        AutoHoeingProgress.IsRunning = true;
        using var root = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "未确认模式", JobSource.Ui));
        AssertStatus(root.ExecutionInstanceId, null, true);
    }

    private void AssertStatus(Guid instanceId, bool? mode, bool progressRunning)
    {
        var response = _handler.HandleTaskStatus(null!, InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus));
        Assert.True(response.Success);
        Assert.Equal(instanceId, Guid.Parse((string)response.Data!["executionInstanceId"]!));
        Assert.Equal(mode, (bool?)response.Data["autoHoeingMultiplayer"]);
        Assert.Equal(progressRunning, (bool)response.Data["autoHoeingRunning"]!);
        var readonlyStatus = _handler.CreateReadOnlyStatusSnapshot();
        Assert.Equal(mode, readonlyStatus.GetType().GetProperty("autoHoeingMultiplayer")!.GetValue(readonlyStatus));
        Assert.Equal(instanceId, Guid.Parse((string)readonlyStatus.GetType().GetProperty("executionInstanceId")!.GetValue(readonlyStatus)!));
    }
}
