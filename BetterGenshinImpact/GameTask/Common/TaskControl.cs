using System;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Model.Area;
using Fischless.GameCapture;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using Vanara.PInvoke;
using System.Net.NetworkInformation;
using BetterGenshinImpact.GameTask.Common.Job;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using System.Linq;
using BetterGenshinImpact.Core.Recognition;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.Core.Simulator.Extensions;

namespace BetterGenshinImpact.GameTask.Common;

public class TaskControl
{
    public static ILogger Logger { get; } = App.GetLogger<TaskControl>();

    public static readonly SemaphoreSlim TaskSemaphore = new(1, 1);
    
    private static DateTime _lastCheckTime = DateTime.MinValue;
    private static DateTime _lastCheckTimeEnter = DateTime.MinValue;
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(TaskContext.Instance().Config.OtherConfig.NetworkDetectionInterval);
    private static readonly TimeSpan CheckIntervalWin = TimeSpan.FromSeconds(30);
    // 网络检查门控（spec network-check-ping-concurrent-reuse-crash-fix）：
    // Wait(0) 非阻塞抢门，保证同一时刻只有一个 CheckNetworkStatusAsync 执行者，
    // 消除对 Ping 实例的并发重入以及 _lastCheckTime/_networkFailureCount/IsSuspendedByNetwork 的并发竞态。
    private static readonly SemaphoreSlim _networkCheckGate = new(1, 1);
    // 恢复单飞门控（spec network-recovery-statemachine-never-exits-suspend-fix / D-2）：
    // 与 _networkCheckGate（5s 检查门控）区分。Wait(0) 抢门标记"恢复进行中"，
    // 抢不到说明已有一个 NetworkRecovery.Start 在跑 → 本次跳过，不重启、不重置 RecoveryNetworkDone。
    private static readonly SemaphoreSlim _networkRecoveryGate = new(1, 1);
    private static readonly bool NetworkDetectionConfig = TaskContext.Instance().Config.OtherConfig.NetworkDetectionConfig;
    private static int _networkFailureCount = 0;

    // 网络恢复自锁修复（spec network-recovery-statemachine-never-exits-suspend-fix）：
    // NetworkRecovery.Start 通过 WaitForElement* → TaskControl.Delay → NewRetry.Do(() => TrySuspend())
    // 重入 TrySuspend；此时 IsSuspendedByNetwork 仍为 true，重入的 TrySuspend 会在 while 循环里
    // 无限 Thread.Sleep 空转不返回，导致 Delay 不返回、Start 永远走不到结尾清除标志 => 死锁。
    // 用 AsyncLocal 标记"当前执行栈正处于网络恢复中"（能随 await 流转到 Start 内部所有 Delay），
    // 恢复栈内的 TrySuspend 不因 IsSuspendedByNetwork 阻塞，使恢复得以跑到终点解除暂停。
    // 顶层任务循环不设此标志，继续等待直到恢复真正完成。
    private static readonly AsyncLocal<bool> _inNetworkRecovery = new();

    // 焦点恢复跨调用状态（spec focus-recovery-no-budget-limit / bugfix.md §4 EB-5）
    // 仅追踪"焦点持续丢失期间是否已打首次 Warning"和"上次进度日志时刻"，
    // 焦点回原神时清零。无预算追踪、无节流戳（决议已删除 N/T/Cooldown）。
    private static bool _focusRecoveryWarningEmitted;
    private static DateTime? _focusRecoveryProgressLastLoggedAt;
    private static DateTime? _focusRecoveryFirstLossAt;

    private static RecognitionObject GetConfirmRa(bool isOcrMatch = false,params string[] targetText)
    {
        var screenArea = CaptureToRectArea();
        var x = (int)(screenArea.Width * 0.3);
        var y = (int)(screenArea.Height * 0.1);
        var width = (int)(screenArea.Width * 0.65);
        var height = (int)(screenArea.Height * 0.87);
        
        return isOcrMatch ? RecognitionObject.OcrMatch(x, y, width, height, targetText) : 
            RecognitionObject.Ocr(x, y, width, height);
    }
    
    public static bool IsSuspendedByNetwork { get; set; } = false;
    
    public static bool IsSuspendedByWindow { get; set; } = false;

    /// <summary>
    /// 截图暂停信号（spec capture-failure-suspend-signal / bugfix.md §2.2 EB-2）。
    /// 当 <see cref="CaptureGameImage"/> 进入 30 秒恢复等待循环时为 true，
    /// 恢复或放弃后立即清零（finally 保证）。
    ///
    /// **重要**：仅用于其他模块"观测"——**不**加入 <see cref="TrySuspend"/> 的 while 循环条件，
    /// 避免与 <see cref="CheckNetworkStatusAsync"/> 形成递归嵌套（后者内部也调 <c>CaptureToRectArea</c>）。
    /// </summary>
    public static bool IsSuspendedByCapture { get; set; } = false;
    
    private static bool _isBless = false;

    private static async Task CheckNetworkStatusAsync()
    {
        // 门控：非阻塞抢门。抢不到说明已有一个检查在跑，本次立即返回（等价于被节流跳过，
        // 符合 §3.2 preservation：同一时刻只跑一个，其余跳过）。
        if (!_networkCheckGate.Wait(0))
        {
            return;
        }

        try
        {
            if (DateTime.UtcNow - _lastCheckTime < CheckInterval)
            {
                if (DateTime.UtcNow - _lastCheckTimeEnter > CheckIntervalWin)
                { 
                    _lastCheckTimeEnter = DateTime.UtcNow;
                    using var qq = CaptureToRectArea();
                    using var okRa = qq.Find(AutoFightAssets.Get(qq).ConfirmRaZ);
                    using var enterRa = qq.Find(AutoWoodAssets.Instance.ExitSwitchRo);
                    //如果现在是4点到4点5分内
                    if (DateTime.UtcNow.Hour == 4 && DateTime.UtcNow.Minute >= 0 && DateTime.UtcNow.Minute < 3)
                    {
                        if ((Bv.IsInBlessingOfTheWelkinMoon(qq)) && !_isBless)   
                        {
                            try
                            {
                                Logger.LogInformation("空月任务4点检测执行");
                                _isBless = true;
                                new BlessingOfTheWelkinMoonTask().Start(CancellationToken.None).Wait(10000);
                            }
                            catch (TaskCanceledException)
                            {
                                Logger.LogWarning("空月任务执行取消");
                            }
                            catch (TimeoutException)
                            {
                                Logger.LogWarning("空月任务执行超时");
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError(ex, "空月任务执行失败");
                            }
                            finally
                            {
                                Logger.LogDebug("空月任务4点检测执行完毕");
                            }
                        }
                    }
                    
                    if (okRa.IsExist()|| enterRa.IsExist())
                    {
                        var enter = qq.FindMulti(GetConfirmRa());
                        using var enterDone = enter.FirstOrDefault(t =>
                            Regex.IsMatch(t.Text, "连接已断开") || Regex.IsMatch(t.Text, "点击进入") || Regex.IsMatch(t.Text, "更新通知"));
                        if (enterDone != null)
                        {
                            IsSuspendedByWindow = true;
                            Logger.LogWarning("点击: {enterDone.Text}",enterDone.Text);
                            if(enterRa.IsExist())enterDone.Click();
                        }
                        else
                        {
                            return;
                        }
                    }
                    
                }
                else
                {
                    return;
                }
            }
            
            _lastCheckTime = DateTime.UtcNow;

            var isSuspend = false; 
            try
            {
                using var ping = new Ping();
                var reply = ping.Send(TaskContext.Instance().Config.OtherConfig.NetworkDetectionUrl);
                isSuspend = reply.Status != IPStatus.Success;
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "网络状态检查：错误");
                isSuspend = true;
            }

            // 恢复触发逻辑移出 ping 的 try/catch：无论 ping.Send 成功还是抛异常（如 DNS 解析失败），
            // 只要处于挂起态就驱动 NetworkRecovery.Start，使真实网络恢复后能自愈解除挂起。
            // spec network-recovery-skipped-on-ping-dns-exception-fix
            // 用独立 try/catch/finally 包裹，保持原语义：
            //  - 恢复块自身抛出的异常同样被吞并记为失败（原来恢复块在 ping 的 try 内，异常由同一 catch(Exception) 捕获）；
            //  - 失败计数/归零逻辑放在 finally，保证无论恢复块是否抛异常都执行（等价原内层 finally）。
            var enteredRecovery = false;
            var recoverySucceeded = false;
            try
            {
                if (NetworkRecoveryDecisions.ShouldEnterRecovery(IsSuspendedByNetwork, IsSuspendedByWindow))
                {
                    enteredRecovery = true;
                    Logger.LogWarning(IsSuspendedByWindow ? "窗口弹窗状态恢复中..." : "网络恢复中...");

                    // 单飞门控（D-2）：Wait(0) 抢恢复门。抢不到 => 已有一个 Start 在跑 => 本次跳过，
                    // 绝不重复启动、绝不触发 Start 开头的 RecoveryNetworkDone=false 重置。
                    if (NetworkRecoveryDecisions.ShouldStartNewRecovery(recoveryInProgress: !_networkRecoveryGate.Wait(0)))
                    {
                        try
                        {
                            // D-1：不再 .Wait(10000) 截断，await 让 Start 跑到自身终点
                            // （内部 WaitForElementAppear(PaimonMenuRo, ..., 60, 1000) 60s 上限兜底）。
                            // 关键：置 AsyncLocal 标志，使 Start 内部所有经 Delay 重入的 TrySuspend
                            // 不因 IsSuspendedByNetwork 阻塞（否则恢复流程自锁，永远走不到结尾清除标志）。
                            _inNetworkRecovery.Value = true;
                            await NetworkRecovery.Start(CancellationToken.None);
                        }
                        finally
                        {
                            _inNetworkRecovery.Value = false;
                            _networkRecoveryGate.Release();
                        }
                    }
                    else
                    {
                        Logger.LogDebug("网络恢复已在进行中，跳过重复启动");
                    }

                    // D-3：解除权威信号来自恢复流程成功（RecoveryNetworkDone），与 ping 解耦。
                    recoverySucceeded = NetworkRecovery.RecoveryNetworkDone;
                }
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "网络状态检查：错误");
            }
            finally
            {
                var pingFailed = isSuspend; // isSuspend 此时即首个 ping(www.baidu.com) 是否失败/抛异常

                if (NetworkRecoveryDecisions.ShouldClearNetworkSuspend(recoverySucceeded, enteredRecovery, pingFailed))
                {
                    _networkFailureCount = 0;
                    IsSuspendedByNetwork = false;
                }
                else if (NetworkRecoveryDecisions.ShouldCountPingFailure(recoverySucceeded, enteredRecovery, pingFailed))
                {
                    _networkFailureCount++;
                    if (_networkFailureCount >= 3)
                    {
                        try
                        {
                            using var ping2 = new Ping();
                            var reply2 = ping2.Send("www.qq.com");
                            if (reply2.Status != IPStatus.Success)
                            {
                                IsSuspendedByNetwork = true;
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "网络状态检查：错误");
                            IsSuspendedByNetwork = true;
                        }
                    }
                }
                // 其余情形（进入恢复但未成功且非"未进入+ping失败"）：静默保持现状，
                // 不清除也不累加，等下一轮单飞恢复继续推进。
            }
            return;
        }
        finally
        {
            // 无论走哪个 return（节流跳过 / 窗口分支 / 正常完成 / 异常），都保证释放门控，绝不死锁。
            _networkCheckGate.Release();
        }
    }

    public static void CheckAndSleep(int millisecondsTimeout)
    {
        TrySuspend();
        CheckAndActivateGameWindow();
        Thread.Sleep(millisecondsTimeout);
    }

    public static void Sleep(int millisecondsTimeout)
    {
        NewRetry.Do(() =>
        {
            TrySuspend();
            CheckAndActivateGameWindow();
        }, TimeSpan.FromSeconds(1), 100);
        Thread.Sleep(millisecondsTimeout);
    }

    private static bool IsKeyPressed(User32.VK key)
    {
        // 获取按键状态
        var state = User32.GetAsyncKeyState((int)key);

        // 检查高位是否为 1（表示按键被按下）
        return (state & 0x8000) != 0;
    }

    public static void TrySuspend()
    {
        if (NetworkDetectionConfig)Task.Run(CheckNetworkStatusAsync);
        // 恢复自锁修复：若当前执行栈正处于网络恢复中（AsyncLocal 标志），本次 TrySuspend
        // 不把"网络暂停"计入阻塞条件——否则恢复流程的 Delay 会在此永久空转，导致恢复自锁。
        // 手动暂停（RunnerContext.Instance.IsSuspend）仍然生效。
        var networkBlocking = IsSuspendedByNetwork && !_inNetworkRecovery.Value;
        var first = true;
        //此处为了记录最开始的暂停状态
        var isSuspend = RunnerContext.Instance.IsSuspend || networkBlocking;
        while (RunnerContext.Instance.IsSuspend || (IsSuspendedByNetwork && !_inNetworkRecovery.Value))
        {
            // 仅在用户手动暂停时清除网络暂停标志并置 RecoveryNetworkDone=true（手动暂停无需走网络恢复流程）。
            // 修复前此行缺大括号，导致 NetworkRecovery.RecoveryNetworkDone = true 每秒无条件执行，
            // 短路了 NetworkRecovery.Start 的实际恢复动作，使网络恢复后无法解除暂停。
            // spec network-recovery-done-flag-prematurely-set-fix
            if (RunnerContext.Instance.IsSuspend)
            {
                IsSuspendedByNetwork = false;
                NetworkRecovery.RecoveryNetworkDone = true;
            }
            if (first)
            {
                RunnerContext.Instance.StopAutoPick();
                //使快捷键本身释放
                Thread.Sleep(300);
                foreach (User32.VK key in Enum.GetValues(typeof(User32.VK)))
                {
                    // 检查键是否被按下
                    if (IsKeyPressed(key)) // 强制转换 VK 枚举为 int
                    {
                        Logger.LogWarning($"解除{key}的按下状态.");
                        Simulation.SendInput.Keyboard.KeyUp(key);
                    }
                }

                Logger.LogWarning(IsSuspendedByNetwork ? "网络检测失败触发暂停，等待解除" : "快捷键触发暂停，等待解除");
                foreach (var item in RunnerContext.Instance.SuspendableDictionary)
                {
                    item.Value.Suspend();
                }

                first = false;
            }

            if (IsSuspendedByNetwork)
            {
                CheckNetworkStatusAsync().Wait(1000, CancellationToken.None);
            }

            Thread.Sleep(1000);
        }

        //从暂停中解除
        if (isSuspend)
        {
            Logger.LogWarning("暂停已经解除");
            RunnerContext.Instance.ResumeAutoPick();
            foreach (var item in RunnerContext.Instance.SuspendableDictionary)
            {
                item.Value.Resume();
            }
        }
    }

    // throwOnForegroundLost：Cfg=Off 严格模式下，前台不是原神时是否抛 RetryException。
    //  - 默认 true = 现有行为，Sleep/Delay/CheckAndSleep 调用方不传参，逐字节不变
    //    （它们外层有 NewRetry.Do(..., 1s, 100) 节流，严格模式抛异常是用户预期）。
    //  - 仅 CaptureGameImage 恢复循环传 false：不抛异常、不抢焦点、直接 return，
    //    让循环继续走到尾部 200ms Thread.Sleep 节流后重试，从根因消除零延迟忙循环。
    // 决议见 spec focus-lost-minimized-capture-busyloop-crash-fix / bugfix.md §Resolved Decisions D-2。
    private static void CheckAndActivateGameWindow(bool throwOnForegroundLost = true)
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

    public static void Sleep(int millisecondsTimeout, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            throw new NormalEndException("取消自动任务");
        }

        if (millisecondsTimeout <= 0)
        {
            return;
        }

        NewRetry.Do(() =>
        {
            if (ct.IsCancellationRequested)
            {
                throw new NormalEndException("取消自动任务");
            }
            TrySuspend();
            CheckAndActivateGameWindow();
        }, TimeSpan.FromSeconds(1), 100);
        Thread.Sleep(millisecondsTimeout);
        if (ct.IsCancellationRequested)
        {
            throw new NormalEndException("取消自动任务");
        }
    }

    public static async Task Delay(int millisecondsTimeout, CancellationToken ct)
    {
        if (ct is { IsCancellationRequested: true })
        {
            throw new NormalEndException("取消自动任务");
        }

        if (millisecondsTimeout <= 0)
        {
            return;
        }

        NewRetry.Do(() =>
        {
            if (ct is { IsCancellationRequested: true })
            {
                throw new NormalEndException("取消自动任务");
            }
            TrySuspend();
            CheckAndActivateGameWindow();
        }, TimeSpan.FromSeconds(1), 100);
        await Task.Delay(millisecondsTimeout, ct);
        if (ct is { IsCancellationRequested: true })
        {
            throw new NormalEndException("取消自动任务");
        }
    }

    /// <summary>
    /// 模拟长按指定动作。使用 try/finally 块确保在任务被取消或发生异常时，按键也能安全释放，防止卡键。
    /// </summary>
    /// <param name="action">需要模拟的游戏动作（如元素战技、普通攻击等）</param>
    /// <param name="holdMs">长按持续的时间（毫秒）</param>
    /// <param name="ct">用于监控任务取消的取消令牌</param>
    public static async Task SimulateHoldActionAsync(GIActions action, int holdMs, CancellationToken ct)
    {
        try
        {
            Simulation.SendInput.SimulateAction(action, KeyType.KeyDown);
            await Delay(holdMs, ct);
        }
        finally
        {
            Simulation.SendInput.SimulateAction(action, KeyType.KeyUp);        
        }
    }

    /// <summary>
    /// 模拟长按元素战技（如万叶长E）。包含释放前摇、长按以及释放后的缓冲延时。
    /// </summary>
    /// <param name="holdMs">元素战技按住的时间（毫秒）</param>
    /// <param name="ct">用于监控任务取消的取消令牌</param>
    /// <param name="releaseLeftMouseBefore">是否在按下元素战技前先松开鼠标左键，避免输入冲突，默认 true</param>
    /// <param name="releaseLeftMouseDelayMs">松开鼠标左键后的缓冲时间（毫秒），默认 10ms</param>
    /// <param name="postKeyUpDelayMs">元素战技释放后的缓冲时间（毫秒），默认 50ms</param>
    public static async Task SimulateHoldElementalSkillAsync(
        int holdMs,
        CancellationToken ct,
        bool releaseLeftMouseBefore = true,
        int releaseLeftMouseDelayMs = 10,
        int postKeyUpDelayMs = 50)
    {
        if (releaseLeftMouseBefore)
        {
            Simulation.SendInput.Mouse.LeftButtonUp();
            await Delay(releaseLeftMouseDelayMs, ct);
        }

        await SimulateHoldActionAsync(GIActions.ElementalSkill, holdMs, ct);   
        await Delay(postKeyUpDelayMs, ct);
    }

    /// <summary>
    /// 模拟鼠标左键连续点击循环（如万叶长E后的下落攻击）。双层 try/finally 设计以确保无论在循环的哪个阶段发生取消或异常，鼠标左键都会被强制释放。
    /// </summary>
    /// <param name="repeatCount">需要循环点击的次数</param>
    /// <param name="ct">用于监控任务取消的取消令牌</param>
    /// <param name="preUpDelayMs">每次点击前，预先抬起左键后的缓冲延时（毫秒），默认 10ms</param>
    /// <param name="downHoldMs">鼠标左键按下的保持时间（毫秒），默认 35ms</param>
    /// <param name="postUpDelayMs">每次点击完成后的等待时间（毫秒），默认 50ms</param>
    public static async Task SimulateMouseLeftClickLoopAsync(
        int repeatCount,
        CancellationToken ct,
        int preUpDelayMs = 10,
        int downHoldMs = 35,
        int postUpDelayMs = 50)
    {
        try
        {
            for (var i = 0; i < repeatCount; i++)
            {
                Simulation.SendInput.Mouse.LeftButtonUp();
                await Delay(preUpDelayMs, ct);
                Simulation.SendInput.Mouse.LeftButtonDown();
                try
                {
                    await Delay(downHoldMs, ct);
                }
                finally
                {
                    Simulation.SendInput.Mouse.LeftButtonUp();
                }

                await Delay(postUpDelayMs, ct);
            }
        }
        finally
        {
            Simulation.SendInput.Mouse.LeftButtonUp();
        }
    }

    public static Mat CaptureGameImage(IGameCapture? gameCapture)
    {
        // 第 1 次尝试（正常路径，零延迟、零日志、零副作用）
        var _diagSw = System.Diagnostics.Stopwatch.GetTimestamp(); // 【诊断】
        var image = gameCapture?.Capture();
        // 【诊断】SoloTask(联机锄地等)取帧入口，记录耗时+画面指纹。与调度器共用同一诊断器。
        CaptureDiagnostics.NotifyCapture(System.Diagnostics.Stopwatch.GetElapsedTime(_diagSw).TotalMilliseconds, image?.Frame);
        if (image != null) return image.Frame;

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
                CheckAndActivateGameWindow(throwOnForegroundLost: false);

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

                image = gameCapture?.Capture();

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

    public static Mat? CaptureGameImageNoRetry(IGameCapture? gameCapture)
    {
        return gameCapture?.Capture()?.Frame;
    }

    /// <summary>
    /// 自动判断当前运行上下文中截图方式，并选择合适的截图方式返回
    /// </summary>
    /// <returns></returns>
    public static ImageRegion CaptureToRectArea(bool forceNew = false)
    {
        var image = CaptureGameImage(TaskTriggerDispatcher.GlobalGameCapture);
        var content = new CaptureContent(image, 0, 0);
        return content.CaptureRectArea;
    }
}
