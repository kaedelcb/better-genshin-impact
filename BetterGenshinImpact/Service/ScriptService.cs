using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.FarmingPlan;
using BetterGenshinImpact.GameTask.LogParse;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.GameTask.TaskProgress;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Core.Config;

namespace BetterGenshinImpact.Service;

public partial class ScriptService : IScriptService
{
    private readonly ILogger<ScriptService> _logger = App.GetLogger<ScriptService>();
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();
    private readonly TaskTriggerDispatcher _triggers;

    public ScriptService(TaskTriggerDispatcher triggers)
    {
        _triggers = triggers;
    }

    private static bool IsCurrentHourEqual(string input)
    {
        // 尝试将输入字符串转换为整数
        if (int.TryParse(input, out int hour))
        {
            // 验证小时是否在合法范围内（0-23）
            if (hour is >= 0 and <= 23)
            {
                // 获取当前小时数
                int currentHour = DateTime.Now.Hour;
                // 判断是否相等
                return currentHour == hour;
            }
        }

        // 如果输入非数字或不合法，返回 false
        return false;
    }
    public bool ShouldSkipTask(ScriptGroupProject project) => ShouldSkipTask(project, out _);

    private bool ShouldSkipTask(ScriptGroupProject project, out bool skippedByCycle)
    {
        skippedByCycle = false;
        // 独立任务不参与路径相关的跳过检查
        if (project.Type == "SoloTask")
        {
            return false;
        }

        if (project.GroupInfo is { Config.PathingConfig.Enabled: true } )
        {
            if (IsCurrentHourEqual(project.GroupInfo.Config.PathingConfig.SkipDuring))
            {
                _logger.LogInformation($"{project.Name}任务已到禁止执行时段，将跳过！");
                return true;
            }

            var tcc = project.GroupInfo.Config.PathingConfig.TaskCycleConfig;
            if (tcc.Enable)
            {
                int index = tcc.GetExecutionOrder();
                if (index == -1)
                {
                    _logger.LogInformation($"{project.Name}周期配置参数错误，配置将不生效，任务正常执行！");
                }
                else if (index != tcc.Index)
                {
                    skippedByCycle = true;
                    _logger.LogInformation($"{project.Name}任务已经不在执行周期（当前值${index}!=配置值${tcc.Index}），将跳过此任务！");
                    return true;
                }
               
            }
            
        }

        if (TaskContext.Instance().Config.OtherConfig.FarmingPlanConfig.Enabled)
        {
            try
            {
                var task = PathingTask.BuildFromFilePath(Path.Combine(MapPathingViewModel.PathJsonPath, project.FolderName, project.Name));
                if (task is null)
                {
                    return true;
                }
                string message;
                if (FarmingStatsRecorder.IsDailyFarmingLimitReached(task.FarmingInfo,out message))
                {
                    _logger.LogInformation($"{project.Name}:{message},跳过此任务！");
                    return true;
                }
            }
            catch (Exception e)
            {
                TaskControl.Logger.LogError($"锄地规划统计异常：{e.Message}");
            }

            
        }
        string skipMessage;
        if (ExecutionRecordStorage.IsSkipTask(project,out skipMessage))
        {
            TaskControl.Logger.LogInformation($"{project.Name}:{skipMessage},跳过此任务！");
            return true;
        }
        return false; // 不跳过
    }
    
    
    
    
    //优先执行的配置组，统计每个project执行次数
    private readonly Dictionary<string, int> _projectExecutionCount = new();

    /// <param name="job">[A2] 作业描述符：非空时经 TaskRunner 漏斗登记进 JobRegistry；null = 不登记（旧行为）。</param>
    public async Task<TaskRunResult> RunMulti(IEnumerable<ScriptGroupProject> projectList, string? groupName = null,TaskProgress? taskProgress = null, JobDescriptor? job = null)
    {
        groupName ??= "默认";
        ExecutionScope? owned = null;
        try { if (ExecutionScope.Current == null) owned = ExecutionScope.Start(job ?? new(JobKind.Group, groupName, JobSource.Ui)); }
        catch (InvalidOperationException ex)
        {
            // UI/调度器入口此前吞掉了 task_busy/takeover_conflict，调用方既没有日志也无法区分“未启动”。
            // 这正是联机锄地收尾闸门未释放时再次点击运行所呈现的“完全没反应”。
            _logger.LogWarning(ex, "配置组 {Name} 未能启动：{Message}", groupName, ex.Message);
            return TaskRunResult.RejectedSlotBusy;
        }
        using var rootLifetime = owned;
        var scope = ExecutionScope.Current!;
        scope.ThrowIfStopped();
        var configurationPath = Global.Absolute(System.IO.Path.Combine("User", "ScriptGroup", groupName + ".json"));
        if (System.IO.File.Exists(configurationPath)) scope.TrackConfigurationFile(configurationPath);
        scope.SetCheckpoint(new SuspendContextCapture.Snapshot("group", groupName, 0, null, null, null, null, null, false));

        // 启动等待之前先进行取消操作的初始化，便于在任务开始前终止任务.
        // 仅在上下文已释放（上一个任务已结束）时重建：无条件 Set() 会在抢锁失败路径上
        // 提前清掉 WasCancelled（旧任务被注入的 task.start 取消后，这里二次伤害导致误报 started）。
        // 上下文存活时说明有任务在跑，保持原状；拿锁成功后 RunCurrentAsync 内部仍会 Set()。
        CancellationContext.Instance.Set();

        var requestedProjects = projectList.ToList();
        var list = ReloadScriptProjects(requestedProjects);
        if (CoordinatedHoeingScope.Current is { } coordinatedHoeing)
        {
            var rootGroup = scope.Descriptor.Kind == JobKind.Group && ReferenceEquals(scope.Descriptor, job)
                && !RunnerContext.Instance.IsPreExecution;
            if (!coordinatedHoeing.TryRegisterNativePlan(CountPlannedCoordinatedNatives(list, rootGroup)))
                coordinatedHoeing.InvalidateNativePlan();
        }
        
        //恢复临时的跳过标志
        foreach (var scriptGroupProject in requestedProjects)
        {
            scriptGroupProject.SkipFlag = false;
        }



        // // 针对JS 脚本，检查是否包含定时器操作
        // var jsProjects = ExtractJsProjects(list);
        // if (!hasTimer && jsProjects.Count > 0)
        // {
        //     var codeList = await ReadCodeList(jsProjects);
        //     hasTimer = HasTimerOperation(codeList);
        // }

        // 没启动时候，启动截图器
        var startupResult = await StartGameTask();
        if (startupResult != TaskRunResult.Ran)
        {
            scope.Observe(startupResult);
            return startupResult;
        }
        
        
        if (!string.IsNullOrEmpty(groupName)&&!RunnerContext.Instance.IsPreExecution)
        {
            // if (hasTimer)
            // {
            //     _logger.LogInformation("配置组 {Name} 包含实时任务操作调用", groupName);
            // }
            _logger.LogInformation("配置组 {Name} 加载完成，共{Cnt}个脚本，开始执行", groupName, list.Count);
        }

        // var timerOperation = hasTimer ? DispatcherTimerOperationEnum.UseCacheImageWithTriggerEmpty : DispatcherTimerOperationEnum.UseSelfCaptureImage;

        
        bool fisrt = true;
        var executedProjects = 0;
        var cycleSkippedProjects = 0;
        // 重载过滤了未知类型时，不能用剩余项目的周期跳过解释整个请求。
        var onlyCycleSkipped = list.Count == requestedProjects.Count;
        
        
        //非优先执行配置下，清空执行计数
        if (!RunnerContext.Instance.IsPreExecution)
        {
            _projectExecutionCount.Clear();
        }


        var runResult = await new TaskRunner()
            .RunThreadAsync(async () =>
            {
                var failuresBefore = scope.FailureCount;
                var stopwatch = new Stopwatch();
                int projectIndex = -1;
                for (int x = 0; x < list.Count; x++)
                {
                    scope.ThrowIfStopped();
                    var project = list[x];
                    //正常情况下，只有一个真正执行的project，存在其他优先执行配置组情况下，会有多个任务。
                    List<ScriptGroupProject> exeProjects = [project];
                    RunnerContext.Instance.IsPreExecution = false;
                    
                    //配置初始化
                    PathingConditionConfig.RetryAssemblyNum = 3;
                    if (!string.IsNullOrEmpty(project.GroupInfo?.Config.PathingConfig.MainAvatarIndex)) 
                    {
                      PathingConditionConfig.InitialMainAvatarIndex =  project.GroupInfo.Config.PathingConfig.MainAvatarIndex;
                    }
                    
                    //优先执行配置组逻辑
                    if (!RunnerContext.Instance.IsPreExecution &&
                        (project.GroupInfo?.Config.PathingConfig.Enabled ?? false) &&
                        project.GroupInfo.Config.PathingConfig.PreExecutionPriorityConfig.Enabled)
                    {
                        var preConfig = project.GroupInfo.Config.PathingConfig.PreExecutionPriorityConfig;
                        var groupNames = preConfig.GroupNames;

                        if (!string.IsNullOrWhiteSpace(groupNames))
                        {
                            // 解析组名集合
                            var groupNameSet = groupNames
                                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                                .Select(name => name.Trim())
                                .Where(name => !string.IsNullOrWhiteSpace(name));

                            // 获取匹配的脚本组
                            var scriptGroups = App.GetService<ScriptControlViewModel>().ScriptGroups
                                .Where(g => groupNameSet.Contains(g.Name, StringComparer.OrdinalIgnoreCase))
                                .ToList();

                            // 收集需要执行的项目
                            var preExecutionProjects = new List<ScriptGroupProject>();
                            foreach (var group in scriptGroups)
                            {
                                var skipConfig = group.Config.PathingConfig.TaskCompletionSkipRuleConfig;
                                var records = ExecutionRecordStorage.GetRecentExecutionRecordsByConfig(skipConfig);

                                foreach (var p in group.Projects)
                                {
                                    // 检查是否应该跳过任务
                                    if (ExecutionRecordStorage.IsSkipTask(p, out _, records))
                                        continue;

                                    // 生成项目唯一标识
                                    var projectKey = $"{p.Name}|{p.FolderName}|{p.GroupInfo?.Name}";

                                    // 检查执行次数
                                    if (!_projectExecutionCount.TryGetValue(projectKey, out var count))
                                    {
                                        count = 0;
                                    }

                                    // 检查是否超过最大重试次数
                                    if (count > preConfig.MaxRetryCount)
                                        continue;

                                    //增加执行计数
                                    //_projectExecutionCount[projectKey] = count + 1;
                                    preExecutionProjects.Add(p);
                                }
                            }

                            // 存在优先执行的项目，则优先执行
                            if (preExecutionProjects.Count > 0)
                            {
                                CoordinatedHoeingScope.Current?.InvalidateNativePlan();
   
                                _logger.LogInformation($"存在{preExecutionProjects.Count}个需优先执行的任务！");
                                // 设置执行状态，进入优先执行任务
                                RunnerContext.Instance.IsPreExecution = true;
                                //重新构造需要执行的配置组
                                exeProjects = preExecutionProjects.Concat(new[] { project }).ToList();
                            }
                        }
                    }

                    if (!RunnerContext.Instance.IsPreExecution)
                    {
                        projectIndex++;
                    }
                    

                    for (int y = 0; y < exeProjects.Count; y++)
                    {
                        var exeProject = exeProjects[y];
                        //最后一个执行的project，恢复正常执行状态
                        if (y == exeProjects.Count - 1)
                        {
                            RunnerContext.Instance.IsPreExecution = false;
                        }
                        if (!RunnerContext.Instance.IsPreExecution && taskProgress != null && taskProgress.Next != null)
                        {
                            if (taskProgress.Next.Index > projectIndex)
                            {
                                continue;
                            }

                            taskProgress.Next = null;
                        }

                        if (exeProject is { SkipFlag: true })
                        {
                            continue;
                        }

                        // 优先插入项目不经过 ReloadScriptProjects，也必须核对实际项目类型。
                        var knownProjectType = exeProject.Type is "Javascript" or "KeyMouse" or "Pathing" or "Shell" or "SoloTask";
                        var enabledProject = exeProject.Status == "Enabled";
                        if (!knownProjectType || (enabledProject && exeProject.RunNum <= 0))
                        {
                            onlyCycleSkipped = false;
                        }

                        if (ShouldSkipTask(exeProject, out var skippedByCycle))
                        {
                            if (enabledProject)
                            {
                                if (knownProjectType && exeProject.RunNum > 0 && skippedByCycle)
                                    cycleSkippedProjects++;
                                else
                                    onlyCycleSkipped = false;
                            }
                            continue;
                        }

                        if (enabledProject)
                            onlyCycleSkipped = false;

                        //月卡检测
                        await _blessingOfTheWelkinMoonTask.Start(CancellationContext.Instance.Cts.Token);
                        if (exeProject.Status != "Enabled")
                        {
                            _logger.LogInformation("脚本 {Name} 状态为禁用，跳过执行", exeProject.Name);
                            continue;
                        }

                        if (CancellationContext.Instance.Cts.IsCancellationRequested)
                        {
                            // _logger.LogInformation("执行被取消");
                            break;
                        }

                        if (fisrt )
                        {
                            fisrt = false;
                            Notify.Event(NotificationEvent.GroupStart).Success($"配置组{groupName}启动");
                        }

                        if (!RunnerContext.Instance.IsPreExecution &&taskProgress != null)
                        {
                            taskProgress.CurrentScriptGroupProjectInfo = new TaskProgress.ScriptGroupProjectInfo
                            {
                                Name = exeProject.Name,
                                FolderName = exeProject.FolderName, Index = projectIndex,
                                GroupName = taskProgress?.CurrentScriptGroupName ?? ""
                            };
                            TaskProgressManager.SaveTaskProgress(taskProgress);
                        }

                        //优先执行的任务，需要计数
                        if (RunnerContext.Instance.IsPreExecution)
                        {
                            // 生成项目唯一标识
                            var projectKey = $"{exeProject.Name}|{exeProject.FolderName}|{exeProject.GroupInfo?.Name}";
                            // 检查执行次数
                            if (!_projectExecutionCount.TryGetValue(projectKey, out var preExecutionCount))
                            {
                                preExecutionCount = 0;
                            }

                            _projectExecutionCount[projectKey] = preExecutionCount + 1;
                        }


                        for (var i = 0; i < exeProject.RunNum; i++)
                        {
                            try
                            {
                                _triggers.ClearTriggers();


                                _logger.LogInformation("------------------------------");

                                stopwatch.Reset();
                                stopwatch.Start();

                                scope.ThrowIfStopped();
                                scope.SetCheckpoint(new SuspendContextCapture.Snapshot("group", groupName,
                                    projectIndex, exeProject.FolderName, exeProject.Name, null, null, null,
                                    SuspendContextCapture.IsOnlineSignalTask(exeProject.Name, exeProject.FolderName),
                                    // 优先执行项目不属于当前配置组的续跑游标，不能让它抢占当前组恢复位置。
                                     HasNextGroupProject: !RunnerContext.Instance.IsPreExecution && x + 1 < list.Count,
                                     PlanNextTaskIndex: !RunnerContext.Instance.IsPreExecution && x + 1 < list.Count
                                         && SuspendContextCapture.IsOnlineSignalTask(exeProject.Name, exeProject.FolderName)
                                             ? projectIndex + 2 : null,
                                     PlanNextFolderName: !RunnerContext.Instance.IsPreExecution && x + 1 < list.Count
                                         && SuspendContextCapture.IsOnlineSignalTask(exeProject.Name, exeProject.FolderName)
                                         ? list[x + 1].FolderName : null,
                                     PlanNextProjectName: !RunnerContext.Instance.IsPreExecution && x + 1 < list.Count
                                         && SuspendContextCapture.IsOnlineSignalTask(exeProject.Name, exeProject.FolderName)
                                         ? list[x + 1].Name : null,
                                     PlanPreExecution: RunnerContext.Instance.IsPreExecution));
                                await ExecuteProject(exeProject);
                                executedProjects++;
                                // 在项目确实返回后立即推进任务中心的空隙水位；不能等后面的 1 秒延时。
                                if (i == exeProject.RunNum - 1 && !RunnerContext.Instance.IsPreExecution
                                    && (scope.Result is TaskRunResult.Ran or TaskRunResult.Skipped))
                                    scope.MarkPlanGroupProjectCompleted(projectIndex + 2,
                                        x + 1 < list.Count ? list[x + 1].FolderName : null,
                                        x + 1 < list.Count ? list[x + 1].Name : null);

                                //多次执行时及时中断
                                if (exeProject.RunNum > 1 && ShouldSkipTask(exeProject))
                                {
                                    continue;
                                }
                            }
                            catch (NormalEndException e)
                            {
                                throw;
                            }
                            catch (OperationCanceledException e)
                            {
                                _logger.LogInformation("取消执行配置组: {Msg}", e.Message);
                                throw;
                            }
                            catch (Exception e)
                            {
                                scope.Observe(TaskRunResult.Failed);
                                _logger.LogDebug(e, "执行脚本时发生异常");
                                _logger.LogError("执行脚本时发生异常: {Msg}", e.Message);
                                if (!RunnerContext.Instance.IsPreExecution && taskProgress != null && taskProgress.CurrentScriptGroupProjectInfo != null)
                                {
                                    taskProgress.CurrentScriptGroupProjectInfo.Status = 2;
                                }
                            }
                            finally
                            {
                                stopwatch.Stop();
                                var elapsedTime = TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds);
                                // _logger.LogDebug("→ 脚本执行结束: {Name}, 耗时: {ElapsedMilliseconds} 毫秒", project.Name, stopwatch.ElapsedMilliseconds);
                                _logger.LogInformation("→ 脚本执行结束: {Name}, 耗时: {Minutes}分{Seconds:0.000}秒", exeProject.Name,
                                    elapsedTime.Hours * 60 + elapsedTime.Minutes, elapsedTime.TotalSeconds % 60);
                                _logger.LogInformation("------------------------------");
                            }

                            await Task.Delay(1000);
                        }

                        if (!RunnerContext.Instance.IsPreExecution && taskProgress != null)
                        {
                            if (taskProgress.CurrentScriptGroupProjectInfo != null)
                            {
                                taskProgress.CurrentScriptGroupProjectInfo.TaskEnd = true;
                                taskProgress.CurrentScriptGroupProjectInfo.EndTime = DateTime.Now;
                                if (taskProgress.CurrentScriptGroupProjectInfo.Status == 1)
                                {
                                    taskProgress.ConsecutiveFailureCount = 0;
                                    taskProgress.LastSuccessScriptGroupProjectInfo =
                                        taskProgress.CurrentScriptGroupProjectInfo;
                                    taskProgress.LastScriptGroupName = taskProgress.CurrentScriptGroupName;
                                }

                                //累计连续失败次数
                                if (taskProgress.CurrentScriptGroupProjectInfo.Status == 2)
                                {
                                    taskProgress.ConsecutiveFailureCount++;
                                }

                                taskProgress?.History?.Add(taskProgress.CurrentScriptGroupProjectInfo);
                                TaskProgressManager.SaveTaskProgress(taskProgress);
                            }

                            //异常达到一次次数，重启bgi
                            var autoconfig = TaskContext.Instance().Config.OtherConfig.AutoRestartConfig;
                            if (autoconfig.Enabled && taskProgress.ConsecutiveFailureCount >= autoconfig.FailureCount)
                            {
                                _logger.LogInformation("调度器任务出现未预期的异常，自动重启bgi");
                                Notify.Event(NotificationEvent.GroupEnd).Error("调度器任务出现未预期的异常，自动重启bgi");
                                if (autoconfig.RestartGameTogether
                                    && TaskContext.Instance().Config.GenshinStartConfig.LinkedStartEnabled
                                    && TaskContext.Instance().Config.GenshinStartConfig.AutoEnterGameEnabled)
                                {
                                    SystemControl.CloseGame();
                                    Thread.Sleep(2000);
                                }

                                SystemControl.RestartApplication(["--TaskProgress", taskProgress.Name]);
                            }
                        }
                    }
                }
                if (executedProjects == 0)
                {
                    // 最后一次快速跳过后也可能收到停止；不能让 no_work 把停止改记成失败。
                    scope.ThrowIfStopped();
                    if (CancellationContext.Instance.IsCancellationRequested)
                        throw new OperationCanceledException("配置组在零完成收尾前已取消");

                    if (!onlyCycleSkipped || cycleSkippedProjects == 0 || scope.FailureCount > failuresBefore)
                        throw new InvalidOperationException("no_work: 没有任何配置组项目实际完成");

                    _logger.LogInformation("配置组 {Name} 的 {Cnt} 个项目全部按执行周期跳过，本组正常结束", groupName, cycleSkippedProjects);
                }
            }, job: job);
        

        // 还原定时器
        // TaskTriggerDispatcher.Instance().SetTriggers(GameTaskManager.LoadInitialTriggers());

        // [A1.1 抢锁显式化] 槽位被占未执行时：TaskRunner 已留 ERR，这里补组级结论并跳过
        // "执行结束"式收尾，避免"从未启动却报执行结束"的假事实（2026-09-13 双入口抢锁事故形态）。
        // taskProgress.Next 清理与原路径（落到 :475 收尾）保持一致。
        if (runResult == TaskRunResult.RejectedSlotBusy)
        {
            _logger.LogError("配置组 {Name} 未能启动：任务槽位被占用，本次未执行", groupName);
            if (taskProgress != null)
            {
                taskProgress.Next = null;
            }
            return runResult;
        }

        // [A6] 让位联机锄地：组从未起步，不走"执行结束"式收尾（同 A1.1 不假事实纪律）；
        // 恢复点已在 TaskRunner 让位点保存，锄地批次结束后由 task.resume 恢复。
        if (runResult == TaskRunResult.Preempted)
        {
            _logger.LogInformation("配置组 {Name} 已让位联机锄地批次，后续节点不再起步", groupName);
            if (taskProgress != null)
            {
                taskProgress.Next = null;
            }
            return runResult;
        }

        if (runResult != TaskRunResult.Ran)
        {
            _logger.LogWarning("配置组 {Name} 未成功完成，结果: {Result}", groupName, runResult);
            if (runResult == TaskRunResult.Failed)
                Notify.Event(NotificationEvent.GroupEnd).Error($"配置组{groupName}执行失败");
            if (taskProgress != null) taskProgress.Next = null;
            return runResult;
        }

        if (!string.IsNullOrEmpty(groupName)&&!RunnerContext.Instance.IsPreExecution)
        {
            _logger.LogInformation("配置组 {Name} 执行结束", groupName);
        }

        if (!fisrt&&!RunnerContext.Instance.IsPreExecution)
        {
            if (CancellationContext.Instance.IsManualStop is false)
            {
                Notify.Event(NotificationEvent.GroupEnd).Success($"配置组{groupName}结束");
            }
        }

        if (taskProgress != null)
        {
            taskProgress.Next = null;
        }
        return runResult;
    }

    /// <summary>The list already carries the execution start/skip cursor; ordinary JS has no native hoeing API.</summary>
    internal static int? CountPlannedCoordinatedNatives(IEnumerable<ScriptGroupProject> projects, bool rootGroup)
    {
        if (!rootGroup) return null; // One-dragon roots and nested groups have no complete direct-group plan.
        long count = 0;
        foreach (var project in projects)
        {
            // Priority groups can be inserted even before a disabled/skipped ordinary project is checked.
            if (project.GroupInfo?.Config.PathingConfig is { Enabled: true, PreExecutionPriorityConfig.Enabled: true })
                return null;
            if (project.Status != "Enabled" || project.SkipFlag == true) continue;
            if (project.Type == "SoloTask" && project.Name is "锄地一条龙（联机）" or "锄地一条龙")
            {
                if (project.RunNum < 0) return null;
                count += project.RunNum;
                if (count > int.MaxValue) return null;
            }
        }
        return (int)count;
    }

    private List<ScriptGroupProject> ReloadScriptProjects(IEnumerable<ScriptGroupProject> projectList)
    {
        var list = new List<ScriptGroupProject>();
        foreach (var project in projectList)
        {
            if (project.Type == "Javascript")
            {
                var newProject = new ScriptGroupProject(new ScriptProject(project.FolderName));
                CopyProjectProperties(project, newProject);
                list.Add(newProject);
            }
            else if (project.Type == "KeyMouse")
            {
                var newProject = ScriptGroupProject.BuildKeyMouseProject(project.Name);
                CopyProjectProperties(project, newProject);
                list.Add(newProject);
            }
            else if (project.Type == "Pathing")
            {
                var newProject = ScriptGroupProject.BuildPathingProject(project.Name, project.FolderName);
                CopyProjectProperties(project, newProject);
                list.Add(newProject);
                // hasTimer = true;
            }
            else if (project.Type == "Shell")
            {
                var newProject = ScriptGroupProject.BuildShellProject(project.Name);
                CopyProjectProperties(project, newProject);
                list.Add(newProject);
            }
            else if (project.Type == "SoloTask")
            {
                var newProject = ScriptGroupProject.BuildSoloTaskProject(project.Name);
                CopyProjectProperties(project, newProject);
                list.Add(newProject);
            }
        }

        return list;
    }

    private void CopyProjectProperties(ScriptGroupProject source, ScriptGroupProject target)
    {
        target.Status = source.Status;
        target.Schedule = source.Schedule;
        target.RunNum = source.RunNum;
        target.JsScriptSettingsObject = source.JsScriptSettingsObject;
        target.SoloTaskSettingsObject = source.SoloTaskSettingsObject;
        target.GroupInfo = source.GroupInfo;
        target.AllowJsNotification = source.AllowJsNotification;
        target.AllowJsHTTPHash = source.AllowJsHTTPHash;
        target.SkipFlag = source.SkipFlag;
    }

    // private List<ScriptProject> ExtractJsProjects(List<ScriptGroupProject> list)
    // {
    //     var jsProjects = new List<ScriptProject>();
    //     foreach (var project in list)
    //     {
    //         if (project is { Type: "Javascript", Project: not null })
    //         {
    //             jsProjects.Add(project.Project);
    //         }
    //     }
    //
    //     return jsProjects;
    // }

    private async Task ExecuteProject(ScriptGroupProject project)
    {
        TaskContext.Instance().CurrentScriptProject = project;
        RunnerContext.Instance.CurrentScriptProject = project;
        // 新项目开始：清掉上一个项目残留的线路名（上一个项目异常退出时不会走到 finally 以外的清理）
        ScriptRouteProgress.Clear();
        try
        {
            if (project.Type == "Javascript")
            {
                if (project.Project == null)
                {
                    throw new Exception("Project 为空");
                }

                _logger.LogInformation("→ 开始执行JS脚本: {Name}", project.Name);
                if (RunnerContext.Instance.IsPreExecution) _logger.LogInformation("此任务为优先执行任务！");
                var hasSettingsBeforeRun = project.JsScriptSettingsObject != null;
                try
                {
                    await project.Run();
                }
                finally
                {
                    SaveScriptGroupAfterJsRun(project, hasSettingsBeforeRun);
                }
            }
            else if (project.Type == "KeyMouse")
            {
                _logger.LogInformation("→ 开始执行键鼠脚本: {Name}", project.Name);
                if (RunnerContext.Instance.IsPreExecution) _logger.LogInformation("此任务为优先执行任务！");
                await project.Run();
            }
            else if (project.Type == "Pathing")
            {
                _logger.LogInformation("→ 开始执行地图追踪任务: {Name}", project.Name);
                if (RunnerContext.Instance.IsPreExecution) _logger.LogInformation("此任务为优先执行任务！");
                await project.Run();
            }
            else if (project.Type == "Shell")
            {
                _logger.LogInformation("→ 开始执行shell: {Name}", project.Name);
                if (RunnerContext.Instance.IsPreExecution) _logger.LogInformation("此任务为优先执行任务！");
                await project.Run();
            }
            else if (project.Type == "SoloTask")
            {
                _logger.LogInformation("→ 开始执行独立任务: {Name}", project.Name);
                if (RunnerContext.Instance.IsPreExecution) _logger.LogInformation("此任务为优先执行任务！");
                await project.Run();
            }
        }
        finally
        {
            // 任务结束后清空 CurrentScriptProject，避免 HandleTaskStatus 的
            // `taskName ??= TaskContext.CurrentScriptProject?.Name` 用残留值补 taskName → running 恒 true → 任务名残留。
            TaskContext.Instance().CurrentScriptProject = null;
            RunnerContext.Instance.CurrentScriptProject = null;
            // 项目结束（正常/异常/取消）清空脚本线路名：下一个任务不该继承上一个脚本的线路显示。
            ScriptRouteProgress.Clear();
        }
    }

    private void SaveScriptGroupAfterJsRun(ScriptGroupProject project, bool hasSettingsBeforeRun)
    {
        if (!hasSettingsBeforeRun)
        {
            project.JsScriptSettingsObject = null;
            return;
        }

        var scriptGroup = project.GroupInfo!;
        try
        {
            var scriptGroupPath = Global.Absolute(@"User\ScriptGroup");
            if (!Directory.Exists(scriptGroupPath))
            {
                Directory.CreateDirectory(scriptGroupPath);
            }

            var file = Path.Combine(scriptGroupPath, $"{scriptGroup.Name}.json");
            scriptGroup.WriteToFileAtomically(file);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存JS脚本配置组失败: {GroupName}", scriptGroup.Name);
        }
    }

    private async Task<List<string>> ReadCodeList(List<ScriptProject> list)
    {
        var codeList = new List<string>();
        foreach (var project in list)
        {
            var code = await project.LoadCode();
            codeList.Add(code);
        }

        return codeList;
    }

    private bool HasTimerOperation(IEnumerable<string> codeList)
    {
        return codeList.Any(code => DispatcherAddTimerRegex().IsMatch(code));
    }

    [GeneratedRegex(@"^(?!\s*\/\/)\s*dispatcher\.\s*addTimer", RegexOptions.Multiline)]
    private static partial Regex DispatcherAddTimerRegex();


}
