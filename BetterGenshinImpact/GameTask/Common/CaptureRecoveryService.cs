using System;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.Common;

// =====================================================================
// 截图失败恢复域（E3）：决策表 + 恢复循环执行体 + 截图暂停信号位，同功能域单文件。
// 独立理由：唯一消费方 TaskControl.cs 是与公版同名的高冲突文件——按 G4.3「决策类单点引用
// 应并入执行体所在文件」收拢为单文件；恢复循环自 TaskControl.CaptureGameImage 逐 token
// 搬入（含 EB-2 信号位），使 TaskControl 保持接近公版形态（PR 时公版侧仅留转发 stub）。
// 恢复循环每 iter 驱动一次焦点恢复（WindowFocusRecoveryService，E2 域），两域仅此一点耦合。
// 见 spec capture-failure-suspend-signal / graphics-capture-session-auto-restart /
// focus-lost-minimized-capture-busyloop-crash-fix
// =====================================================================

/// <summary>
/// 截图重试决策枚举。
///
/// **References**: spec capture-failure-suspend-signal / bugfix.md §5 D1-D6 / design.md §2.1
/// </summary>
public enum CaptureRetryDecision
{
    /// <summary>当次 Capture() 已返回非 null Mat，调用方直接 return。</summary>
    ReturnImage,

    /// <summary>仍在 MaxRecoveryWait 时长内，应继续 Sleep(RetryDelay) 后重试。</summary>
    RetryAfterDelay,

    /// <summary>已达 MaxRecoveryWait 时长，应放弃（调用方 SHALL throw RetryException）。</summary>
    Abandon,
}

/// <summary>
/// 独立理由：同文件内由 CaptureRecoveryService（执行体）单一消费，按 G4.3 并入执行体所在文件。
/// 截图重试决策表（纯函数，PBT 友好）。
/// 不持有任何外部依赖（无 logger / GameCapture / Mat）。
///
/// 决策基于 <c>elapsed</c>（自首次失败以来的等待时长）而非"重试次数"：
/// 30 秒恢复窗口 + 200 ms 重试间隔覆盖典型场景（UAC / Win+L / 切桌面 / 短弹窗）。
/// </summary>
public static class CaptureRetryDecisions
{
    /// <summary>D1：恢复等待最大时长（30 秒）。覆盖 UAC / Win+L / 切桌面 / 长弹窗。</summary>
    public static readonly TimeSpan MaxRecoveryWait = TimeSpan.FromSeconds(30);

    /// <summary>D2：每次重试前的等待间隔（200 ms）。30 s / 200 ms = 最多 ~150 次 Capture() 机会。</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    /// <summary>D3：恢复期内进度 Debug 日志间隔（5 秒）。30 s 最多 6 条 Debug，不刷屏。</summary>
    public static readonly TimeSpan ProgressLogInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Capture 持续 null 多久后触发 capture session 重启（2s）。
    /// 见 spec graphics-capture-session-auto-restart / bugfix.md §6 D2。
    /// </summary>
    public static readonly TimeSpan CaptureRestartThreshold = TimeSpan.FromSeconds(2);

    /// <summary>主决策：根据 elapsed 路由到 ReturnImage / RetryAfterDelay / Abandon。</summary>
    public static CaptureRetryDecision Decide(bool lastAttemptSucceeded, TimeSpan elapsed)
    {
        if (lastAttemptSucceeded) return CaptureRetryDecision.ReturnImage;
        if (elapsed >= MaxRecoveryWait) return CaptureRetryDecision.Abandon;
        return CaptureRetryDecision.RetryAfterDelay;
    }

    /// <summary>是否到了打进度 Debug 的时机（每 ProgressLogInterval 一次）。</summary>
    public static bool ShouldLogProgress(TimeSpan elapsed, TimeSpan lastLoggedAt)
    {
        return (elapsed - lastLoggedAt) >= ProgressLogInterval;
    }
}

/// <summary>
/// 独立理由：唯一消费方 TaskControl.CaptureGameImage（转发 stub）——执行体逐 token 搬入本文件，
/// 使 TaskControl 保持接近公版形态（G4.3）。见文件头说明。
/// 截图失败恢复执行体：30s 恢复等待循环 + 焦点恢复驱动 + capture session 重建 + 进度日志。
/// </summary>
public static class CaptureRecoveryService
{
    // 引用别名：保持搬移后的方法体与 TaskControl 原文逐 token 一致；
    // 日志 SourceContext 仍为 TaskControl，与搬移前日志输出逐字节一致。
    private static ILogger Logger => TaskControl.Logger;

    /// <summary>
    /// 截图暂停信号（spec capture-failure-suspend-signal / bugfix.md §2.2 EB-2，自 TaskControl 逐字搬入）。
    /// 当 <see cref="TaskControl.CaptureGameImage"/> 进入 30 秒恢复等待循环时为 true，
    /// 恢复或放弃后立即清零（finally 保证）。
    ///
    /// **重要**：仅用于其他模块"观测"——**不**加入 TrySuspend 的 while 循环条件，
    /// 避免与 CheckNetworkStatusAsync 形成递归嵌套（后者内部也调 <c>CaptureToRectArea</c>）。
    /// </summary>
    public static bool IsSuspendedByCapture { get; set; } = false;

    /// <summary>
    /// 截图失败恢复执行体（自 TaskControl.CaptureGameImage 第 1 次尝试之后的全部逻辑逐 token 搬入）：
    /// ≤ MaxRecoveryWait（30s）的等待循环，成功返回帧，超时抛 RetryException。
    /// 调用方：TaskControl.CaptureGameImage（第 1 次尝试失败后进入）。
    /// </summary>
    public static Mat RecoverAndCapture(IGameCapture? gameCapture)
    {
        // 进入恢复等待循环：≤ MaxRecoveryWait（30s）
        // 决议见 spec capture-failure-suspend-signal / bugfix.md §5 D1-D6
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var lastProgressLogAt = TimeSpan.Zero;
        bool sessionRestartAttempted = false;       // spec graphics-capture-session-auto-restart / D2

        Logger.LogWarning(
            "[CaptureGameImage] 截图失败，进入恢复等待（最长 {Max}s，每 {Interval}ms 重试）",
            (int)CaptureRetryDecisions.MaxRecoveryWait.TotalSeconds,
            (int)CaptureRetryDecisions.RetryDelay.TotalMilliseconds);

        // EB-2：设暂停信号供其他模块观测；finally 保证清零
        IsSuspendedByCapture = true;
        try
        {
            while (true)
            {
                // 用户已停止任务 → 立即抛 NormalEndException 退出 30s 等待循环
                // （spec focus-recovery-driven-by-capture-loop 漏掉的取消语义补丁）
                if (BetterGenshinImpact.Core.Script.CancellationContext.Instance.IsCancellationRequested)
                {
                    throw new NormalEndException("取消自动任务");
                }

                var decision = CaptureRetryDecisions.Decide(
                    lastAttemptSucceeded: false, elapsed: sw.Elapsed);

                if (decision == CaptureRetryDecision.Abandon)
                {
                    Logger.LogWarning(
                        "[CaptureGameImage] 等待 {Elapsed}s 后仍截图失败，放弃",
                        (int)sw.Elapsed.TotalSeconds);
                    // 文案保留以兼容 LogParse / 既有日志告警规则
                    throw new RetryException("尝试多次后,截图失败!");
                }

                // 关键：每次 iter 顶部驱动一次焦点恢复（单步），让主线程在等画面时也持续抢焦点。
                // 没有这一步，CheckAndActivateGameWindow 仅在 Sleep/Delay 调用栈触发，主线程
                // 进入本循环后焦点恢复永远不会被驱动 → 焦点抢不回 → 帧不来 → 30s 后 RetryException 终止任务。
                // 传 throwOnForegroundLost:false —— Cfg=Off 失焦/最小化时不抛异常、不抢焦点、直接 return，
                // 由本循环尾部 200ms Thread.Sleep 节流后重试（安静等画面回来），消除零延迟忙循环。
                // Cfg=On 时本参数不影响（走 FocusRecoveryDecisions 抢焦点路径）。
                // 决议见 spec focus-lost-minimized-capture-busyloop-crash-fix / bugfix.md §Resolved Decisions D-2。
                WindowFocusRecoveryService.CheckAndActivateGameWindow(throwOnForegroundLost: false);

                // elapsed ≥ 2s 且本轮未重启过 → 重建 capture session。
                // 应对 GraphicsCapture._captureItem.Closed 触发后 session 永久失效（Win11 反复最小化 race）。
                // 决议见 spec graphics-capture-session-auto-restart / bugfix.md §4 EB-2 / §6 D2。
                if (!sessionRestartAttempted
                    && sw.Elapsed >= CaptureRetryDecisions.CaptureRestartThreshold)
                {
                    Logger.LogWarning(
                        "[Capture] 截图持续失败 {Elapsed}s，重建 capture session",
                        (int)sw.Elapsed.TotalSeconds);
                    TaskTriggerDispatcher.Instance().RestartCapture();
                    sessionRestartAttempted = true;
                }

                // RetryAfterDelay：等 200ms 再试。
                Thread.Sleep((int)CaptureRetryDecisions.RetryDelay.TotalMilliseconds);

                // 搬移适配：原方法体外层声明的 image 留在 TaskControl stub，循环内改为局部声明（行为等价：
                // 进入循环前外层 image 已判定为 null，循环内先赋值后读取）。
                var image = gameCapture?.Capture();

                if (image != null)
                {
                    Logger.LogWarning(
                        "[CaptureGameImage] 截图恢复，等待耗时 {Elapsed}ms",
                        (int)sw.Elapsed.TotalMilliseconds);
                    return image.Frame;
                }

                // D3：每 5s 打一条 Debug 进度，避免刷屏
                if (CaptureRetryDecisions.ShouldLogProgress(sw.Elapsed, lastProgressLogAt))
                {
                    Logger.LogDebug(
                        "[CaptureGameImage] 仍在等待画面恢复... 已等待 {Elapsed}s / {Max}s",
                        (int)sw.Elapsed.TotalSeconds,
                        (int)CaptureRetryDecisions.MaxRecoveryWait.TotalSeconds);
                    lastProgressLogAt = sw.Elapsed;
                }
            }
        }
        finally
        {
            IsSuspendedByCapture = false;
        }
    }
}
