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
                }, ct, VerifyOwnership);
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
