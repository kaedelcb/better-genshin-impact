using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

// =====================================================================
// 低血量生存域（A7）：切人（决策表 + 像素检测 + 执行体）与死亡/复苏确认弹窗处理，同功能域单文件。
// 独立理由（R4）：消费方 PathExecutor.cs 是与公版同名的高冲突文件（切人逻辑原本
// 内联三份拷贝）——全部逻辑收拢进本零冲突新文件后，公版侧 PR 仅需在
// RecoverWhenLowHp / MoveTo 插入共约 7 行调用。
//
// 公版可直接编译验收（R4 复制性门）：本文件全部依赖均为公版已有成员——
// TaskControl.CaptureToRectArea / CombatScenes / Avatar.IsActive·Index /
// PathingPartyConfig / AvatarSwitchIndexDecisions / Simulation·GIActions / AutoFightAssets。
// 唯一例外：ConfirmRa 是茶包在公版 AutoFightAssets 上新增的共享识别资产（SwitchAvatar 等
// 多处使用，不能搬入本文件），PR 时以纯新增成员随本功能带入（R4.3，账目见 §H）。
// 唯一茶包私有成员 PathExecutor.SwitchAvatar 经 lambda 委托注入，不引用茶包独有签名。
//
// 业务规格（用户拍板 2026-09-13）：
//   检测到当前角色红血 → 有行走位切行走位；无行走位切下一位（不能是万叶）；
//   无路可切 → 不切，交由调用方的神像兜底流程。
// =====================================================================

/// <summary>
/// 低血量像素检测场景（两处调用点的健康色判定集合与采样节奏原实现即不同，参数化保留）。
/// </summary>
public enum SurvivalPixelScene
{
    /// <summary>红血恢复场景（RecoverWhenLowHp）：健康色仅绿色，采样间隔 100ms。</summary>
    Recovery,

    /// <summary>战斗点临近场景（MoveTo）：健康色容忍绿色+蓝色，采样间隔 50ms。</summary>
    NearFight,
}

/// <summary>
/// 生存切人决策纯函数（PBT 友好，无外部依赖）。同文件内由 SurvivalAvatarSwitchService 单一消费（G4.3）。
/// </summary>
public static class SurvivalAvatarSwitchDecisions
{
    /// <summary>
    /// 决策纯函数：红血换人目标序号。
    /// 规则（用户拍板）：
    ///   1. 万叶永远不是切换目标（行走位==万叶 → 视为无行走位）；
    ///   2. 行走位优先：行走位合法（∈[1,N]、非万叶、非当前）→ 切行走位；
    ///   3. 否则从当前活跃序号轮换 (x % N) + 1，跳过万叶；
    ///      唯一非万叶候选 == 自己（如两人队 [万叶, X] 的 X）→ 0 = 无路可切，交神像兜底。
    /// 返回：目标序号 ∈ [1,N]；0 = 无路可切 / 输入非法（防御：起点越界、N &lt; 1）。
    /// 无万叶且无行走位时与原硬编码 (current % 4) + 1 在 N==4 下逐字节等价（PBT 守护）。
    /// </summary>
    public static int PlanSwitchTarget(int currentActiveIndex, int effectiveCount, int kazuhaIndex, int walkerIndex)
    {
        if (effectiveCount < 1) return 0;
        if (currentActiveIndex < 1 || currentActiveIndex > effectiveCount) return 0;

        var hasKazuha = kazuhaIndex >= 1 && kazuhaIndex <= effectiveCount;

        // 行走位优先
        if (walkerIndex >= 1 && walkerIndex <= effectiveCount
            && walkerIndex != currentActiveIndex
            && !(hasKazuha && walkerIndex == kazuhaIndex))
        {
            return walkerIndex;
        }

        // 轮换避万叶
        if (!hasKazuha)
        {
            var next = (currentActiveIndex % effectiveCount) + 1;
            return next == currentActiveIndex ? 0 : next;   // N==1：切来切去只有自己 → 无路可切
        }

        var candidate = currentActiveIndex;
        for (var i = 0; i < effectiveCount; i++)
        {
            candidate = (candidate % effectiveCount) + 1;
            if (candidate != kazuhaIndex && candidate != currentActiveIndex) return candidate;
        }

        return 0;   // 唯一非万叶候选 == 自己 → 无路可切
    }
}

/// <summary>
/// 红血生存切人执行体：像素检测（是否仍低血）→ 决策（PlanSwitchTarget）→ 切换执行 → 回写行走位。
/// 自 TaskExecutor 切人块三份内联拷贝（RecoverWhenLowHp / MoveTo，含已漂移的两套检测参数）收拢而来；
/// 修复：越界 throw（原 SelectAvatar 越界单机抛异常）、万叶守卫失效（原把序号串当名字查）、两人队自切空转。
/// </summary>
public static class SurvivalAvatarSwitchService
{
    // 引用别名（E2 同款手法）：静态类不能作 GetLogger<T> 的类型参数，复用 TaskControl.Logger；
    // 日志消息自带 [SurvivalSwitch] 前缀，可检索性不受 SourceContext 影响。
    private static ILogger Logger => TaskControl.Logger;

    // 原实现的经验像素位与"健康色"：绿色(34,215,150)≈常规健康态；蓝色(50,204,255)≈能量/元素态
    // （战斗点临近场景原实现即额外容忍蓝色，避免误判）。连续 sampleCount 次非健康色 → 判定仍低血。
    // 通道序与 OpenCV Vec3b（BGR 序）一致，数值照搬原实现：C0=34, C1=215, C2=150 等。
    private const int PixelX = 1010;
    private const int PixelY = 814;
    private static readonly (byte C0, byte C1, byte C2) RecoveryHealthyColor = (34, 215, 150);
    private static readonly (byte C0, byte C1, byte C2) NearFightExtraHealthyColor = (50, 204, 255);

    /// <summary>
    /// 便捷入口（RecoverWhenLowHp 用）：像素检测仍低血 → 行走位优先 / 轮换避万叶切人 → 回写行走位。
    /// 返回 true = 已执行切换；false = 未触发切换（健康 / 名册不可用 / 未识别活跃角色 / 无路可切）。
    /// </summary>
    public static async Task<bool> TrySwitchWhenStillLowHpAsync(CombatScenes? combatScenes,
        PathingPartyConfig partyConfig, CancellationToken ct, Func<string, Task<Avatar?>> switchAvatar)
    {
        if (!await IsStillLowHpAsync(ct)) return false;
        await SwitchAwayFromLowHpAsync(combatScenes, partyConfig, switchAvatar);
        return true;
    }

    /// <summary>
    /// 像素检测：连续采样均非健康色 → 判定仍低血。
    /// 场景参数保留原两份拷贝的既有差异（Recovery：仅绿色 / 100ms；NearFight：绿色+蓝色 / 50ms）。
    /// </summary>
    public static async Task<bool> IsStillLowHpAsync(CancellationToken ct,
        SurvivalPixelScene scene = SurvivalPixelScene.Recovery, int sampleIntervalMs = 100, int sampleCount = 2)
    {
        var stillLowCount = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            using var bitmap = TaskControl.CaptureToRectArea();
            var pixelValue = bitmap.SrcMat.At<Vec3b>(PixelX, PixelY);
            var isHealthy =
                MatchColor(pixelValue, RecoveryHealthyColor)
                || (scene == SurvivalPixelScene.NearFight && MatchColor(pixelValue, NearFightExtraHealthyColor));
            stillLowCount = isHealthy ? 0 : stillLowCount + 1;
            if (i < sampleCount - 1) await Task.Delay(sampleIntervalMs, ct);
        }

        return stillLowCount >= sampleCount;
    }

    /// <summary>
    /// 切人执行：实测当前活跃序号（不信任登记值）→ 决策 → 切换 → 回写行走位。
    /// 无活跃角色 / 无路可切时仅记日志返回（调用方继续走神像兜底流程），绝不越界 SelectAvatar。
    /// partyConfig 经参数注入（PathExecutor.PartyConfig 为实例属性，写回与原实现同一实例、行为一致）。
    /// </summary>
    public static async Task SwitchAwayFromLowHpAsync(CombatScenes? combatScenes,
        PathingPartyConfig partyConfig, Func<string, Task<Avatar?>> switchAvatar)
    {
        var roster = combatScenes?.GetAvatars();
        var effectiveCount = AvatarSwitchIndexDecisions.EffectiveAvatarCount(roster?.Count);
        if (roster == null || roster.Count == 0 || effectiveCount < 1)
        {
            Logger.LogInformation("[SurvivalSwitch] 队伍名册不可用，跳过生存切人，交由后续流程兜底");
            return;
        }

        // 实测当前活跃序号（起点不信任登记值，原实现登记值与实际可能失真）
        using var bitmap = TaskControl.CaptureToRectArea();
        var currentActiveIndex = 0;
        var kazuhaIndex = 0;
        for (var i = 1; i <= effectiveCount; i++)
        {
            var avatar = combatScenes!.SelectAvatar(i);   // i ∈ [1, 名册数]，按构造不越界
            if (avatar != null && avatar.IsActive(bitmap)) currentActiveIndex = i;
            if (avatar != null && AvatarSwitchIndexDecisions.IsKazuha(avatar.Name)) kazuhaIndex = i;
        }

        if (currentActiveIndex == 0)
        {
            Logger.LogInformation("[SurvivalSwitch] 未识别到当前活跃角色，跳过生存切人，交由后续流程兜底");
            return;
        }

        // 行走位（Q1：配置"作为主要行走角色"；TryParse 防御垃圾串——原 int.Parse 可崩溃）
        var walkerIndex = 0;
        if (int.TryParse(partyConfig.MainAvatarIndex, out var parsedWalker)
            && parsedWalker >= 1 && parsedWalker <= effectiveCount)
        {
            walkerIndex = parsedWalker;
        }

        var target = SurvivalAvatarSwitchDecisions.PlanSwitchTarget(
            currentActiveIndex, effectiveCount, kazuhaIndex, walkerIndex);
        if (target == 0)
        {
            Logger.LogInformation(
                "[SurvivalSwitch] 无可用切换目标（活跃={Cur}，行走位={Walker}，万叶位={Kazuha}，N={N}），交由神像兜底",
                currentActiveIndex, walkerIndex, kazuhaIndex, effectiveCount);
            return;
        }

        Logger.LogInformation(
            "[SurvivalSwitch] 仍低血，切换人：活跃={Cur} → 目标={Target}（行走位={Walker}，万叶位={Kazuha}，N={N}）",
            currentActiveIndex, target, walkerIndex, kazuhaIndex, effectiveCount);

        partyConfig.MainAvatarIndex = target.ToString();   // 回写行走位（原 if 分支行为，统一化到两条路径）
        await switchAvatar(target.ToString());
    }

    /// <summary>
    /// 死亡/复苏确认弹窗处理（自 RecoverWhenLowHp 内联块收拢）：
    /// 命中确认按钮 → 松开移动键停止赶路 → 点击确认 → 偏移点击关闭弹窗 → 使用道具（营养袋）。
    /// 返回 true = 命中并处理了弹窗。
    /// </summary>
    public static async Task<bool> TryHandleDeathConfirmPopupAsync(CancellationToken ct)
    {
        using var bitmap = TaskControl.CaptureToRectArea();
        var confirmRectArea = bitmap.Find(AutoFightAssets.Get(bitmap).ConfirmRa);
        if (confirmRectArea.IsEmpty()) return false;

        Simulation.ReleaseAllKey();
        confirmRectArea.Click();
        await Task.Delay(399, ct);
        confirmRectArea.ClickTo(-100, 0);
        await Task.Delay(300, ct);
        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
        await Task.Delay(500, ct);
        return true;
    }

    private static bool MatchColor(Vec3b actual, (byte C0, byte C1, byte C2) expected)
    {
        // 原实现的容差：每通道 ±10（通道序照搬原实现，数值含义见常量注释）
        return Math.Abs(actual.Item0 - expected.C0) <= 10
               && Math.Abs(actual.Item1 - expected.C1) <= 10
               && Math.Abs(actual.Item2 - expected.C2) <= 10;
    }
}
