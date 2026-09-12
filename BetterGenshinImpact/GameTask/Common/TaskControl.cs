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
    /// <summary>
    /// 薄转发：实现已整体搬入 WindowFocusRecoveryService（T1 批量收口 E2，方法体逐 token 一致搬移）。
    /// 签名不变 → Sleep/Delay/CheckAndSleep/CaptureGameImage 五处调用点零改动。
    /// </summary>
    private static void CheckAndActivateGameWindow(bool throwOnForegroundLost = true)
    {
        WindowFocusRecoveryService.CheckAndActivateGameWindow(throwOnForegroundLost);
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
        var image = gameCapture?.Capture();
        if (image != null) return image.Frame;

        // 截图失败恢复（30s 恢复窗 / 焦点驱动 / session 重建）整体在 CaptureRecoveryService
        // （新文件，决策+执行体+信号位同域单文件，PR 零冲突；见 G4.3）
        return CaptureRecoveryService.RecoverAndCapture(gameCapture);
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
