using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.GameUI;
using Fischless.WindowsInput;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 吃药执行结果（multiplayer-hoeing-auto-eat-food-by-period）。
/// Opened：食物页是否成功打开（=本次是否算成功，决定调用方是否写 CD）。
/// RecoverySlots：本次被识别为"需选角恢复药、不支持"的食物格序号列表（升序去重），
///   调用方据此把对应配置行运行时周期置 0，本次运行不再吃这些格。
/// </summary>
public readonly record struct MedicineEatResult(bool Opened, IReadOnlyList<int> RecoverySlots);

/// <summary>
/// 联机锄地"按周期吃食物"执行器（multiplayer-hoeing-auto-eat-food-by-period spec）。
/// 负责在传送同步点：打开食物页 → 按升序依次点击各到期格 + 确定 → 退回主界面。
/// 成功判定（OQ-2）= 食物页是否成功打开；成功则调用方对每个 slot 写时间戳，
/// 打开失败则调用方不写任何时间戳（下个同步点因 CD 未推进自然重试）。
/// 本执行器不碰全局 CD / 不接线协调器（那是 Task 7.x 的职责）。
/// </summary>
public sealed class MedicineEatExecutor
{
    /// <summary>
    /// 食物页前 4 格固定坐标（1080p 基准）。索引 0~3 对应 slot 1~4。
    /// 点击时经 <see cref="GameCaptureRegion.GameRegion1080PPosClick"/> 按 AssetScale 缩放到实际分辨率。
    /// </summary>
    private static readonly (double X, double Y)[] SlotPoints1080P =
    [
        (180, 177), // slot 1
        (320, 177), // slot 2
        (470, 177), // slot 3
        (620, 177), // slot 4
    ];

    /// <summary>选角确认框模板1（comfirm_btn1.png）：命中表示该食物为需选择角色的恢复药，不支持。</summary>
    private static readonly RecognitionObject CharSelectConfirmBtn1Ro = new RecognitionObject
    {
        Name = "MedicineCharSelectConfirm1",
        RecognitionType = RecognitionTypes.TemplateMatch,
        TemplateImageMat = GameTaskManager.LoadAssetImage("AutoSkip", "comfirm_btn1.png"),
        Threshold = 0.7,
        DrawOnWindow = false,
        RegionOfInterest = new OpenCvSharp.Rect(
            (int)(950 * TaskContext.Instance().SystemInfo.AssetScale),
            (int)(700 * TaskContext.Instance().SystemInfo.AssetScale),
            (int)(120 * TaskContext.Instance().SystemInfo.AssetScale),
            (int)(110 * TaskContext.Instance().SystemInfo.AssetScale))
    }.InitTemplate();

    /// <summary>选角确认框模板2（comfirm_btn2.png）：同上。</summary>
    private static readonly RecognitionObject CharSelectConfirmBtn2Ro = new RecognitionObject
    {
        Name = "MedicineCharSelectConfirm2",
        RecognitionType = RecognitionTypes.TemplateMatch,
        TemplateImageMat = GameTaskManager.LoadAssetImage("AutoSkip", "comfirm_btn2.png"),
        Threshold = 0.7,
        DrawOnWindow = false,
        RegionOfInterest = new OpenCvSharp.Rect(
            (int)(950 * TaskContext.Instance().SystemInfo.AssetScale),
            (int)(700 * TaskContext.Instance().SystemInfo.AssetScale),
            (int)(120 * TaskContext.Instance().SystemInfo.AssetScale),
            (int)(110 * TaskContext.Instance().SystemInfo.AssetScale))
    }.InitTemplate();

    private readonly InputSimulator _input;
    private readonly ILogger _logger;

    public MedicineEatExecutor(InputSimulator? input = null, ILogger? logger = null)
    {
        _input = input ?? Simulation.SendInput;
        _logger = logger ?? App.GetLogger<MedicineEatExecutor>();
    }

    /// <summary>
    /// 打开食物页并依次吃掉指定格子的食物。
    /// </summary>
    /// <param name="foodSlots">要吃的食物格序号列表（已由决策层保证升序、合法 1~4）。</param>
    /// <param name="ct">取消令牌；取消(OperationCanceledException)透传，调用方据此不写时间戳。</param>
    /// <returns>
    /// <see cref="MedicineEatResult"/>：Opened = 食物页是否成功打开（= 本次是否算成功）；
    /// RecoverySlots = 本次被识别为"需选角恢复药、不支持"的食物格序号列表，调用方据此把对应配置行运行时周期置 0。
    /// </returns>
    public async Task<MedicineEatResult> EatFoodAsync(IReadOnlyList<int> foodSlots, CancellationToken ct)
    {
        var __recoverySlots = new List<int>();
        await new ReturnMainUiTask().Start(ct);

        // 打开食物页（本执行器自实现的轮询流程）——成功打开 = 本次成功
        var opened = await TryOpenFoodPageAsync(ct);
        if (!opened)
        {
            _logger.LogWarning("[按周期吃食物] 食物页打开失败，尽力退回主界面，本次判失败（不写时间戳）");
            await TryReturnMainUiSafelyAsync(ct);
            return new MedicineEatResult(false, System.Array.Empty<int>()); // 打开失败 → 失败，调用方不写任何时间戳
        }

        // 打开成功后：按升序依次点击每个 slot 的固定坐标 + 确定按钮（尽力而为）
        foreach (var slot in foodSlots)
        {
            ct.ThrowIfCancellationRequested();

            if (slot < 1 || slot > 4)
            {
                // 决策层已保证合法，这里仅作防御性跳过，不影响本次成功判定
                _logger.LogWarning("[按周期吃食物] 跳过越界食物格序号 {Slot}（预期 1~4）", slot);
                continue;
            }

            var point = SlotPoints1080P[slot - 1];
            // GameRegion1080PPosClick 内部按 AssetScale(ScaleTo1080PRatio) 缩放到实际游戏窗口坐标
            GameCaptureRegion.GameRegion1080PPosClick(point.X, point.Y);

            // 轮询"使用"按钮：找到即点（也确认已进入吃药详情页）。每 200ms 一次、最多 7 次，
            // 就绪快时立刻点（省掉固定等待），慢时也能等到。该格无食物 / 超时仍无使用按钮 → 记日志跳过，不影响本次成功判定。
            bool __used = false;
            for (int __useTry = 0; __useTry < 14; __useTry++)
            {
                using (var ra = CaptureToRectArea())
                {
                    if (Bv.ClickWhiteConfirmButton(ra)) { __used = true; break; }
                }
                await Delay(100, ct);
            }
            if (!__used)
            {
                _logger.LogWarning("[按周期吃食物] 第 {Slot} 格未识别到使用按钮（可能无食物），跳过该格", slot);
                continue;
            }

            // 恢复类药物（需要选择角色）不支持：点使用后轮询检测选角确认框（comfirm_btn1/2），
            // 每 200ms 一次、最多 3 次；任一次命中即判为恢复药，按 ESC 取消并跳过该格。
            bool charSelectDetected = false;
            for (int __poll = 0; __poll < 3; __poll++)
            {
                await Delay(200, ct);
                using var raAfter = CaptureToRectArea();
                using var confirm1 = raAfter.Find(CharSelectConfirmBtn1Ro);
                using var confirm2 = raAfter.Find(CharSelectConfirmBtn2Ro);
                if (confirm1.IsExist() || confirm2.IsExist())
                {
                    charSelectDetected = true;
                    break;
                }
            }

            if (charSelectDetected)
            {
                _logger.LogWarning("[按周期吃食物] 第 {Slot} 格为需选择角色的恢复类药物，不支持，按 ESC 跳过", slot);
                Simulation.SendInput.Keyboard.KeyPress(Vanara.PInvoke.User32.VK.VK_ESCAPE);
                await Delay(400, ct);
                __recoverySlots.Add(slot);
            }
            else
            {
                _logger.LogInformation("[按周期吃食物] 已吃第 {Slot} 格食物", slot);
            }

            await Delay(300, ct);
        }

        await new ReturnMainUiTask().Start(ct);
        return new MedicineEatResult(true, __recoverySlots); // 食物页成功打开过 → 成功
    }

    /// <summary>
    /// 打开食物页并验证是否真的打开（本执行器自实现，不复用公共 OpenInventory）。
    /// 公共 OpenInventory 内部有固定延时（按 B 后 Delay(1200)、末尾 Delay(800)）拖慢速度，
    /// 且是全项目共用方法不能改，故这里自实现一个用轮询替代固定延时的开食物页流程，只影响吃药功能：
    /// 1) 按 B 开背包，轮询右上角关闭按钮 MapCloseButton 确认背包已开（命中即提前退出）；
    /// 2) 切到食物页标签，轮询 BagFoodChecked 确认食物页已打开。
    /// </summary>
    private async Task<bool> TryOpenFoodPageAsync(CancellationToken ct)
    {
        // 按 B 打开背包（不调用公共 OpenInventory，避免其内部 1200ms/800ms 固定延时）
        _input.SimulateAction(GIActions.OpenInventory);

        // 轮询右上角关闭按钮 MapCloseButton：每 200ms 一次、最多 8 次（最坏约 1.6s，命中即提前退出）。
        // （原先用的暂停菜单图标是 ESC 菜单专属，按 B 打开的背包界面不出现、实机检测不到；改用背包/地图等
        //  界面右上角的 X 关闭按钮——按 B 后存在——作为"背包已打开"的判据。）
        // 命中过期物品弹窗则点确认；仍在主界面则再按一次 B 兜底。
        bool bagOpened = false;
        for (int i = 0; i < 15; i++)
        {
            ct.ThrowIfCancellationRequested();
            using (var ra = CaptureToRectArea())
            {
                if (Bv.IsInPromptDialog(ra))
                {
                    // 过期物品提示：点确认（与 OpenInventory 同处理）
                    Bv.ClickWhiteConfirmButton(ra, new OpenCvSharp.Rect(0, 0, ra.Width, (int)Math.Round(ra.Height - ra.Height * 0.2)));
                }
                else
                {
                    using var closeBtn = ra.Find(RecognitionAssets.Get("QuickTeleport", "MapCloseButton", ra));
                    if (closeBtn.IsExist()) { bagOpened = true; break; }
                    if (Bv.IsInMainUi(ra)) _input.SimulateAction(GIActions.OpenInventory); // 仍在主界面 → 再按一次 B 兜底
                }
            }
            await Delay(200, ct);
        }
        if (!bagOpened)
        {
            _logger.LogWarning("[按周期吃食物] 轮询未检测到界面右上角关闭按钮(MapCloseButton)，开背包失败");
            return false;
        }

        // 切到食物页标签：已选中则直接就绪；未选中则点击切换。轮询 BagFoodChecked 确认。
        return await NewRetry.WaitForAction(() =>
        {
            using var ra = CaptureToRectArea();
            using var checkedRa = ra.Find(ElementRecognition.Get("BagFoodChecked", ra));
            if (checkedRa.IsExist()) return true;
            using var uncheckedRa = ra.Find(ElementRecognition.Get("BagFoodUnchecked", ra));
            if (uncheckedRa.IsExist()) uncheckedRa.Click();
            return false;
        }, ct, retryTimes: 8, delayMs: 200);
    }

    /// <summary>
    /// 尽力退回主界面（用于打开失败时的收尾）。
    /// 取消透传；其余异常仅记日志吞掉——因为这是失败收尾的"尽力而为"路径，
    /// 即使退主界面失败也不应遮蔽"本次判失败"这一主要结果。
    /// </summary>
    private async Task TryReturnMainUiSafelyAsync(CancellationToken ct)
    {
        try
        {
            await new ReturnMainUiTask().Start(ct);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消透传，交由上层取消流程处理
        }
        catch (Exception ex)
        {
            // 可恢复：退主界面失败不影响"本次判失败"的返回，记警告后吞掉继续返回 false
            _logger.LogWarning(ex, "[按周期吃食物] 打开失败后尽力退回主界面时发生异常，已忽略");
        }
    }
}
