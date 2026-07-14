using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Config;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.ViewModel.Pages;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Model;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.View.Drawable;
using Compunet.YoloSharp;
using Compunet.YoloSharp.Data;
using Microsoft.Extensions.DependencyInjection;
using BetterGenshinImpact.GameTask.Common.Element.Assets;

namespace BetterGenshinImpact.GameTask.AutoFight.Model;

/// <summary>
/// 队伍内的角色
/// </summary>
public class Avatar
{
    /// <summary>
    /// 配置文件中的角色信息
    /// </summary>
    public readonly CombatAvatar CombatAvatar;

    /// <summary>
    /// 角色名称 中文
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// 队伍内序号
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 最近一次OCR识别出的CD到期时间
    /// </summary>
    private DateTime OcrSkillCd { get; set; }

    /// <summary>
    /// 手动配置的技能CD，有它就不使用OCR,小于0为自动
    /// </summary>
    public double ManualSkillCd { get; set; }

    /// <summary>
    /// 最近一次使用元素战技的时间
    /// </summary>
    public DateTime LastSkillTime { get; set; }
    
    /// <summary>
    /// 元素战技检测锁
    /// </summary>
    private static readonly object SkillCheckLock = new object();

    /// <summary>
    /// 玛薇卡摩托状态判定的色差阈值。色差小于该值判为摩托状态，大于等于判为非摩托状态。
    /// 数值来自对大量截图的观察：摩托状态下色差一般在 10-15 之间，非摩托状态一般在 20 以上。
    /// </summary>
    private const double MotorcycleColorDifferenceThreshold = 15;

    /// <summary>玛薇卡摩托状态两个采样点所在行（Sample_Point_A 与 B 同一行）。</summary>
    private const int MotorcycleSampleRow = 991;

    /// <summary>Sample_Point_A 所在列。</summary>
    private const int MotorcycleSampleColA = 1678;

    /// <summary>Sample_Point_B 所在列。</summary>
    private const int MotorcycleSampleColB = 1728;

    /// <summary>
    /// 元素爆发是否就绪
    /// </summary>
    public bool IsBurstReady { get; set; }

    /// <summary>
    /// 阿蕾奇诺红血才放Q 门控开关（运行期由 AutoFightTask 经 AutoFightParam 注入当前配置组/独立任务的值）。
    /// 默认 false：不门控，正常放Q。仅在战斗启动注入；其他放Q路径（秘境/深渊等）保持默认 false 不感知。
    /// 详见 .kiro/specs/arlecchino-q-low-hp-gate/design.md。
    /// </summary>
    public bool ArlecchinoBurstLowHpGateEnabled { get; set; } = false;
    
    //阿蕾奇诺自动EQ
    public bool ArlecchinoAutoEnabled { get; set; } = false;

    /// <summary>
    /// 玛薇卡摩托状态检测开关（位置1：重击分支用）。运行期由 AutoFightTask 经 AutoFightParam 注入当前配置组/独立任务的值，
    /// 替代原先读全局 AutoFightConfig 的做法，修复"配置组开关对位置1失效"问题。默认 false。
    /// </summary>
    public bool MavuikaMotorcycleCheckEnabled { get; set; } = false;
    
    public int QiKong { get; set; } = 0;

    /// <summary>
    /// 名字所在矩形位置
    /// </summary>
    public Rect NameRect { get; set; }

    /// <summary>
    /// 名字右边的编号位置
    /// </summary>
    public Rect IndexRect { get; set; }

    /// <summary>
    /// 任务取消令牌
    /// </summary>
    public CancellationToken Ct { get; set; }

    /// <summary>
    /// 战斗场景
    /// </summary>
    public CombatScenes CombatScenes { get; set; }

    /// <summary>
    /// 脱困方向数组（前/后/左/右）
    /// </summary>
    private static readonly GIActions[] UnstuckDirections =
    {
        GIActions.MoveForward,
        GIActions.MoveBackward,
        GIActions.MoveLeft,
        GIActions.MoveRight
    };

    private static readonly Random UnstuckRandom = new();

    private static readonly Lazy<BgiYoloPredictor> QBurstClassifierLazy = new(() =>
        App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().CreateYoloPredictor(BgiOnnxModel.BgiQClassify));
    public static string? LastActiveAvatar { get; internal set; } = null;
    
    private static PathingPartyConfig? _partyConfig;
    public static PathingPartyConfig PartyConfig
    {
        get => _partyConfig ?? PathingPartyConfig.BuildDefault();
        set => _partyConfig = value;
    }
    
    private static PathingConditionConfig PathingConditionConfig { get; set; } = TaskContext.Instance().Config.PathingConditionConfig;


    public Avatar(CombatScenes combatScenes, string name, int index, Rect nameRect, double manualSkillCd = -1)
    {
        CombatScenes = combatScenes;
        Name = name;
        Index = index;
        NameRect = nameRect;
        CombatAvatar = DefaultAutoFightConfig.CombatAvatarMap[name];
        ManualSkillCd = manualSkillCd;
        AutoFightTask.FightStatusFlag = false;
    }


    /// <summary>
    /// 是否存在角色被击败
    /// 通过判断确认按钮
    /// </summary>
    /// <param name="region"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    private static void ThrowWhenDefeated(ImageRegion region, CancellationToken ct)
    {
        if (!AutoFightTask.IsTpForRecover && Bv.IsInRevivePrompt(region))
        {
            // 关闭自动吃药时，直接关闭弹窗去神像（同公版逻辑）
            if (!TaskContext.Instance().Config.AutoEatConfig.Enabled)
            {
                Logger.LogWarning("检测到复苏界面，自动吃药已关闭，前往七天神像复活");
                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                Sleep(600, ct);
                TpForRecover(ct, new RetryException("检测到复苏界面，前往七天神像复活"));
                return;
            }

            // AutoEatCount >= MaxRecoverAttempts 表示吃药超额，直接去七天神像
            if (AutoEatRecoveryDecisions.ShouldGoStatue(PathingConditionConfig.AutoEatCount))
            {
                Logger.LogWarning("检测到复苏界面，吃药已超额(AutoEatCount={t})，前往七天神像", PathingConditionConfig.AutoEatCount);
                // 维持"已放弃嗑药、去神像中"的抑制态，取代原反语义清零 AutoEatCount = 0。
                // 抑制态确保去神像未完成期间再次命中复苏/采集确认界面时不会重新 QuickUseGadget（误触化种匣）。
                PathingConditionConfig.RecoverSuppressed = true;
                TpForRecover(ct, new RetryException("检测到复苏界面，存在角色被击败，前往七天神像复活"));
            }
            else if (AutoEatRecoveryDecisions.ShouldUseGadget(PathingConditionConfig.RecoverSuppressed, PathingConditionConfig.AutoEatCount))
            {
                // 还有吃药次数且未被抑制，尝试点击确认使用小道具
                if (DateTime.UtcNow > PathingConditionConfig.LastEatTime.AddSeconds(1.5))
                {
                    PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                    Logger.LogWarning("自动吃药：尝试使用小道具恢复-n {t}", PathingConditionConfig.AutoEatCount);
                    var confirmRectArea = region.Find(AutoFightAssets.Instance.ConfirmRa);
                    if (!confirmRectArea.IsEmpty())
                    {
                        PathingConditionConfig.AutoEatCount++;
                        Simulation.ReleaseAllKey();
                        confirmRectArea.Click();
                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                    }
                }
                else
                {
                    Sleep(300, ct);
                }
                return;
            }
        }
        else if(AutoFightParam.SwimmingEnabled && !AutoFightTask.FightEndFlag && SwimmingConfirm(region))
        {
            if (AutoFightTask.FightWaypoint is not null)
            {
                Sleep(1000, ct);
                using var bitmap = CaptureToRectArea();
                if (!SwimmingConfirm(bitmap)) //二次确认
                {
                    return;
                }
                
                Logger.LogInformation("游泳检测：尝试回到战斗地点");
                // 使用using语句确保CancellationTokenSource被正确释放
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

                try
                {
                    var pathExecutor = new PathExecutor(cts.Token);
                    pathExecutor.FaceTo(AutoFightTask.FightWaypoint).Wait(2000, cts.Token);
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward,KeyType.KeyDown);
                    Delay(2000, cts.Token).Wait(cts.Token);
                    Simulation.SendInput.SimulateAction(GIActions.MoveForward,KeyType.KeyUp);
                    AutoFightTask.FightWaypoint.MoveMode = MoveModeEnum.Fly.Code; // 改为跳飞
                    Simulation.SendInput.Mouse.RightButtonDown();
                    pathExecutor.MoveTo(AutoFightTask.FightWaypoint, false,null,null,null,6,false).Wait(15000, cts.Token);
                    Logger.LogInformation("游泳检测：移动结束");
                    cts?.Cancel();
                }
                catch (OperationCanceledException)
                {
                    Logger.LogInformation("操作被取消");
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "执行过程中发生异常");
                }
                finally
                {
                    // 确保在任何情况下都能释放鼠标右键
                    AutoFightTask.FightWaypoint = null;
                    Simulation.SendInput.Mouse.RightButtonUp();
                    Simulation.ReleaseAllKey();
                    cts?.Cancel();
                    GC.Collect();//释放内存
                    GC.WaitForPendingFinalizers();//释放内存
                    MatchTemplateHelper.CleanupMemory();
                }
                
                using var bitmap2 = CaptureToRectArea();
                if (!SwimmingConfirm(bitmap2))
                {
                    Logger.LogInformation("游泳检测：游泳脱困成功");
                   return;
                }
                
                GC.Collect();//释放内存
                GC.WaitForPendingFinalizers();//释放内存
                MatchTemplateHelper.CleanupMemory();
                Logger.LogWarning("游泳检测：回到战斗地点失败");
            }
            
            Logger.LogWarning("战斗过程检测到游泳，前往七天神像重试");
            TpForRecover(ct, new RetryException("战斗过程检测到游泳，前往七天神像重试"));
        }
    }
    
    /// <summary>
    /// 游泳检测（色块连通性检测）
    /// </summary>
    public static bool SwimmingConfirm(Region region)
    {
        using var regionMat = region.ToImageRegion().DeriveCrop(1819, 1028, 9, 7);
        using var mask = OpenCvCommonHelper.Threshold(regionMat.SrcMat, 
            new Scalar(242, 223, 39),new Scalar(255, 233, 44));// new Scalar(242, 223, 39),new Scalar(255, 233, 44));
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

        return numLabels > 1;
    }

    /// <summary>
    /// tp 到七天神像恢复
    /// </summary>
    /// <param name="ct"></param>
    /// <param name="ex"></param>
    /// <exception cref="RetryException"></exception>
    public static void TpForRecover(CancellationToken ct, Exception ex)
    {
        // return-to-point-not-suspended-during-tpforrecover-fix spec：
        // Avatar.TpForRecover 是绕过 PathExecutor.TpStatueOfTheSeven 的独立去神像路径，
        // 前一 spec 只在 PathExecutor.TpStatueOfTheSeven 置位 IsTeleportingToStatue，遗漏了本路径。
        // 此处补齐写点：进入去神像流程即置位标志（方法入口最前、复苏弹窗 ESC 处理之前），
        // 使两条"战斗中回点"后台循环（万叶 KazuhaContinuousReturnLoopAsync + 通用
        // GeneralReturnToFightPointLoopAsync）已有的 gate ShouldStopReturnForTeleport(IsTeleportingToStatue)
        // 立即命中 return 终止本场回点循环，不再把角色拉离神像。窗口取方法整体（方案 B，最宽）：
        // 角色此刻已倒地/红血，回点本就无意义，一进入流程就终止最贴合用户诉求。
        // finally 复位保证任何退出路径（末尾 throw ex 抛 RetryException / 传送抛异常 / 取消）都复位，
        // 标志不会永久悬挂。复用前一 spec 的同一标志/纯函数/gate，不新增信号、不改回点循环代码。
        // 严禁与 IsTpForRecover（吃药作用域）/ IsSuspend / IsSuspendedByCapture 混用。
        // 详见 design.md 改动 / Property 1 / Property 4。
        AutoFightTask.IsTeleportingToStatue = true;
        try
        {
            using (var bitmap = CaptureToRectArea())
            {
                if (Bv.IsInRevivePrompt(bitmap))
                {
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    Sleep(300, ct);
                }
            }

            // tp 到七天神像复活（通知 WorldStateMonitor 进入传送抑制期）
            BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.CurrentWorldStateMonitor?.BeginTeleportSuppression();
            try
            {
                var tpTask = new TpTask(ct);
                tpTask.TpToStatueOfTheSeven(requireLoadingScreen: BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.CurrentMultiplayerCoordinator != null).Wait(ct);
            }
            finally
            {
                BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.CurrentWorldStateMonitor?.EndTeleportSuppression();
            }
            Logger.LogInformation("血量恢复完成。【设置】-【七天神像设置】可以修改回血相关配置。-p");
            throw ex;
        }
        finally
        {
            AutoFightTask.IsTeleportingToStatue = false;
        }
    }

    /// <summary>
    /// 切换到本角色
    /// 切换cd是1秒，如果切换失败，会尝试再次切换，最多尝试5次
    /// </summary>
    public void Switch()
    {
        var context = new AvatarActiveCheckContext();
        for (var i = 0; i < 30; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return;
            }

            using var region = CaptureToRectArea();
            ThrowWhenDefeated(region, Ct);

            // 切换成功
            if (CombatScenes.GetActiveAvatarIndex(region, context) == Index)
            {
                return;
            }

            SimulateSwitchAction(Index);
            // Debug.WriteLine($"切换到{Index}号位");
            // Cv2.ImWrite($"log/切换.png", region.SrcMat);
            
            Offset60Fix(i);
            
            if (region.Find(AutoFightAssets.Instance.ConfirmRa).IsExist())
            {
                return;
            }
            
            Sleep(240, Ct);
        }
    }

    /// <summary>
    /// 尝试切换到本角色
    /// </summary>
    /// <param name="tryTimes"></param>
    /// <param name="needLog"></param>
    /// <returns></returns>
    public bool TrySwitch(int tryTimes = 4, bool needLog = true)
    {
        var context = new AvatarActiveCheckContext();
        for (var i = 0; i < tryTimes; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return false;
            }

            using var region = CaptureToRectArea();
            ThrowWhenDefeated(region, Ct);

            // 切换成功
            if (CombatScenes.GetActiveAvatarIndex(region, context,true) == Index)
            {
                if (needLog && i > 0)
                {
                    Logger.LogInformation("成功切换角色:{Name}", Name);
                }
                AutoFightTask.SwitchTryCount = 0;
                
                return true;
            }

            SimulateSwitchAction(Index);
            
            Offset60Fix(i);
            
            var resultRa = region.Find(AutoFightAssets.Instance.ConfirmRa);
            if (resultRa.IsExist())
            {
                if (i == 9)
                {
                    resultRa.Click();
                    resultRa.ClickTo(-100,0);
                }
                
                using (var bitmap = CaptureToRectArea()) //复活界面检测，自动战斗期间，不进行BGI的复活检测，超出吃药上限后才会检测
                {
                    var confirmRa = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                    if (confirmRa.IsExist())
                    {
                        confirmRa.Click();
                        Task.Delay(500, Ct).Wait(500);
                        using var bitmap2 = CaptureToRectArea();
                        var okRa = bitmap2.Find(AutoFightAssets.Instance.ConfirmRa);
                        {
                            if (okRa.IsExist())
                            {
                                Logger.LogInformation("自动吃药：{text} 复活界面-2", "退出");
                                Task.Delay(200, Ct).Wait(1000);
                                Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                                Task.Delay(500, Ct).Wait(1000);
                                try
                                {

                                    if (AutoEatRecoveryDecisions.ShouldUseGadget(PathingConditionConfig.RecoverSuppressed, PathingConditionConfig.AutoEatCount)
                                        && !AutoFightSkill.MedicinalCdAsync(Logger, false, 1, Ct).Result)
                                    {
                                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget); //1800,816 1838,835
                                        Simulation.ReleaseAllKey();
                                    }
                                }
                                catch (OperationCanceledException ex)
                                {
                                    Console.WriteLine($"自动结束吃药123：{ex.Message}");
                                }
                                catch (Exception ex)
                                {
                                    Console.WriteLine($"自动结束吃药发生异常123: {ex.Message}");
                                }
                            }
                        }
                    }
                }
                
                Logger.LogInformation("切换识别失败1:{Name} 索引:{Index}", Name,Index);
                return false;
            }

            Sleep(240, Ct);
        }
        Logger.LogInformation("切换识别失败2:{Name} 索引:{Index}", Name,Index);
        return false;
    }
    
    /// <summary>
    /// 尝试切换到本角色
    /// </summary>
    /// <param name="tryTimes"></param>
    /// <param name="needLog"></param>
    /// <returns></returns>
    public bool TrySwitch2(int tryTimes = 4, bool needLog = true)
    {
        var context = new AvatarActiveCheckContext();
        for (var i = 0; i < tryTimes; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return false;
            }

            using var region = CaptureToRectArea();
            // ThrowWhenDefeated(region, Ct);
            
            var resultRa = region.Find(AutoFightAssets.Instance.ConfirmRa);
            if (resultRa.IsExist())
            {
                Logger.LogError("复活窗口出现，尝试点击确认");
                resultRa.Click();
                resultRa.ClickTo(-100,0);
                return false;
            }

            // 切换成功
            if (CombatScenes.GetActiveAvatarIndex(region, context,true) == Index)
            {
                // if (needLog && i > 0)
                // {
                //     Logger.LogInformation("成功切换角色:{Name}", Name);
                // }
                AutoFightTask.SwitchTryCount = 0;
                return true;
            }

            SimulateSwitchAction(Index);
            
            Offset60Fix(i);

            Sleep(240, Ct);
        }
        
        Logger.LogWarning("切换角色失败:{Name}", Name);

        return false;
    }

    private void SimulateSwitchAction(int index)
    {
        Simulation.SendInput.SimulateAction(GIActions.Drop); //反正会重试就不等落地了
        switch (index)
        {
            case 1:
                // Logger.LogDebug("切换到第1号角色");
                Simulation.SendInput.SimulateAction(GIActions.SwitchMember1);
                break;
            case 2:
                // Logger.LogDebug("切换到第2号角色");
                Simulation.SendInput.SimulateAction(GIActions.SwitchMember2);
                break;
            case 3:
                // Logger.LogDebug("切换到第3号角色");
                Simulation.SendInput.SimulateAction(GIActions.SwitchMember3);
                break;
            case 4:
                // Logger.LogDebug("切换到第4号角色");
                Simulation.SendInput.SimulateAction(GIActions.SwitchMember4);
                break;
            case 5:
                // Logger.LogDebug("切换到第5号角色");
                Simulation.SendInput.SimulateAction(GIActions.SwitchMember5);
                break;
            default:
                break;
        }
    }

    /// <summary>
    /// 切换到本角色
    /// 切换cd是1秒，如果切换失败，会尝试再次切换，最多尝试5次
    /// </summary>
    public void SwitchWithoutCts()
    {
        var context = new AvatarActiveCheckContext();
        for (var i = 0; i < 10; i++)
        {
            using var region = CaptureToRectArea();
            ThrowWhenDefeated(region, Ct);

            if (CombatScenes.GetActiveAvatarIndex(region, context) == Index)
            {
                return;
            }

            SimulateSwitchAction(Index);
            
            if (region.Find(AutoFightAssets.Instance.ConfirmRa).IsExist())
            {
                return;
            }

            Sleep(240);
        }
    }

    private static readonly Random Random = new Random();
    private void Offset60Fix(int i)
    {
        // 3次失败考虑是否偏移出现问题，修改偏移位置
        if (i <= 2 || AutoFightTask.FightStatusFlag)
        {
            // Logger.LogInformation("切换角色1111111 {t}",i);
            if (i == 13 && AutoFightTask.FightStatusFlag)
            {
                AutoFightTask.SwitchTryCount += 1;
                //战斗中防卡死

                Simulation.SendInput.SimulateAction(GIActions.Jump);
                
                var direction = Random.Next(4); // 返回一个 0 到 3 之间的随机整数
                Logger.LogWarning("战斗中切换角色失败，尝试移动 {direction} ", direction);
                Simulation.ReleaseAllKey();
                
                switch (direction)
                {
                    case 0:
                        Simulation.SendInput.SimulateAction(GIActions.MoveBackward, KeyType.KeyDown);
                        SimulateSwitchAction(Index);
                        Logger.LogWarning("战斗中切换角色失败，尝试移动后退 {direction}", direction);
                        break;
                    case 1:
                        Logger.LogWarning("战斗中切换角色失败，尝试移动前进 {direction}", direction);
                        SimulateSwitchAction(Index);
                        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyDown);
                        break;
                    case 2:
                        Logger.LogWarning("战斗中切换角色失败，尝试移动右移 {direction}", direction);
                        SimulateSwitchAction(Index);
                        Simulation.SendInput.SimulateAction(GIActions.MoveRight, KeyType.KeyDown);
                        break;
                    case 3:
                        SimulateSwitchAction(Index);
                        Logger.LogWarning("战斗中切换角色失败，尝试移动左移 {direction}", direction);
                        Simulation.SendInput.SimulateAction(GIActions.MoveLeft, KeyType.KeyDown);
                        break;
                }
                Thread.Sleep(1000);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                //释放所有按键
                Simulation.ReleaseAllKey();
                
                if (AutoFightTask.SwitchTryCount > 15)
                {
                    using var bitmap = CaptureToRectArea();
                    if (Bv.IsInRevivePrompt(bitmap))
                    {
                        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                        Sleep(300, Ct);
                    }
        
                    TpForRecover(Ct, new RetryException("战斗中切换角色连续失败，前往七天神像后重试"));
                    AutoFightTask.SwitchTryCount = 0;
                }
            }
            
            Simulation.SendInput.SimulateAction(GIActions.Drop);
        }
    }

    /// <summary>
    /// 是否出战状态
    /// </summary>
    /// <returns></returns>
    public bool IsActive(ImageRegion region)
    {
        if (IndexRect == default)
        {
            throw new Exception("IndexRect为空");
        }
        else
        {
            var white = IsIndexRectWhite(region, IndexRect);
            return !white;
        }
    }

    private bool IsIndexRectWhite(ImageRegion region, Rect rect)
    {
        // 剪裁出IndexRect区域
        var indexRa = region.DeriveCrop(rect);
        using var mat = indexRa.CacheGreyMat;
        var count = OpenCvCommonHelper.CountGrayMatColor(mat, 251, 255);
        if (count * 1.0 / (mat.Width * mat.Height) > 0.5)
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 是否出战状态
    /// </summary>
    /// <returns></returns>
    [Obsolete]
    public bool IsActiveNoIndexRect(ImageRegion region)
    {
        // 通过寻找右侧人物编号来判断是否出战
        if (IndexRect == default)
        {
            var assetScale = TaskContext.Instance().SystemInfo.AssetScale;
            // 剪裁出队伍区域
            var teamRa = region.DeriveCrop(AutoFightAssets.Instance.TeamRect);
            var blockX = NameRect.X + NameRect.Width * 2 - 10;
            var block = teamRa.DeriveCrop(new Rect(blockX, NameRect.Y, teamRa.Width - blockX, NameRect.Height * 2));
            // Cv2.ImWrite($"block_{Name}.png", block.SrcMat);
            // 取白色区域
            using var bMat = OpenCvCommonHelper.Threshold(block.SrcMat, new Scalar(255, 255, 255), new Scalar(255, 255, 255));
            // Cv2.ImWrite($"block_b_{Name}.png", bMat);
            // 矩形识别
            Cv2.FindContours(bMat, out var contours, out _, RetrievalModes.External,
                ContourApproximationModes.ApproxSimple);
            if (contours.Length > 0)
            {
                var boxes = contours.Select(Cv2.BoundingRect)
                    .Where(w => w.Width >= 20 * assetScale && w.Height >= 18 * assetScale)
                    .OrderByDescending(w => w.Width).ToList();
                if (boxes.Count is not 0)
                {
                    IndexRect = boxes.First();
                    return false;
                }
            }
        }
        else
        {
            // 剪裁出IndexRect区域
            var teamRa = region.DeriveCrop(AutoFightAssets.Instance.TeamRect);
            var blockX = NameRect.X + NameRect.Width * 2 - 10;
            var indexBlock = teamRa.DeriveCrop(new Rect(blockX + IndexRect.X, NameRect.Y + IndexRect.Y, IndexRect.Width,
                IndexRect.Height));
            // Cv2.ImWrite($"indexBlock_{Name}.png", indexBlock.SrcMat);
            var count = OpenCvCommonHelper.CountGrayMatColor(indexBlock.CacheGreyMat, 255);
            if (count * 1.0 / (IndexRect.Width * IndexRect.Height) > 0.5)
            {
                return false;
            }
        }

        Logger.LogInformation("{Name} 当前出战", Name);
        return true;
    }

    /// <summary>
    /// 普通攻击
    /// </summary>
    /// <param name="ms">攻击时长，建议是200的倍数</param>
    public void Attack(int ms = 0)
{
    var isTimes = 0;
    var qiTimes = 0;

    // 新增：计时变量
    var lastChargeTime = DateTime.Now;
    var redTime = DateTime.Now;
    var qiTime = DateTime.Now;

    while (ms >= 0)
    {
        if (Ct is { IsCancellationRequested: true })
        {
            return;
        }

        if (ArlecchinoAutoEnabled && Name == "阿蕾奇诺")
        {
            if (AutoFightTask.FightEndTotoly)
            {
                return;
            }
            Avatar? alqn = CombatScenes.SelectAvatar("阿蕾奇诺");
            using var region1 = CaptureToRectArea();
            // 每隔5秒执行一次 Charge(350)
            if ((DateTime.Now - lastChargeTime).TotalMilliseconds >= 1080)
            {
                // Logger.LogWarning("闪A");
                Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
                Simulation.SendInput.SimulateAction(GIActions.MoveBackward,KeyType.KeyDown);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
                Sleep(35); // 冲刺不能被cts取消
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                Sleep(20); // 冲刺不能被cts取消
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                Sleep(15); // 冲刺不能被cts取消
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                Simulation.SendInput.SimulateAction(GIActions.MoveBackward,KeyType.KeyUp);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                Simulation.SendInput.SimulateAction(GIActions.MoveForward);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                {
                    return;
                }
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                lastChargeTime = DateTime.Now; // 重置计时
                return;
            }
            else
            {
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
            }
            
            if (!AutoFightSkill.AvatarSkillAsync(Logger, alqn, false, 1, Ct, region1, false).Result)
            {
                if (!IsQi(region1) && region1.Find(ElementAssets.Instance.PaimonMenuRo).IsExist())
                {
                    Logger.LogInformation("特化检测：阿蕾奇诺->空契放E");
                    if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                    {
                        return;
                    }
                    Sleep(50, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    Sleep(50, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    Sleep(50, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    Sleep(50, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    Sleep(50, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    Sleep(50, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                    Sleep(695, Ct);
                    if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                    {
                        return;
                    }

                    for (int i = 0; i < 3; i++)
                    {
                        Charge(450);
                        Sleep(150, Ct);
                        using var region13 = CaptureToRectArea();
                        var bb = IsQi(region13);
                        if (bb)
                        {
                            Logger.LogInformation("特化检测：阿蕾奇诺->回收契成功..");
                            break;
                        }
                        Logger.LogInformation("特化检测：阿蕾奇诺->回收契失败，重试..");
                    }
                    
                    return;
                }
            }

            if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
            {
                return;
            }
            
            using var region99 = CaptureToRectArea();
            var aa = CombatHealthDetector.IsRedBlood(region99);
            var cc = alqn.GetSkillCurrentCd(region99);
            var bb22 = (!IsQi(region99)) && ArlecchinoAutoEqDecisions.ShouldReleaseQByCd(cc, TaskContext.Instance().Config.AutoFightConfig.SkillCdForQ);
            if (bb22 || aa)
            {
                // Logger.LogError("E-CD:{t}", cc);
                if (isTimes > 2 || qiTimes > 2)
                {
                    isTimes = 0;
                    qiTimes = 0;
                    // Logger.LogError("放Q情况 {t1} {t2}", bb22, aa);
                    using var region999 = CaptureToRectArea();
                    var bb222 = !IsQi(region999);
                    if (bb222 || aa)
                    {
                        Logger.LogInformation(aa?"特化检测：阿蕾奇诺->红血放Q..{t1} {t2} {t3}":"特化检测：阿蕾奇诺->契空放Q..{t1} {t2} {t3}",aa, bb22,cc);
                        Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                        if (!IsReady(region999,1805,979,15,12,new Scalar(255,250,238),new Scalar(255,251,240),2))
                        {
                            Sleep(100, Ct); 
                            using var imageAfterBurst = CaptureToRectArea();
                            if (imageAfterBurst.Find(ElementAssets.Instance.PaimonMenuRo).IsEmpty())
                            {
                                Sleep(1995, Ct);
                                if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                                {
                                    return;
                                }

                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                Sleep(50, Ct);
                                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                Sleep(795, Ct);
                                if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                                {
                                    return;
                                }

                                for (int i = 0; i < 3; i++)
                                {
                                    Charge(450);
                                    Sleep(150, Ct);
                                    using var region13 = CaptureToRectArea();
                                    var bb2 = IsQi(region13);
                                    if (bb2)
                                    {
                                        Logger.LogInformation("特化检测：阿蕾奇诺->回收契成功..");
                                        break;
                                    }

                                    Logger.LogInformation("特化检测：阿蕾奇诺->回收契失败，重试..");
                                }
                            }
                        }
                    }
                }

                if (aa)
                {
                    if ((DateTime.Now - redTime).TotalMilliseconds >= 100)
                    {
                        redTime = DateTime.Now;
                        isTimes += 2; 
                    }
                    else
                    {
                        isTimes = 0;
                    }
                }

                if (bb22)
                {
                    if ((DateTime.Now - qiTime).TotalMilliseconds >= 100)
                    {
                        qiTime = DateTime.Now;
                        qiTimes += 1; 
                    }
                    else
                    {
                        qiTimes = 0;
                    }
                }
                
                if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                {
                    return;
                }
            }
            else
            {
                if(!aa)isTimes = 0;
                if(!bb22)qiTimes = 0;
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                Sleep(100, Ct);
            }
            
            
            if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
            {
                return;
            }
            
            ms -= 50;
            Sleep(50, Ct);
        }
        else
        {
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
            ms -= 200;
            Sleep(200, Ct);
        }
    }
}
    
    // 阿蕾奇诺契检测区域
    private const int QiX = 840;//1030
    private const int QiY = 1000;
    private const int QiW = 220;//30
    private const int QiH = 20;

    // 阿蕾奇诺契 BGR: (255, 144, 140) ±12
    private static readonly Scalar QiLower = new Scalar(252, 120, 120);
    private static readonly Scalar QiUpper = new Scalar(255, 160, 160);
    public bool IsQi(ImageRegion ra)
    {
        using var bloodRect = ra.DeriveCrop(QiX, QiY, QiW-QiKong, QiH);
        using var mask = OpenCvCommonHelper.Threshold(bloodRect.SrcMat, QiLower, QiUpper);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        // Logger.LogWarning("numLabelsQ : {t}", numLabels);
        return numLabels >= 3;
    }

    public static bool IsReady(ImageRegion ra, int x, int y, int w, int h, Scalar lower, Scalar upper, int num = 1)
    {
        using var bloodRect = ra.DeriveCrop(x, y, w, h);
        using var mask = OpenCvCommonHelper.Threshold(bloodRect.SrcMat, lower, upper);
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var numLabels = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        // Logger.LogWarning("numLabelsQ : {t}", numLabels);
        return numLabels >= num;
    }

    /// <summary>
    /// 计算两个 BGR 像素颜色的欧氏色差：sqrt((b1-b2)^2 + (g1-g2)^2 + (r1-r2)^2)。
    /// 纯函数，无副作用。Vec3b 三分量依次为 蓝(Item0)/绿(Item1)/红(Item2)。
    /// </summary>
    public static double MotorcycleColorDifference(Vec3b a, Vec3b b)
    {
        return Math.Sqrt(
            Math.Pow(a.Item0 - b.Item0, 2) + // 蓝通道差值的平方
            Math.Pow(a.Item1 - b.Item1, 2) + // 绿通道差值的平方
            Math.Pow(a.Item2 - b.Item2, 2)   // 红通道差值的平方
        );
    }

    /// <summary>
    /// 给定两个 BGR 像素颜色，判定是否处于摩托状态：色差 &lt; 阈值 即为摩托状态。
    /// 纯函数，无副作用、无屏幕采样、无日志。供属性测试直接调用。
    /// </summary>
    public static bool IsMotorcycleColor(Vec3b a, Vec3b b)
    {
        return MotorcycleColorDifference(a, b) < MotorcycleColorDifferenceThreshold;
    }

    /// <summary>
    /// 判断当前角色是否处于玛薇卡摩托状态。
    /// 非玛薇卡角色记录一条 LogDebug 诊断日志并返回 false（不截图）。
    /// 玛薇卡角色截取当前画面，读取两个采样点 BGR 颜色，按色差阈值判定。
    /// </summary>
    public bool IsMotorcycle()
    {
        if (Name != "玛薇卡")
        {
            Logger.LogDebug("对非玛薇卡角色 {Name} 调用了 IsMotorcycle()，直接返回 false（未执行屏幕采样）", Name);
            return false;
        }

        using var region = CaptureToRectArea();
        var a = region.SrcMat.At<Vec3b>(MotorcycleSampleRow, MotorcycleSampleColA);
        var b = region.SrcMat.At<Vec3b>(MotorcycleSampleRow, MotorcycleSampleColB);
        return IsMotorcycleColor(a, b);
    }

    private bool _arlecchino = false;
    /// <summary>
    /// 使用元素战技 E
    /// </summary>
    public void UseSkill(bool hold = false,int retryTimes = 1)
    {
        for (var i = 0; i < retryTimes; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return;
            }
            
            var mwk = false;
            
            if (!_arlecchino && ArlecchinoAutoEnabled && Name == "阿蕾奇诺")
            {
                _arlecchino = true;
                Avatar? alqn = CombatScenes.SelectAvatar("阿蕾奇诺");
                var region1 = CaptureToRectArea();
                if (!AutoFightSkill.AvatarSkillAsync(Logger, alqn, false, 1, Ct, region1, false).Result)
                {
                    if (region1.Find(ElementAssets.Instance.PaimonMenuRo).IsExist())
                    {
                        Logger.LogInformation("特化检测UseSkill：阿蕾奇诺->空契放E");
                        if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                        {
                            return;
                        }
                        Sleep(50, Ct);
                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                        Sleep(50, Ct);
                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                        Sleep(50, Ct);
                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                        Sleep(600, Ct);
                        if (Ct is { IsCancellationRequested: true } || AutoFightTask.FightEndTotoly)
                        {
                            return;
                        }

                        for (int jj = 0; jj < 3; jj++)
                        {
                            Charge(350);
                            Sleep(100, Ct);
                            using var region13 = CaptureToRectArea();
                            var bb = IsQi(region13);
                            if (bb)
                            {
                                Logger.LogInformation("特化检测UseSkill：阿蕾奇诺->回收契成功..");
                                break;
                            }
                            Logger.LogInformation("特化检测UseSkill：阿蕾奇诺->回收契失败，重试..");
                        }
                        
                        return;
                    }
                }
            }
            else if (Name == "玛薇卡")
            {
                using var region2 = CaptureToRectArea();
                // 获取两个点的颜色值
                var pos = region2.SrcMat.At<Vec3b>(991, 1678);
                var pos2 = region2.SrcMat.At<Vec3b>(991, 1728);
                double colorDifference = Math.Sqrt(
                    Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                    Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                    Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
                );
                // Logger.LogInformation("玛薇卡技能颜色差值:{ColorDifference}", Math.Round(colorDifference, 2));
                if (colorDifference < 15)
                {
                    mwk = true;
                }
            }

            if (hold)
            {
                if (Name == "纳西妲")
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyDown);
                    Sleep(300, Ct);
                    for (int j = 0; j < 10; j++)
                    {
                        Simulation.SendInput.Mouse.MoveMouseBy(1000, 0);
                        Sleep(50); // 持续操作不应该被cts取消
                    }

                    Sleep(300); // 持续操作不应该被cts取消
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                }
                else if (Name == "坎蒂丝")
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyDown);
                    Thread.Sleep(3000);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                }
                else if(Name == "莉奈娅")
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Thread.Sleep(200);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Thread.Sleep(200);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Thread.Sleep(200);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Thread.Sleep(200);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Thread.Sleep(200);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                }
                else if (Name == "恰斯卡")
                {
                    Logger.LogInformation("进入恰斯卡特化逻辑");
                    // 恰斯卡e(hold)：先确保E抬起，再点按E起飞，然后按住左键进入瞄准蓄力
                    Logger.LogInformation("恰斯卡：释放E键（确保抬起）");
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                    Sleep(100, Ct);
                    Logger.LogInformation("恰斯卡：点按E键起飞");
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Sleep(500, Ct);
                    Logger.LogInformation("恰斯卡：按住左键进入瞄准蓄力");
                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);

                    var dpi = TaskContext.Instance().DpiScale;

                    // 目标瞄准点：对应1920×1080屏幕的(960, 500) — 左右居中
                    // FindBloodBars的检测区域为1500×900左上裁切，该点在此空间内为(960, 500)
                    const double targetX = 960;
                    const double targetY = 500;

                    // 循环检测，直到退出飞行姿态或超时（20秒）
                    var startTime = DateTime.UtcNow;
                    var consecutiveNoBlood = 0;
                    var hadBloodBar = false;
                    var hadLegendaryBar = false;
                    var legendaryLostFrames = 0;
                    var rotationCount = 0;
                    var distinctBulletPatterns = new HashSet<string>();
                    var lastBulletPatternTime = DateTime.UtcNow;
                    var rotateIntervalMultiplier = 1.0;
                    var ocrResetCount = 0;
                    var ocrResetCap = 2;
                    while (true)
                    {
                        Sleep(50, Ct);
                        var chascaX = TaskContext.Instance().Config.AutoFightConfig.ChascaXSensitivity;
                        var chascaY = TaskContext.Instance().Config.AutoFightConfig.ChascaYSensitivity;
                        var chascaInterval = TaskContext.Instance().Config.AutoFightConfig.ChascaLegendaryRotateInterval;
                        var chascaRotateLimit = TaskContext.Instance().Config.AutoFightConfig.ChascaRotateCountLimit;
                        var chascaFrameMs = TaskContext.Instance().Config.AutoFightConfig.SpecializedFrameIntervalMs;

                        // 退出条件
                        if (!Avatar.IsFlying())
                        {
                            break;
                        }
                        if ((DateTime.UtcNow - startTime).TotalSeconds >= 20)
                        {
                            break;
                        }

                        // 每帧检测有效子弹数量（子弹按顺序填充，0-6）
                        var (bulletStatusChars, _, _, _) = Avatar.AnalyzeAllChascaBullets();
                        var effectiveBulletCount = bulletStatusChars.Count(c => c != '空');
                        // 子弹类型直接使用检测结果：火/水/雷/冰/空
                        var compressedChars = bulletStatusChars.Skip(1).ToArray();
                        var pattern = new string(compressedChars);
                        Logger.LogInformation("当前子弹有效数量：{Count}，子弹类型{Pattern}", effectiveBulletCount, pattern);
                        // 调试采样：每帧输出亮度花纹，不干扰原逻辑
                        Avatar.DebugSampleChascaBullets(bulletStatusChars);
                        // 满弹（≥6）→ 进入双倍超时模式
                        if (effectiveBulletCount >= 6)
                        {
                            rotateIntervalMultiplier = 2.0;
                        }
                        distinctBulletPatterns.Add(pattern);
                        // 当出现第3种不同模式时，重置计时器（说明正在稳定命中，子弹变化多样）
                        if (distinctBulletPatterns.Count > 2)
                        {
                            distinctBulletPatterns.Clear();
                            distinctBulletPatterns.Add(pattern);
                            lastBulletPatternTime = DateTime.UtcNow;
                            ocrResetCount = 0;
                        }

                        // 血条检测
                        var bloodBars = Avatar.FindBloodBars();
                        var regularBars = bloodBars.Where(b => b.y >= 96).ToList();
                        var legendaryBars = bloodBars.Where(b => b.y >= 50 && b.y < 96).ToList();
                        // 传奇已被击杀检测：曾出现传奇血条，现在不再存在且子弹不再变动 → 退出
                        if (hadLegendaryBar && legendaryBars.Count == 0)
                        {
                            legendaryLostFrames++;
                            if (legendaryLostFrames >= 2 &&
                                (DateTime.UtcNow - lastBulletPatternTime).TotalSeconds >= chascaInterval)
                            {
                                Logger.LogInformation("传奇血条消失且子弹不再变动，推测已击杀，退出循环");
                                break;
                            }
                        }
                        else
                        {
                            legendaryLostFrames = 0;
                        }
                        using (var drawRegion = CaptureToRectArea())
                        {
                            if (regularBars.Count > 0 && legendaryBars.Count == 0)
                            {
                                // 逻辑1：有普通血条，且无传奇血条 → 索敌
                                Logger.LogInformation("识别到{Count}个血条", regularBars.Count);
                                var nearest = regularBars
                                    .OrderBy(b => Math.Pow(b.x - targetX, 2) + Math.Pow(b.y - targetY, 2))
                                    .First();
                                var drawList = regularBars
                                    .Select(b =>
                                    {
                                        var rect = new Rect(b.x, b.y, b.width, b.height);
                                        if (b.x == nearest.x && b.y == nearest.y && b.width == nearest.width && b.height == nearest.height)
                                            return drawRegion.ToRectDrawable(rect, "target", new System.Drawing.Pen(System.Drawing.Color.LimeGreen, 2));
                                        return drawRegion.ToRectDrawable(rect, "blood");
                                    })
                                    .ToList();
                                VisionContext.Instance().DrawContent.PutOrRemoveRectList("ChascaBloodBars", drawList);
                                consecutiveNoBlood = 0;
                                hadBloodBar = true;
                                var offsetX = nearest.x - targetX;
                                var offsetY = nearest.y - targetY;
                                Simulation.SendInput.Mouse.MoveMouseBy(
                                    (int)(offsetX * dpi * 0.45),
                                    (int)(offsetY * dpi * 0.80));
                                Sleep(4 * chascaFrameMs, Ct);
                            }
                            else if (legendaryBars.Count > 0)
                            {
                                // 逻辑3：有传奇血条 → 根据子弹填充状态判断
                                var drawList = legendaryBars
                                    .Select(lb => drawRegion.ToRectDrawable(new Rect(lb.x, lb.y, lb.width, lb.height), "legendary", new System.Drawing.Pen(System.Drawing.Color.Orange, 2)))
                                    .ToList();
                                VisionContext.Instance().DrawContent.PutOrRemoveRectList("ChascaBloodBars", drawList);
                                consecutiveNoBlood = 0;
                                hadBloodBar = true;
                                hadLegendaryBar = true;

                                if ((DateTime.UtcNow - lastBulletPatternTime).TotalSeconds >= chascaInterval * rotateIntervalMultiplier)
                                {
                                    Logger.LogInformation("传奇：子弹模式在{Count}种状态间变化超过{Interval}s，旋转搜索（倍率={Mult}）", distinctBulletPatterns.Count, chascaInterval * rotateIntervalMultiplier, rotateIntervalMultiplier);
                                    Simulation.SendInput.Mouse.MoveMouseBy(
                                        (int)(500 * dpi * chascaX),
                                        (int)(50 * 0.23 * 8 * dpi * chascaY));
                                    Sleep(6 * chascaFrameMs, Ct);
                                    distinctBulletPatterns.Clear();
                                    lastBulletPatternTime = DateTime.UtcNow;
                                    rotateIntervalMultiplier = 1.0;
                                    ocrResetCount = 0;
                                }
                                else
                                {
                                    // 子弹变动中且有传奇血条，尝试OCR寻敌
                                    if (OcrSeekEnemy(dpi, chascaFrameMs, Ct))
                                    {
                                        // OCR命中 → 重置旋转超时（认为还在打）
                                        if (ocrResetCount < ocrResetCap)
                                        {
                                            lastBulletPatternTime = DateTime.UtcNow;
                                            ocrResetCount++;
                                        }
                                    }
                                    else
                                    {
                                        Sleep(chascaFrameMs, Ct);
                                    }
                                }
                            }
                            else
                            {
                                // 逻辑2：无任何血条 → 旋转
                                VisionContext.Instance().DrawContent.PutOrRemoveRectList("ChascaBloodBars", null);
                                if (hadBloodBar)
                                {
                                    // 刚从有血条切换到无血条，容错一帧不旋转
                                    hadBloodBar = false;
                                    Logger.LogInformation("血条丢失，容错一帧");
                                    Sleep(2 * chascaFrameMs, Ct);
                                }
                                else
                                {
                                    consecutiveNoBlood++;
                                    rotationCount++;
                                    Logger.LogInformation("无血条，进行旋转（第{Count}次）", consecutiveNoBlood);
                                    if (consecutiveNoBlood >= chascaRotateLimit)
                                    {
                                        break;
                                    }
                                    Simulation.SendInput.Mouse.MoveMouseBy(
                                        (int)(500 * dpi * chascaX),
                                        rotationCount % 5 == 0 ? (int)(50 * 0.23 * 4 * dpi * chascaY) : 0);
                                    Sleep(6 * chascaFrameMs, Ct);
                                }
                            }
                        }
                    }

                    // 下车：松左键 → 200ms → 松所有键 → 100ms → 点按E → 100ms → 松所有键
                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
                    Sleep(500, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                    Sleep(100, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyPress);
                    Sleep(100, Ct);
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.KeyUp);
                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
                    Logger.LogInformation("恰斯卡特化逻辑结束");
                }
                else
                {
                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill, KeyType.Hold);
                }
            }
            else
            {
                if(_arlecchino)return;
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
            }
            
            if (Name == "玛薇卡")
            {
                if (mwk)
                {
                    Sleep(300, Ct);
                    using var region2 = CaptureToRectArea();
                    // 获取两个点的颜色值
                    var pos = region2.SrcMat.At<Vec3b>(991, 1678);
                    var pos2 = region2.SrcMat.At<Vec3b>(991, 1728);
                    double colorDifference = Math.Sqrt(
                        Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                        Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                        Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
                    );
                    // Logger.LogInformation("玛薇卡技能颜色差值-2:{ColorDifference}", Math.Round(colorDifference, 2));
                    if (colorDifference >=15)
                    { 
                        ManualSkillCd = 15.6;
                        LastSkillTime = DateTime.UtcNow;
                        Logger.LogInformation("{Name} 元素战技，技能Cd:{Cd} 秒",Name, Math.Round(GetSkillCdSeconds(), 2));
                    } 
                }
                else
                {
                    ManualSkillCd = -1;
                    var cdRounded = Math.Round(DateTime.UtcNow.Subtract(LastSkillTime).TotalSeconds, 2);
                    Logger.LogInformation("{Name} 元素战技，技能cd:{Cd} 秒", Name, cdRounded > 0 && cdRounded <= 16 ? cdRounded : "未更新");
                }
                Sleep(150, Ct);
            }
            else
            {
                Sleep(200, Ct);
                var region = CaptureToRectArea();
                ThrowWhenDefeated(region, Ct);
            
                double cd = 0;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    if (attempt > 0) region = CaptureToRectArea(); // 非首次尝试时重新截图
                    cd = AfterUseSkill(region);
                    if (cd > 0) break;
                    Thread.Sleep(Name == "茜特菈莉"? 200:100);
                }
                region.Dispose();
                
                var recordedCd = ESkillCdTracker.Record(Name, cd);
                if (recordedCd <= 0)
                {
                    recordedCd = ESkillCdTracker.ApplyFallback(Name);
                }
            
                if (cd > 0)
                {
                    Logger.LogInformation(hold ? "{Name} 长按元素战技，cd:{Cd} 秒" : "{Name} 点按元素战技，cd:{Cd} 秒", Name,
                        Math.Round(cd, 2));
                    return;
                } 
            }
        }
    }

    /// <summary>
    /// 使用完元素战技的回调,注意,不会在这里检测是不是需要跑七天神像 <br/>
    /// UseSkill 方法内会调用，如果没有使用UseSkill但是释放了技能之后记得调用一下这个方法
    /// </summary>
    /// <returns>当前技能CD</returns>
    public double AfterUseSkill(ImageRegion? givenRegion = null)
    {
        LastSkillTime = DateTime.UtcNow;
        if (ManualSkillCd > 0)
        {
            return GetSkillCdSeconds();
        }

        var region = givenRegion ?? CaptureToRectArea();
        return GetSkillCurrentCd(region);
    }

    /// <summary>
    /// 元素战技是否正在CD中
    /// 右下 267x132
    /// 77x77
    /// </summary>
    private double GetSkillCurrentCd(ImageRegion imageRegion)
    {
        var eRa = imageRegion.DeriveCrop(AutoFightAssets.Instance.ECooldownRect);
        var eRaWhite = OpenCvCommonHelper.InRangeHsv(eRa.SrcMat, new Scalar(0, 0, 235), new Scalar(0, 25, 255));
        var text = OcrFactory.Paddle.OcrWithoutDetector(eRaWhite);
        var cd = StringUtils.TryParseDouble(text);
        if (cd > 0 && cd <= CombatAvatar.SkillCd)
        {
            OcrSkillCd = DateTime.UtcNow.AddSeconds(cd);
        }

        return cd;
    }

    /// <summary>
    /// 【纯读屏，无副作用】OCR 读取 E 图标当前剩余 CD 秒数，读不到返回 0。
    /// 与 <see cref="GetSkillCurrentCd"/> 共用同一识别管线（同 region/HSV/OCR），
    /// 但不写 OcrSkillCd / LastSkillTime，专供 KazuhaCollectExecutor 残留 CD 等待环节使用，
    /// 避免污染时间戳状态（不影响 GetSkillCdSeconds / WaitSkillCd / IsSkillReady）。
    /// kazuha-collect-residual-cd-empty-cast-fix 改动 1。
    /// </summary>
    /// <param name="imageRegion">当前帧截图</param>
    /// <returns>OCR 识别到的 E 技能剩余 CD 秒数；无法识别为有效正数时返回 0</returns>
    public double ReadSkillCdSecondsByOcr(ImageRegion imageRegion)
    {
        var eRa = imageRegion.DeriveCrop(AutoFightAssets.Instance.ECooldownRect);
        var eRaWhite = OpenCvCommonHelper.InRangeHsv(eRa.SrcMat, new Scalar(0, 0, 235), new Scalar(0, 25, 255));
        var text = OcrFactory.Paddle.OcrWithoutDetector(eRaWhite);
        var cd = StringUtils.TryParseDouble(text);
        return cd > 0 ? cd : 0;
    }


    /// <summary>
    /// 使用元素爆发 Q
    /// Q释放等待 2s 超时认为没有Q技能
    /// </summary>
    public void UseBurst()
    {
        // CD 中立即返回，其余场景尝试释放
        using var region1 = CaptureToRectArea();
        if (IsBurstReadyByClassify(region1) != BurstReadyState.Ready)
        {
            // Logger.LogInformation("Q在CD，跳过");
            return;
        }

        // 阿蕾奇诺红血才放Q门控：复用 region1，红血才继续释放，非红血跳过；检测异常兜底放Q。
        if (ArlecchinoBurstGateDecisions.ShouldGate(ArlecchinoBurstLowHpGateEnabled, Name))
        {
            bool releaseQ = true;
            try
            {
                releaseQ = CombatHealthDetector.IsRedBlood(region1);
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Name} 红血检测异常，兜底释放Q1", Name);
                releaseQ = true;
            }

            if (!releaseQ)
            {
                Logger.LogInformation("{Name} 红血门控：未红血，跳过Q1", Name);
                return;
            }
            Logger.LogInformation("{Name} 红血门控：红血，继续释放Q1", Name);
        }

        for (var i = 0; i < 10; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return;
            }

            // Logger.LogInformation("释放Q");
            Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
            Sleep(200, Ct);

            using var region = CaptureToRectArea();
            ThrowWhenDefeated(region, Ct);

            if (!PartyAvatarSideIndexHelper.HasAnyIndexRect(region))
            {
                // 找不到角色编号块意味者技能释放成功
                Sleep(1500, Ct);
                return;
            }
            else
            {
                // 找到编号块判断是否进入了CD，四星角色没有大招动画
                if (IsBurstReadyByClassify(region) != BurstReadyState.Ready)
                {
                    // Logger.LogInformation("释放Q后检查到CD");
                    Sleep(1500, Ct);
                    return;
                }
            }
        }
    }

    private static BurstReadyState IsBurstReadyByClassify(ImageRegion imageRegion)
    {
        using var qRa = imageRegion.DeriveCrop(AutoFightAssets.Instance.QRectForClassify);
        var result = QBurstClassifierLazy.Value.Predictor.Classify(qRa.CacheImage);
        var topClass = result.GetTopClass();
        var topClassName = topClass.Name.Name;
        // Logger.LogInformation("Q技能冷却分类：{ClassName}，置信度：{Confidence:F2}", topClassName, topClass.Confidence);
        
        // 置信度不足时，直接返回未知，避免误判导致漏放/乱放
        if (topClass.Confidence <= 0.7)
        {
            // Logger.LogInformation("Q技能冷却分类置信度不足：{Confidence:F2}，类别：{ClassName}", topClass.Confidence, topClassName);
            return BurstReadyState.Unknown;
        }

        if (topClassName.Contains("cd 1", StringComparison.OrdinalIgnoreCase))
        {
            return BurstReadyState.Cooldown;
        }

        if (topClassName.Contains("energy 1 cd 0", StringComparison.OrdinalIgnoreCase))
        {
            return BurstReadyState.Ready;
        }

        return BurstReadyState.Unknown;
    }

    // /// <summary>
    // /// 元素爆发是否正在CD中
    // /// 右下 157x165
    // /// 110x110
    // /// </summary>
    // public double GetBurstCurrentCd(CaptureContent content)
    // {
    //     var qRa = content.CaptureRectArea.Crop(AutoFightAssets.Instance.QRect);
    //     var text = OcrFactory.Paddle.Ocr(qRa.SrcGreyMat);
    //     return StringUtils.TryParseDouble(text);
    // }

    /// <summary>
    /// 冲刺
    /// </summary>
    public void Dash(int ms = 0)
    {
        if (Ct is { IsCancellationRequested: true })
        {
            return;
        }

        if (ms == 0)
        {
            ms = 200;
        }

        Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyDown);
        Sleep(ms); // 冲刺不能被cts取消
        Simulation.SendInput.SimulateAction(GIActions.SprintMouse, KeyType.KeyUp);
    }

    public void Walk(string key, int ms)
    {
        if (Ct is { IsCancellationRequested: true })
        {
            return;
        }

        User32.VK vk = User32.VK.VK_NONAME;
        if (key == "w")
        {
            vk = GIActions.MoveForward.ToActionKey().ToVK();
        }
        else if (key == "s")
        {
            vk = GIActions.MoveBackward.ToActionKey().ToVK();
        }
        else if (key == "a")
        {
            vk = GIActions.MoveLeft.ToActionKey().ToVK();
        }
        else if (key == "d")
        {
            vk = GIActions.MoveRight.ToActionKey().ToVK();
        }

        if (vk == User32.VK.VK_NONAME)
        {
            return;
        }

        Simulation.SendInput.Keyboard.KeyDown(vk);
        Sleep(ms); // 行走不能被cts取消
        Simulation.SendInput.Keyboard.KeyUp(vk);
    }

    /// <summary>
    /// 移动摄像机
    /// </summary>
    /// <param name="pixelDeltaX">负数是左移，正数是右移</param>
    /// <param name="pixelDeltaY"></param>
    public void MoveCamera(int pixelDeltaX, int pixelDeltaY)
    {
        Simulation.SendInput.Mouse.MoveMouseBy(pixelDeltaX, pixelDeltaY);
    }

    /// <summary>
    /// 等待
    /// </summary>
    /// <param name="ms"></param>
    public void Wait(int ms)
    {
        Sleep(ms); // 由于存在宏操作，等待不应被cts取消
    }
    
    /// <summary>
    /// 等待完成
    /// </summary>
    public void Ready()
    {
        Sleep(10, Ct);

        for (int i = 0; i < 20; i++)
        {
            if (Ct is { IsCancellationRequested: true })
            {
                return;
            }

            using var region = CaptureToRectArea();
            // 等待角色编号块出现
            if (PartyAvatarSideIndexHelper.HasAnyIndexRect(region))
            {
                region.Dispose();
                return;
            }

            Sleep(150, Ct);
        }
    }

    /// <summary>
    ///
    /// 根据cd推算E技能是否好了
    /// </summary>
    /// <param name="skillCd">强制指定技能CD</param>
    /// <param name="printLog">log是否输出</param>
    /// <returns>是否好了</returns>
    public bool IsSkillReady(bool printLog = false)
    {
        var cd = GetSkillCdSeconds();
        if (cd > 0)
        {
            if (printLog)
            {
                Logger.LogInformation("{Name}的E技能未准备好,CD还有{Seconds}秒", Name, Math.Round(cd, 2));
            }

            return false;
        }

        return true;
    }

    /// <summary>
    ///  计算上一次使用技能到现在还剩下多长时间的cd
    /// </summary>
    /// <returns></returns>
    public double GetSkillCdSeconds()
    {
        switch (ManualSkillCd)
        {
            case < 0:
            {
                var now = DateTime.UtcNow;
                // 若未经过OCR的技能释放,上次时间加上最长的技能时间
                var maxCd = Math.Max(CombatAvatar.SkillHoldCd, CombatAvatar.SkillCd);
                var target =
                    LastSkillTime >= OcrSkillCd
                        ? LastSkillTime.AddSeconds(Math.Max(CombatAvatar.SkillHoldCd, CombatAvatar.SkillCd))
                        : OcrSkillCd;
                var result = now > target ? 0d : (target - now).TotalSeconds;
                if (!(result > maxCd)) return result;
                Logger.LogWarning("{Name}的当前技能CD大于其最大技能CD{MaxCd}。如果你没有调整系统时间的话，这是一个bug。", Name, maxCd);
                return maxCd;
            }
            case > 0:
            {
                // 用户设置，所以直接通过上次释放技能的时间计算
                var dif = DateTime.UtcNow - LastSkillTime;
                if (ManualSkillCd > dif.TotalSeconds)
                {
                    return ManualSkillCd - dif.TotalSeconds;
                }

                break;
            }
        }

        return 0;
    }

    /// <summary>
    /// 等待技能CD
    /// </summary>
    /// <param name="ct">CancellationToken</param>
    public async Task WaitSkillCd(CancellationToken ct = default)
    {
        // 获取CD时间
        if (IsSkillReady())
        {
            return;
        }

        var s = GetSkillCdSeconds() + 0.2;
        Logger.LogInformation("{Name}的E技能CD未结束，等待{Seconds}秒", Name, Math.Round(s, 2));
        await Delay((int)Math.Ceiling(s * 1000), ct);
    }

    /// <summary>
    /// 跳跃
    /// </summary>
    public void Jump()
    {
        Simulation.SendInput.SimulateAction(GIActions.Jump);
    }

    /// <summary>
    /// 重击
    /// </summary>
    public void Charge(int ms = 0)
    {
        if (ms == 0)
        {
            ms = 1000;
        }

        if (Name == "那维莱特")
        {
            var dpi = TaskContext.Instance().DpiScale;
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
            while (ms >= 0)
            {
                if (Ct is { IsCancellationRequested: true })
                {
                    return;
                }

                Simulation.SendInput.Mouse.MoveMouseBy((int)(1000 * dpi), 0);
                ms -= 50;
                Sleep(50); // 持续操作不应该被cts取消
            }

            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
        }
        else if (Name == "恰斯卡")
        {
            var dpi = TaskContext.Instance().DpiScale;
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
            int tick = -4; // 起飞那一刻需要多一点点时间用来矫正视角高度
            while (ms >= 0)
            {
                if (Ct is { IsCancellationRequested: true })
                {
                    return;
                }

                // 恰在蓄力时转得越快越容易把视角趋向于水平
                // 基于上面这个特性，如果我们用同一个鼠标方向向量，大致能在所有设备上控制视角高低（只要帧率不太低）

                // 恰的子弹上膛机制：怪物要在HUD准星框内超过一定时长（体感0.2-0.3秒）才能让子弹上膛。所以搜索敌人要低速。不然敌人体型小或者远就很容易锁不上。
                const double lowspeed = 0.7, highspeed = 50;
                double rateX, rateY;
                if (tick < 3)
                {
                    rateX = highspeed;
                    rateY = highspeed * 0.23;
                }
                else if (tick < 40)
                {
                    rateX = lowspeed * 0.7;
                    rateY = 0;
                }
                else if (tick < 43)
                {
                    rateX = highspeed;
                    rateY = highspeed * 0.4;
                }
                else if (tick < 70)
                {
                    rateX = lowspeed * 0.9;
                    rateY = 0;
                }
                else if (tick < 73)
                {
                    rateX = highspeed;
                    rateY = highspeed;
                }
                else
                {
                    rateX = lowspeed;
                    rateY = 0;
                }

                Simulation.SendInput.Mouse.MoveMouseBy((int)(rateX * 50 * dpi), (int)(rateY * 50 * dpi));

                tick = (tick + 1) % 100;
                Sleep(25);
                ms -= 25;
            }

            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
        }
        else if (Name == "桑多涅")
        {
            var dpi = TaskContext.Instance().DpiScale;
            var cfg = TaskContext.Instance().Config.AutoFightConfig;
            var preAimX = cfg.SandroneChargePreAimX;
            var timeSeqStr = cfg.SandroneChargeTimeSequence;
            var rotateSpeed = cfg.SandroneChargeRotateSpeed;

            // 旋转速度为0时禁用特化逻辑，使用标准重击
            if (rotateSpeed == 0)
            {
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
                Sleep(ms);
                Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
                return;
            }

            // 解析时间序列（逗号分隔，单位秒）
            var seq = new List<double>();
            if (!string.IsNullOrWhiteSpace(timeSeqStr))
            {
                foreach (var part in timeSeqStr.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (double.TryParse(part.Trim(), out var sec))
                        seq.Add(sec);
                }
            }

            var startTime = DateTime.UtcNow;
            var maxMs = ms;

            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);

            // 预计算累计时间边界（ms）
            var boundaries = new double[seq.Count];
            double sum = 0;
            for (int i = 0; i < seq.Count; i++) { sum += seq[i] * 1000; boundaries[i] = sum; }

            var prevSeg = -2;                // 上一帧所属段
            var bloodFoundInOdd = false;     // 当前奇数段是否见过血条
            var exitAfterEven = false;       // 等待偶数段结束退出
            var lastSeenBlood = DateTime.UtcNow; // 序列耗尽后使用
            var logged = false;              // 偶数段日志

            while (!Ct.IsCancellationRequested && (DateTime.UtcNow - startTime).TotalMilliseconds < maxMs)
            {
                var elapsed = (DateTime.UtcNow - startTime).TotalMilliseconds;

                // 确定当前段索引（-1 表示超出序列）
                int seg = -1;
                for (int i = 0; i < boundaries.Length; i++) { if (elapsed < boundaries[i]) { seg = i; break; } }

                // 段切换处理
                if (seg != prevSeg)
                {
                    if (prevSeg >= 0 && prevSeg % 2 == 0 && !bloodFoundInOdd && prevSeg + 1 < seq.Count)
                    {
                        // 奇数段无血条 → 执行完下一个偶数段后退出
                        exitAfterEven = true;
                    }
                    bloodFoundInOdd = false;
                    logged = false;
                    prevSeg = seg;
                }

                double segMs = seg >= 0 ? seq[seg] * 1000 : 0;
                double segStart = seg > 0 ? boundaries[seg - 1] : 0;
                double segElapsed = seg >= 0 ? elapsed - segStart : 0;
                double progress = segMs > 0 ? segElapsed / segMs : 0;

                // 传奇血条检测：跳过序列逻辑，使用OCR寻敌
                var legendaryBars = FindBloodBars().Where(b => b.x > 200 && b.y >= 50 && b.y < 96).ToList();
                if (legendaryBars.Count > 0)
                {
                    if (!OcrSeekEnemy(dpi, cfg.SpecializedFrameIntervalMs, Ct, 900, 540))
                    {
                        Simulation.SendInput.Mouse.MoveMouseBy((int)(500 * rotateSpeed * dpi), 0);
                    }
                    Sleep(cfg.SpecializedFrameIntervalMs);
                    continue;
                }

                switch (seg)
                {
                    case -1:
                        // 序列耗尽后：持续对准 + 旋转搜索
                        var exhaustedBars = FindBloodBars();
                        var exhaustedValid = exhaustedBars.Where(b => b.x > 200 && b.y >= 96).ToList();
                        using (var drawRegion = CaptureToRectArea())
                        {
                            var drawList = new List<View.Drawable.RectDrawable>
                            {
                                drawRegion.ToRectDrawable(new Rect(preAimX - 25, 540 - 25, 50, 50), "preAim", new System.Drawing.Pen(System.Drawing.Color.Red, 2))
                            };
                            if (exhaustedValid.Count > 0)
                            {
                                lastSeenBlood = DateTime.UtcNow;
                                var nearest = exhaustedValid.OrderBy(b => Math.Abs((b.x + b.width / 2) - preAimX)).First();
                                var offsetX = (nearest.x + nearest.width / 2) - preAimX;
                                var offsetY = (nearest.y + nearest.height / 2) - 480;
                                Simulation.SendInput.Mouse.MoveMouseBy((int)(offsetX * 0.5 * dpi), (int)(offsetY * 0.5 * dpi));

                                foreach (var b in exhaustedValid)
                                {
                                    var rect = new Rect(b.x, b.y, b.width, b.height);
                                    if (b.x == nearest.x && b.y == nearest.y && b.width == nearest.width && b.height == nearest.height)
                                        drawList.Add(drawRegion.ToRectDrawable(rect, "target", new System.Drawing.Pen(System.Drawing.Color.LimeGreen, 2)));
                                    else
                                        drawList.Add(drawRegion.ToRectDrawable(rect, "blood"));
                                }
                            }
                            else
                            {
                                // 无血条时先用OCR替补
                                if (!OcrSeekEnemy(dpi, cfg.SpecializedFrameIntervalMs, Ct, 900, 540))
                                {
                                    Simulation.SendInput.Mouse.MoveMouseBy((int)(500 * rotateSpeed * dpi), 0);
                                    if ((DateTime.UtcNow - lastSeenBlood).TotalSeconds >= 1)
                                    {
                                        Logger.LogInformation("序列耗尽后超过1秒未找到血条，提前退出");
                                        VisionContext.Instance().DrawContent.PutOrRemoveRectList("SandroneBloodBars", drawList);
                                        goto done;
                                    }
                                }
                                else
                                {
                                    lastSeenBlood = DateTime.UtcNow; // OCR命中，重置计时
                                }
                            }
                            VisionContext.Instance().DrawContent.PutOrRemoveRectList("SandroneBloodBars", drawList);
                        }
                        break;

                    default:
                        if (seg % 2 == 0)
                        {
                            // 奇数段：瞄准阶段
                            var bars = FindBloodBars();
                            var valid = bars.Where(b => b.x > 200 && b.y >= 96).ToList();
                            using (var drawRegion = CaptureToRectArea())
                            {
                                var drawList = new List<View.Drawable.RectDrawable>
                                {
                                    drawRegion.ToRectDrawable(new Rect(preAimX - 25, 540 - 25, 50, 50), "preAim", new System.Drawing.Pen(System.Drawing.Color.Red, 2))
                                };
                                if (valid.Count > 0)
                                {
                                    bloodFoundInOdd = true;

                                    int targetX;
                                    (int x, int y, int w, int h) tracked = (0, 0, 0, 0);
                                    // 后半段（closest）至少占1s
                                    var secondHalfMs = Math.Max(1000.0, segMs * 0.5);
                                    var splitThreshold = segMs > 0 ? Math.Max(0, 1.0 - secondHalfMs / segMs) : 0.5;
                                    if (progress < splitThreshold)
                                    {
                                        var leftmost = valid.OrderBy(b => b.x).First();
                                        targetX = leftmost.x + leftmost.width / 2;
                                        tracked = (leftmost.x, leftmost.y, leftmost.width, leftmost.height);
                                    }
                                    else
                                    {
                                        var closest = valid.OrderBy(b => Math.Abs((b.x + b.width / 2) - preAimX)).First();
                                        targetX = closest.x + closest.width / 2;
                                        tracked = (closest.x, closest.y, closest.width, closest.height);
                                    }

                                    foreach (var b in valid)
                                    {
                                        var rect = new Rect(b.x, b.y, b.width, b.height);
                                        if (b.x == tracked.x && b.y == tracked.y && b.width == tracked.w && b.height == tracked.h)
                                            drawList.Add(drawRegion.ToRectDrawable(rect, "target", new System.Drawing.Pen(System.Drawing.Color.LimeGreen, 2)));
                                        else
                                            drawList.Add(drawRegion.ToRectDrawable(rect, "blood"));
                                    }

                                    var offset = targetX - preAimX;
                                    Simulation.SendInput.Mouse.MoveMouseBy((int)(offset * 0.25 * dpi), 0);
                                }
                                else
                                {
                                    Simulation.SendInput.Mouse.MoveMouseBy((int)(500 * rotateSpeed * dpi), 0);
                                }
                                VisionContext.Instance().DrawContent.PutOrRemoveRectList("SandroneBloodBars", drawList);
                            }
                        }
                        else
                        {
                            // 偶数段：纯向右水平旋转
                            if (!logged) { Logger.LogInformation("向右 转！"); logged = true; }
                            Simulation.SendInput.Mouse.MoveMouseBy((int)(500 * rotateSpeed * dpi), 0);

                            if (exitAfterEven && elapsed >= boundaries[seg])
                            {
                                Logger.LogInformation("奇数段无血条，偶数段完毕，提前退出");
                                goto done;
                            }
                        }
                        break;
                }

                Sleep(cfg.SpecializedFrameIntervalMs);
            }

            done:
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
        }
        else if (MavuikaMotorcycleCheckEnabled && Name == "玛薇卡")
        {
            Avatar? mwk = CombatScenes.SelectAvatar("玛薇卡");
            
            if (AutoFightSkill.AvatarSkillAsync(Logger, mwk, false, 1, Ct).Result)
            {
                Logger.LogWarning("玛薇卡E技能CD..");
                return;
            }
            
            using var region2 = CaptureToRectArea();
            if (!Bv.IsInMainUi(region2))
            {
                Logger.LogWarning("没有在主界面..");
                return;
            }
            // 玛薇卡摩托状态检测开关（位置1：读注入的配置组/独立任务开关，非全局）。关闭时跳过检测与开摩托
            //摩托状态才执行
            Sleep(200);
            using var region = CaptureToRectArea();
            var pos = region.SrcMat.At<Vec3b>(991, 1678);
            var pos2 = region.SrcMat.At<Vec3b>(991, 1728);
            double colorDifference = Math.Sqrt(
                Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                Math.Pow(pos.Item2 - pos2.Item2, 2)   // 红通道差值的平方
            );
            // var pixelValue = region.SrcMat.At<Vec3b>(32, 67);
            // // 检查每个通道的值是否在允许的范围内
            // var paimon = (!(Math.Abs(pixelValue[0] - 143) <= 10 &&
            //                 Math.Abs(pixelValue[1] - 196) <= 10 &&
            //                 Math.Abs(pixelValue[2] - 233) <= 10));

            // Logger.LogInformation("玛薇卡蓄力颜色差值:{ColorDifference}", Math.Round(colorDifference, 2));
            if (colorDifference >= 15) // 这个数值是通过观察大量截图得来的，摩托状态下差值一般在10-15之间，非摩托状态一般在20以上
            {
                Logger.LogWarning("{Name} 重击命令可能没有进入摩托状态，尝试开摩托！ {t}", Name,Math.Round(colorDifference, 2));
                //点按E技能
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                Sleep(200);
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                Sleep(100);
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                Sleep(200);
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
            }
            else
            {
                Logger.LogWarning("{Name} 当前处于摩托状态，执行重击 {t}", Name,Math.Round(colorDifference, 2));
            }

            // 重击普攻：不受开关影响，始终执行
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
            Sleep(ms, Ct); // 持续操作应该被cts取消
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
        }
        else
        {
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyDown);
            Sleep(ms); // 持续操作不应该被cts取消
            Simulation.SendInput.SimulateAction(GIActions.NormalAttack, KeyType.KeyUp);
        }
    }

    public void MouseDown(string key = "left")
    {
        key = key.ToLower();
        if (key == "left")
        {
            Simulation.SendInput.Mouse.LeftButtonDown();
        }
        else if (key == "right")
        {
            Simulation.SendInput.Mouse.RightButtonDown();
        }
        else if (key == "middle")
        {
            Simulation.SendInput.Mouse.MiddleButtonDown();
        }
    }

    public void MouseUp(string key = "left")
    {
        key = key.ToLower();
        if (key == "left")
        {
            Simulation.SendInput.Mouse.LeftButtonUp();
        }
        else if (key == "right")
        {
            Simulation.SendInput.Mouse.RightButtonUp();
        }
        else if (key == "middle")
        {
            Simulation.SendInput.Mouse.MiddleButtonUp();
        }
    }

    public void Click(string key = "left")
    {
        key = key.ToLower();
        if (key == "left")
        {
            Simulation.SendInput.Mouse.LeftButtonClick();
        }
        else if (key == "right")
        {
            Simulation.SendInput.Mouse.RightButtonClick();
        }
        else if (key == "middle")
        {
            Simulation.SendInput.Mouse.MiddleButtonClick();
        }
    }

    public void MoveBy(int x, int y)
    {
        GlobalMethod.MoveMouseBy(x, y);
    }

    public void Scroll(int scrollAmountInClicks)
    {
        Simulation.SendInput.Mouse.VerticalScroll(scrollAmountInClicks);
    }

    public void KeyDown(string key)
    {
        var vk = KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(key));
        switch (key)
        {
            case "VK_LBUTTON":
                Simulation.SendInput.Mouse.LeftButtonDown();
                break;
            case "VK_RBUTTON":
                Simulation.SendInput.Mouse.RightButtonDown();
                break;
            case "VK_MBUTTON":
                Simulation.SendInput.Mouse.MiddleButtonDown();
                break;
            case "VK_XBUTTON1":
                Simulation.SendInput.Mouse.XButtonDown(0x0001);
                break;
            case "VK_XBUTTON2":
                Simulation.SendInput.Mouse.XButtonDown(0x0001);
                break;
            default:
                Simulation.SendInput.Keyboard.KeyDown(vk);
                break;
        }
    }

    public void KeyUp(string key)
    {
        var vk = KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(key));
        switch (key)
        {
            case "VK_LBUTTON":
                Simulation.SendInput.Mouse.LeftButtonUp();
                break;
            case "VK_RBUTTON":
                Simulation.SendInput.Mouse.RightButtonUp();
                break;
            case "VK_MBUTTON":
                Simulation.SendInput.Mouse.MiddleButtonUp();
                break;
            case "VK_XBUTTON1":
                Simulation.SendInput.Mouse.XButtonUp(0x0001);
                break;
            case "VK_XBUTTON2":
                Simulation.SendInput.Mouse.XButtonUp(0x0001);
                break;
            default:
                Simulation.SendInput.Keyboard.KeyUp(vk);
                if (vk == User32.VK.VK_E)
                {
                    if (Monitor.TryEnter(SkillCheckLock))
                    {
                        try
                        {
                            Task.Run(() =>
                            {
                                Thread.Sleep(200);
                                double cd = 0;
                                var cooldownDetected = false;

                                for (var attempt = 0; attempt < 4; attempt++)
                                {
                                    using var region = CaptureToRectArea();
                                    cd = AfterUseSkill(region);
                                    region.Dispose();

                                    if (cd > 0)
                                    {
                                        cooldownDetected = true;
                                        break;
                                    }

                                    if (attempt < 3)
                                    {
                                        Thread.Sleep(100);
                                    }
                                }

                                if (cooldownDetected)
                                {
                                    Logger.LogInformation("{Name} 元素战技，cd:{Cooldown} 秒",
                                        Name, Math.Round(cd, 2));
                                }
                                else
                                {
                                    Logger.LogWarning("{Name} 战技cd未更新", Name);
                                }
                            },Ct);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "元素战技检测异常");
                        }
                        finally
                        {
                            Monitor.Exit(SkillCheckLock);
                        }
                    }
                }
                break;
        }
    }

    public void KeyPress(string key)
    {
        var vk = KeyBindingsSettingsPageViewModel.MappingKey(User32Helper.ToVk(key));
        if (ArlecchinoBurstGateDecisions.ShouldGate(ArlecchinoBurstLowHpGateEnabled, Name) && vk == User32.VK.VK_Q)
        {
            // 阿蕾奇诺红血才放Q：红血放Q，非红血跳过；检测异常兜底放Q。
            bool releaseQ = true;
            try
            {
                using var region = CaptureToRectArea();
                releaseQ = CombatHealthDetector.IsRedBlood(region);
            }
            catch (Exception ex)
            {
                // 检测失败兜底放Q（宁可放也不憋大招），记录告警便于排查。
                Logger.LogWarning(ex, "{Name} 红血检测异常，兜底释放Q2", Name);
                releaseQ = true;
            }

            if (releaseQ)
            {
                Logger.LogInformation("{Name} 红血门控：释放Q2", Name);
                Simulation.SendInput.Keyboard.KeyPress(vk);
            }
            else
            {
                Logger.LogInformation("{Name} 红血门控：未红血，跳过Q2", Name);
            }
            return;
        }
        switch (key)
        {
            case "VK_LBUTTON":
                Simulation.SendInput.Mouse.LeftButtonClick();
                break;
            case "VK_RBUTTON":
                Simulation.SendInput.Mouse.RightButtonClick();
                break;
            case "VK_MBUTTON":
                Simulation.SendInput.Mouse.MiddleButtonClick();
                break;
            case "VK_XBUTTON1":
                Simulation.SendInput.Mouse.XButtonClick(0x0001);
                break;
            case "VK_XBUTTON2":
                Simulation.SendInput.Mouse.XButtonClick(0x0001);
                break;
            default:
                Simulation.SendInput.Keyboard.KeyPress(vk);
                if (vk == User32.VK.VK_E)
                {
                    if (Monitor.TryEnter(SkillCheckLock))
                    {
                        try
                        {
                            Task.Run(() =>
                            {
                                Thread.Sleep(200);
                                double cd = 0;
                                var cooldownDetected = false;

                                for (var attempt = 0; attempt < 4; attempt++)
                                {
                                    using var region = CaptureToRectArea();
                                    cd = AfterUseSkill(region);
                                    region.Dispose();

                                    if (cd > 0)
                                    {
                                        cooldownDetected = true;
                                        break;
                                    }

                                    if (attempt < 3)
                                    {
                                        Thread.Sleep(100);
                                    }
                                }

                                if (cooldownDetected)
                                {
                                    Logger.LogInformation("{Name} 元素战技，cd:{Cooldown} 秒",
                                        Name, Math.Round(cd, 2));
                                }
                                else
                                {
                                    Logger.LogWarning("{Name} 战技cd未更新", Name);
                                }
                            },Ct);
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "元素战技检测异常");
                        }
                        finally
                        {
                            Monitor.Exit(SkillCheckLock);
                        }
                    }
                }
                break;
        }
    }

    /// <summary>
    /// 从配置字符串中查找角色cd
    /// 仅有角色名时返回 -1 ,没找到角色返回null
    /// </summary>
    /// <param name="avatarName">角色名</param>
    /// <param name="input">序列</param>
    /// <returns></returns>
    public static double? ParseActionSchedulerByCd(string avatarName, string input)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(avatarName))
            return null;

        var searchIndex = input.Length - 1;

        while (true)
        {
            // 逆向查找角色名最后一次出现的位置
            var foundIndex = input.LastIndexOf(avatarName, searchIndex, StringComparison.Ordinal);
            if (foundIndex == -1) return null;

            // 验证前向边界（分号或字符串起点）
            var startValid = foundIndex == 0 ||
                             input[foundIndex - 1] == ';';

            // 验证后向边界（逗号或分号/字符串终点）
            var endValid = foundIndex + avatarName.Length == input.Length ||
                           input[foundIndex + avatarName.Length] == ',' ||
                           input[foundIndex + avatarName.Length] == ';';

            if (startValid && endValid)
            {
                var valueStart = foundIndex + avatarName.Length;
                // 处理逗号后的数值部分
                if (valueStart >= input.Length || input[valueStart] != ',') return -1;
                var valueEnd = input.IndexOf(';', valueStart);
                if (valueEnd == -1) valueEnd = input.Length;

                if (double.TryParse(input.AsSpan(valueStart + 1, valueEnd - valueStart - 1),
                        out var result))
                {
                    return result;
                }

                // 存在角色名但没有数值的情况
                return -1;
            }

            // 更新搜索范围继续查找
            searchIndex = foundIndex - 1;
            if (searchIndex < 0) break;
        }

        return null;
    }

    /// <summary>
    /// 检测当前帧视野内所有红色血条的位置和高度。
    /// 照抄 AutoFightSeek 的色值 + 连通域逻辑，返回 (x, y, width, height) 列表。
    /// </summary>
    public static List<(int x, int y, int width, int height)> FindBloodBars()
    {
        var results = new List<(int x, int y, int width, int height)>();

        using var image = CaptureToRectArea();
        var bloodLower = new Scalar(255, 90, 90); // BGR 红色

        using Mat mask = OpenCvCommonHelper.Threshold(
            image.DeriveCrop(0, 0, 1500, 900).SrcMat, bloodLower);

        using Mat labels = new Mat();
        using Mat stats = new Mat();
        using Mat centroids = new Mat();

        int numLabels = Cv2.ConnectedComponentsWithStats(
            mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);

        for (int i = 1; i < numLabels; i++)
        {
            using Mat row = stats.Row(i);
            if (row.GetArray(out int[] arr))
            {
                int x = arr[0], y = arr[1], width = arr[2], height = arr[3];
                // 排除顶部玩家自身血条（y < 50）
                if (y < 50)
                    continue;
                results.Add((x, y, width, height));
            }
        }

        return results;
    }

    /// <summary>
    /// OCR寻敌 — 仅传奇模式使用。
    /// 在 450,240-1600,900 区域进行OCR识别，按文字区域大小加权计算目标中心，移动视角朝向该坐标。
    /// 力度为普通血条对准的 1/3 (0.45→0.15, 0.80→0.27)。
    /// </summary>
    public static bool OcrSeekEnemy(double dpi, int frameMs, CancellationToken ct, double targetX = 960, double targetY = 500)
    {
        using var ra = CaptureToRectArea();
        var ocrResults = ra.FindMulti(RecognitionObject.Ocr(450, 240, 1150, 660));

        if (ocrResults == null || ocrResults.Count == 0)
            return false;

        var validResults = ocrResults.Where(r => !string.IsNullOrWhiteSpace(r.Text) && !r.Text.StartsWith("+")).ToList();
        if (validResults.Count == 0)
            return false;

        // 按面积加权计算中心
        double totalWeight = 0, wx = 0, wy = 0;
        foreach (var r in validResults)
        {
            double w = r.Width * r.Height;
            wx += (r.X + r.Width / 2.0) * w;
            wy += (r.Y + r.Height / 2.0) * w;
            totalWeight += w;
        }

        double cx = wx / totalWeight;
        double cy = wy / totalWeight;

        var dx = cx - targetX;
        var dy = cy - targetY;

        Simulation.SendInput.Mouse.MoveMouseBy(
            (int)(dx * dpi * 0.15),
            (int)(dy * dpi * 0.27));

        Logger.LogInformation("OCR寻敌：加权中心({Cx:F0},{Cy:F0}) 偏移({Dx:F0},{Dy:F0}) 共{Count}个文本",
            cx, cy, dx, dy, validResults.Count);

        Sleep(frameMs, ct);
        return true;
    }

    /// <summary>
    /// 使用元素视野方法定位传奇（首领）敌人的身体中心位置。
    /// 返回是否存在传奇敌人及其中心坐标（检测空间 1500×900 内）。
    /// 基于传奇血条位置估计身体位置，待后续补充实际视觉检测逻辑。
    /// </summary>
    public static (bool exists, int centerX, int centerY) FindLegendaryBoss()
    {
        // 激活元素视野，等待1秒后截图检测
        Simulation.SendInput.Mouse.MiddleButtonDown();
        Sleep(1000);
        try
        {
            var bloodBars = FindBloodBars();
            var legendaryBars = bloodBars.Where(b => b.y >= 50 && b.y < 96).ToList();
            if (legendaryBars.Count == 0)
                return (false, 0, 0);

            // 取所有传奇血条中面积最大的作为目标
            var best = legendaryBars.OrderByDescending(b => b.width * b.height).First();
            var bloodCenterX = best.x + best.width / 2;
            var bloodCenterY = best.y + best.height / 2;
            // 身体中心Y ≈ 血条中心Y + 350px（从屏幕顶部血条到身体中心的偏移）
            return (true, bloodCenterX, bloodCenterY + 350);
        }
        finally
        {
            Simulation.SendInput.Mouse.MiddleButtonUp();
        }
    }

    /// <summary>
    /// 检测恰斯卡/流浪者等飞行角色是否处于飞行姿态。
    /// 原理：读取 (1584, 1028) 位置的像素，若为白色（RGB >= 250），
    /// 说明空格键图标处于活跃状态（飞行中）。
    /// 提取自 SpaceAtSecondPlaceExist（SkillBoostHelper）。
    /// </summary>
    public static bool IsFlying()
    {
        using var region = CaptureToRectArea();
        var pixel = region.SrcMat.At<Vec3b>(1028, 1584);
        return pixel.Item0 >= 250 && pixel.Item1 >= 250 && pixel.Item2 >= 250;
    }

    /// <summary>
    /// 恰斯卡子弹元素样本颜色。对每个像素找最近的参考点，距离小于阈值则归入对应分组。
    /// </summary>
    private static readonly (string name, string[] hexColors)[] ChascaBulletSamples =
    [
        ("风", ["#8CFFFF", "#94D0D2", "#4C8EA3", "#85FFFF", "#9BD3D5"]),
        ("水", ["#1D65B4", "#1D65B5", "#98FBFE", "#E2FEFE", "#89F4FD", "#56BEF6"]),
        ("冰", ["#DBFFFF", "#D9FEFF", "#63B3E4", "#FDFFFF", "#D6FCFF"]),
        ("火", ["#FFE791", "#9B5D33", "#FCD682", "#ECAD72", "#FCC074", "#D07635"]),
        ("雷", ["#74488A", "#FBE7FC", "#FFC4FF", "#A261BB", "#FBBDFF", "#9D8CA2", "#EEB0F8"]),
    ];

    /// <summary>
    /// 所有参考点（元素样本 + 纯白），静态初始化时由 hex 自动转换。
    /// </summary>
    private static readonly (string group, int r, int g, int b)[] ChascaReferencePoints;

    /// <summary>
    /// 最近邻分类的距离阈值（RGB Euclidean）。小于此值才归入对应分组，否则为"其他"。
    /// </summary>
    private const double ChascaColorThreshold = 80;

    static Avatar()
    {
        var list = new List<(string, int, int, int)>();

        // 元素样本
        foreach (var (name, hexes) in ChascaBulletSamples)
        {
            foreach (var hex in hexes)
            {
                list.Add((name,
                    Convert.ToInt32(hex.Substring(1, 2), 16),
                    Convert.ToInt32(hex.Substring(3, 2), 16),
                    Convert.ToInt32(hex.Substring(5, 2), 16)));
            }
        }

        // 纯白参考点
        list.Add(("白", 255, 255, 255));

        ChascaReferencePoints = list.ToArray();
    }

    /// <summary>
    /// 对一个 RGB 像素，通过最近邻分类归入分组。
    /// 返回 (groupName, distance) — groupName 为空串表示"其他"（距离过大）。
    /// </summary>
    private static (string name, double dist) ClassifyBulletPixel(int r, int g, int b)
    {
        var bestName = "";
        var bestDist = ChascaColorThreshold; // 超过阈值即为"其他"

        foreach (var (group, sr, sg, sb) in ChascaReferencePoints)
        {
            var dr = r - sr;
            var dg = g - sg;
            var db = b - sb;
            var dist = Math.Sqrt(dr * dr + dg * dg + db * db);

            if (dist < bestDist)
            {
                bestDist = dist;
                bestName = group;
            }
        }

        return (bestName, bestDist);
    }

    /// <summary>
    /// 检测恰斯卡当前已装填的子弹数量（0~6）。
    /// </summary>
    public static int GetChascaBulletCount()
    {
        return GetChascaBulletStatus().Count(c => c != '空');
    }

    /// <summary>
    /// 调试用：检测恰斯卡6个弹匣的详细状态。
    /// 返回如 "风雷火冰冰空" 的字符串（元素名/空），始终检测全部6个位置。
    /// </summary>
    public static string DebugGetChascaBulletStatus()
    {
        return GetChascaBulletStatus();
    }

    /// <summary>
    /// 调试用：返回恰斯卡6个弹匣的检测结果摘要。
    /// 输出格式："子弹分析：空 空 火 雷 冰 水"
    /// </summary>
    public static string DebugAnalyzeChascaBullets()
    {
        var (statusChars, _, _, _) = AnalyzeAllChascaBullets();
        return $"当前子弹分析：{string.Join(" ", statusChars.Select(c => c.ToString()))}";
    }

    /// <summary>
    /// 核心检测逻辑：对6个弹匣逐像素最近邻分类，返回状态字符。
    /// </summary>
    private static string GetChascaBulletStatus()
    {
        return new string(AnalyzeAllChascaBullets().statusChars);
    }

    /// <summary>
    /// 6个子弹区域 (x, y, width, height) @ 1920x1080
    /// </summary>
    private static readonly (int x, int y, int w, int h)[] ChascaBulletRects =
    [
        (906,  144, 54, 76),
        (1016, 163, 33, 58),
        (1113, 190, 39, 49),
        (1189, 256, 50, 46),
        (1237, 339, 63, 39),
        (1266, 427, 102, 40),
    ];

    // ========== HSV 条件（数据驱动） ==========
    private static bool IsFireBright(int h, int s, int v) => h >= 8 && h <= 30 && s > 100 && v > 180;
    private static bool IsFireDark(int h, int s, int v) => h >= 5 && h <= 25 && s >= 110 && s <= 230 && v >= 100 && v <= 180;

    private static bool IsElectroBright(int h, int s, int v) => h >= 130 && h <= 152 && s > 80 && v > 180;
    private static bool IsElectroDark(int h, int s, int v) => h >= 135 && h <= 150 && s >= 90 && s <= 160 && v >= 100 && v <= 175;

    private static bool IsCryoBright(int h, int s, int v) => false; // 冰弹无明暗之分
    private static bool IsCryoDark(int h, int s, int v) => h >= 95 && h <= 110 && s > 100 && v > 220;

    private static bool IsHydroBright(int h, int s, int v) => h >= 90 && h <= 115 && s > 100 && v > 230;
    private static bool IsHydroDark(int h, int s, int v) => h >= 90 && h <= 120 && s > 160 && v >= 130 && v <= 195;

    // ===== 共识花纹模板数据 =====
    // 实际数据存储在 GameTask/AutoFight/Assets/chasca-patterns.json（JSON资源文件）
    // 由 generate_csharp_templates.py 生成
    private static readonly Lazy<(int cols, int rows, byte[] data)[][]> _ptTemplatesLazy
        = new Lazy<(int cols, int rows, byte[] data)[][]>(LoadChascaPatterns);

    private static (int cols, int rows, byte[] data)[][] _ptTemplates => _ptTemplatesLazy.Value;

    private static (int cols, int rows, byte[] data)[][] LoadChascaPatterns()
    {
        var jsonPath = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            "GameTask", "AutoFight", "Assets", "chasca-patterns.json");
        var json = System.IO.File.ReadAllText(jsonPath);
        var dict = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, ChascaSlotData>>(json);

        string[] slotKeys = ["slot2", "slot3", "slot4", "slot5", "slot6"];
        string[] elemKeys = ["fire", "electro", "cryo", "hydro"];

        var result = new (int cols, int rows, byte[] data)[5][];
        for (int ti = 0; ti < 5; ti++)
        {
            var slot = dict[slotKeys[ti]];
            int cols = slot.cols;
            int rows = slot.rows;
            var arr = new (int cols, int rows, byte[] data)[4];
            for (int e = 0; e < 4; e++)
            {
                var list = e switch
                {
                    0 => slot.fire,
                    1 => slot.electro,
                    2 => slot.cryo,
                    3 => slot.hydro,
                    _ => null
                };
                if (list == null || list.Count == 0)
                {
                    arr[e] = (0, 0, Array.Empty<byte>());
                    continue;
                }
                var bytes = list.Select(v => (byte)v).ToArray();
                arr[e] = (cols, rows, bytes);
            }
            result[ti] = arr;
        }
        return result;
    }

    private class ChascaSlotData
    {
        public int cols { get; set; }
        public int rows { get; set; }
        public List<int> fire { get; set; }
        public List<int> electro { get; set; }
        public List<int> cryo { get; set; }
        public List<int> hydro { get; set; }
    }

    /// <summary>
    /// 基于共识花纹模板的 Jaccard 匹配检测。
    /// 对每个槽位划分网格，逐格采样 HSV 后与内置花纹模板计算 Jaccard 相似度，
    /// 取最高分元素，低于阈值则标空。
    /// 仅第1槽（第1发）固定为风弹标空；第2-6槽参与检测。
    /// </summary>
    private static (char[] statusChars, Dictionary<string, int>[] perRegionCounts, int[] otherCounts, int[] totals) AnalyzeAllChascaBullets()
    {
        var assetScale = TaskContext.Instance().SystemInfo.AssetScale;
        using var image = CaptureToRectArea();

        var statusChars = new char[ChascaBulletRects.Length];
        var allCounts = new Dictionary<string, int>[ChascaBulletRects.Length];
        var otherCounts = new int[ChascaBulletRects.Length];
        var totals = new int[ChascaBulletRects.Length];
        string[] elemNames = ["火", "雷", "冰", "水"];

        // 6个槽位依次处理
        for (int i = 0; i < ChascaBulletRects.Length; i++)
        {
            // 仅第1槽（第1发）固定为风弹，直接标空
            if (i == 0)
            {
                statusChars[i] = '空';
                allCounts[i] = new Dictionary<string, int>();
                otherCounts[i] = 0;
                totals[i] = 0;
                continue;
            }

            var r = ChascaBulletRects[i];
            var scaledRect = new Rect(
                (int)(r.x * assetScale),
                (int)(r.y * assetScale),
                (int)(r.w * assetScale),
                (int)(r.h * assetScale));

            using var region = image.DeriveCrop(scaledRect).SrcMat;
            using var hsv = new Mat();
            Cv2.CvtColor(region, hsv, ColorConversionCodes.BGR2HSV);

            int totalPixels = hsv.Width * hsv.Height;
            totals[i] = totalPixels;

            // 获取当前槽位的模板索引（i=1→slot2, i=2→slot3, i=3→slot4, i=4→slot5, i=5→slot6）
            int ti = i - 1;
            bool hasTemplate = ti >= 0 && _ptTemplates.Length > ti;

            // 每个元素的 Jaccard 相似度 + 检测cell数
            double[] jaccardScores = new double[4];
            int[] detectedCells = new int[4];

            if (hasTemplate)
            {
                for (int e = 0; e < 4; e++)
                {
                    int cols = _ptTemplates[ti][e].cols;
                    int rows = _ptTemplates[ti][e].rows;
                    if (cols <= 0 || rows <= 0) continue;

                    byte[] template = _ptTemplates[ti][e].data;
                    double cellW = (double)hsv.Width / cols;
                    double cellH = (double)hsv.Height / rows;

                    int match = 0;      // detected ∩ template
                    int detected = 0;   // detected
                    int templateActive = 0; // template

                    for (int rIdx = 0; rIdx < rows; rIdx++)
                    {
                        for (int cIdx = 0; cIdx < cols; cIdx++)
                        {
                            // 采样cell中心像素
                            int px = (int)(cIdx * cellW + cellW / 2);
                            int py = (int)(rIdx * cellH + cellH / 2);
                            px = Math.Min(px, hsv.Width - 1);
                            py = Math.Min(py, hsv.Height - 1);

                            var pixel = hsv.At<Vec3b>(py, px);
                            int h = pixel.Item0, s = pixel.Item1, v = pixel.Item2;

                            bool activated = e switch
                            {
                                0 => IsFireDark(h, s, v) || IsFireBright(h, s, v),
                                1 => IsElectroDark(h, s, v) || IsElectroBright(h, s, v),
                                2 => IsCryoDark(h, s, v),
                                3 => IsHydroDark(h, s, v) || IsHydroBright(h, s, v),
                                _ => false
                            };

                            bool isTpl = template[rIdx * cols + cIdx] == 1;
                            if (isTpl) templateActive++;

                            if (activated)
                            {
                                detected++;
                                if (isTpl) match++;
                            }
                        }
                    }

                    detectedCells[e] = detected;
                    int union = detected + templateActive - match;
                    jaccardScores[e] = union > 0 ? (double)match / union : 0;

                    // 覆盖率辅助：检测到的cell数占总cell比例
                    double coverage = (double)detected / (cols * rows);

                    // 对Jaccard施加覆盖率影响：低覆盖率时降分
                    // 使用 sqrt(coverage) 作为乘数，使得 coverage=0.25 时 ×0.5, coverage=1.0 时 ×1.0
                    jaccardScores[e] *= Math.Sqrt(coverage);
                }
            }

            // 找最高Jaccard分数的元素
            int bestElem = -1;
            double bestScore = 0;
            for (int e = 0; e < 4; e++)
            {
                if (jaccardScores[e] > bestScore)
                {
                    bestScore = jaccardScores[e];
                    bestElem = e;
                }
            }

            // 空判定
            var counts = new Dictionary<string, int>();
            allCounts[i] = counts;

            if (bestElem < 0 || bestScore < 0.15)
            {
                statusChars[i] = '空';
                otherCounts[i] = totalPixels;
            }
            else
            {
                char elem = elemNames[bestElem][0];
                statusChars[i] = elem;
                counts[elem.ToString()] = detectedCells[bestElem];
                otherCounts[i] = totalPixels - detectedCells[bestElem];
            }
        }

        return (statusChars, allCounts, otherCounts, totals);
    }

    /// <summary>
    /// 调试用：采样6个子弹槽位的 HSV 并写入文件。
    /// 每个采样点输出 H/S/V 值，附带每槽平均值。
    /// 写入到解决方案根目录的 chasca-bullet-samples.txt（追加模式）。
    /// 每次测试后请重命名该文件以标注对应元素类型，然后清空或删除以便下次采集。
    /// </summary>
    public static void DebugSampleChascaBullets(char[] label)
    {
        // 定位到解决方案根目录（向上查找 .sln 文件）
        var solutionDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(solutionDir);
        while (dir != null && !dir.GetFiles("*.sln").Any())
            dir = dir.Parent;
        var outputDir = dir?.FullName ?? solutionDir;
        var filePath = System.IO.Path.Combine(outputDir, "chasca-bullet-samples.txt");

        var assetScale = TaskContext.Instance().SystemInfo.AssetScale;
        using var image = CaptureToRectArea();

        using var writer = new StreamWriter(filePath, append: true);

        var frameTime = DateTime.UtcNow.ToString("HH:mm:ss.fff");
        writer.WriteLine($"=== Frame {frameTime} ===");

        for (int si = 0; si < ChascaBulletRects.Length; si++)
        {
            var (rx, ry, rw, rh) = ChascaBulletRects[si];
            var scaledRect = new OpenCvSharp.Rect(
                (int)(rx * assetScale),
                (int)(ry * assetScale),
                (int)(rw * assetScale),
                (int)(rh * assetScale));

            using var region = image.DeriveCrop(scaledRect).SrcMat;
            using var hsv = new Mat();
            Cv2.CvtColor(region, hsv, ColorConversionCodes.BGR2HSV);

            // 自适应步长，目标输出约 12x10 个采样点
            var stepX = Math.Max(1, region.Width / 12);
            var stepY = Math.Max(1, region.Height / 10);

            var labelChar = si < label.Length ? label[si] : '?';
            writer.WriteLine($"Slot{si} label={labelChar} ({rw}x{rh}) step={stepX},{stepY}:");

            double sumH = 0, sumS = 0, sumV = 0;
            int count = 0;

            for (int y = 0; y < hsv.Height; y += stepY)
            {
                for (int x = 0; x < hsv.Width; x += stepX)
                {
                    var pixel = hsv.At<Vec3b>(y, x);
                    int h = pixel.Item0, s = pixel.Item1, v = pixel.Item2;
                    writer.Write($"{h,3}/{s,3}/{v,3} ");
                    sumH += h; sumS += s; sumV += v;
                    count++;
                }
                writer.Write('\n');
            }

            if (count > 0)
            {
                writer.WriteLine($"  => avg H={sumH / count:F0} S={sumS / count:F0} V={sumV / count:F0}  n={count}");
            }
        }

        writer.WriteLine(); // 空行分隔帧
    }
}
