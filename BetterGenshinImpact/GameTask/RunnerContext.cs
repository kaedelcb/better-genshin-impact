using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Suspend;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.Model;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask;

/// <summary>
/// 使用 TaskRunner 运行任务时的上下文
/// </summary>
public class RunnerContext : Singleton<RunnerContext>
{
    /// <summary>
    /// 是否是连续执行配置组的场景
    /// </summary>
    public bool IsContinuousRunGroup { get; set; }
    
    public TaskProgress.TaskProgress? taskProgress  { get; set; }

    /// <summary>
    /// 当前独立任务名：仅 TaskRunner.RunSoloTaskAsync 直启路径写入（RunCurrentAsync 拿锁后写入、finally 清空），
    /// 供 task.status / ext 事件兜底识别独立任务。配置组/一条龙路径不写此字段，身份仍走 taskProgress / CurrentScriptProject。
    /// </summary>
    public string? SoloTaskName { get; set; }

    /// <summary>
    /// 暂停逻辑
    /// </summary>
    public bool IsSuspend { get; set; }

    /// <summary>
    /// 优先执行配置组
    /// </summary>
    public bool IsPreExecution { get; set; } = false;

    /// <summary>
    /// 联机锄地助手已接管锄地流程（AutoHoeingTask.TryLaunchAssistant 拉起助手成功时置 true）。
    /// 一条龙 OnOneKeyExecute 检测到后停止执行后续配置组，避免与助手 IPC 双线启动同一批配置组
    /// （"好感任务"/"关直播"等绑定在助手 OnAllReady 里的配置组，两条路径竞争会导致任务启动失败/被跳过）。
    /// </summary>
    public bool IsMultiplayerAssistantActivated { get; set; }
    /// <summary>
    /// 暂停实现
    /// </summary>
    public Dictionary<string, ISuspendable> SuspendableDictionary = new();
    
    /// <summary>
    /// 是否正在自动领取派遣任务
    /// </summary>
    public bool isAutoFetchDispatch { get; set; }

    /// <summary>
    /// 当前使用队伍名称
    /// 游戏内定义的队伍名称
    /// </summary>
    public string? PartyName { get; set; }


    /// <summary>
    /// 自动拾取暂停计数，当大于0时暂停，等于0时不限制。
    /// </summary>
    public int AutoPickTriggerStopCount { get; private set; } = 0;



    /// <summary>
    /// 当前队伍角色信息
    /// </summary>
    private CombatScenes? _combatScenes;

    /// <summary>
    /// 队伍信息缓存是否已命中。
    /// 用于 KazuhaCollectSyncCoordinator.BeginPreparationAsync 在走路途中安全地后台 prefetch：
    /// 仅在缓存命中时调用 GetCombatScenes（不会走 ReturnMainUiTask 分支按 ESC 中断走位）。
    /// </summary>
    public bool HasCombatScenesCached => _combatScenes != null;

    public async Task<CombatScenes?> GetCombatScenes(CancellationToken ct,bool? forceRefresh = false)
    {
        if (_combatScenes == null || forceRefresh == true)
        {
            // 返回主界面再识别
            var returnMainUiTask = new ReturnMainUiTask();
            await returnMainUiTask.Start(ct);

            await Delay(200, ct);
            
            if (forceRefresh == true)
            {
                var oldCombatScenes = _combatScenes;
                for (var i = 0; i < 2; i++)
                {
                    var combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
                    if (!combatScenes.CheckTeamInitialized() && _combatScenes != null)
                    {
                        if (i == 1)
                        {
                            Logger.LogWarning("队伍角色识别失败，使用原有队伍信息");
                            if (oldCombatScenes != null)
                            {
                                _combatScenes = oldCombatScenes;
                            }
                            else
                            {
                                Logger.LogError("原有队伍信息也为空，无法恢复");
                            }
                            break;
                        }
                    }
                    else
                    {
                        // 重新识别成功后，按同名角色从旧队伍继承实时技能 CD 状态（LastSkillTime / OcrSkillCd）。
                        // 游戏内 CD 实时流逝，传送/线路切换期间也在转；不继承会把仍在 CD 中的角色误判为"技能就绪"，
                        // 导致盾奶等角色被无效切出空放技能（见联机锄地传送后盾奶误切人问题）。
                        // 换队场景走的是 ClearCombatScenes()（oldCombatScenes 为 null），此时不继承，避免跨队错误继承 CD。
                        if (oldCombatScenes != null)
                        {
                            foreach (var newAvatar in combatScenes.GetAvatars())
                            {
                                newAvatar.InheritCdStateFrom(oldCombatScenes.SelectAvatar(newAvatar.Name));
                            }
                        }
                        _combatScenes = combatScenes;
                        break;
                    }
                }
            }
            else
            {
                _combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
            }

            if (!_combatScenes.CheckTeamInitialized())
            {
                Logger.LogError("队伍角色识别失败");
                _combatScenes = null;
            }
        }

        return _combatScenes;
    }

    /// <summary>
    /// 尝试静默识别当前队伍信息（无副作用，不出错）
    /// </summary>
    public CombatScenes? TrySyncCombatScenesSilent()
    {
        try
        {
            using var region = CaptureToRectArea();
            var scenes = new CombatScenes(logger: Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance).InitializeTeamSilent(region);
            if (scenes.CheckTeamInitialized())
            {
                return scenes;
            }
            scenes.Dispose();
        }
        catch
        {
            // 静默模式忽略一切异常
        }
        return null;
    }

    public void ClearCombatScenes()
    {
        _combatScenes = null;
    }

    /// <summary>
    /// 补偿一段"游戏世界冻结时间"对当前缓存队伍 CD 的影响。
    /// 传送/加载期间游戏暂停、技能 CD 不流逝，但 CD 计算基于挂钟时间外推，会把冻结时间误算成 CD 流逝。
    /// 此方法把缓存队伍每个角色的 CD 时间戳整体后推 <paramref name="frozen"/>，抵消该误差。
    /// 缓存为空（尚未识别队伍）时跳过——无 CD 可补，传送后重新识别会拿到新时间戳。
    /// 纯内存操作、无 IO、无副作用，不影响未持有队伍缓存的调用方。
    /// </summary>
    /// <param name="frozen">游戏世界冻结的时长。</param>
    public void CompensateFrozenCd(TimeSpan frozen)
    {
        if (frozen <= TimeSpan.Zero)
        {
            return;
        }

        var scenes = _combatScenes;
        if (scenes == null)
        {
            return;
        }

        foreach (var avatar in scenes.GetAvatars())
        {
            avatar.ShiftCdBy(frozen);
        }
    }

    /// <summary>
    /// 任务结束后的清理
    /// </summary>
    public void Clear()
    {
        // 连续执行配置组的情况下，不清理当前队伍
        if (!IsContinuousRunGroup)
        {
            PartyName = null;
        }

        _combatScenes = null;
        IsSuspend = false;
        isAutoFetchDispatch = false;
        SuspendableDictionary.Clear();
    }

    /// <summary>
    /// 彻底恢复到初始状态
    /// </summary>
    public void Reset()
    {
        IsContinuousRunGroup = false;
        PartyName = null;
        _combatScenes = null;
        IsSuspend = false;
        isAutoFetchDispatch = false;
        SuspendableDictionary.Clear();
        AutoPickTriggerStopCount = 0;
        taskProgress = null;
        SoloTaskName = null;
        IsPreExecution = false;
    }

    /// <summary>
    /// 暂停自动拾取，如果传入时间大于0(单位秒)，则在该时间之后自动取消此次暂停（暂停自动拾取计数器-1）,反之暂停拾取（暂停自动拾取计数器+1），此时需要恢复需要手动调用ResumeAutoPick。
    /// </summary>
    public void StopAutoPick(int time = -1)
    {
        AutoPickTriggerStopCount++;
        Logger.LogInformation("暂停自动拾取拾取:"+AutoPickTriggerStopCount);
        ResumeAutoPick(time);
    }
    /// <summary>
    /// 恢复自动拾取（暂停自动拾取计数器-1）。传入参数决定几秒后恢复
    /// </summary>
    public void ResumeAutoPick(int time=0)
    {
        if (time == -1)
        {
            return;
        }

        if (time>0)
        {
            Logger.LogInformation(time+"秒后恢复自动拾取:"+AutoPickTriggerStopCount);
        }
       
        if (time <= 0)
        {
            if (AutoPickTriggerStopCount>0)
            {
                AutoPickTriggerStopCount--;
                Logger.LogInformation("恢复自动拾取:"+AutoPickTriggerStopCount);
            }
        }
        else
        {
            new Thread(() =>
            {
                while (time>0)
                {
                    if (AutoPickTriggerStopCount == 0)
                    {
                        return;
                    }
                    Thread.Sleep(1000);
                    time--;
                }

                ResumeAutoPick();

            }).Start();
        }

    }
    /// <summary>
    /// 在暂停拾取情况下，执行任务
    /// </summary>
    public async Task StopAutoPickRunTask(Func<Task> taskFactory,int time=0)
    {
        try
        {
            AutoPickTriggerStopCount++;
            await taskFactory();
        }
        finally
        {
            ResumeAutoPick(time);
        }

    }
    public void stop()
    {
        _combatScenes = null;
    }
}