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
using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.GameTask.Common;
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
}
