using System;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoOnline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// [A6] 中断上下文捕获/保存的单一事实源（ADR-2026-09-16）。
/// 两个调用方：IPC task.suspend（HandleTaskSuspend，活体受害者）与
/// PreemptionGate 让位点（TaskRunner.RunCurrentAsync 拿锁后让位时）。
/// 2.5（WasCancelled 不保存）/2.6（信号任务不保存）两条实机修复规则在此收口，两端语义一致。
/// </summary>
internal static class SuspendContextCapture
{
    /// <summary>捕获结果（字段口径与 SuspendedTaskContext 一一对应）。</summary>
    internal sealed record Snapshot(
        string TaskType,
        string? GroupName,
        int TaskIndex,
        string? FolderName,
        string? ProjectName,
        int OneDragonTaskIndex,
        string? SubTaskGroupName,
        string? SoloSettingsJson,
        bool IsOnlineSignalTask);

    /// <summary>
    /// 判定"当前在跑的项目"是否为「联机锄地上线」信号任务（原 InstanceRequestHandler 步骤 2.6 判定）。
    /// 按项目内容识别：任务注册名（<see cref="NotifyOnlineTask.TaskName"/>）+ 独立任务项目恒为空的 FolderName
    /// （<c>ScriptGroupProject.BuildSoloTaskProject</c> 构造时 FolderName=""），不按组名——用户可以把组叫任何名字。
    /// JS/Pathing/KeyMouse 项目的 FolderName 均非空，不会误判；Shell 项目 FolderName 虽为空但 Name 是命令串，也不会撞名。
    /// </summary>
    internal static bool IsOnlineSignalTask(string? projectName, string? folderName)
        => projectName == NotifyOnlineTask.TaskName && string.IsNullOrEmpty(folderName);

    /// <summary>
    /// suspend 模式：读取"当前正在运行"的任务上下文（原 HandleTaskSuspend 步骤 1 三分支逻辑逐字移植）。
    /// 项目级信息（CurrentScriptGroupProjectInfo）在此模式下可信——活体任务持锁期间它就是当前项目。
    /// 返回 null = 识别不到任何任务。
    /// </summary>
    internal static Snapshot? CaptureCurrent()
    {
        string? taskType = null;
        string? groupName = null;
        var taskIndex = 0;
        string? folderName = null;
        string? projectName = null;
        var oneDragonTaskIndex = 0;
        string? subTaskGroupName = null;
        string? soloSettingsJson = null;
        var isOnlineSignalTask = false;

        var ctx = RunnerContext.Instance;
        var progress = ctx?.taskProgress;

        if (progress?.CurrentScriptGroupProjectInfo != null)
        {
            var info = progress.CurrentScriptGroupProjectInfo;
            groupName = progress.CurrentScriptGroupName;
            taskIndex = info.Index;
            folderName = info.FolderName;
            projectName = info.Name;
            taskType = "group";
            // 时序窗口：solo 任务脚本 0.01s 执行完毕后，ExecuteProject 的 finally 已清空
            // CurrentScriptProject，但 RunMulti 不会在项目结束时清理 CurrentScriptGroupProjectInfo
            // （只标 TaskEnd=true，等下一项目覆盖）。suspend 在"信号任务已自结、组包装未结束"的
            // 几百毫秒窗口内到达时，info 仍指向该信号任务——按 info 判定正好覆盖该窗口。
            isOnlineSignalTask = IsOnlineSignalTask(info.Name, info.FolderName);
        }
        else if (progress?.CurrentScriptGroupName != null)
        {
            // 一条龙场景：progress.CurrentScriptGroupName 是当前执行的配置组名（OneDragonFlowViewModel 配置组分支设置了 taskProgress）
            // 一条龙配置名 + 条目索引需要从 OneDragonFlowViewModel 取
            try
            {
                var oneDragonVm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>();
                if (oneDragonVm?.SelectedConfig != null)
                {
                    groupName = oneDragonVm.SelectedConfig.Name;
                    oneDragonTaskIndex = oneDragonVm.CurrentExecutingTaskIndex;
                }
            }
            catch
            {
                // ViewModel 不可用时，退回用配置组名作 GroupName（降级为配置组恢复）
                groupName = progress.CurrentScriptGroupName;
                oneDragonTaskIndex = 0;
            }

            subTaskGroupName = progress.CurrentScriptGroupName;
            if (progress.CurrentScriptGroupProjectInfo != null)
            {
                taskIndex = progress.CurrentScriptGroupProjectInfo.Index;
                folderName = progress.CurrentScriptGroupProjectInfo.FolderName;
                projectName = progress.CurrentScriptGroupProjectInfo.Name;
            }

            taskType = "onedragon";
        }
        else
        {
            // 独立任务或 JS 脚本
            var currentProject = TaskContext.Instance()?.CurrentScriptProject;
            var taskName = currentProject?.Name;
            if (!string.IsNullOrEmpty(taskName))
            {
                projectName = taskName;
                taskType = "solo";
                isOnlineSignalTask = currentProject is { Type: "SoloTask" }
                                     && taskName == NotifyOnlineTask.TaskName;
                groupName = currentProject!.GroupInfo?.Name;
                if (currentProject.SoloTaskSettingsObject != null)
                {
                    try
                    {
                        soloSettingsJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                            currentProject.SoloTaskSettingsObject);
                    }
                    catch
                    {
                        // 序列化失败则不带快照（恢复时退化为全局配置，同旧行为）
                    }
                }
            }
        }

        return taskType == null
            ? null
            : new Snapshot(taskType, groupName, taskIndex, folderName, projectName,
                oneDragonTaskIndex, subTaskGroupName, soloSettingsJson, isOnlineSignalTask);
    }

    /// <summary>
    /// 让位模式（PreemptionGate 让位点专用）：只信组级/龙级数据。
    /// 让位发生在 RunCurrentAsync 拿锁成功后、RunMulti 项目循环写入项目级进度之前——
    /// CurrentScriptGroupProjectInfo 必为上个月组的残留（TaskEnd=true），绝不可用；
    /// 而 CurrentScriptGroupName（OneDragonFlowViewModel 在 RunMulti 前写入）与龙级水位线
    /// （条目执行前回写）在此时已指向新任务，是可靠事实源。
    /// </summary>
    /// <param name="source">让位任务的作业来源（OneDragonInternal = 龙内条目，走龙级恢复点）。</param>
    /// <param name="fallbackName">组名缺失时的兜底名（通常为 job.Name / soloTaskName）。</param>
    internal static Snapshot? CaptureForYield(JobSource source, string? fallbackName)
    {
        var progress = RunnerContext.Instance?.taskProgress;

        // 龙内条目：恢复点 = 龙配置 + 水位线条目（条目未执行，重跑是既定语义，A5-1 注释同款）
        if (source == JobSource.OneDragonInternal)
        {
            try
            {
                var oneDragonVm = App.ServiceProvider.GetService<BetterGenshinImpact.ViewModel.Pages.OneDragonFlowViewModel>();
                if (oneDragonVm?.SelectedConfig != null)
                {
                    return new Snapshot("onedragon", oneDragonVm.SelectedConfig.Name, 0, null, null,
                        oneDragonVm.CurrentExecutingTaskIndex, progress?.CurrentScriptGroupName, null, false);
                }
            }
            catch
            {
                // VM 不可用降级为组级恢复
            }
        }

        // 配置组级：组尚未执行任何项目，恢复点 = 从头重跑该组
        if (!string.IsNullOrEmpty(progress?.CurrentScriptGroupName))
        {
            return new Snapshot("group", progress.CurrentScriptGroupName, 0, null, null,
                0, null, null, false);
        }

        // 独立任务/JS：与 suspend 模式相同的 solo 分支（CurrentScriptProject 由调用方在启动前写入）
        var currentProject = TaskContext.Instance()?.CurrentScriptProject;
        var taskName = currentProject?.Name ?? fallbackName;
        if (string.IsNullOrEmpty(taskName))
        {
            return null;
        }

        string? soloSettingsJson = null;
        if (currentProject?.SoloTaskSettingsObject != null)
        {
            try
            {
                soloSettingsJson = Newtonsoft.Json.JsonConvert.SerializeObject(
                    currentProject.SoloTaskSettingsObject);
            }
            catch
            {
                // 序列化失败则不带快照（同旧行为）
            }
        }

        var isSignal = currentProject is { Type: "SoloTask" } && taskName == NotifyOnlineTask.TaskName;
        return new Snapshot("solo", currentProject?.GroupInfo?.Name, 0, null, taskName,
            0, null, soloSettingsJson, isSignal);
    }

    /// <summary>
    /// 按 2.6 规则保存：信号任务绝不入 SuspendedTaskContext（ABABAB 防护，任何路径不例外）。
    /// 返回 true = 已保存；false = 命中 2.6 未保存（或配置不可用）。
    /// </summary>
    internal static bool Save(ILogger logger, Snapshot snapshot, string channelTag)
    {
        if (snapshot.IsOnlineSignalTask)
        {
            logger.LogInformation("[{Tag}] 被中断的是联机锄地上线信号任务，其意图已兑现，不保存中断上下文（避免恢复后重复触发上线）", channelTag);
            return false;
        }

        var allConfig = TaskContext.Instance()?.Config;
        if (allConfig == null)
        {
            return false;
        }

        allConfig.SuspendedTaskContext = new SuspendedTaskContext
        {
            TaskType = snapshot.TaskType,
            GroupName = snapshot.GroupName ?? "",
            TaskIndex = snapshot.TaskIndex,
            FolderName = snapshot.FolderName ?? "",
            ProjectName = snapshot.ProjectName ?? "",
            OneDragonTaskIndex = snapshot.OneDragonTaskIndex,
            SubTaskGroupName = snapshot.SubTaskGroupName ?? "",
            SoloSettingsJson = snapshot.SoloSettingsJson ?? ""
        };
        logger.LogInformation("[{Tag}] 已保存中断上下文: Type={TaskType}, Group={GroupName}, Index={TaskIndex}, OneDragonIndex={OneDragonTaskIndex}",
            channelTag, snapshot.TaskType, snapshot.GroupName, snapshot.TaskIndex, snapshot.OneDragonTaskIndex);
        return true;
    }
}
