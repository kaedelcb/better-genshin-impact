using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 共享的战斗结束检测逻辑
/// 从 AutoFightTask.CheckFightFinish 提取，供 TXT 和 JSON 两种战斗模式共用
/// </summary>
public static class AutoFightEndDetection
{
    /// <summary>
    /// 检查战斗是否结束（茶包风味完整版）
    /// 包含：派蒙像素检测、进度条检测、双重确认、复活弹窗处理、旋转寻敌
    /// </summary>
    internal static async Task<bool> CheckFightEnd(
        TaskFightFinishDetectConfig config,
        int rotaryFactor,
        int goDistance,
        bool kazuhaContinuousReturn,
        bool fightDurationExceeded,
        CancellationToken ct,
        CancellationToken fightCt,
        Avatar? avatar = null)
    {
        // 战斗开始后指定时间内不检测（EndModel + FightWaitNotEndTime）
        if (fightDurationExceeded)
        {
            return false;
        }

        // 初始派蒙像素预检（快速过滤误触发）
        using var captureToRectArea = CaptureToRectArea();
        var pixelValue = captureToRectArea.SrcMat.At<Vec3b>(32, 67);
        var paiMon = Math.Abs(pixelValue[0] - 143) <= 10 &&
                     Math.Abs(pixelValue[1] - 196) <= 10 &&
                     Math.Abs(pixelValue[2] - 233) <= 10;
        if (!paiMon)
        {
            return false;
        }

        if (Dispatcher.IsCustomCts)
        {
            return false;
        }

        // 旋转寻敌
        if (config.RotateFindEnemyEnabled)
        {
            bool? result = null;
            try
            {
                if (config.RotationMode && config.RotateFindEnemyEnabled)
                {
                    Task.Run(async () =>
                    {
                        result = await AutoFightSeek.SeekAndFightAsync(Logger, config.DetectDelayTime, config.DelayTime, ct, false, rotaryFactor, avatar, goDistance, config.EndModel, config.RotationMode,
                            kazuhaContinuousReturn: kazuhaContinuousReturn,
                            returnIntervalMs: 1000,
                            returnDistanceThreshold: 1.0);
                        AutoFightSeek.RotationCount = (result == null) ? AutoFightSeek.RotationCount + 1 : 0;
                    }, ct);
                }
                else
                {
                    result = await AutoFightSeek.SeekAndFightAsync(Logger, config.DetectDelayTime, config.DelayTime, ct, false, rotaryFactor, avatar, goDistance, config.PaimonEndModel ? config.PaimonEndModel : config.EndModel, config.RotationMode,
                        kazuhaContinuousReturn: kazuhaContinuousReturn,
                        returnIntervalMs: 1000,
                        returnDistanceThreshold: 1.0);
                    AutoFightSeek.RotationCount = (result == null) ? AutoFightSeek.RotationCount + 1 : 0;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "SeekAndFightAsync 方法发生异常");
                return true;
            }

            if (result != null)
            {
                return result.Value;
            }
        }

        if (config.RotateFindEnemyEnabled && !config.EndModel)
        {
            await Delay(config.DelayTime, ct);
        }

        // 打开编队界面检查战斗是否结束（双次循环支持双重确认）
        var doubleEndLogo = true;
        for (int i = 0; i < 2; i++)
        {
            if (i == 1)
            {
                using var captureToRectArea2 = CaptureToRectArea();
                var pixelValue22 = captureToRectArea2.SrcMat.At<Vec3b>(32, 67);
                var paiMon22 = Math.Abs(pixelValue22[0] - 143) <= 10 &&
                               Math.Abs(pixelValue22[1] - 196) <= 10 &&
                               Math.Abs(pixelValue22[2] - 233) <= 10;
                if (!paiMon22)
                {
                    return false;
                }
            }

            Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
            var effectiveDetectDelay = ((config.EndModel && config.RotateFindEnemyEnabled) || config.PaimonEndModel)
                ? config.FastCheckDelay
                : config.DetectDelayTime;
            await Delay(effectiveDetectDelay, ct);

            using var ra = CaptureToRectArea();
            Simulation.SendInput.SimulateAction(GIActions.Drop);

            Vec3b pixelValue2;
            var paiMon2 = false;
            if ((config.EndModel && config.RotateFindEnemyEnabled) || config.PaimonEndModel)
            {
                pixelValue2 = ra.SrcMat.At<Vec3b>(32, 67); // 派蒙
                paiMon2 = Math.Abs(pixelValue2[0] - 143) <= 10 &&
                          Math.Abs(pixelValue2[1] - 196) <= 10 &&
                          Math.Abs(pixelValue2[2] - 233) <= 10;
            }
            else
            {
                pixelValue2 = ra.SrcMat.At<Vec3b>(50, 790); // 进度条颜色
                var whiteTile = ra.SrcMat.At<Vec3b>(50, 768); // 白块
                paiMon2 = !(IsWhite(whiteTile[2], whiteTile[1], whiteTile[0]) &&
                            IsYellow(pixelValue2[2], pixelValue2[1], pixelValue2[0]));
            }

            // 检查是否在吃药CD
            var aa = AutoFightSkill.MedicinalCdAsync(Logger, true, 1, ct).Result;

            if (!paiMon2 && !aa)
            {
                // 优先检测复活弹窗，避免弹窗滤镜导致派蒙像素不匹配而误判战斗结束
                using var popupCheck = CaptureToRectArea();
                var reviveConfirmRa = popupCheck.Find(AutoFightAssets.Get(popupCheck).ConfirmRa);
                if (reviveConfirmRa.IsExist())
                {
                    var autoEatEnabled = TaskContext.Instance().Config.AutoEatConfig.Enabled;
                    if (autoEatEnabled)
                    {
                        Logger.LogInformation("派蒙模式：检测到复活弹窗，尝试复活");
                        await Delay(100, ct);
                        reviveConfirmRa.Click(); // 点击确认（尝试复活）
                        await Delay(300, ct);

                        // 检测弹窗是否仍在（复活药CD时确认无效，弹窗不会关闭）
                        using var popupCheck2 = CaptureToRectArea();
                        var reviveExitRa = popupCheck2.Find(AutoFightAssets.Get(popupCheck2).ExitRa);
                        if (reviveExitRa.IsExist())
                        {
                            reviveExitRa.Click(); // 点击取消关闭弹窗
                            Logger.LogInformation("派蒙模式：复活药可能在CD，点击取消关闭弹窗");
                            await Delay(200, ct);
                            reviveExitRa.ClickTo(-150, 0);
                        }

                        return false; // 战斗未结束
                    }
                    else
                    {
                        // 自动吃药关闭时，直接关闭弹窗，走回神像逻辑
                        Logger.LogInformation("派蒙模式：检测到复活弹窗，自动吃药已关闭，关闭弹窗去神像");
                        var reviveExitRa = popupCheck.Find(AutoFightAssets.Get(popupCheck).ExitRa);
                        if (reviveExitRa.IsExist())
                        {
                            reviveExitRa.Click(); // 点击取消关闭弹窗
                            await Delay(200, ct);
                            reviveExitRa.ClickTo(-150, 0);
                        }
                        return true; // 返回战斗结束，由后续流程去七天神像
                    }
                }

                // 派蒙模式下的二次确认（防止误判）
                if (config.PaimonEndModel && config.DoubleEndEnbled && doubleEndLogo)
                {
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    Logger.LogInformation("派蒙模式：进行二次检测，延时 {doubleEndDelay} ms", config.DoubleEndDelay);
                    doubleEndLogo = false;
                    await Delay(config.DoubleEndDelay, ct);
                    continue;
                }

                // 确认界面检测（防止背包/地图等界面误判）
                using var bitmap = CaptureToRectArea();
                var confirmRa = bitmap.Find(AutoFightAssets.Get(bitmap).ConfirmRa);
                if (confirmRa.IsExist())
                {
                    Logger.LogInformation("识别到确认界面，可能是误判，继续战斗");
                    return false;
                }

                Logger.LogInformation("{mode}：识别到战斗结束",
                    config.EndModel && config.RotateFindEnemyEnabled ? "派蒙模式" : "默认模式");

                Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                return true;
            }

            // 未检测到结束
            if (rotaryFactor != 1 && !config.EndModel && config.RotateFindEnemyEnabled)
                Logger.LogInformation("{mode}：未识别到战斗结束",
                    config.EndModel && config.RotateFindEnemyEnabled ? "快速模式" : "默认模式");

            // 寻敌模式下未检测到结束→继续前进
            if (config.RotateFindEnemyEnabled && rotaryFactor != 1)
            {
                try
                {
                    // 注意：此处使用 await 确保异常能被正确捕获
                    // TXT 版本的 AutoFightTask.CheckFightFinish 中未使用 await，异常可能被吞掉
                    Task.Run(async () =>
                    {
                        Scalar bloodLower = new Scalar(255, 90, 90);
                        await MoveForwardTask.MoveForwardAsync(bloodLower, bloodLower, Logger, fightCt,
                            goDistance,
                            AutoFightSeekDecisions.GetNearHeightThreshold(PathingConditionConfig.MultiplayerFightTimeoutOverride.HasValue));
                    }, ct);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception ex)
                {
                    Logger.LogError($"任务运行时发生异常: {ex.Message}");
                }
            }

            return false;
        }

        return false;
    }

    private static bool IsYellow(int r, int g, int b)
    {
        return (r >= 200 && r <= 255) &&
               (g >= 200 && g <= 255) &&
               (b >= 0 && b <= 100);
    }

    private static bool IsWhite(int r, int g, int b)
    {
        return (r >= 240 && r <= 255) &&
               (g >= 240 && g <= 255) &&
               (b >= 240 && b <= 255);
    }
}
