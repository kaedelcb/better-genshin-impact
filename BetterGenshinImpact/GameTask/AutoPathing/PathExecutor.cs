using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.AutoSkip;
using BetterGenshinImpact.GameTask.AutoSkip.Assets;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map;
using BetterGenshinImpact.GameTask.Model.Area;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.GameTask.AutoPathing.Suspend;
using BetterGenshinImpact.GameTask.Common;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static BetterGenshinImpact.GameTask.SystemControl;
using ActionEnum = BetterGenshinImpact.GameTask.AutoPathing.Model.Enum.ActionEnum;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.Common.Job;

namespace BetterGenshinImpact.GameTask.AutoPathing;

public class PathExecutor
{
    private readonly CameraRotateTask _rotateTask;
    private readonly TrapEscaper _trapEscaper;
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();
    private AutoSkipTrigger? _autoSkipTrigger;
    public int SuccessFight = 0;
    //路径追踪完全走完所有路径结束的标识
    public bool SuccessEnd = false;
    private PathingPartyConfig? _partyConfig;
    private CancellationToken ct;
    private PathExecutorSuspend pathExecutorSuspend;
    private string _hurryOnAvatar = "";
    private bool _MwkFly = true;
    private readonly ReturnMainUiTask _returnMainUiTask = new();
    
    public PathingPartyConfig PartyConfig
    {
        get => _partyConfig ?? PathingPartyConfig.BuildDefault();
        set => _partyConfig = value;
    }
    
    public PathExecutor(CancellationToken ct)
    {
        _trapEscaper = new(ct);
        _rotateTask = new(ct);
        this.ct = ct;
        pathExecutorSuspend = new PathExecutorSuspend(this);
    }
    
    /// <summary>
    /// 判断是否中止地图追踪的条件
    /// </summary>
    public Func<ImageRegion, bool>? EndAction { get; set; }

    public CombatScenes? _combatScenes;
    // private readonly Dictionary<string, string> _actionAvatarIndexMap = new();

    private DateTime _elementalSkillLastUseTime = DateTime.MinValue;
    private DateTime _useGadgetLastUseTime = DateTime.MinValue;

    private const int RetryTimes = 2;
    private int _inTrap = 0;


    //记录当前相关点位数组
    public (int, List<WaypointForTrack>) CurWaypoints { get; set; }

    //记录当前点位
    public (int, WaypointForTrack) CurWaypoint { get; set; }

    //记录恢复点位数组
    private (int, List<WaypointForTrack>) RecordWaypoints { get; set; }

    //记录恢复点位
    private (int, WaypointForTrack) RecordWaypoint { get; set; }

    //跳过除走路径以外的操作
    private bool _skipOtherOperations = false;

    // 最近一次获取派遣奖励的时间
    private DateTime _lastGetExpeditionRewardsTime = DateTime.MinValue;

    //记录上一个节点
    private WaypointForTrack? _lastWaypoint = null;
    
    // 朝向标记位
    private bool _faceToMark = false;
    
    //当到达恢复点位
    public void TryCloseSkipOtherOperations()
    {
        // Logger.LogWarning("判断是否跳过地图追踪:" + (CurWaypoint.Item1 < RecordWaypoint.Item1));
        if (RecordWaypoints == CurWaypoints && CurWaypoint.Item1 < RecordWaypoint.Item1)
        {
            return;
        }

        if (_skipOtherOperations)
        {
            Logger.LogWarning("已到达上次点位，地图追踪功能恢复");
        }

        _skipOtherOperations = false;
    }

    //记录点位，方便后面恢复
    public void StartSkipOtherOperations()
    {
        // Logger.LogWarning("记录恢复点位，地图追踪将到达上次点位之前将跳过走路之外的操作 {t} - {t2}",PathingConditionConfig.AutoEatCount,CurWaypoints);
        _skipOtherOperations = true;
        RecordWaypoints = CurWaypoints;
        RecordWaypoint = CurWaypoint;
    }

    public async Task Pathing(PathingTask task)
    {
        // SuspendableDictionary;
        const string sdKey = "PathExecutor";
        var sd = RunnerContext.Instance.SuspendableDictionary;
        sd.Remove(sdKey);
        
        RunnerContext.Instance.SuspendableDictionary.TryAdd(sdKey, pathExecutorSuspend);

        if (!task.Positions.Any())
        {
            Logger.LogWarning("没有路径点，寻路结束");
            return;
        }

        // 切换队伍
        if (!await SwitchPartyBefore(task))
        {
            return;
        }

        // 校验路径是否可以执行
        if (!await ValidateGameWithTask(task))
        {
            return;
        }

        InitializePathing(task);
        
        // 转换、按传送点分割路径
        var waypointsList = ConvertWaypointsForTrack(task.Positions, task);

        await Delay(100, ct);
        Navigation.WarmUp(task.Info.MapMatchMethod);
        
        await InitializeAutoEat();//初始化自动吃药
        PathingConditionConfig.PartyConfigBackUp = PartyConfig;
        // Logger.LogError("开始寻路{t1}-{t2}",PathingConditionConfig.PartyConfigBackUp.RecoverAvatarIndex,PartyConfig.RecoverAvatarIndex);

        foreach (var waypoints in waypointsList) // 按传送点分割的路径
        {
            AutoFightTask.IsTpForRecover = false;
            _faceToMark = false;
            CurWaypoints = (waypointsList.FindIndex(wps => wps == waypoints), waypoints);
            for (var i = 0; i < RetryTimes; i++)
            {
                try
                {
                    if (PartyConfig.AutoEatEnabled && PathingConditionConfig.AutoEatCount < 3) PathingConditionConfig.AutoEatCount = 0;
                    
                    await ResolveAnomalies(); // 异常场景处理

                    // 如果首个点是非TP点位，强制设置在这个点位附近优先做局部匹配
                    if (waypoints[0].Type != WaypointType.Teleport.Code)
                    {
                        Navigation.SetPrevPosition((float)waypoints[0].X, (float)waypoints[0].Y);
                    }
                    
                    Waypoint? nextWaypoint = null;
                    double? nextDdistance = null;
                    foreach (var waypoint in waypoints) // 一条路径
                    {
                        CurWaypoint = (waypoints.FindIndex(wps => wps == waypoint), waypoint);
                        
                        //计算下一个节点到当前节点的距离
                        nextWaypoint = waypoint == waypoints.Last() ? null : waypoints[waypoints.IndexOf(waypoint) + 1];
                        if (nextWaypoint != null)
                        {
                           nextDdistance = Navigation.GetDistance(waypoint, new Point2f((float)nextWaypoint.X, (float)nextWaypoint.Y));
                        }
                        
                        TryCloseSkipOtherOperations();
                        await RecoverWhenLowHp(waypoint,PartyConfig.RedBloodSwitchOnly); // 低血量恢复

                        if (waypoint.Type == WaypointType.Teleport.Code)
                        {
                            if (CurWaypoints.Item1 > 0)
                            {
                                var prevWaypoints = waypointsList[CurWaypoints.Item1 - 1];
                                var prevWaypoint = prevWaypoints[prevWaypoints.Count - 1];
                                if (prevWaypoint.Type == WaypointType.Teleport.Code
                                    || prevWaypoint.Action == ActionEnum.Fight.Code
                                    || prevWaypoint.Action == ActionEnum.NahidaCollect.Code
                                    || prevWaypoint.Action == ActionEnum.PickAround.Code)
                                {
                                    // No delay
                                }
                                else
                                {
                                    await Delay(1000, ct);
                                }
                            }
                            await HandleTeleportWaypoint(waypoint);
                            if (_lastWaypoint == null || waypoint.MapName != _lastWaypoint.MapName)
                            {
                                // Logger.LogInformation("线路切换，强制校验");
                                await ValidateGameWithTask(task,true);
                            }
                        }
                        else
                        {
                            await BeforeMoveToTarget(waypoint);
                            
                            // Path不用走得很近，Target需要接近，但都需要先移动到对应位置
                            if (waypoint.Type == WaypointType.Orientation.Code)
                            {
                                // 方位点，只需要朝向
                                // 考虑到方位点大概率是作为执行action的最后一个点，所以放在此处处理，不和传送点一样单独处理
                                await FaceTo(waypoint);
                            }
                            else if (waypoint.Type == WaypointType.ActionOnly.Code)
                            {
                                Logger.LogInformation("执行 {t}","ActionOnly");
                                // 找到当前出战角色
                                 var ra = CaptureToRectArea();
                                 for (int k = 1; k <= 4; k++)
                                 {
                                     var avatar = _combatScenes?.SelectAvatar(k);
                                     if (avatar != null && avatar.IsActive(ra))
                                     {
                                         Logger.LogInformation("当前出战角色 {t}",avatar.Name);
                                         if (!string.IsNullOrEmpty(waypoint.ActionParams))
                                         {
                                             // waypoint.ActionParams = avatar.Name + " " + waypoint.ActionParams;
                                         }
                                         break;
                                     }
                                 }
                                 ra.Dispose();
                            }
                            else if (waypoint.Action != ActionEnum.UpDownGrabLeaf.Code)
                            {
                                await MoveTo(waypoint,true,task,nextWaypoint,nextDdistance);
                            }

                            await BeforeMoveCloseToTarget(waypoint);

                            if (IsTargetPoint(waypoint))
                            {
                                await MoveCloseTo(waypoint);
                            }

                            //skipOtherOperations如果重试，则跳过相关操作，
                            if ((!string.IsNullOrEmpty(waypoint.Action) && !_skipOtherOperations) ||
                                waypoint.Action == ActionEnum.CombatScript.Code)
                            {
                                if (waypoint.Action == ActionEnum.Fight.Code)
                                {
                                    AutoFightTask.FightWaypoint = waypoint;
                                    PathingConditionConfig.CombatScenesGoBackUp = _combatScenes;//把地图追踪的战斗CD等同步给战斗节点
                                }
                                else
                                {
                                    AutoFightTask.FightEndFlag = true;
                                    AutoFightTask.FightWaypoint = null;
                                }
                                // 执行 action11
                                
                                //如果上一节点和当前节点坐标一致，不执行action以避免卡死
                                await AfterMoveToTarget(waypoint,nextWaypoint);
                                
                                if (waypoint.Action == ActionEnum.Fight.Code)
                                {
                                    if(!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex)) PartyConfig.MainAvatarIndex = PathingConditionConfig.InitialMainAvatarIndex;
                                    PathingConditionConfig.CombatScenesGoBackUp = null;
                                    if (PartyConfig.AutoEatEnabled && PathingConditionConfig.AutoEatCount < 3)
                                    {
                                        PathingConditionConfig.AutoEatCount = 0;
                                    }
                                }
                            }
                        }
                        _lastWaypoint = waypoint;
                    }

                    if (waypoints == waypointsList.Last())
                    {
                        SuccessEnd = true;
                    }
                    break;
                }
                catch (HandledException handledException)
                {
                    SuccessEnd = true;
                    break;
                }
                catch (NormalEndException normalEndException)
                {
                    Logger.LogInformation(normalEndException.Message);
                    if (!RunnerContext.Instance.isAutoFetchDispatch && RunnerContext.Instance.IsContinuousRunGroup)
                    {
                        throw;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (TaskCanceledException e)
                {
                    if (!RunnerContext.Instance.isAutoFetchDispatch && RunnerContext.Instance.IsContinuousRunGroup)
                    {
                        throw;
                    }
                    else
                    {
                        break;
                    }
                }
                catch (RetryException retryException)
                {
                    // Logger.LogError("retryException.Message11111111");
                    StartSkipOtherOperations();
                    Logger.LogWarning(retryException.Message);
                    if (PartyConfig.AutoEatEnabled && PathingConditionConfig.AutoEatCount < 3)  PathingConditionConfig.AutoEatCount = 0;
                }
                catch (RetryNoCountException retryException)
                {
                    //特殊情况下，重试不消耗次数
                    i--;
                    StartSkipOtherOperations();
                    Logger.LogWarning(retryException.Message);
                }
                finally
                {
                    // 不管咋样，松开所有按键
                    Simulation.SendInput.Keyboard.KeyUp(User32.VK.VK_W);
                    Simulation.SendInput.Mouse.RightButtonUp();
                    PathingConditionConfig.CombatScenesGoBackUp = null;
                    GC.Collect();//释放内存
                    GC.WaitForPendingFinalizers();//释放内存
                }
            }
        }
    }
    
    private async Task InitializeAutoEat()
    {
        if (!PartyConfig.AutoEatEnabled)
        {
            PathingConditionConfig.AutoEatCount = 3;
            return;
        }
        
        using (var ra = CaptureToRectArea())
        {
            using var bloodtRect = ra.DeriveCrop(1817, 781, 4, 14);
            using var mask = OpenCvCommonHelper.Threshold(bloodtRect.SrcMat,new Scalar(185, 225, 95), new Scalar(200, 240, 110));//new Scalar(192, 233, 102), new Scalar(193, 233, 103
            using var labels = new Mat();
            using var stats = new Mat();
            using var centroids = new Mat();

            var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
            
            if (numLabels <= 1)
            {
                if (PathingConditionConfig.RetryAssemblyNum > 0)
                {
                    if (await RetryAssembly())
                    {
                        PathingConditionConfig.AutoEatCount = 0;
                        return;
                    }
                }
                PathingConditionConfig.AutoEatCount = 3;
                Logger.LogInformation("自动吃药：未发现营养袋，自动吃药{text}", "关闭");
            }
            else
            {
                PathingConditionConfig.AutoEatCount = 0;
                // Logger.LogInformation("自动吃药：已发现营养袋，自动吃药{text}", "开启");
            }
        }
    }
    
    private async Task<bool> RetryAssembly()
    { 
        var result = await NewRetry.WaitForAction( () =>
            {
                _returnMainUiTask.Start(ct).Wait(5000,ct);
                Logger.LogInformation("自动吃药：尝试装配便携式营养袋剩余次数 {t}",PathingConditionConfig.RetryAssemblyNum);
                Delay(1000, ct).Wait();
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget, KeyType.KeyDown);
                Delay(1000, ct).Wait();
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget, KeyType.KeyUp);
                Delay(1500, ct).Wait();
                using (var ra2 = CaptureToRectArea())
                {
                    var boon = ra2.Find(AutoFightAssets.Instance.NutritionBagRa);
                    if (boon.IsExist())
                    {
                        boon.Click();
                        return true;
                    }
                    Logger.LogWarning("自动吃药：小道具页面未发现营养袋");
                }
                //点击一下鼠标
                Simulation.SendInput.Mouse.LeftButtonClick();
                return false;
            },
            ct,
            1,
            1000
        );
        
        PathingConditionConfig.RetryAssemblyNum--;
        return result;
    }

    private bool IsTargetPoint(WaypointForTrack waypoint)
    {
        // 方位点不需要接近
        if (waypoint.Type == WaypointType.Orientation.Code || waypoint.Action == ActionEnum.UpDownGrabLeaf.Code)
        {
            return false;
        }


        var action = ActionEnum.GetEnumByCode(waypoint.Action);
        if (action is not null && action.UseWaypointTypeEnum != ActionUseWaypointTypeEnum.Custom)
        {
            // 强制点位类型的 action，以 action 为准
            return action.UseWaypointTypeEnum == ActionUseWaypointTypeEnum.Target;
        }

        // 其余情况和没有action的情况以点位类型为准
        return waypoint.Type == WaypointType.Target.Code;
    }

    private async Task<bool> SwitchPartyBefore(PathingTask task)
    {
        var ra = CaptureToRectArea();

        // 切换队伍前判断是否全队死亡 // 可能队伍切换失败导致的死亡
        if (Bv.ClickIfInReviveModal(ra))
        {
            await Bv.WaitForMainUi(ct); // 等待主界面加载完成
            Logger.LogInformation("复苏完成");
            await Delay(4000, ct);
            // 血量肯定不满，直接去七天神像回血
            await TpStatueOfTheSeven();
        }

        var pRaList = ra.FindMulti(AutoFightAssets.Instance.PRa); // 判断是否联机
        if (pRaList.Count > 0)
        {
            Logger.LogInformation("处于联机状态下，不切换队伍");
        }
        else
        {
            if (PartyConfig is { Enabled: false })
            {
                // 调度器未配置的情况下，根据地图追踪条件配置切换队伍
                var partyName = FilterPartyNameByConditionConfig(task);
                if (!await SwitchParty(partyName))
                {
                    Logger.LogError("切换队伍失败，无法执行此路径！请检查地图追踪设置！");
                    return false;
                }
            }
            else if (!string.IsNullOrEmpty(PartyConfig.PartyName))
            {
                if (!await SwitchParty(PartyConfig.PartyName))
                {
                    Logger.LogError("切换队伍失败，无法执行此路径！请检查配置组中的地图追踪配置！");
                    return false;
                }
            }
        }

        return true;
    }
    
    private void InitializePathing(PathingTask task)
    {
        LogScreenResolution();
        InitializeTravelMode();
        WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(this,
            "UpdateCurrentPathing", new object(), task));
    }

    private void InitializeTravelMode()
    {
        if (PartyConfig.HurryOnAvatar == "自动" && _combatScenes!= null)
        {
            foreach (var avatar in _combatScenes.GetAvatars())
            {
                if (PartyConfig.HurryOnAvatarList.Contains(avatar.Name))
                {
                    _hurryOnAvatar = avatar.Name;  
                }
            }
        }
        else
        {
            _hurryOnAvatar = PartyConfig.HurryOnAvatar;
        }

        if (string.IsNullOrEmpty(PartyConfig.TravelMode))
        {
            PartyConfig.TravelMode = "精准靠近";
        }
        
        _MwkFly = PartyConfig.MwkFlyEnabled;
    }

    private void LogScreenResolution()
    {
        var gameScreenSize = SystemControl.GetGameScreenRect(TaskContext.Instance().GameHandle);
        if (gameScreenSize.Width * 9 != gameScreenSize.Height * 16)
        {
            Logger.LogError("游戏窗口分辨率不是 16:9 ！当前分辨率为 {Width}x{Height} , 非 16:9 分辨率的游戏无法正常使用地图追踪功能！",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率不是 16:9 ！无法使用地图追踪功能！");
        }

        if (gameScreenSize.Width < 1920 || gameScreenSize.Height < 1080)
        {
            Logger.LogError("游戏窗口分辨率小于 1920x1080 ！当前分辨率为 {Width}x{Height} , 小于 1920x1080 的分辨率的游戏地图追踪的效果非常差！",
                gameScreenSize.Width, gameScreenSize.Height);
            throw new Exception("游戏窗口分辨率小于 1920x1080 ！无法使用地图追踪功能！");
        }
    }

    /// <summary>
    /// 切换队伍
    /// </summary>
    /// <param name="partyName"></param>
    /// <returns></returns>
    private async Task<bool> SwitchParty(string? partyName)
    {
        bool success = true;
        if (!string.IsNullOrEmpty(partyName))
        {
            if (RunnerContext.Instance.PartyName == partyName)
            {
                return success;
            }

            bool forceTp = PartyConfig.IsVisitStatueBeforeSwitchParty;

            if (forceTp) // 强制传送模式
            {
                await new TpTask(ct).TpToStatueOfTheSeven(); // fix typos
                success = await new SwitchPartyTask().Start(partyName, ct);
            }
            else // 优先原地切换模式
            {
                try
                {
                    success = await new SwitchPartyTask().Start(partyName, ct);
                }
                catch (PartySetupFailedException)
                {
                    await new TpTask(ct).TpToStatueOfTheSeven();
                    success = await new SwitchPartyTask().Start(partyName, ct);
                }
            }

            if (success)
            {
                RunnerContext.Instance.PartyName = partyName;
                RunnerContext.Instance.ClearCombatScenes();
            }
        }

        return success;
    }


    private static string? FilterPartyNameByConditionConfig(PathingTask task)
    {
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        var materialName = task.GetMaterialName();
        var specialActions = task.Positions
            .Select(p => p.Action)
            .Where(action => !string.IsNullOrEmpty(action))
            .Distinct()
            .ToList();
        var partyName = pathingConditionConfig.FilterPartyName(materialName, specialActions);
        return partyName;
    }

    /// <summary>
    /// 校验
    /// </summary>
    /// <param name="task"></param>
    ///  <param name="force">是否强制校验，默认false</param>
    /// <returns></returns>
    private async Task<bool> ValidateGameWithTask(PathingTask task , bool? force = false)
    {
        _combatScenes = await RunnerContext.Instance.GetCombatScenes(ct, force);
        if (_combatScenes == null)
        {
            return false;
        }

        // 没有强制配置的情况下，使用地图追踪内的条件配置
        // 必须放在这里，因为要通过队伍识别来得到最终结果
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        if (PartyConfig is { Enabled: false })
        {
            PartyConfig = pathingConditionConfig.BuildPartyConfigByCondition(_combatScenes);
        }

        // 校验角色是否存在
        if (task.HasAction(ActionEnum.NahidaCollect.Code))
        {
            var avatar = _combatScenes.SelectAvatar("纳西妲");
            if (avatar == null)
            {
                Logger.LogError("此路径存在纳西妲收集动作，队伍中没有纳西妲角色，无法执行此路径！");
                return false;
            }

            // _actionAvatarIndexMap.Add("nahida_collect", avatar.Index.ToString());
        }

        // 把所有需要切换的角色编号记录下来
        Dictionary<string, ElementalType> map = new()
        {
            { ActionEnum.HydroCollect.Code, ElementalType.Hydro },
            { ActionEnum.ElectroCollect.Code, ElementalType.Electro },
            { ActionEnum.AnemoCollect.Code, ElementalType.Anemo }
        };

        foreach (var (action, el) in map)
        {
            if (!ValidateElementalActionAvatarIndex(task, action, el, _combatScenes))
            {
                return false;
            }
        }

        return true;
    }

    private bool ValidateElementalActionAvatarIndex(PathingTask task, string action, ElementalType el,
        CombatScenes combatScenes)
    {
        if (task.HasAction(action))
        {
            foreach (var avatar in combatScenes.GetAvatars())
            {
                if (ElementalCollectAvatarConfigs.Get(avatar.Name, el) != null)
                {
                    return true;
                }
            }

            Logger.LogError("此路径存在 {El}元素采集 动作，队伍中没有对应元素角色:{Names}，无法执行此路径！", el.ToChinese(),
                string.Join(",", ElementalCollectAvatarConfigs.GetAvatarNameList(el)));
            return false;
        }
        else
        {
            return true;
        }
    }

    private List<List<WaypointForTrack>> ConvertWaypointsForTrack(List<Waypoint> positions, PathingTask task)
    {
        // 把 X Y 转换为 MatX MatY
        var allList = positions.Select(waypoint =>
        {
            WaypointForTrack wft = new WaypointForTrack(waypoint, task.Info.MapName, task.Info.MapMatchMethod);
            wft.Misidentification=waypoint.PointExtParams.Misidentification;
            wft.MonsterTag = waypoint.PointExtParams.MonsterTag;
            wft.EnableMonsterLootSplit = waypoint.PointExtParams.EnableMonsterLootSplit;
            return wft;
        }).ToList();

        // 按照WaypointType.Teleport.Code切割数组
        var result = new List<List<WaypointForTrack>>();
        var tempList = new List<WaypointForTrack>();
        foreach (var waypoint in allList)
        {
            if (waypoint.Type == WaypointType.Teleport.Code)
            {
                if (tempList.Count > 0)
                {
                    result.Add(tempList);
                    tempList = new List<WaypointForTrack>();
                }
            }

            tempList.Add(waypoint);
        }

        result.Add(tempList);

        return result;
    }

    /// <summary>
    /// 尝试队伍回血，如果单人回血，由于记录检查时是哪位残血，则当作行走位处理。
    /// </summary>
    public async Task<bool> TryPartyHealing(CombatScenes? combatScenes = null,PathingPartyConfig? partyConfig = null)
    {
        if (_combatScenes is null)
        {
            if (combatScenes is null)
            {
                Logger.LogWarning("回血失败，未获取到战斗场景");
                return false; 
            }
            _combatScenes = combatScenes;
        }

        if (_combatScenes is null)
        {
            Logger.LogWarning("回血失败，未获取到战斗场景2");
            return false; 
        }

        if (partyConfig is not null)
        {
            PartyConfig = partyConfig;
        }
        
        var avatars = _combatScenes.GetAvatars();
        foreach (var avatar in avatars)
        {
            if (avatar.Name == "白术")
            {
                if (avatar.TrySwitch())
                {
                    //1命白术能两次
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(800, ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(800, ct);
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    await Delay(4000, ct);
                    return true;
                }

                break;
            }
            else if (avatar.Name == "希格雯")
            {
                if (avatar.TrySwitch())
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(11000, ct);
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    return true;
                }

                break;
            }
            else if (avatar.Name == "珊瑚宫心海")
            {
                if (avatar.TrySwitch())
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    await Delay(500, ct);
                    //尝试Q全队回血
                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                    //单人血只给行走位加血
                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                    await Delay(5000, ct);
                    return true;
                }
            }
            else if (avatar.Name == "爱可菲" || avatar.Name == "闲云" || avatar.Index.ToString() == PartyConfig.RecoverAvatarIndex)
            {
                // //获取出战角色
                // var avatarCurrent = _combatScenes.CurrentAvatar();
                // //计算出战角色在avatars队伍中的序号
                // var currentIndex = avatars.FirstOrDefault(a => a.Name == avatarCurrent)?.Index;
                // if (currentIndex == null) return false;
               
                var currentIndex = 0;
                using (var bitmap = CaptureToRectArea())
                {
                    if (PartyConfig.RecoverAvatarIndex != null)
                    {
                        for (int i = 1; i <= 4; i++)
                        {
                            var avatar2 = _combatScenes.SelectAvatar(i);
                            if (avatar2.IsActive(bitmap))
                            {
                                currentIndex = i;
                            }
                        }
                    
                        // Logger.LogInformation("当前行走角色序号：{Index}", currentIndex);

                        if (currentIndex == 0)
                        {
                            return false;
                        } 
                        var num = _combatScenes.GetAvatars().Count();
                        List<int> useEqList = Enumerable.Range(1, num).ToList();
                        try
                        {
                            var qSkill = await AutoFightSkill.AvatarQSkillAsync(bitmap, useEqList, currentIndex);
                            if (qSkill.Contains(avatar.Index))
                            {
                                if (avatar.TrySwitch())
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                    await Delay(5000, ct);
                                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                                    return true;
                                }
                            }
                            else if ((avatar.Name == "爱可菲" || avatar.Name == "闲云") && qSkill.Contains(avatar.Index))
                            {
                                if (avatar.TrySwitch())
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                    await Delay(5000, ct);
                                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                                    return true;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError("尝试识别元素爆发技能失败，原因：{Ex}", ex.Message);
                        }
                    }
                }
            }
        }

        return false;
    }

    private async Task RecoverWhenLowHp(WaypointForTrack waypoint,bool switchOnly = false)
    {
        if (PartyConfig.OnlyInTeleportRecover && waypoint.Type != WaypointType.Teleport.Code)
        {
            return;
        }
        using var region = CaptureToRectArea();
        if (Bv.CurrentAvatarIsLowHp(region) && !(await TryPartyHealing() && Bv.CurrentAvatarIsLowHp(region)))
        {
            Logger.LogInformation("当前角色血量过低，去七天神像恢复-1 {t}", PathingConditionConfig.AutoEatCount);
            
            using (var bitmap = CaptureToRectArea())
            {
                var pixel = 0;

                for (int i = 0; i < 2; i++)
                {
                    using (var bitmap2 = CaptureToRectArea())
                    {
                        var pixelValue = bitmap2.SrcMat.At<Vec3b>(1010,814);
                        if (!(Math.Abs(pixelValue[0] - 34) <= 10 &&
                              Math.Abs(pixelValue[1] - 215) <= 10 &&
                              Math.Abs(pixelValue[2] - 150) <= 10))
                        {
                            pixel += 1;
                        }
                        else
                        {
                            pixel = 0;
                        }
                    }
                    await Task.Delay(100, ct);
                }
                
                if (pixel >= 2)
                { 
                    Logger.LogInformation("当前行走角色血量仍过低，尝试切换人-1");
                        
                    if (!string.IsNullOrWhiteSpace(PartyConfig.MainAvatarIndex))
                    {
                        var avatarIndex = int.Parse(PartyConfig.MainAvatarIndex);
                        var nextAvatarIndex = (avatarIndex % 4) + 1;
                        if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                            _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                        {
                            nextAvatarIndex = (nextAvatarIndex % 4) + 1;
                        }
            
                        var avatar = _combatScenes?.SelectAvatar(avatarIndex);
            
                        await Delay(300, ct);
            
                        if (avatar != null && avatar.IsActive(bitmap))
                        {
                            PartyConfig.MainAvatarIndex = nextAvatarIndex.ToString();
                            await SwitchAvatar(nextAvatarIndex.ToString());
                        }
                        else
                        {
                            await SwitchAvatar(PartyConfig.MainAvatarIndex);
                        }
                    }
                    else
                    {
                        for (int i = 1; i <= 4; i++)
                        {
                            var avatar = _combatScenes?.SelectAvatar(i);
                            if (avatar != null && avatar.IsActive(bitmap))
                            {
                                var nextAvatarIndex = (i % 4) + 1;
                                if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                                    _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                                {
                                    nextAvatarIndex = (nextAvatarIndex % 4) + 1;
                                }
                                await SwitchAvatar(nextAvatarIndex.ToString());
                                break;
                            }
                        }
                    }
                }
            }
            
            using (var bitmap = CaptureToRectArea())
            {
                var confirmRectArea = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                if (!confirmRectArea.IsEmpty())
                {
                    Simulation.ReleaseAllKey();
                    confirmRectArea.Click();
                    await Task.Delay(399, ct);
                    confirmRectArea.ClickTo(-100, 0);
                    await Task.Delay(300, ct);
                    Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); 
                    await Task.Delay(500, ct);
                }
            }
            
            await TpStatueOfTheSeven(switchOnly);
            if (PathingConditionConfig.AutoEatCount < 2) return;
            throw new RetryException("回血完成后重试路线-1");
        }
        else if (Bv.ClickIfInReviveModal(region))
        {
            await Bv.WaitForMainUi(ct); // 等待主界面加载完成
            Logger.LogInformation("复苏完成-1");
            await Delay(4000, ct);
            // 血量肯定不满，直接去七天神像回血
            await TpStatueOfTheSeven();
            if (PathingConditionConfig.AutoEatCount < 2) return;
            throw new RetryException("回血完成后重试路线-2");
        }
    }
    
    private async Task TpStatueOfTheSeven(bool switchOnly = false)
    {
        // Logger.LogInformation("AutoEatCount111 {text}",PathingConditionConfig.AutoEatCount);
        if (PartyConfig.AutoEatEnabled && PathingConditionConfig.AutoEatCount < 2)
        {
            if (DateTime.UtcNow > PathingConditionConfig.LastEatTime.AddSeconds(1.5))
            {
                Simulation.ReleaseAllKey();
                
                if (!switchOnly)
                {
                    PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                    Logger.LogWarning("自动吃药：尝试使用小道具恢复-2");
                    if(PathingConditionConfig.AutoEatCount < 1)Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                    PathingConditionConfig.AutoEatCount++;
                } 
                
                Logger.LogInformation("自动吃药：检测到红血，尝试恢复-3 {t}", PathingConditionConfig.AutoEatCount);
                
                using (var bitmap = CaptureToRectArea())
                {
                    var confirmRectArea = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                    if (!confirmRectArea.IsEmpty())
                    {
                        Simulation.ReleaseAllKey();
                        confirmRectArea.Click();
                        await Task.Delay(399, ct);
                        confirmRectArea.ClickTo(-100, 0);
                        await Task.Delay(300, ct);
                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); 
                        await Task.Delay(500, ct);
                        // PathingConditionConfig.AutoEatCount++;
                    }
                }
            }
            else
            { 
                using (var bitmap = CaptureToRectArea())
                {
                    if (Bv.IsInRevivePrompt(bitmap))
                    {
                        PathingConditionConfig.AutoEatCount++;
                        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    }
                }
                Logger.LogWarning("自动吃药：距离上次吃药时间过小，等待重试-3");
            }
            if(PathingConditionConfig.AutoEatCount < 2)return;
        }

        using (var bitmap = CaptureToRectArea())
        {
            if (Bv.IsInRevivePrompt(bitmap))
            {
                Logger.LogInformation("复苏弹窗出现，尝试复苏-4");
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Task.Delay(500, ct);
            }
        }

        // tp 到七天神像回血
        var tpTask = new TpTask(ct);
        await RunnerContext.Instance.StopAutoPickRunTask(async () => await tpTask.TpToStatueOfTheSeven(), 5);
        PartyConfig.MainAvatarIndex = PathingConditionConfig.InitialMainAvatarIndex;
        Logger.LogInformation("血量恢复完成。【设置】-【七天神像设置】可以修改回血相关配置-k {t}。",PathingConditionConfig.AutoEatCount);
    }

    /// <summary>
    /// 尝试自动领取派遣奖励，
    /// </summary>
    /// <returns>是否可以领取派遣奖励</returns>
    private async Task<bool> TryGetExpeditionRewardsDispatch(TpTask? tpTask = null)
    {
        // 检查配置组设置：是否禁用自动领取派遣奖励
        if (PartyConfig?.DisableAutoFetchDispatch == true)
        {
            return false;
        }

        if (tpTask == null)
        {
            tpTask = new TpTask(ct);
        }
        
        // 最小5分钟间隔
        if ( _combatScenes?.CurrentMultiGameStatus?.IsInMultiGame == true || (DateTime.UtcNow - _lastGetExpeditionRewardsTime).TotalMinutes < 5)
        {
            return false;
        }

        using (var bitmap = CaptureToRectArea())
        {
            if (bitmap.Find(AutoFightAssets.Instance.PRa).IsExist())
            {
                return false;
            }
        }

        //打开大地图操作
        await tpTask.OpenBigMapUi();
        bool changeBigMap = false;
        string adventurersGuildCountry =
            TaskContext.Instance().Config.OtherConfig.AutoFetchDispatchAdventurersGuildCountry;
        if (!RunnerContext.Instance.isAutoFetchDispatch && adventurersGuildCountry != "无" && !string.IsNullOrEmpty(adventurersGuildCountry))
        {
            var ra1 = CaptureToRectArea();
            var textRect = new Rect(60, 20, 160, 260);
            var textMat = new Mat(ra1.SrcMat, textRect);
            string text = OcrFactory.Paddle.Ocr(textMat);
            if (text.Contains("探索派遣奖励"))
            {
                changeBigMap = true;
                Logger.LogInformation("开始自动领取派遣任务！");
                try
                {
                    RunnerContext.Instance.isAutoFetchDispatch = true;
                    await RunnerContext.Instance.StopAutoPickRunTask(
                        async () => await new GoToAdventurersGuildTask().Start(adventurersGuildCountry, ct, null, true),
                        5);
                    Logger.LogInformation("自动领取派遣结束，回归原任务！");
                }
                catch (Exception e)
                {
                    Logger.LogInformation("未知原因，发生异常，尝试继续执行任务！");
                }
                finally
                {
                    RunnerContext.Instance.isAutoFetchDispatch = false;
                    _lastGetExpeditionRewardsTime = DateTime.UtcNow; // 无论成功与否都更新时间
                    GC.Collect();//释放内存
                    GC.WaitForPendingFinalizers();//释放内存
                }
            }
        }

        return changeBigMap;
    }

    private async Task HandleTeleportWaypoint(WaypointForTrack waypoint,WaypointForTrack? lastWaypoint = null)
    {
        var forceTp = waypoint.Action == ActionEnum.ForceTp.Code;
        TpTask tpTask = new TpTask(ct);
        await TryGetExpeditionRewardsDispatch(tpTask);
        var (tpX, tpY) = await tpTask.Tp(waypoint.GameX, waypoint.GameY, waypoint.MapName, forceTp);
        var (tprX, tprY) = MapManager.GetMap(waypoint.MapName, waypoint.MapMatchMethod)
            .ConvertGenshinMapCoordinatesToImageCoordinates(new Point2f((float)tpX, (float)tpY));
        Navigation.SetPrevPosition(tprX, tprY); // 通过上一个位置直接进行局部特征匹配
        await Delay(500, ct); // 多等一会
        //如果前后地图不同
    }

    public async Task FaceTo(WaypointForTrack waypoint)
    {
        var screen = CaptureToRectArea();
        var position = await GetPosition(screen, waypoint);
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        Logger.LogDebug("朝向点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        await WaitUntilRotatedTo(targetOrientation, 2);
        await Delay(450, ct);
    }

    public DateTime moveToStartTime;

    public async Task MoveTo(WaypointForTrack waypoint,bool isGetOut = true, PathingTask? task = null, Waypoint? nextWaypoint = null,double? nextDistance = null)
    {
        // 切人
        Task.Run(async () =>
        {
            // 替换位置：在 MoveTo 方法内的类似代码块
            if (!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex))
            {
                var idxStr = PartyConfig.MainAvatarIndex.Trim();
                if (int.TryParse(idxStr, out var idx) && idx >= 1 && idx <= 4)
                {
                    var vk = idx switch
                    {
                        1 => User32.VK.VK_1,
                        2 => User32.VK.VK_2,
                        3 => User32.VK.VK_3,
                        4 => User32.VK.VK_4
                    };
                    Simulation.SendInput.Keyboard.KeyPress(vk);
                }
                // 其它值：不按按键
            }
        }, ct);
        using var screen = CaptureToRectArea();
        var pixelYellowValue = screen.SrcMat.At<Vec3b>(1010, 814);
        var yellowBlood = (Math.Abs(pixelYellowValue[0] - 50) <= 10 &&
                            Math.Abs(pixelYellowValue[1] - 204) <= 10 &&
                            Math.Abs(pixelYellowValue[2] - 255) <= 10);
        if (!yellowBlood && _combatScenes?.GetAvatars().Count > 1)
        {
            await SwitchAvatar(PartyConfig.MainAvatarIndex, false, task, true);
        }
        
        var (position, additionalTimeInMs) = await GetPositionAndTime(screen, waypoint);
        var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
        Logger.LogDebug("粗略接近途经点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");
        await WaitUntilRotatedTo(targetOrientation, 5);
        moveToStartTime = DateTime.UtcNow;
        var lastPositionRecord = DateTime.UtcNow;
        var fastMode = false;
        var prevPositions = new List<Point2f>();
        var fastModeColdTime = DateTime.MinValue;
        var prevNotTooFarPosition = position;
        int num = 0, distanceTooFarRetryCount = 0, consecutiveRotationCountBeyondAngle = 0;
        var distanceCount = 0;
        var nextistanceCount = 0;
        var hurryOnLogo = true;
        var hurryOnIn = false;
        var sprintMouseLogo = true;
        var trackingLogo = true;
        var mavikaFlyCount = 0;
        var runCount = 0;
        var flyDelay = waypoint.MoveMode == MoveModeEnum.Fly.Code;
        bool? hurryOnBool = null;
        var continueHurryOn = 0;
        var isClimbLogo = 0;
        var isFlyingMwk = false;
        var aa = false;
        bool? runToDash = false;
        double distanceHalf = 0;
        bool dushed = true;
        bool relifed = false;
        
        string nextAvatarIndexStop = "";
        Avatar? avatar = null;
        if (_combatScenes is not null)
        {
            avatar = _combatScenes.SelectAvatar(_hurryOnAvatar);
            
            var mainAvatarIndex = _combatScenes.SelectAvatar(PartyConfig.MainAvatarIndex);
            if (mainAvatarIndex != null)
            {
                if (mainAvatarIndex.Name == _hurryOnAvatar)
                {
                    nextAvatarIndexStop = (mainAvatarIndex.Index % 4 + 1).ToString(); 
                }
                else
                {
                    nextAvatarIndexStop = _combatScenes.SelectAvatar(1).Name == _hurryOnAvatar ? "2" : "1"; 
                }
            }
            else
            {
                 nextAvatarIndexStop = _combatScenes.SelectAvatar(1).Name == _hurryOnAvatar ? "2" : "1";
            }
        }
        
        //测试节点信息
        // Logger.LogWarning("赶路测试log:当前节点:({x2}),动作:({t1}),类型({t2}))", waypoint.Type, waypoint.Action, waypoint.MoveMode);
        // Logger.LogWarning("赶路测试log:Next节点:({x2}),动作:({t1}),间隔距离({x3}),类型({t2}))", nextWaypoint?.Type?? "null", nextWaypoint?.MoveMode ,nextWaypoint?.Action, (int)Math.Round(nextDistance.Value));

        // 按下w，一直走
        if (!flyDelay) Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown); 
        
        while (!ct.IsCancellationRequested)
        {
            if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
            {
                if (!flyDelay) Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            }
            flyDelay = false;

            num++;
            if ((DateTime.UtcNow - moveToStartTime).TotalSeconds > 240)
            {
                Logger.LogWarning("执行超时，放弃此次追踪");
                throw new RetryException("路径点执行超时，放弃整条路径");
            }

            using var screen2 = CaptureToRectArea();

            EndJudgment(screen2);
            
             (position, additionalTimeInMs) = await GetPositionAndTime(screen2, waypoint);
             if (additionalTimeInMs>0)
             {
                 if (!Simulation.IsKeyDown(GIActions.MoveForward.ToActionKey().ToVK()))
                 {
                     if (!flyDelay)Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                 }

                 additionalTimeInMs = additionalTimeInMs + 1000;//当做起步补偿
             }
            var distance = Navigation.GetDistance(waypoint, position);
            Debug.WriteLine($"接近目标点中，距离为{distance}");

            if(runToDash == false && distance > 40 && waypoint.MoveMode == MoveModeEnum.Run.Code && avatar?.Name == "玛薇卡")
            {
                runToDash = true;
                distanceHalf = distance*2/4;
                waypoint.MoveMode = MoveModeEnum.Dash.Code;
            }
            else if (runToDash == true && distance < distanceHalf)
            {
                waypoint.MoveMode = MoveModeEnum.Run.Code;
                Task.Run(async () =>
                {
                    Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                    await Delay(1000, ct);
                    Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);  
                }, ct);
                runToDash = null;
            }
            
            hurryOnBool ??= (waypoint.MoveMode == MoveModeEnum.Run.Code ||
                               waypoint.MoveMode == MoveModeEnum.Dash.Code ||
                               ((waypoint.MoveMode == MoveModeEnum.Climb.Code || (waypoint.MoveMode == MoveModeEnum.Fly.Code && distance > 35) 
                                                                              || waypoint.MoveMode == MoveModeEnum.Jump.Code || (waypoint.MoveMode == MoveModeEnum.Climb.Code && distance < 20)) && avatar?.Name == "玛薇卡"));
            

            if (avatar != null)
            {
                // 自动赶路的靠近节点模式
                if (!hurryOnLogo && trackingLogo && 
                    (PartyConfig.TravelMode == "精准靠近" && distance < (!string.IsNullOrEmpty(nextWaypoint?.Action) ? 30 : avatar.Name == "瓦雷莎" ? 30 : 25) //精准靠近
                     || (PartyConfig.TravelMode == "连续赶路" && distance < 40 && (nextDistance < 25 || nextWaypoint?.Type == WaypointType.Target.Code || waypoint.Type == WaypointType.Target.Code 
                                                                               || nextWaypoint?.Action == MoveModeEnum.Fly.Code || waypoint?.Action == ActionEnum.CombatScript.Code
                                                                               ||(nextDistance < 25 && nextWaypoint?.Action == ActionEnum.CombatScript.Code))))) //连续赶路
                {
                    trackingLogo = false;
                    if (avatar.IsActive(screen2))
                    {
                        if (avatar.Name == "玛薇卡")
                        {
                            var pos = screen2.SrcMat.At<Vec3b>(978, 1692);
                            var pos2 = screen2.SrcMat.At<Vec3b>(995, 1702);
                            var pos3 = screen2.SrcMat.At<Vec3b>(1028, 1584);
                            double colorDifference = Math.Sqrt(
                                Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                                Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                                Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
                            );
                            if (colorDifference < 15)
                            {
                                hurryOnIn = true;
                                if (Bv.GetMotionStatus(screen2) != MotionStatus.Fly || !(pos3.Item0 == 255 && pos3.Item1 == 255 && pos3.Item2 == 255)
                                    || nextWaypoint?.Action != MoveModeEnum.Fly.Code|| waypoint?.Action != MoveModeEnum.Fly.Code)
                                {
                                    Logger.LogInformation("自动赶路：{t} 节点接近...-i {t2} {t3} {t4}",PartyConfig.TravelMode,nextAvatarIndexStop,waypoint?.MoveMode,Math.Round(colorDifference));
                                    
                                    using var screen3 = CaptureToRectArea(); 
                                    var isFlying = Bv.GetMotionStatus(screen3) == MotionStatus.Fly;
                                    if (!isFlying)
                                    {
                                        Task.Run(async () =>
                                        {
                                            var switchedAvatar = await SwitchAvatar2(nextAvatarIndexStop);
                                           if( switchedAvatar == null)
                                           {
                                               if (PathingConditionConfig.AutoEatCount < 3)
                                               {
                                                   PathingConditionConfig.AutoEatCount = 2;
                                               }
                                               relifed = true;
                                           }
                                           else
                                           {
                                               relifed = false;
                                           }
                                        }, ct);
                                    }   
                                }
                            }
                        }
                        else if (avatar.Name == "瓦雷莎")
                        {
                            if (await AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 2, ct))
                            {
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward,KeyType.KeyUp);
                                await Delay(300, ct);
                            } 
                        }
                        else 
                        {
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward,KeyType.KeyUp);
                        }
                    }

                    hurryOnIn = false;
                    if ((nextDistance < 25 || distance < 20) && waypoint?.MoveMode != MoveModeEnum.Climb.Code)
                    {
                        nextistanceCount ++;
                        if (nextistanceCount > 3)
                        {
                            Logger.LogWarning("赶路靠近超时-2");
                            break;
                        }
                    }
                }
                
                //飞行模式下，判断状态并处理&&nextWaypoint?.MoveMode != MoveModeEnum.Fly.Code 
                if (waypoint?.MoveMode == MoveModeEnum.Fly.Code && PartyConfig.TravelMode == "连续赶路"
                    || waypoint?.Action == ActionEnum.StopFlying.Code || waypoint?.MoveMode == MoveModeEnum.Dash.Code)
                {
                    if (distance > 4)
                    {
                        var isClimb = Bv.GetMotionStatus(screen2) == MotionStatus.Climb;
                        if (isClimb && !hurryOnLogo&& isClimbLogo<2 && waypoint.MoveMode != MoveModeEnum.Climb.Code)
                        {
                            await Delay(1000, ct);
                            Simulation.SendInput.SimulateAction(GIActions.Drop);
                            await Delay(500, ct);
                            isClimbLogo ++ ;
                        }
                    }
                } 

                // 自动赶路的特殊处理模式，防止异常情况
                if (!hurryOnLogo)
                {
                    if (avatar.Name == "玛薇卡") //玛薇卡冲坡判断
                    {
                        var pos = screen2.SrcMat.At<Vec3b>(1012,1574);
                        var pos2 = screen2.SrcMat.At<Vec3b>(1006, 1608);
                        var pos3 = screen2.SrcMat.At<Vec3b>(1028, 1584);
                        var colorDifference = Math.Sqrt(
                            Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                            Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                            Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
                        );
                        
                        if (colorDifference < 15 && !isFlyingMwk)
                        {
                            if (pos3.Item0 == 255 && pos3.Item1 == 255 && pos3.Item2 == 255)
                            {
                                mavikaFlyCount++;
                                if (mavikaFlyCount > 5 && avatar.IsActive(screen2))
                                {
                                    if(nextWaypoint?.MoveMode != MoveModeEnum.Fly.Code)Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    mavikaFlyCount = 0;
                                    Logger.LogInformation("自动赶路：靠近节点切换 {t}...-h {t2}",nextAvatarIndexStop,waypoint?.MoveMode);
                                } 
                            }
                        }
                    }
                    else if (avatar.Name == "瓦雷莎") //瓦雷莎冲刺判断
                    {
                        var lower = new Scalar(220, 150, 150);
                        var higher = new Scalar(230, 160, 180);
                        using var mask = OpenCvCommonHelper.Threshold(screen2.DeriveCrop(948, 410, 26, 30).SrcMat, lower,higher);
                        using var labels = new Mat();
                        using var stats = new Mat();
                        using var centroids = new Mat();

                        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
                        
                        if (numLabels > 3 && numLabels <40)
                        {
                            mavikaFlyCount++;
                            if (mavikaFlyCount > 2 && avatar.IsActive(screen2))
                            {
                                hurryOnLogo = true;
                                Task.Run(async () =>
                                {
                                    await Delay(1000, ct);
                                    using var region3 = CaptureToRectArea();
                                    if (avatar.IsActive(region3))
                                    {
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        await Delay(100, ct);
                                        using var region4 = CaptureToRectArea();
                                        var isFlying = Bv.GetMotionStatus(region4) == MotionStatus.Fly;
                                        if (isFlying)
                                        {
                                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                            Logger.LogInformation("自动赶路：{t} 下落攻击...","瓦蕾莎");  
                                        }
                                    }
                                    mavikaFlyCount = 0;
                                }, ct);
                            }
                        }
                    }
                }
                
                //自动赶路
                if (hurryOnLogo&& !yellowBlood && !string.IsNullOrEmpty(_hurryOnAvatar) &&
                    distance >  (PartyConfig.Distance) && (hurryOnBool ?? false))
                {
                    //判断是否在飞行状态
                    if (Bv.GetMotionStatus(screen2) != MotionStatus.Fly)
                    {
                        await SwitchAvatar(avatar.Index.ToString());    
                    }
                    
                    if (avatar.Name == "瓦雷莎")
                    {
                        waypoint.MoveMode = MoveModeEnum.Run.Code;
                        sprintMouseLogo = false;
                    }
                    
                    // if (waypoint.MoveMode != MoveModeEnum.Walk.Code)

                    hurryOnLogo = false; 
              
                    Logger.LogInformation("自动赶路：{t} 赶路...{t2}",avatar.Name,Math.Round(distance));
                    if (avatar.Name == "玛薇卡") //连续点按E类型
                    {
                        // 获取两个点的颜色值
                        var pos = screen2.SrcMat.At<Vec3b>(978, 1692);
                        var pos2 = screen2.SrcMat.At<Vec3b>(995, 1702);
                        double colorDifference = Math.Sqrt(
                            Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                            Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                            Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
                        );
                        // Logger.LogInformation("玛薇卡技能颜色差值-2:{ColorDifference}", Math.Round(colorDifference, 2));
                        
                        if (colorDifference >15)
                        {
                            Task.Run(async () =>
                            {
                                // hurryOnIn = true;
                                // await Delay(100, ct);
                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                await Delay(200, ct);
                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                await Delay(300, ct);
                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                await Delay(700, ct);
                                // Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                                // await Delay(400, ct);
                                
                                using var region3 = CaptureToRectArea();

                                double colorDifference2 = 0;
                                
                                if (waypoint?.MoveMode == MoveModeEnum.Fly.Code && _MwkFly)
                                {
                                    // Logger.LogInformation("玛薇卡技能11111");
                                    // waypoint.MoveMode = MoveModeEnum.Run.Code;
                                    // var pos11 = region3.SrcMat.At<Vec3b>(1012,1574);
                                    // var pos22 = region3.SrcMat.At<Vec3b>(1006, 1608);
                                    var pos33 = region3.SrcMat.At<Vec3b>(1028, 1584);
                                    //  colorDifference2 = Math.Sqrt(
                                    //     Math.Pow(pos11.Item0 - pos22.Item0, 2) + // 蓝通道差值的平方
                                    //     Math.Pow(pos11.Item1 - pos22.Item1, 2) + // 绿通道差值的平方
                                    //     Math.Pow(pos11.Item2 - pos22.Item2, 2)   // 红通道差值的平方
                                    // );
                                    isFlyingMwk = (pos33.Item0 == 255 && pos33.Item1 == 255 && pos33.Item2 == 255);
                                    
                                    if (!aa && isFlyingMwk)
                                    {
                                        if (int.TryParse(waypoint.ActionParams, out int actionParams))//&& isFlyingMwk
                                        {
                                            var param = actionParams switch
                                            {
                                                > 10000 => 0.07,
                                                > 8000 => 0.08,
                                                > 7000 => 0.10,
                                                > 6000 => 0.11,
                                                > 5000 => 0.12,
                                                > 4000 => 0.13,
                                                > 3000 => 0.14,
                                                > 2000 => 0.15,
                                                > 1000 => 0.18,
                                                > 500 => 0.2,
                                                _ => 0.2,
                                            };
                                            waypoint.ActionParams = (actionParams + actionParams*param).ToString();
                                        }
                                        else
                                        {
                                            // Logger.LogInformation("自动赶路：222333y {t}",waypoint.ActionParams);
                                            waypoint.ActionParams = "1000";
                                            // Logger.LogInformation("自动赶路：222333yy {t}",waypoint.ActionParams);
                                        }
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        aa = true;
                                    }
                                }
                                else
                                {
                                    // Logger.LogInformation("玛薇卡技能2222");
                                    isFlyingMwk = false;
                                    // 获取两个点的颜色值
                                    var pos3 = region3.SrcMat.At<Vec3b>(978, 1692);
                                    var pos4 = region3.SrcMat.At<Vec3b>(995, 1702);
                                    colorDifference2 = Math.Sqrt(
                                        Math.Pow(pos3.Item0 - pos4.Item0, 2) + // 蓝通道差值的平方
                                        Math.Pow(pos3.Item1 - pos4.Item1, 2) + // 绿通道差值的平方
                                        Math.Pow(pos3.Item2 - pos4.Item2, 2)   // 红通道差值的平方
                                    );
                                }
                                
                                // Logger.LogInformation("玛薇卡技能颜色差值-3:{ColorDifference} - {isFlyingMwk}", Math.Round(colorDifference2, 2),isFlyingMwk);
                                
                                if (colorDifference2 > 15 || isFlyingMwk)// colorDifference2 < 15
                                {
                                    continueHurryOn++;
                                    
                                    
                                    //  if(waypoint.MoveMode != MoveModeEnum.Fly.Code)
                                    // {
                                    //     hurryOnLogo = true; 
                                    // }
                                    // else 
                                     if (continueHurryOn > 0 && (waypoint.MoveMode != MoveModeEnum.Fly.Code && !isFlyingMwk))//?????
                                    {
                                        Logger.LogInformation("自动赶路：继续...");
                                        hurryOnLogo = true;
                                        continueHurryOn = 0;
                                    }
                                    // hurryOnLogo = true;
                                    
                                    var isClimb = Bv.GetMotionStatus(region3) == MotionStatus.Climb;
                                    if (isClimb)
                                    {
                                        // Logger.LogError("自动赶路：878567");
                                        Simulation.SendInput.SimulateAction(GIActions.Drop);
                                        await Delay(500, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    }
                                    
                                    // Logger.LogInformation("自动赶路：{t} 继续...", distance);

                                    if (distance > 10)
                                    {
                                        if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
                                        {
                                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                        }
                                        else if (waypoint.MoveMode == MoveModeEnum.Run.Code)
                                        {
                                            runCount++;
                                            if (runCount < 5)
                                            {
                                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                            }
                                        }
                                        else if(waypoint.MoveMode == MoveModeEnum.Fly.Code && isFlyingMwk)
                                        {
                                            var flyTime = distance switch
                                            {
                                                > 140 => 3500,
                                                > 100 => 2400,
                                                > 80 => 900,
                                                > 70 => 500,
                                                > 60 => 270,
                                                > 50 => 80,
                                                // > 40 => 10, 
                                                _ => 0 
                                            };

                                            Logger.LogInformation("自动赶路：{t} 飞行 {t2} ms 距离 {t3}","玛薇卡", flyTime,Math.Round(distance));
                                            
                                            if (flyTime > 0)
                                            {
                                                waypoint.MoveMode = MoveModeEnum.Dash.Code;
                                                await Delay(flyTime, ct);
                                            }
                                            waypoint.MoveMode = MoveModeEnum.Fly.Code;
                                            hurryOnLogo = false;
                                        }
                                    }
                                }
                                else
                                {
                                    avatar.LastSkillTime = DateTime.UtcNow;
                                }
                            },ct);
                        }
                        else
                        {
                            hurryOnIn = false;
                            hurryOnLogo = true;
                        }
                    }
                    else if (avatar.Name == "瓦雷莎") //长E类型
                    {
                        await Delay(300, ct);
                        if (!await AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 2, ct))
                        {
                            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyDown);
                            await Delay(300, ct);
                            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                            await Delay(200, ct);
                            avatar.LastSkillTime = DateTime.UtcNow;

                            if (!await AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 2, ct))
                            {
                                Logger.LogInformation("自动赶路：继续...");
                                hurryOnLogo = true;
                                if (distance > 20)
                                {
                                    if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
                                    {
                                        Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                    }
                                    else if (waypoint.MoveMode == MoveModeEnum.Run.Code)
                                    {
                                        if (runCount < 2)
                                        {
                                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                        }
                                    } 
                                }
                            }
                            else
                            {
                                var higher = new Scalar(0, 221, 250);
                                using var region2 = CaptureToRectArea();
                                using var mask = OpenCvCommonHelper.Threshold(region2.DeriveCrop(1686, 949, 10, 10).SrcMat,higher);
                                using var labels = new Mat();
                                using var stats = new Mat();
                                using var centroids = new Mat();

                                var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
                                    connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
                                
                                if (numLabels > 1)
                                {
                                    Logger.LogInformation("自动赶路：继续...");
                                    hurryOnLogo = true;
                                    if (distance > 20)
                                    {
                                        if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
                                        {
                                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                        }
                                        else if (waypoint.MoveMode == MoveModeEnum.Run.Code)
                                        {
                                            if (runCount <2)
                                            {
                                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                            }
                                        } 
                                    }
                                }
                            }
                        }
                        else
                        {
                            sprintMouseLogo = true;
                            hurryOnLogo = true;
                        }
                    }
                    else if (avatar.Name == "希诺宁") //短E类型
                    {
                        await Delay(400, ct);
                        if (!await AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 2, ct))
                        {
                            Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                            await Delay(300, ct);
                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                            avatar.LastSkillTime = DateTime.UtcNow;
                        }
                        else
                        {
                            hurryOnLogo = true;
                        }
                    }
                }
                
                //接近战斗点，确保行走位不是丝血
                if (waypoint?.Action == ActionEnum.Fight.Code && distance < 30 && _combatScenes?.GetAvatars().Count > 1)
                {
                    using (var bitmap = CaptureToRectArea())
                    {
                        var pixel = 0;

                        for (int i = 0; i < 2; i++)
                        {
                            using (var bitmap2 = CaptureToRectArea())
                            {
                                var pixelValue = bitmap2.SrcMat.At<Vec3b>(1010,814);
                                if (!(Math.Abs(pixelValue[0] - 34) <= 10 &&
                                      Math.Abs(pixelValue[1] - 215) <= 10 &&
                                      Math.Abs(pixelValue[2] - 150) <= 10) && !(Math.Abs(pixelValue[0] - 50) <= 10 &&
                                                                                Math.Abs(pixelValue[1] - 204) <= 10 &&
                                                                                Math.Abs(pixelValue[2] - 255) <= 10))
                                {
                                    pixel += 1;
                                }
                                else
                                {
                                    pixel = 0;
                                }
                            }
                            await Task.Delay(50, ct);
                        }
                    
                        if (pixel >= 2)
                        {
                            if (distance < 10)
                            {
                                // 抬起w键
                                Logger.LogInformation("到达战斗点附近-2");
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                                return;
                            }
                            
                            Logger.LogInformation("当前行走角色血量仍过低，尝试切换人-2");

                            if (!string.IsNullOrWhiteSpace(PartyConfig.MainAvatarIndex))
                            {
                                var avatarIndex = int.Parse(PartyConfig.MainAvatarIndex);
                                
                                var nextAvatarIndex = (avatarIndex % 4) + 1;
                                if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                                    _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                                {
                                    nextAvatarIndex = (nextAvatarIndex % 4) + 1;
                                }
                                
                                var avatar2 = _combatScenes?.SelectAvatar(avatarIndex);

                                await Delay(300, ct);

                                if (avatar2 != null && avatar2.IsActive(bitmap))
                                {
                                    PartyConfig.MainAvatarIndex = nextAvatarIndex.ToString();
                                    await SwitchAvatar(nextAvatarIndex.ToString());
                                }
                                else
                                {
                                    await SwitchAvatar(PartyConfig.MainAvatarIndex);
                                }
                            }
                            else
                            {
                                for (int i = 1; i <= 4; i++)
                                {
                                    var avatar2 = _combatScenes?.SelectAvatar(i);
                                    if (avatar2 != null && avatar2.IsActive(bitmap))
                                    {
                                        var nextAvatarIndex = (i % 4) + 1;
                                        if (_combatScenes?.SelectAvatar(nextAvatarIndex).Name == "枫原万叶" && 
                                            _combatScenes?.SelectAvatar(PathingConditionConfig.InitialMainAvatarIndex)?.Name != "枫原万叶")
                                        {
                                            nextAvatarIndex = (nextAvatarIndex % 4) + 1;
                                        }
                                        await SwitchAvatar(nextAvatarIndex.ToString());
                                        break;
                                    }
                                }
                            }
                        }
                    }
                    
                    //防转圈，卡地形
                    if (distance < 15)
                    {
                        distanceCount ++;
                        if (distanceCount > 10)
                        {
                            Logger.LogWarning("战斗节点靠近超时-1");
                            break;
                        }
                    }                   
                }
            }
            
            if (distance < (hurryOnLogo? 4 : 6))
            {
                if (hurryOnIn)
                {
                    using (var bitmap2 = CaptureToRectArea())
                    {
                        var pos3 = bitmap2.SrcMat.At<Vec3b>(978, 1692);
                        var pos4 = bitmap2.SrcMat.At<Vec3b>(995, 1702);
                        var colorDifference2 = Math.Sqrt(
                            Math.Pow(pos3.Item0 - pos4.Item0, 2) + // 蓝通道差值的平方
                            Math.Pow(pos3.Item1 - pos4.Item1, 2) + // 绿通道差值的平方
                            Math.Pow(pos3.Item2 - pos4.Item2, 2)   // 红通道差值的平方
                        );
                
                        if (colorDifference2 < 15)
                        {
                            Logger.LogWarning("到达路径点附近-9");
                            await SwitchAvatar(nextAvatarIndexStop); 
                        
                        } 
                    }
                   
                }
                Logger.LogDebug("到达路径点附近");
                break;
            }

            if (distance > 500)
            {
                if (pathExecutorSuspend.CheckAndResetSuspendPoint())
                {
                    throw new RetryNoCountException("可能暂停导致路径过远，重试一次此路线！");
                }
                else
                {
                    distanceTooFarRetryCount++;
                    if (distanceTooFarRetryCount > 50)
                    {
                        if (position == new Point2f())
                        {
                            throw new HandledException("重试多次后，当前点位无法被识别，放弃此路径！");
                        }
                        else
                        {
                            Logger.LogWarning($"距离过远（{position.X},{position.Y}）->（{waypoint.X},{waypoint.Y}）={distance}，重试多次后仍然失败，放弃此路径点！");
                            throw new HandledException("目标距离过远，可能是当前点位无法识别，放弃此路径！");
                        }
                    }
                    else
                    {
                        // 取余减少日志输出频率
                        if (distanceTooFarRetryCount % 5 == 0)
                        {
                            Logger.LogWarning($"距离过远（{position.X},{position.Y}）->（{waypoint.X},{waypoint.Y}）={distance}，重试");
                        }
                        // 取余减少判断频率
                        if (distanceTooFarRetryCount % 10 == 0)
                        {
                            await ResolveAnomalies(screen);
                            Logger.LogInformation($"重置到上次正确识别的坐标 ({prevNotTooFarPosition.X},{prevNotTooFarPosition.Y})");
                            Navigation.SetPrevPosition(prevNotTooFarPosition.X, prevNotTooFarPosition.Y);
                            // 淡入淡出特效
                            await Delay(500, ct);
                        }
                        await Delay(50, ct);
                        continue;
                    }
                }
            } else
            {
                prevNotTooFarPosition = position;
            }

            // 非攀爬状态下，检测是否卡死（脱困触发器）
            if (waypoint?.MoveMode != MoveModeEnum.Climb.Code && isGetOut)
            {
                if ((DateTime.UtcNow - lastPositionRecord).TotalMilliseconds > 1000 + additionalTimeInMs)
                {
                    lastPositionRecord = DateTime.UtcNow;
                    prevPositions.Add(position);
                    if (prevPositions.Count > 8)
                    {
                        var delta = prevPositions[^1] - prevPositions[^8];
                        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 3)
                        {
                            //停止吃药
                            var autoEatCount = PathingConditionConfig.AutoEatCount;
                            var recoverCount =  AutoFightTask.RecoverCount;
                            PathingConditionConfig.AutoEatCount = 3;
                            AutoFightTask.RecoverCount = 3;
                            
                            if (_inTrap > 0)
                            {
                                throw new RetryException("此路线出现3次卡死，重试一次路线或放弃此路线！");
                            }
                            
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                            Simulation.SendInput.SimulateAction(GIActions.Drop);
                            await Delay(1000, ct);
                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            Simulation.SendInput.SimulateAction(GIActions.Jump);
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                            
                            if (_lastWaypoint is not null && _inTrap == 0 && !_faceToMark)
                            {
                                _faceToMark = true;
                                Logger.LogWarning("尝试朝向上一个节点...");
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                                Simulation.SendInput.SimulateAction(GIActions.Drop);
                                await Delay(500, ct);
                                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                Simulation.SendInput.SimulateAction(GIActions.Jump);
                                
                                await FaceTo(_lastWaypoint);
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                                await Delay(1500, ct);
                                await FaceTo(waypoint);
                                await Delay(500, ct);
                                Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                                Simulation.SendInput.SimulateAction(GIActions.Drop);
                                Simulation.SendInput.SimulateAction(GIActions.Jump);
                                Logger.LogInformation("尝试继续行走...");
                                
                                PathingConditionConfig.AutoEatCount = autoEatCount;
                                AutoFightTask.RecoverCount = recoverCount;
                                continue;
                            }
                            
                            _inTrap++;
                            Logger.LogWarning("疑似卡死，尝试随机脱离...");
                            //调用脱困代码，由TrapEscaper接管移动
                            await _trapEscaper.RotateAndMove();
                            await _trapEscaper.MoveTo(waypoint);
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                            Logger.LogInformation("卡死脱离结束");

                            PathingConditionConfig.AutoEatCount = autoEatCount;
                            AutoFightTask.RecoverCount = recoverCount;
                            continue;
                        }
                    }
                }
            }

            // 旋转视角
            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            //执行旋转
            var diff = _rotateTask.RotateToApproach(targetOrientation, screen2);
            if (num > 20)
            {
                if (Math.Abs(diff) > 5)
                {
                    consecutiveRotationCountBeyondAngle++;
                }
                else
                {
                    consecutiveRotationCountBeyondAngle = 0;
                }

                if (consecutiveRotationCountBeyondAngle > 10)
                {
                    // 直接站定好转向
                    await WaitUntilRotatedTo(targetOrientation, 2);
                }
            }

            // 根据指定方式进行移动
            if (waypoint.MoveMode == MoveModeEnum.Fly.Code)
            {
                var isFlying = Bv.GetMotionStatus(screen2) == MotionStatus.Fly;
                if (!isFlying)
                {
                    Debug.WriteLine("未进入飞行状态，按下空格");
                    Simulation.SendInput.SimulateAction(GIActions.Jump);
                    await Delay(200, ct);
                }

                await Delay(100, ct);
                continue;
            }

            if (waypoint.MoveMode == MoveModeEnum.Jump.Code)
            {
                Simulation.SendInput.SimulateAction(GIActions.Jump);
                await Delay(200, ct);
                continue;
            }

            // 只有设置为run才会一直疾跑
            if (waypoint.MoveMode == MoveModeEnum.Run.Code)
            {
                if (distance > ((waypoint.Action == ActionEnum.Fight.Code ? 5 :20))!= fastMode) // 距离大于20时可以使用疾跑/自由泳
                {
                    if (fastMode)
                    {
                        Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
                    }
                    else
                    {
                        if (sprintMouseLogo)
                        {
                            // Logger.LogInformation("333");
                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                        }
                    }

                    fastMode = !fastMode;
                }
            }
            else if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
            {
                if (distance > (waypoint.Action == ActionEnum.Fight.Code ? 5 : (!hurryOnLogo ? 35 : 20))) // 距离大于25时可以使用疾跑
                {
                    if (Math.Abs((fastModeColdTime - DateTime.UtcNow).TotalMilliseconds) > 1000) //冷却一会
                    {
                        fastModeColdTime = DateTime.UtcNow;
                        if (!hurryOnLogo && dushed)
                        {
                            dushed = false;
                            Task.Run(async () =>
                            {
                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                                await Delay(200, ct);
                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
                                dushed = true;
                            }, ct);
                        }
                        else
                        {
                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                        }
                    }
                }
            }
            else if (waypoint.MoveMode != MoveModeEnum.Climb.Code) //否则自动短疾跑
            {
                // 使用 E 技能
                if (distance > 10 && !string.IsNullOrEmpty(PartyConfig.GuardianAvatarIndex) &&
                    double.TryParse(PartyConfig.GuardianElementalSkillSecondInterval, out var s))
                {
                    if (s < 1)
                    {
                        Logger.LogWarning("元素战技冷却时间设置太短，不执行！");
                        return;
                    }

                    var ms = s * 1000;
                    if ((DateTime.UtcNow - _elementalSkillLastUseTime).TotalMilliseconds > ms)
                    {
                        // 可能刚切过人在冷却时间内
                        if (num <= 5 && (!string.IsNullOrEmpty(PartyConfig.MainAvatarIndex) &&
                                         PartyConfig.GuardianAvatarIndex != PartyConfig.MainAvatarIndex))
                        {
                            await Delay(800, ct); // 总共1s
                        }

                        await UseElementalSkill();
                        _elementalSkillLastUseTime = DateTime.UtcNow;
                    }
                }

                // 自动疾跑
                if (distance > 20 && PartyConfig.AutoRunEnabled)
                {
                    // var dashTime = nextDistance > 90 ? 3500 : 2500;
                    if (Math.Abs((fastModeColdTime - DateTime.UtcNow).TotalMilliseconds) > 2500) //冷却时间2.5s，回复体力用
                    {
                        fastModeColdTime = DateTime.UtcNow;
                        Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                    }
                }
            }

            // 使用小道具
            if (PartyConfig.UseGadgetIntervalMs > 0)
            {
                if ((DateTime.UtcNow - _useGadgetLastUseTime).TotalMilliseconds > PartyConfig.UseGadgetIntervalMs)
                {
                    Simulation.ReleaseAllKey();
                    Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                    _useGadgetLastUseTime = DateTime.UtcNow;
                }
            }

            await Delay(80, ct);
            
        }
        
        // 抬起w键
        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
    }

    private async Task UseElementalSkill()
    {
        if (string.IsNullOrEmpty(PartyConfig.GuardianAvatarIndex))
        {
            return;
        }

        await Delay(200, ct);

        // 切人
        Logger.LogInformation("切换盾、回血角色，使用元素战技");
        var avatar = await SwitchAvatar(PartyConfig.GuardianAvatarIndex, true);
        if (avatar == null)
        {
            return;
        }

        // 钟离往身后放柱子
        if (avatar.Name == "钟离")
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
            await Delay(50, ct);
            Simulation.SendInput.SimulateAction(GIActions.MoveBackward);
            await Delay(200, ct);
        }

        avatar.UseSkill(PartyConfig.GuardianElementalSkillLongPress);

        // 钟离往身后放柱子 后继续走路
        if (avatar.Name == "钟离")
        {
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
        }
    }

    private async Task MoveCloseTo(WaypointForTrack waypoint)
    {
        ImageRegion screen;
        Point2f position;
        int targetOrientation;
        Logger.LogDebug("精确接近目标点，位置({x2},{y2})", $"{waypoint.GameX:F1}", $"{waypoint.GameY:F1}");

        var stepsTaken = 0;
        while (!ct.IsCancellationRequested)
        {
            stepsTaken++;
            if (stepsTaken > 25)
            {
                Logger.LogWarning("精确接近超时");
                break;
            }

            screen = CaptureToRectArea();

            EndJudgment(screen);

            position = await GetPosition(screen, waypoint);
            if (Navigation.GetDistance(waypoint, position) < 2)
            {
                Logger.LogDebug("已到达路径点");
                break;
            }

            targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            await WaitUntilRotatedTo(targetOrientation, 2);
            // 小碎步接近
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
            Thread.Sleep(60);
            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
            // Simulation.SendInput.Keyboard.KeyDown(User32.VK.VK_W).Sleep(60).KeyUp(User32.VK.VK_W);
            await Delay(20, ct);
        }

        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);

        // 到达目的地后停顿一秒
        await Delay(string.IsNullOrEmpty(_hurryOnAvatar)?1000:300, ct);
      
    }

    private async Task BeforeMoveCloseToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.MoveMode == MoveModeEnum.Fly.Code && waypoint.Action == ActionEnum.StopFlying.Code)
        {
            await ActionFactory.GetBeforeHandler(ActionEnum.StopFlying.Code).RunAsync(ct, waypoint);
        }
    }

    private async Task BeforeMoveToTarget(WaypointForTrack waypoint)
    {
        if (waypoint.Action == ActionEnum.UpDownGrabLeaf.Code)
        {
            Simulation.SendInput.Mouse.MiddleButtonClick();
            await Delay(300, ct);
            var screen = CaptureToRectArea();
            var position = await GetPosition(screen, waypoint);
            var targetOrientation = Navigation.GetTargetOrientation(waypoint, position);
            await WaitUntilRotatedTo(targetOrientation, 10);
            var handler = ActionFactory.GetBeforeHandler(waypoint.Action);
            await handler.RunAsync(ct, waypoint);
        }
        else if (waypoint.Action == ActionEnum.LogOutput.Code)
        {
            Logger.LogInformation(waypoint.LogInfo);
        }
    }

    private async Task AfterMoveToTarget(WaypointForTrack waypoint, Waypoint? nextWaypoint = null)
    {
        if (waypoint.Action == ActionEnum.NahidaCollect.Code
            || waypoint.Action == ActionEnum.PickAround.Code
            || waypoint.Action == ActionEnum.Fight.Code
            || waypoint.Action == ActionEnum.HydroCollect.Code
            || waypoint.Action == ActionEnum.ElectroCollect.Code
            || waypoint.Action == ActionEnum.AnemoCollect.Code
            || waypoint.Action == ActionEnum.PyroCollect.Code
            || waypoint.Action == ActionEnum.CombatScript.Code
            || waypoint.Action == ActionEnum.Mining.Code
            || waypoint.Action == ActionEnum.Fishing.Code
            || waypoint.Action == ActionEnum.ExitAndRelogin.Code
            || waypoint.Action == ActionEnum.EnterAndExitWonderland.Code
            || waypoint.Action == ActionEnum.SetTime.Code
            || waypoint.Action == ActionEnum.UseGadget.Code
            || waypoint.Action == ActionEnum.PickUpCollect.Code)
        {
            var handler = ActionFactory.GetAfterHandler(waypoint.Action);
            await handler.RunAsync(ct, waypoint, PartyConfig);
            
            //统计结束战斗的次数
            if (waypoint.Action == ActionEnum.Fight.Code)
            {
                SuccessFight++;
            }

            if (PartyConfig.QuicklySkip && (_lastWaypoint?.Action == ActionEnum.Fight.Code || waypoint.Action == ActionEnum.Fight.Code || nextWaypoint?.Action == ActionEnum.Fight.Code))
            {
                if (nextWaypoint?.Type != WaypointType.Teleport.Code)
                {
                    // Logger.LogWarning("6611");
                    return;
                }
                
                await Delay(100, ct);
                // Logger.LogWarning("9911");
                return;
            }
            
            await Delay(900, ct);
        }
    }

    private async Task<Avatar?> SwitchAvatar(string index, bool needSkill = false , PathingTask? pathingTask = null, bool? forceRefresh = false)
    {
        if (string.IsNullOrEmpty(index) && !(int.TryParse(index, out var idx) && _combatScenes?.GetAvatars().Count <= idx))
        {
            return null;
        }

        var avatar = _combatScenes?.SelectAvatar(int.Parse(index));
        if (avatar == null) return null;
        if (needSkill && !avatar.IsSkillReady())
        {
            Logger.LogInformation("角色{Name}技能未冷却，跳过。", avatar.Name);
            return null;
        }
        
        var success = avatar.TrySwitch(15);
        if (success)
        {
            await Delay(100, ct);
            return avatar;
        }

        using (var bitmap = CaptureToRectArea())
        {
            var confirmRectArea = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
            if (!confirmRectArea.IsEmpty())
            {
                Simulation.ReleaseAllKey();
                if(PathingConditionConfig.AutoEatCount <2)PathingConditionConfig.AutoEatCount ++;
                Logger.LogInformation("死亡，点击确认-s1 {t}",PathingConditionConfig.AutoEatCount);
                confirmRectArea.Click();
                confirmRectArea.ClickTo(-100, 0);
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
            }
            
            var pixelValue = bitmap.SrcMat.At<Vec3b>(1010,814);
            // var pixelValue2 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(1).IndexRect).SrcMat.At<Vec3b>(1, 1);
            // var pixelValue22 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(2).IndexRect).SrcMat.At<Vec3b>(1, 1);
            if (pathingTask is not null && forceRefresh == true && !(Math.Abs(pixelValue[0] - 50) <= 10 &&
                                                                    Math.Abs(pixelValue[1] - 204) <= 10 &&
                                                                    Math.Abs(pixelValue[2] - 255) <= 10))
            {
                // Logger.LogInformation("切换失败，尝试识别角色-1{t} {t2} {3}",pixelValue[0],pixelValue[1],pixelValue[2]);
                await ValidateGameWithTask(pathingTask,forceRefresh);
            }
        }
        
        Logger.LogInformation("尝试切换角色{Name}失败！ {t}", avatar.Name,forceRefresh);
        return null;
    }
    
    private async Task<Avatar?> SwitchAvatar2(string index, bool needSkill = false , PathingTask? pathingTask = null, bool? forceRefresh = false)
    {
        if (string.IsNullOrEmpty(index) && !(int.TryParse(index, out var idx) && _combatScenes?.GetAvatars().Count <= idx))
        {
            return null;
        }

        var avatar = _combatScenes?.SelectAvatar(int.Parse(index));
        if (avatar == null) return null;
        if (needSkill && !avatar.IsSkillReady())
        {
            Logger.LogInformation("角色{Name}技能未冷却，跳过。", avatar.Name);
            return null;
        }
        
        var success = avatar.TrySwitch2(5);
        if (success)
        {
            await Delay(100, ct);
            return avatar;
        }

        using (var bitmap = CaptureToRectArea())
        {
            var confirmRectArea = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
            if (!confirmRectArea.IsEmpty())
            {
                Simulation.ReleaseAllKey();
                if(PathingConditionConfig.AutoEatCount <2)PathingConditionConfig.AutoEatCount ++;
                Logger.LogInformation("死亡，点击确认-s2 {t}",PathingConditionConfig.AutoEatCount);
                confirmRectArea.Click();
                confirmRectArea.ClickTo(-100, 0);
                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
            }
            
            var pixelValue = bitmap.SrcMat.At<Vec3b>(1010,814);
            // var pixelValue2 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(1).IndexRect).SrcMat.At<Vec3b>(1, 1);
            // var pixelValue22 = bitmap.DeriveCrop(_combatScenes.SelectAvatar(2).IndexRect).SrcMat.At<Vec3b>(1, 1);
            if (pathingTask is not null && forceRefresh == true && !(Math.Abs(pixelValue[0] - 50) <= 10 &&
                                                                    Math.Abs(pixelValue[1] - 204) <= 10 &&
                                                                    Math.Abs(pixelValue[2] - 255) <= 10))
            {
                // Logger.LogInformation("切换失败，尝试识别角色-1{t} {t2} {3}",pixelValue[0],pixelValue[1],pixelValue[2]);
                await ValidateGameWithTask(pathingTask,forceRefresh);
            }
        }
        
        Logger.LogInformation("尝试切换角色{Name}失败！ {t}", avatar.Name,forceRefresh);
        return null;
    }
    
    /// <summary>
    /// 根据时间在两个点之间插值。
    /// </summary>
    /// <param name="startPoint">起点坐标</param>
    /// <param name="endPoint">终点坐标</param>
    /// <param name="startTime">起始时间</param>
    /// <param name="midTime">中间时间</param>
    /// <param name="endTime">结束时间</param>
    /// <returns>中间点坐标</returns>
    public static Point2f InterpolatePointByTime(
        Point2f startPoint,
        Point2f endPoint,
        DateTime startTime,
        DateTime midTime,
        DateTime endTime)
    {
        // 计算时间差
        double totalMillis = (endTime - startTime).TotalMilliseconds;
        double midMillis = (midTime - startTime).TotalMilliseconds;

        // 防止除以0
        if (totalMillis == 0)
            return startPoint;

        // 计算比例
        float t = (float)(midMillis / totalMillis);
        if (t>1.0f)
        {
            t = 1.0f;
        }
        // 插值计算
        float x = startPoint.X + (endPoint.X - startPoint.X) * t;
        float y = startPoint.Y + (endPoint.Y - startPoint.Y) * t;

        return new Point2f(x, y);
    }
    
    private  Point2f prePosition;
    private  DateTime preTime;
    //自动构造点位的最大时间
    private int maxAutoPositionTime=10000; 
    private async Task WaitForCloseMap(int maxAttempts, int delayMs)
    {
        await Delay(delayMs, ct);
        for (var i = 0; i < maxAttempts; i++)
        {
            using var capture = CaptureToRectArea();
            if (Bv.IsInMainUi(capture))
            {
                return;
            }

            await Delay(delayMs, ct);
        }
        
    }

    private async Task<Point2f> GetPosition(ImageRegion imageRegion, WaypointForTrack waypoint)
    {
        return (await GetPositionAndTime(imageRegion, waypoint)).point;
    }
    //
    public bool GetPositionAndTimeSuspendFlag = false;
    private async Task<(Point2f point,int additionalTimeInMs)> GetPositionAndTime(ImageRegion imageRegion, WaypointForTrack waypoint)
    {
        var position = Navigation.GetPosition(imageRegion, waypoint.MapName, waypoint.MapMatchMethod);
        int time = 0;
        if (position == new Point2f())
        {
            if (!Bv.IsInMainUi(imageRegion))
            {
                Logger.LogDebug("小地图位置定位失败，且当前不是主界面，进入异常处理");
                await ResolveAnomalies(imageRegion);
            }
        }

        var distance = Navigation.GetDistance(waypoint, position);
        //中途暂停过，地图未识别到
        if (position is {X:0,Y:0} && GetPositionAndTimeSuspendFlag)
        {
            GetPositionAndTimeSuspendFlag = false;
            throw new RetryNoCountException("可能暂停导致路径过远，重试一次此路线！");
        }
        //何时处理   pathTooFar  路径过远  unrecognized 未识别
        if ((position is {X:0,Y:0} && waypoint.Misidentification.Type.Contains("unrecognized")) || (distance>500 && waypoint.Misidentification.Type.Contains("pathTooFar")))
        {
            if (waypoint.Misidentification.HandlingMode == "previousDetectedPoint")
            {
                if (prePosition != default)
                {
                    position = prePosition;
                    Logger.LogInformation(@$"未识别到具体路径，取上次点位");
                }
            }else if (waypoint.Misidentification.HandlingMode == "mapRecognition"){
                //大地图识别坐标
                DateTime start = DateTime.UtcNow;
                TpTask tpTask = new TpTask(ct);
                await tpTask.OpenBigMapUi();
                try
                {
                    position =MapManager.GetMap(waypoint.MapName, waypoint.MapMatchMethod).ConvertGenshinMapCoordinatesToImageCoordinates(tpTask.GetPositionFromBigMap(waypoint.MapName));
                }
                catch (Exception e)
                {
                    Logger.LogInformation(@$"地图中心点识别失败！");
                }
               
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                //Bv.IsInMainUi(imageRegion);
                await WaitForCloseMap(10,200);
                DateTime end = DateTime.UtcNow;
                time=(int)(end - start).TotalMilliseconds;
                Logger.LogInformation(@$"未识别到具体路径，打开地图计算中心点({position.X},{position.Y})");
            }
            
            /*if (prePosition!=default)
            {*/
                //position = InterpolatePointByTime(prePosition,new Point2f((float)waypoint.GameX,(float)waypoint.GameY),preTime,DateTime.Now,preTime.AddMilliseconds(maxAutoPositionTime));
                //Logger.LogInformation(@$"未识别到具体路径，预测其路径为（{position.X},{position.Y}）,开始结束点位为：（{prePosition.X},{prePosition.Y}）（{waypoint.GameX},{waypoint.GameY}）");
                //Point2f GetBigMapCenterPoint(string mapName)

               // Logger.LogInformation(@$"未识别到具体路径，打开地图计算中心点({position.X},{position.Y})");
                //position =prePosition;
           // }

        }
        else
        {
            prePosition = position;
            preTime = DateTime.UtcNow;
        }

        //Logger.LogDebug("识别到路径："+position.X+","+position.Y);
        return (position,time);
    }

    private async Task WaitUntilRotatedTo(int targetOrientation, int maxDiff)
    {
        if (await _rotateTask.WaitUntilRotatedTo(targetOrientation, maxDiff))
        {
            return;
        }
        await ResolveAnomalies();
        await _rotateTask.WaitUntilRotatedTo(targetOrientation, maxDiff);
    }

    /**
     * 处理各种异常场景
     * 需要保证耗时不能太高
     */
    private async Task ResolveAnomalies(ImageRegion? imageRegion = null)
    {
        if (imageRegion == null)
        {
            imageRegion = CaptureToRectArea();
        }

        // 一些异常界面处理
        var cookRa = imageRegion.Find(AutoSkipAssets.Instance.CookRo);
        var closeRa = imageRegion.Find(AutoSkipAssets.Instance.PageCloseMainRo);
        var closeRa2 = imageRegion.Find(ElementAssets.Instance.PageCloseWhiteRo);
        var closeRa3 = imageRegion.Find(AutoSkipAssets.Instance.PageCloseRo);
        if (cookRa.IsExist() || closeRa.IsExist() || closeRa2.IsExist() || closeRa3.IsExist())
        {
            // 排除大地图
            if (Bv.IsInBigMapUi(imageRegion))
            {
                return;
            }

            Logger.LogInformation("检测到其他界面，使用ESC关闭界面");
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
            await Delay(1000, ct); // 等待界面关闭
        }


        // 处理月卡
        await _blessingOfTheWelkinMoonTask.Start(ct);

        if (PartyConfig.AutoSkipEnabled)
        {
            // 判断是否进入剧情
            await AutoSkip();
        }
    }

    private async Task AutoSkip()
    {
        var ra = CaptureToRectArea();
        var disabledUiButtonRa = ra.Find(AutoSkipAssets.Instance.DisabledUiButtonRo);
        if (disabledUiButtonRa.IsExist())
        {
            Logger.LogWarning("进入剧情，自动点击剧情直到结束");

            if (_autoSkipTrigger == null)
            {
                _autoSkipTrigger = new AutoSkipTrigger(new AutoSkipConfig
                {
                    Enabled = true,
                    QuicklySkipConversationsEnabled = true, // 快速点击过剧情
                    ClosePopupPagedEnabled = true,
                    ClickChatOption = "优先选择最后一个选项",
                });
                _autoSkipTrigger.Init();
            }

            int noDisabledUiButtonTimes = 0;

            while (true)
            {
                ra = CaptureToRectArea();
                disabledUiButtonRa = ra.Find(AutoSkipAssets.Instance.DisabledUiButtonRo);
                if (disabledUiButtonRa.IsExist())
                {
                    _autoSkipTrigger.OnCapture(new CaptureContent(ra));
                    noDisabledUiButtonTimes = 0;
                }
                else
                {
                    noDisabledUiButtonTimes++;
                    if (noDisabledUiButtonTimes > 10)
                    {
                        Logger.LogInformation("自动剧情结束");
                        break;
                    }
                }

                await Delay(210, ct);
            }
        }
    }

    private void EndJudgment(ImageRegion ra)
    {
        if (EndAction != null && EndAction(ra))
        {
            throw new HandledException("达成结束条件，结束地图追踪");
        }
    }
}