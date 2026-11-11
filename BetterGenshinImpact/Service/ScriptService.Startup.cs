using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Service.Execution;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Service;

public partial class ScriptService
{
    /// <summary>
    /// 未就绪一段时间后先尝试一次"返回主界面"（Esc 关闭设置、地图、弹窗等页面），再继续检测。
    /// </summary>
    private static async Task SoftRecoverToMainUiAsync(CancellationToken ct, Action verifyOwnership)
    {
        verifyOwnership();
        if (!SystemControl.IsGenshinImpactActive())
        {
            SystemControl.ActivateWindow();
            await Task.Delay(500, ct);
        }
        TaskControl.Logger.LogInformation("联机组队阶段游戏仍未就绪，尝试返回主界面一次");
        using var pause = TaskTriggerDispatcher.Instance().PauseForInference();
        verifyOwnership();
        await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(ct);
    }

    /// <summary>
    /// 联机组队阶段游戏长时间进不了主界面：关闭游戏并按 1920x1080 窗口化参数重启一次。
    /// 返回 false 表示没有重启（取不到启动路径等），游戏保持原状。
    /// </summary>
    private static async Task<bool> RestartGameForStartupAsync(GameRuntimeService runtime,
        BetterGenshinImpact.Core.Config.GenshinStartConfig cfg, CancellationToken ct, Action verifyOwnership)
    {
        // 先取启动路径，取不到就不关游戏，避免关了拉不起来。
        string? exePath = null;
        try
        {
            if (runtime.Current?.Window is BetterGenshinImpact.GameTask.Runtime.Win32.Win32GameWindow w)
            {
                using var p = System.Diagnostics.Process.GetProcessById(w.ProcessId);
                exePath = p.MainModule?.FileName;
            }
        }
        catch (Exception ex)
        {
            TaskControl.Logger.LogDebug(ex, "读取游戏进程路径失败，改用配置的安装路径");
        }
        if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath)) exePath = cfg.InstallPath;
        if (string.IsNullOrEmpty(exePath) || !System.IO.File.Exists(exePath))
        {
            TaskControl.Logger.LogWarning("联机组队阶段游戏长时间未就绪，但无法确定游戏启动路径，未重启游戏");
            return false;
        }

        TaskControl.Logger.LogWarning("联机组队阶段游戏 {Seconds} 秒仍未进入主界面，按 1920x1080 窗口化参数重启游戏一次（原因：卡在登录/弹窗页面或分辨率不符）",
            cfg.MultiplayerRestartGameAfterSeconds);
        // 不取消当前任务令牌：调用者自己就是等待中的启动任务。
        await runtime.StopAsync(cancelRunningTasks: false);
        verifyOwnership();
        runtime.CloseGame();
        // 等旧进程完全退出再拉起新的，避免二次拉起。
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (SystemControl.FindGenshinImpactHandle() != IntPtr.Zero && sw.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(500, ct);
        }
        if (SystemControl.FindGenshinImpactHandle() != IntPtr.Zero)
        {
            TaskControl.Logger.LogWarning("旧游戏进程未能退出，放弃重启");
            return false;
        }
        verifyOwnership();
        var hWnd = await SystemControl.StartFromLocalAsync(exePath, forceStandardResolution: true);
        if (hWnd == IntPtr.Zero) return false;
        verifyOwnership();
        return await runtime.StartAsync(ct);
    }

    public static async Task<TaskRunResult> StartGameTask(bool waitForMainUi = true)
    {
        var scope = ExecutionScope.Current;
        var stopVersion = ExecutionScope.StopVersionNow;
        var context = CancellationContext.Instance;
        // 地图编辑器可能尚未创建任务上下文；普通任务在调用前已经 Set()。
        var taskToken = context.IsDisposed ? CancellationToken.None : context.Token;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(
            taskToken, scope?.Token ?? CancellationToken.None);
        var ct = lifetime.Token;
        void VerifyOwnership()
        {
            ct.ThrowIfCancellationRequested();
            scope?.ThrowIfStopped();
            if (stopVersion != ExecutionScope.StopVersionNow)
                throw new OperationCanceledException("启动等待已被停止", ct);
        }

        try
        {
            var runtime = App.GetService<GameRuntimeService>()!;
            var waitingLogged = false;
            // 只在联机锄地批次（组队阶段）启用；单人任务和手动任务保持原行为。
            var restartCfg = waitForMainUi && BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope.IsActive
                             && TaskContext.Instance().Config.GenshinStartConfig is { RestartGameWhenNotReadyInMultiplayer: true } cfg
                ? cfg : null;
            await GameTaskStartupGate.PrepareAsync(waitForMainUi,
                token => runtime.IsRunning ? Task.FromResult(true) : runtime.StartAsync(token),
                token =>
                {
                    // 与实时自动开门逐帧互斥，避免两边使用同一旧画面同时点击。
                    // 每帧结束即释放，保留现有 B 服登录、适龄提示等实时处理。
                    using var pause = TaskTriggerDispatcher.Instance().PauseForInference();
                    VerifyOwnership();
                    if (!runtime.IsRunning || !TaskContext.Instance().IsInitialized) return false;
                    using var frame = TaskControl.CaptureToRectArea();
                    var ready = CheckStartupScreen(frame, VerifyOwnership);
                    if (!ready && !waitingLogged)
                    {
                        waitingLogged = true;
                        TaskControl.Logger.LogInformation("游戏尚未就绪，等待登录、加载和领奖完成后执行任务（最长5分钟，可随时停止）");
                    }
                    return ready;
                }, ct, VerifyOwnership,
                restartGame: restartCfg == null ? null : token => RestartGameForStartupAsync(runtime, restartCfg, token, VerifyOwnership),
                restartAfter: restartCfg == null ? null : TimeSpan.FromSeconds(Math.Max(30, restartCfg.MultiplayerRestartGameAfterSeconds)),
                softRecover: restartCfg == null ? null : token => SoftRecoverToMainUiAsync(token, VerifyOwnership),
                softRecoverAfter: restartCfg == null ? null : TimeSpan.FromSeconds(Math.Min(60, Math.Max(30, restartCfg.MultiplayerRestartGameAfterSeconds) / 2)));
            VerifyOwnership();
            return TaskRunResult.Ran;
        }
        catch (OperationCanceledException)
        {
            var result = scope?.Result == TaskRunResult.Preempted ? TaskRunResult.Preempted : TaskRunResult.Cancelled;
            scope?.Observe(result);
            TaskControl.Logger.LogInformation("启动等待已取消，未开始执行任务");
            return result;
        }
        catch (Exception ex)
        {
            scope?.Observe(TaskRunResult.Failed);
            TaskControl.Logger.LogError(ex, "游戏启动准备失败，未开始执行任务：{Message}", ex.Message);
            return TaskRunResult.Failed;
        }
    }

    /// <summary>
    /// 外部前置（账号核对等）发现游戏刚由 BGI 冷启动时调用：沿用任务启动同一套就绪判断，
    /// 处理登录/领奖界面并等待游戏进入可操作界面（最长 5 分钟，跟随调用方令牌取消）。
    /// </summary>
    internal static Task WaitColdStartedGameReadyAsync(CancellationToken ct)
    {
        var runtime = App.GetService<GameRuntimeService>()!;
        var waitingLogged = false;
        return GameTaskStartupGate.PrepareAsync(true,
            token => runtime.IsRunning ? Task.FromResult(true) : runtime.StartAsync(token),
            token =>
            {
                using var pause = TaskTriggerDispatcher.Instance().PauseForInference();
                token.ThrowIfCancellationRequested();
                if (!runtime.IsRunning || !TaskContext.Instance().IsInitialized) return false;
                using var frame = TaskControl.CaptureToRectArea();
                var ready = CheckStartupScreen(frame, token.ThrowIfCancellationRequested);
                if (!ready && !waitingLogged)
                {
                    waitingLogged = true;
                    TaskControl.Logger.LogInformation("游戏刚启动，等待登录、加载和领奖完成后再核对账号（最长5分钟，可随时停止）");
                }
                return ready;
            }, ct);
    }

    private static bool CheckStartupScreen(ImageRegion frame, Action verifyOwnership)
    {
        // 沿用 UID 登录准备的识别资源；先处理登录/领奖遮挡，再判断原有的游戏内就绪界面。
        using var chooseEnter = frame.Find(RecognitionAssets.Get("GameLoading", "ChooseEnterGame", frame));
        using var enterGame = frame.Find(RecognitionAssets.Get("GameLoading", "EnterGame", frame));
        using var primogem = frame.Find(ElementRecognition.Get("Primogem", frame));
        Action? click = null;
        string? screenName = null;
        if (chooseEnter.IsExist())
        {
            click = () => chooseEnter.Click();
            screenName = "进入游戏确认";
        }
        else if (enterGame.IsExist())
        {
            click = () => enterGame.Click();
            screenName = "登录界面";
        }
        else if (Bv.IsInBlessingOfTheWelkinMoon(frame) || primogem.IsExist())
        {
            click = () => GameCaptureRegion.GameRegion1080PPosClick(100, 100);
            screenName = "月卡或原石领取界面";
        }

        if (click is null && (Bv.IsInMainUi(frame) || Bv.IsInAnyClosableUi(frame) || Bv.IsInDomain(frame)))
            return true;

        verifyOwnership();
        if (!SystemControl.IsGenshinImpactActive())
        {
            SystemControl.ActivateWindow();
            return false; // 激活窗口后等待新帧，不能继续使用激活前的识别结果点击。
        }
        if (click is not null)
        {
            verifyOwnership();
            click();
            TaskControl.Logger.LogInformation("任务启动前：识别到{Screen}，点击后等待游戏就绪", screenName);
        }
        // 未识别的画面只等待，不对加载画面盲按 Esc/Enter。
        return false;
    }
}
