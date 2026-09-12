using System;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;

namespace BetterGenshinImpact.GameTask.Common;

// =====================================================================
// 窗口焦点恢复域（E2）：决策表 + 单步恢复执行体 + 跨调用状态，同功能域单文件。
// 独立理由：唯一消费方 TaskControl.cs 是与公版同名的高冲突文件——按 G4.3「决策类单点引用
// 应并入执行体所在文件」收拢为单文件；执行体自 TaskControl.CheckAndActivateGameWindow
// 逐 token 搬入，使 TaskControl 保持接近公版形态（PR 时公版侧仅留转发 stub）。
// 被 TaskControl 的 Sleep/Delay/CheckAndSleep 三处经薄转发调用，并被
// CaptureRecoveryService（E3 域）恢复循环每 iter（200ms）直接驱动一次。
// 见 spec focus-recovery-no-budget-limit / focus-recovery-driven-by-capture-loop / bugfix.md §4 EB-1/EB-5
// =====================================================================

/// <summary>
/// 焦点恢复决策（PBT 友好的纯函数）。
/// 单纯判断"是否需要恢复 + 怎么恢复"，不再有放弃 / 节流路径——
/// 用户开 RestoreFocusOnLostEnabled 即明示"持续抢焦点直到成功"。
///
/// **References**: spec focus-recovery-no-budget-limit / bugfix.md §4 EB-1/EB-2
/// </summary>
public enum FocusRecoveryDecision
{
    /// <summary>跳过恢复（Cfg=Off 旧分支由调用方处理 / 焦点已回到原神）。</summary>
    Skip,

    /// <summary>原神窗口处于 Iconic（最小化），需要 ShowWindow(SW_RESTORE) 恢复显示。</summary>
    TryRestoreIconic,

    /// <summary>原神窗口已可见但不在前台，需要 FocusWindow 切前台。</summary>
    TryFocus,
}

/// <summary>
/// 焦点恢复决策输入快照（简化为 3 字段，无预算追踪）。
/// </summary>
public readonly record struct FocusRecoveryState(
    bool RestoreFocusOnLost,
    bool ForegroundIsGenshin,
    bool GameWindowMinimized);

/// <summary>
/// 独立理由：同文件内由 WindowFocusRecoveryService（执行体）单一消费，按 G4.3 并入执行体所在文件。
/// 焦点恢复决策表（纯函数）。无 N/T/Cooldown 预算上限。
/// </summary>
public static class FocusRecoveryDecisions
{
    /// <summary>主决策：根据 state 路由到 Skip / TryRestoreIconic / TryFocus。</summary>
    public static FocusRecoveryDecision Decide(FocusRecoveryState s)
    {
        if (!s.RestoreFocusOnLost) return FocusRecoveryDecision.Skip;
        if (s.ForegroundIsGenshin) return FocusRecoveryDecision.Skip;
        if (s.GameWindowMinimized) return FocusRecoveryDecision.TryRestoreIconic;
        return FocusRecoveryDecision.TryFocus;
    }
}

/// <summary>
/// 窗口焦点恢复服务（自 TaskControl.CheckAndActivateGameWindow 整体搬入，方法体逐 token 一致）。
/// 独立理由：TaskControl.cs 是与公版同名的高冲突文件，本服务把茶包焦点恢复重设计
/// （单步决策驱动 + 3 个跨调用静态状态字段）整体搬出，使 TaskControl 保持接近公版形态；
/// 被 TaskControl 的 Sleep/Delay/CheckAndSleep 三处经薄转发调用，
/// 并被 CaptureRecoveryService（E3 域）恢复循环每 iter（200ms）直接驱动一次
/// （G4.3 支撑类·独立生命周期/状态白名单项 3）。
///
/// 单步语义：每次调用只执行一步恢复动作（不 sleep 不循环），节奏由调用方控制——
/// - Sleep/Delay/CheckAndSleep 路径：NewRetry.Do 1s 节奏由外层重试驱动
/// - CaptureGameImage 30s 等待循环：每 iter（200ms）顶部触发一次
/// 见 spec focus-recovery-driven-by-capture-loop / focus-recovery-no-budget-limit / bugfix.md §4 EB-1/EB-5
/// </summary>
public static class WindowFocusRecoveryService
{
    // 引用别名：保持搬移后的方法体与 TaskControl 原文逐 token 一致；
    // 日志 SourceContext 仍为 TaskControl，与搬移前日志输出逐字节一致。
    private static ILogger Logger => TaskControl.Logger;
    private static bool IsSuspendedByNetwork => TaskControl.IsSuspendedByNetwork;

    // 焦点恢复跨调用状态（spec focus-recovery-no-budget-limit / bugfix.md §4 EB-5）
    // 仅追踪"焦点持续丢失期间是否已打首次 Warning"和"上次进度日志时刻"，
    // 焦点回原神时清零。无预算追踪、无节流戳（决议已删除 N/T/Cooldown）。
    private static bool _focusRecoveryWarningEmitted;
    private static DateTime? _focusRecoveryProgressLastLoggedAt;
    private static DateTime? _focusRecoveryFirstLossAt;

    public static void CheckAndActivateGameWindow(bool throwOnForegroundLost = true)
    {
        // 用户按快捷键暂停 → 这是"希望 BGI 停下"的唯一明确信号（steering spec-adjacent-state-audit §2）。
        // 暂停期间不抢焦点、不持续检测，直到用户再次按热键解除暂停。
        // 注意：Sleep/Delay 路径已被 TrySuspend 的 while 循环挡住；本守门主要覆盖
        // CaptureGameImage 30s 等待循环这条调用栈（它每 200ms 调一次本函数）。
        if (RunnerContext.Instance.IsSuspend)
        {
            return;
        }

        if (IsSuspendedByNetwork)
        {
            Logger.LogInformation("网络恢复中，暂停尝试恢复窗口");
            return;
        }

        // 用户已停止任务 → 立即 return，不再抢焦点（spec focus-recovery-driven-by-capture-loop 漏掉的取消语义补丁）
        if (BetterGenshinImpact.Core.Script.CancellationContext.Instance.IsCancellationRequested)
        {
            return;
        }

        // P-1：Cfg=Off 旧分支——用户显式希望"前台不是原神就抛异常暂停"的严格模式
        if (!TaskContext.Instance().Config.OtherConfig.RestoreFocusOnLostEnabled)
        {
            if (!SystemControl.IsGenshinImpactActiveByProcess())
            {
                // throwOnForegroundLost=true（Sleep/Delay/CheckAndSleep 默认）：保留严格模式抛异常（外层 NewRetry 1s 节流）。
                // throwOnForegroundLost=false（CaptureGameImage 恢复循环）：不抛、不打刷屏告警、直接 return，
                //   交由循环尾部 200ms Thread.Sleep 节流 + 每 5s 一条 Debug 进度处理，消除零延迟自旋。
                if (throwOnForegroundLost)
                {
                    var name = SystemControl.GetActiveByProcess();
                    Logger.LogWarning($"当前获取焦点的窗口为: {name}，不是原神，暂停");
                    throw new RetryException("当前获取焦点的窗口不是原神");
                }
            }
            return;
        }

        var gameHandle = TaskContext.Instance().GameHandle;

        // 焦点回原神 → 清零跨调用状态并 return
        if (SystemControl.IsGenshinImpactActiveByProcess())
        {
            if (_focusRecoveryWarningEmitted && _focusRecoveryFirstLossAt is { } firstLossAt)
            {
                var elapsedMs = (int)(DateTime.UtcNow - firstLossAt).TotalMilliseconds;
                Logger.LogWarning("[FocusRecovery] 焦点已回原神，等待耗时 {Elapsed}ms", elapsedMs);
            }
            _focusRecoveryWarningEmitted = false;
            _focusRecoveryProgressLastLoggedAt = null;
            _focusRecoveryFirstLossAt = null;
            return;
        }

        var state = new FocusRecoveryState(
            RestoreFocusOnLost: true,
            ForegroundIsGenshin: false,
            GameWindowMinimized: User32.IsWindow(gameHandle) && User32.IsIconic(gameHandle));

        var decision = FocusRecoveryDecisions.Decide(state);

        // Skip 不应在此处出现（已在前面 if 处理 ForegroundIsGenshin；Cfg=Off 已 return），保留兜底
        if (decision == FocusRecoveryDecision.Skip) return;

        if (!_focusRecoveryWarningEmitted)
        {
            _focusRecoveryFirstLossAt = DateTime.UtcNow;
            var fgName = SystemControl.GetActiveByProcess();
            Logger.LogWarning(
                "[FocusRecovery] 尝试恢复原神窗口焦点（前台={Fg}），将持续抢回直到成功",
                fgName);
            _focusRecoveryWarningEmitted = true;
            _focusRecoveryProgressLastLoggedAt = DateTime.UtcNow;
        }
        else
        {
            // 后续 iter：每 5 秒一条 Debug 进度
            var now = DateTime.UtcNow;
            if (_focusRecoveryProgressLastLoggedAt is { } last
                && (now - last) >= TimeSpan.FromSeconds(5))
            {
                var elapsedSec = _focusRecoveryFirstLossAt is { } firstLossAt
                    ? (int)(now - firstLossAt).TotalSeconds
                    : 0;
                Logger.LogDebug(
                    "[FocusRecovery] 仍在抢焦点... 已等待 {Elapsed}s, decision={Decision}",
                    elapsedSec, decision);
                _focusRecoveryProgressLastLoggedAt = now;
            }
        }

        if (decision == FocusRecoveryDecision.TryRestoreIconic)
        {
            if (User32.IsWindow(gameHandle))
            {
                _ = User32.ShowWindow(gameHandle, ShowWindowCommand.SW_RESTORE);
            }
        }
        else // TryFocus
        {
            SystemControl.FocusWindow(gameHandle);
        }

        // 单步：不 sleep 不循环，调用方控制节奏。
        // - Sleep/Delay/CheckAndSleep 路径：每次调用触发一次（NewRetry.Do 1s 节奏由外层重试驱动）
        // - CaptureGameImage 30s 等待循环：每 iter（200ms）顶部触发一次
        // 见 spec focus-recovery-driven-by-capture-loop / bugfix.md §4 EB-1
    }
}
