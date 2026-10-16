using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging.Abstractions;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

/// <summary>同一个原生任务承载单机和联机；状态协议必须传实际执行模式。</summary>
[Collection("TaskTakeoverIncident")]
public sealed class HoeingModeStatusTests : IDisposable
{
    private readonly AllConfig? _previousConfig = ConfigService.Config;
    private static readonly PropertyInfo ConfigProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;

    public HoeingModeStatusTests() => ConfigProperty.SetValue(null, new AllConfig());

    public void Dispose()
    {
        AutoHoeingProgress.Clear();
        ConfigProperty.SetValue(null, _previousConfig);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BothStatusChannelsExposeActualModeBeforeAndDuringRouteProgress(bool multiplayer)
    {
        AutoHoeingProgress.Clear();
        try
        {
            AutoHoeingProgress.BeginExecution(multiplayer);
            // 组队准备尚未写入路线进度，也不能把正在准备的联机当普通任务抢占。
            Assert.False(AutoHoeingProgress.IsRunning);
            Assert.Equal(multiplayer, AutoHoeingProgress.MultiplayerMode);
            var handler = new InstanceRequestHandler(new InstanceContext(BetterGiInstanceType.Primary, "hoeing-mode", null),
                null!, null!, _ => { }, _ => { }, NullLogger.Instance);
            foreach (var progressRunning in new[] { false, true })
            {
                AutoHoeingProgress.IsRunning = progressRunning;
                var response = handler.HandleTaskStatus(null!, InstanceIpcEnvelope.Request(InstanceOperations.TaskStatus));
                Assert.True(response.Success);
                Assert.Equal(multiplayer, (bool?)response.Data!["autoHoeingMultiplayer"]);
                Assert.Equal(progressRunning, (bool)response.Data["autoHoeingRunning"]!);
                var readonlyStatus = handler.CreateReadOnlyStatusSnapshot();
                Assert.Equal(multiplayer, readonlyStatus.GetType().GetProperty("autoHoeingMultiplayer")!.GetValue(readonlyStatus));
            }
        }
        finally { AutoHoeingProgress.Clear(); }
        Assert.Null(AutoHoeingProgress.MultiplayerMode);
        Assert.False(AutoHoeingProgress.IsRunning);
    }

    [Fact]
    public void LegacyRunningFlagAloneDoesNotInventAMode()
    {
        AutoHoeingProgress.Clear();
        try
        {
            AutoHoeingProgress.IsRunning = true;
            Assert.Null(AutoHoeingProgress.MultiplayerMode);
        }
        finally { AutoHoeingProgress.Clear(); }
    }
}
