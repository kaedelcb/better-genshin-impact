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
/// 2.5（WasCancelled 不保存）/2.6（独立信号不保存、配置组信号保存下一项续跑点）两条实机规则在此收口。
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
        string? OneDragonTaskId,
        string? SubTaskGroupName,
        string? SoloSettingsJson,
        bool IsOnlineSignalTask,
        Guid? RootRunId = null,
        Guid? AttemptId = null,
        System.Collections.Generic.Dictionary<string, string>? ConfigurationRevisions = null,
        string? NodeId = null, int? Iteration = null, string? TaskId = null, string? ConfigRevision = null,
        // R4.6 B6：收尾抑制权限随恢复现场携带（缺省 false，旧现场零变化）
        bool SuppressCompletionAction = false,
        // R4.6 B1：出现序号/尝试号（缺省 null）
        int? Occurrence = null, int? Attempt = null,
        // 配置组信号任务的续跑判据：true=后面仍有配置组项目，恢复可从下一项开始。
        bool HasNextGroupProject = true);

    /// <summary>
    /// 判定"当前在跑的项目"是否为「联机锄地上线」信号任务。
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
    internal static Snapshot? CaptureCurrent() => ExecutionScope.Capture();

    // Rejected tasks never owned a running context and cannot create a restore point.
    internal static Snapshot? CaptureForYield(JobSource source, string? fallbackName) => null;

    /// <summary>
    /// 保存中断上下文：独立信号任务仍不保存，避免恢复后重复触发上线；
    /// 配置组内的信号任务保存组游标，恢复入口会用 TaskIndex+1 从下一项继续。
    /// 返回 true = 已保存；false = 无需恢复（独立信号/组尾信号）或配置不可用。
    /// </summary>
    internal static bool Save(ILogger logger, Snapshot snapshot, string channelTag)
    {
        if (snapshot.IsOnlineSignalTask)
        {
            if (snapshot.TaskType != "group")
            {
                logger.LogInformation("[{Tag}] 被中断的是独立联机锄地上线信号任务，其意图已兑现，不保存中断上下文（避免恢复后重复触发上线）", channelTag);
                return false;
            }

            if (!snapshot.HasNextGroupProject)
            {
                logger.LogInformation("[{Tag}] 配置组中的联机锄地上线信号已完成，后面没有可恢复项目，不保存中断上下文", channelTag);
                return false;
            }

            logger.LogInformation("[{Tag}] 配置组中的联机锄地上线信号已完成，保存组游标 Index={TaskIndex}，恢复时从下一项开始", channelTag, snapshot.TaskIndex);
        }

        var allConfig = TaskContext.Instance()?.Config;
        if (allConfig == null)
        {
            return false;
        }

        allConfig.SuspendedTaskContext = new SuspendedTaskContext
        {
            RootRunId = snapshot.RootRunId,
            AttemptId = snapshot.AttemptId,
            NodeId = snapshot.NodeId, Iteration = snapshot.Iteration,
            TaskId = snapshot.TaskId, ConfigRevision = snapshot.ConfigRevision,
            ConfigurationRevisions = snapshot.ConfigurationRevisions,
            TaskType = snapshot.TaskType,
            GroupName = snapshot.GroupName ?? "",
            TaskIndex = snapshot.TaskIndex,
            FolderName = snapshot.FolderName ?? "",
            ProjectName = snapshot.ProjectName ?? "",
            OneDragonTaskId = snapshot.OneDragonTaskId,
            SubTaskGroupName = snapshot.SubTaskGroupName ?? "",
            SoloSettingsJson = snapshot.SoloSettingsJson ?? "",
            SuppressCompletionAction = snapshot.SuppressCompletionAction,
            HasNextGroupProject = snapshot.HasNextGroupProject
        };
        logger.LogInformation("[{Tag}] 已保存中断上下文: Type={TaskType}, Group={GroupName}, Index={TaskIndex}, OneDragonIndex={OneDragonTaskIndex}",
            channelTag, snapshot.TaskType, snapshot.GroupName, snapshot.TaskIndex, snapshot.OneDragonTaskId);
        return true;
    }
}
