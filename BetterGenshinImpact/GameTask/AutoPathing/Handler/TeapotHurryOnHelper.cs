using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using ActionEnum = BetterGenshinImpact.GameTask.AutoPathing.Model.Enum.ActionEnum;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 茶包版赶路逻辑（UseNewHurrySystem == false 时启用）。
/// 原内联在 PathExecutor.MoveTo 的 else 分支（约 600 行），逐字节抽离至此，行为不变。
/// 公版赶路（SkillBoostHelper.cs / TryHurryOnAsync）与本文件平行、互不影响。
/// </summary>
public partial class PathExecutor
{
    /// <summary>
    /// 茶包版赶路跨帧状态。原为 MoveTo 的局部变量，抽离后需跨帧保持 + 供 fire-and-forget 后台任务回写，
    /// 故收进本对象（由 MoveTo 每次进入时 new 一个，与公版 HurryOnState 平行独立）。
    /// </summary>
    private class TeapotHurryOnState
    {
        public bool HurryOnLogo = true;
        public bool HurryOnIn = false;
        public bool SprintMouseLogo = true;
        public bool TrackingLogo = true;
        public int NextistanceCount = 0;
        public int MavikaFlyCount = 0;
        public int RunCount = 0;
        public int ContinueHurryOn = 0;
        public int IsClimbLogo = 0;
        public bool IsFlyingMwk = false;
        public bool Aa = false;
        public bool Relifed = false;
        public bool Mwktiao = true;
        public bool MwktiaoIn = false;
        public bool IsFlyingIn = false;
        public DateTime LastElementalSkillTime = DateTime.MinValue;
    }

    /// <summary>
    /// 执行茶包版赶路一帧逻辑。返回 true 表示 MoveTo 主循环应 break（原 else 块内的"赶路靠近超时-2"）。
    /// position / additionalTimeInMs 按值传入：块内 fire-and-forget 后台任务对它们（及 distance）的写为死写，
    /// 方法返回后即丢弃，与选项1约定一致，对赶路行为无影响。
    /// </summary>
    private async Task<bool> ExecuteTeapotHurryOnAsync(
        WaypointForTrack waypoint,
        Waypoint? nextWaypoint,
        double? nextDistance,
        double distance,
        Point2f position,
        int additionalTimeInMs,
        Avatar avatar,
        ImageRegion screen2,
        int num,
        bool isPoint,
        bool yellowBlood,
        bool skipOldHurry,
        bool? hurryOnBool,
        string nextAvatarIndexStop,
        TeapotHurryOnState st)
    {
                // 以下为旧赶路系统逻辑（仅在 UseNewHurrySystem=false 时执行）
                // 自动赶路的靠近节点模式
                if (!st.HurryOnLogo && st.TrackingLogo && 
                    (PartyConfig.TravelMode == "精准靠近" && distance < (!string.IsNullOrEmpty(nextWaypoint?.Action) ? 30 : avatar.Name == "瓦雷莎" ? 30 : 25) //精准靠近
                     || (PartyConfig.TravelMode == "连续赶路" && distance < 40 && (nextDistance < 25 || nextWaypoint?.Type == WaypointType.Target.Code || waypoint.Type == WaypointType.Target.Code 
                                                                               || nextWaypoint?.Action == MoveModeEnum.Fly.Code || waypoint?.Action == ActionEnum.CombatScript.Code
                                                                               ||(nextDistance < 25 && nextWaypoint?.Action == ActionEnum.CombatScript.Code))))) //连续赶路
                {
                    st.TrackingLogo = false;
                    if (avatar.IsActive(screen2))
                    {
                        if (avatar.Name == "玛薇卡")
                        {
                            if (IsMavuikaOnMotorcycleByTemplate(screen2))
                            {
                                st.HurryOnIn = true;
                                if (Bv.GetMotionStatus(screen2) != MotionStatus.Fly || !(screen2.SrcMat.At<Vec3b>(1028, 1584).Item0 == 255 && screen2.SrcMat.At<Vec3b>(1028, 1584).Item1 == 255 && screen2.SrcMat.At<Vec3b>(1028, 1584).Item2 == 255)
                                    || nextWaypoint?.Action != MoveModeEnum.Fly.Code || waypoint?.Action != MoveModeEnum.Fly.Code)
                                {
                                    Logger.LogInformation("自动赶路：{t} 节点接近...-i {t2} {t3} {t4}", PartyConfig.TravelMode, nextAvatarIndexStop, waypoint?.MoveMode, "onMoto");
                                    
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
                                               st.Relifed = true;
                                           }
                                           else
                                           {
                                               st.Relifed = false;
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

                    st.HurryOnIn = false;
                    if ((nextDistance < 25 || distance < 20) && waypoint?.MoveMode != MoveModeEnum.Climb.Code)
                    {
                        st.NextistanceCount ++;
                        if (st.NextistanceCount > 3)
                        {
                            Logger.LogWarning("赶路靠近超时-2");
                            return true;
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
                        if (isClimb && !st.HurryOnLogo&& st.IsClimbLogo<2 && waypoint.MoveMode != MoveModeEnum.Climb.Code)
                        {
                            await Delay(1000, ct);
                            Simulation.SendInput.SimulateAction(GIActions.Drop);
                            await Delay(500, ct);
                            st.IsClimbLogo ++ ;
                        }
                    }
                } 

                // 自动赶路的特殊处理模式，防止异常情况
                if (!st.HurryOnLogo || st.MwktiaoIn)
                {
                    // if(mwktiaoIn) Logger.LogWarning("444444");
                    if (avatar.Name == "玛薇卡") //玛薇卡冲坡判断
                    {
                        var isOnMoto = IsMavuikaOnMotorcycleByTemplate(screen2);
                        
                        if ((isOnMoto && !st.IsFlyingMwk) || st.MwktiaoIn)
                        {
                        
                
                            
                            if (screen2.SrcMat.At<Vec3b>(1028, 1584).Item0 == 255 && screen2.SrcMat.At<Vec3b>(1028, 1584).Item1 == 255 && screen2.SrcMat.At<Vec3b>(1028, 1584).Item2 == 255 || isOnMoto)
                            {
                                st.MavikaFlyCount++;
                                
                                if (st.MavikaFlyCount > (st.MwktiaoIn?15:4) && avatar.IsActive(screen2))
                                {
                                    if (nextWaypoint?.MoveMode != MoveModeEnum.Fly.Code &&
                                        Bv.GetMotionStatus(screen2) == MotionStatus.Fly && _lastWaypoint?.MoveMode != MoveModeEnum.Fly.Code && waypoint?.ActionParams is null)
                                    {
                                        Logger.LogWarning("测试:st.MavikaFlyCount1 {t}",st.MavikaFlyCount);
                                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    }

                                    if (st.MwktiaoIn && isOnMoto)
                                    {
                                        Logger.LogWarning("测试:st.MavikaFlyCount2 {t}",st.MavikaFlyCount);
                                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    }
                                    st.MavikaFlyCount = 0;
                                    st.MwktiaoIn = false;
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
                            st.MavikaFlyCount++;
                            if (st.MavikaFlyCount > 2 && avatar.IsActive(screen2))
                            {
                                st.HurryOnLogo = true;
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
                                    st.MavikaFlyCount = 0;
                                }, ct);
                            }
                        }
                    }
                }
                
                //自动赶路
                if (!skipOldHurry && st.HurryOnLogo&& !yellowBlood && !string.IsNullOrEmpty(_hurryOnAvatar) &&
                    distance >  (PartyConfig.Distance) && (hurryOnBool ?? false))
                {
                    //判断是否在飞行状态
                    var notflying = Bv.GetMotionStatus(screen2) != MotionStatus.Fly;
                    if (notflying)
                    {
                        await SwitchAvatar(avatar.Index.ToString());    
                    }
                    
                    if (avatar.Name == "瓦雷莎")
                    {
                        waypoint.MoveMode = MoveModeEnum.Run.Code;
                        st.SprintMouseLogo = false;
                    }

                    if(_mwkFlyJumpDistance>0 && distance < _mwkFlyJumpDistance)st.HurryOnLogo = false; 
              
                    if(num % 5 == 1)Logger.LogInformation("自动赶路：{t} 赶路...{t2}",avatar.Name,Math.Round(distance));

                    if (avatar.Name == "玛薇卡") //连续点按E类型
                    {
                        var isOnMoto = IsMavuikaOnMotorcycleByTemplate(screen2);

                        if (!isOnMoto || st.MwktiaoIn || isOnMoto)
                        {
                            Task.Run(async () =>
                            {
                                if (!isOnMoto)
                                {
                                    if ((DateTime.UtcNow - st.LastElementalSkillTime).TotalMilliseconds > 600  && notflying)
                                    {
                                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                        await Delay(200, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                        await Delay(300, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                        await Delay(100, ct);
                                    }
                                }

                                if (notflying)
                                {
                                    var isOnMoto2 = await AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 2, ct);
                                    if (isOnMoto2)
                                    {
                                        st.LastElementalSkillTime = DateTime.UtcNow;
                                    }
                                }
                                else
                                {
                                    await Delay(20, ct);
                                }
                                
                                await Delay(480, ct);
                                
                                using var region3 = CaptureToRectArea();
                                if (waypoint?.MoveMode == MoveModeEnum.Fly.Code && _MwkFly)
                                {
                                    var pos33 = region3.SrcMat.At<Vec3b>(1028, 1584);
                                    st.IsFlyingMwk = (pos33.Item0 == 255 && pos33.Item1 == 255 && pos33.Item2 == 255);
                                    
                                    if (!st.Aa && st.IsFlyingMwk)
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
                                            waypoint.ActionParams = "1000";
                                        }
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        st.Aa = true;
                                    }
                                }
                                else
                                {
                                    st.IsFlyingMwk = false;
                                }

                                if (!IsMavuikaOnMotorcycleByTemplate(region3) || st.IsFlyingMwk)
                                {
                                    st.ContinueHurryOn++;
                                    
                                    var cd = !AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 1, ct).Result;
                                     if (st.ContinueHurryOn > 0 && cd && (waypoint.MoveMode != MoveModeEnum.Fly.Code && !st.IsFlyingMwk))//?????
                                    {
                                        Logger.LogInformation("自动赶路：继续...");
                                        var isF = Bv.GetMotionStatus(region3) == MotionStatus.Fly;
                                        if (isF && (DateTime.UtcNow - st.LastElementalSkillTime).TotalMilliseconds > 1000 )
                                        {
                                            Logger.LogInformation("自动赶路：普攻...");
                                            st.LastElementalSkillTime = DateTime.UtcNow;
                                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);  
                                            
                                        }
                                        st.HurryOnLogo = true;
                                        st.ContinueHurryOn = 0;
                                    }
                                    
                                    var isClimb = Bv.GetMotionStatus(region3) == MotionStatus.Climb;
                                    if (isClimb)
                                    {
                                        Simulation.SendInput.SimulateAction(GIActions.Drop);
                                        await Delay(500, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                    }

                                    if (distance > 10)
                                    {
                                        if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
                                        {
                                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                        }
                                        else if (waypoint.MoveMode == MoveModeEnum.Run.Code)
                                        {
                                            st.RunCount++;
                                            if (st.RunCount < 5)
                                            {
                                                Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                            }
                                        }
                                        else if(waypoint.MoveMode == MoveModeEnum.Fly.Code && st.IsFlyingMwk)
                                        {
                                            var flyTime = distance switch
                                            {
                                                > 220 => 5900,
                                                > 200 => 5000,
                                                > 180 => 4500,
                                                > 160 => 4000,
                                                > 140 => 3500,
                                                > 115 => 2400,
                                                > 100 => 2100,
                                                > 80 => 900,
                                                > 70 => 500,
                                                > 60 => 270,
                                                > 55 => 80,
                                                > 50 => 10,
                                                // > 40 => 10, 
                                                _ => 0 
                                            };

                                            Logger.LogInformation("自动赶路：{t} 飞行 {t2} ms 距离 {t3}","玛薇卡", flyTime,Math.Round(distance));
                                            st.IsFlyingIn = true;
                                            if (flyTime > 0)
                                            {
                                                waypoint.MoveMode = MoveModeEnum.Dash.Code;
                                                using var flyDetectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                                                var detectToken = flyDetectCts.Token;
                                                // 后台检测循环：宽限期后每隔 DetectInterval 截图判定，若脱离飞行则取消主等待提前收尾
                                                var detectTask = Task.Run(async () =>
                                                {
                                                    try
                                                    {
                                                        // 宽限期内不检测，避免刚起飞未进入稳定飞行就被误判打断
                                                        await Task.Delay(TakeoffGracePeriod, detectToken);
                                                        while (!detectToken.IsCancellationRequested)
                                                        {
                                                            try
                                                            {
                                                                await Task.Delay(DetectInterval, detectToken);
                                                                using var ra = CaptureToRectArea();
                                                                // 获取两个点的颜色值
                                                                var pos33 = ra.SrcMat.At<Vec3b>(1028, 1584);
                                                                var isMwk = (pos33.Item0 == 255 && pos33.Item1 == 255 && pos33.Item2 == 255);
                                                                if (!isMwk)
                                                                {
                                                                    // Logger.LogWarning("111");
                                                                    flyDetectCts.Cancel();
                                                                    break;
                                                                }
                                                            }
                                                            catch (OperationCanceledException)
                                                            {
                                                                // 主等待结束或外部取消触发的检测间隔等待被取消，正常退出检测循环
                                                                break;
                                                            }
                                                            catch (Exception ex)
                                                            {
                                                                // 截图/识别异常：降级为不提前结束、走满 flyTime，不传播中断赶路
                                                                Logger.LogWarning(ex, "自动赶路：玛薇卡飞行脱离检测截图/识别异常，降级为走满 flyTime");
                                                            }
                                                        }
                                                    }
                                                    catch (OperationCanceledException)
                                                    {
                                                        // 宽限期等待期间主等待已结束或外部取消，正常退出检测任务（此时不应打断，走满 flyTime 逻辑由主等待负责）
                                                    }
                                                }, detectToken);
                                                try
                                                {
                                                    await Delay(flyTime, detectToken);
                                                }
                                                catch (Exception ex) when (ex is (OperationCanceledException or NormalEndException) && !ct.IsCancellationRequested)
                                                {
                                                    // 内部检测提前结束等待，非外部取消，正常继续收尾
                                                    _ = ex;
                                                }
                                                finally
                                                {
                                                    flyDetectCts.Cancel();
                                                    await detectTask;
                                                }
                                            }

                                            Simulation.SendInput.SimulateAction(GIActions.Jump);
                                            waypoint.MoveMode = MoveModeEnum.Fly.Code;
                                            st.HurryOnLogo = false;
                                        }
                                    }
                                }
                                else
                                {
                                    avatar.LastSkillTime = DateTime.UtcNow;
                                }
                                
                                if (!st.IsFlyingIn&&MwkFlyJumpDecisions.ShouldTriggerMwkFlyJump(_mwkFlyJumpDistance, distance, Bv.GetMotionStatus(region3) == MotionStatus.Fly))
                                {
                                    // 检查距离上次执行的时间间隔，至少1.5秒
                                    var timeSinceLastJump = (DateTime.UtcNow - _lastMwkFlyJumpTime).TotalSeconds;
                                    if (timeSinceLastJump > 1.5 && waypoint?.MoveMode != MoveModeEnum.Fly.Code)
                                    {
                                        Logger.LogDebug("自动赶路：玛薇卡跳飞冷却中，距离上次执行 {time:F1}秒", timeSinceLastJump);
                                        _lastMwkFlyJumpTime = DateTime.UtcNow;
                                        st.HurryOnLogo = true;
                                        st.Mwktiao = false;
                                        st.MwktiaoIn = true;
                                        Logger.LogInformation("自动赶路：玛薇卡跳飞，距离 {d}", Math.Round(distance));
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        await Delay(150, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        await Delay(100, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        await Delay(10, ct);
                                        Simulation.SendInput.SimulateAction(GIActions.Jump);
                                        await Delay(150, ct);
                                        st.Mwktiao = true;
                                        st.MavikaFlyCount = 0;
                                    
                                    using var screen2334 = CaptureToRectArea();
                                    (position, additionalTimeInMs) = await GetPositionAndTime(screen2334, waypoint,isPoint);

                                        if (position is  { X: 0, Y: 0 })
                                        {
                                            if ((DateTime.UtcNow - _prePositionUpdateTime).TotalSeconds <= 5)
                                            {
                                                position = prePosition;
                                            }
                                            else
                                            {
                                                Logger.LogWarning("prePosition 已过时，触发全局匹配2");
                                                Navigation.Reset();
                                                prePosition = default;
                                                _prePositionMapKey = string.Empty;
                                            }
                                        }
                                
                                        distance = Navigation.GetDistance(waypoint, position);
                                        if (distance > _mwkFlyJumpDistance)
                                        {
                                            Logger.LogWarning("自动赶路：玛薇卡跳飞结束，距离 {d}", Math.Round(distance));
                                            st.HurryOnLogo = true;
                                        }
                                        else
                                        {
                                            st.HurryOnLogo = false;
                                        } 
                                    }
                                }
                                
                            },ct);
                        }
                        else
                        {
                            if (IsMavuikaOnMotorcycleByTemplate(screen2) && (Bv.GetMotionStatus(screen2) == MotionStatus.Fly))
                            {
                                Logger.LogInformation("自动赶路：飞行下落...");
                                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            }
                                
                            st.HurryOnIn = false;
                            if (_mwkFlyJumpDistance > 0 && distance < _mwkFlyJumpDistance)
                            {
                                st.HurryOnLogo = false;
                            }
                            else
                            {
                                st.HurryOnLogo = true;
                            }
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
                                st.HurryOnLogo = true;
                                if (distance > 20)
                                {
                                    if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
                                    {
                                        Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                    }
                                    else if (waypoint.MoveMode == MoveModeEnum.Run.Code)
                                    {
                                        if (st.RunCount < 2)
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
                                    st.HurryOnLogo = true;
                                    if (distance > 20)
                                    {
                                        if (waypoint.MoveMode == MoveModeEnum.Dash.Code)
                                        {
                                            Simulation.SendInput.SimulateAction(GIActions.SprintMouse);
                                        }
                                        else if (waypoint.MoveMode == MoveModeEnum.Run.Code)
                                        {
                                            if (st.RunCount <2)
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
                            st.SprintMouseLogo = true;
                            st.HurryOnLogo = true;
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
                            st.HurryOnLogo = true;
                        }
                    }
                }

                return false;
    }
}
