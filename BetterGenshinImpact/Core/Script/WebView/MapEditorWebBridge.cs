using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using Serilog.Core;

namespace BetterGenshinImpact.Core.Script.WebView;

/// <summary>
/// 给 WebView 提供的桥接类
/// 用于调用 C# 方法
/// </summary>
[ClassInterface(ClassInterfaceType.AutoDual)]
[ComVisible(true)]
public class MapEditorWebBridge
{
    public void ChangeMapName(string mapName)
    {
        TaskContext.Instance().Config.DevConfig.RecordMapName = mapName;
    }

    public async Task RunPathing(string json)
    {
        // 与其他任务入口一样，先取得执行归属，再建立本次取消令牌。
        // 不能继承上次取消留下的令牌，也不能在其他任务运行时重置它。
        ExecutionScope scope;
        try
        {
            scope = ExecutionScope.Start(new JobDescriptor(JobKind.Solo, "地图编辑器路径测试", JobSource.Ui));
        }
        catch (InvalidOperationException ex)
        {
            TaskControl.Logger.LogWarning(ex, "地图编辑器任务未获执行权");
            return;
        }
        using var root = scope;
        CancellationContext.Instance.Set();
        if (await ScriptService.StartGameTask() != TaskRunResult.Ran)
        {
            CancellationContext.Instance.Clear();
            return;
        }
        SystemControl.ActivateWindow();
        await new TaskRunner().RunThreadAsync(async () =>
        {
            var task = PathingTask.BuildFromJson(json);
            var pathExecutor = new PathExecutor(CancellationContext.Instance.Cts.Token);
            pathExecutor.PartyConfig = new PathingPartyConfig { AutoFightEnabled = false };
            await pathExecutor.Pathing(task);
        });
    }
}
