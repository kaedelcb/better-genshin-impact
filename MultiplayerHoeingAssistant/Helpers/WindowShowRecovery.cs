using System.Diagnostics;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// 仅用于无业务副作用的窗口显示。全局异常出口不能猜测出错对象，也不能重放任务。
/// 一次请求最多延后一轮重试；隐藏/退出会撤销重试，连续失败后不再自动重试。
/// </summary>
internal sealed class WindowShowRecovery(
    Action<Action> defer,
    Action<Exception, string> reportFailure,
    Action<string> writeLog)
{
    private long _generation;
    private bool _busy;
    private bool _retryExhausted;

    internal void Cancel()
    {
        if (_busy)
            writeLog("[主窗口恢复] 显示请求已撤销，取消待处理重试；未恢复。");
        _generation++;
        _busy = false;
    }

    internal void Show(Action show, Func<bool> canShow)
    {
        if (_busy || !canShow()) return;
        _busy = true;
        var generation = ++_generation;
        try
        {
            show();
            _retryExhausted = false;
            _busy = false;
        }
        catch (Exception error) when (!RuntimeLog.IsCritical(error))
        {
            // 只接受框架实际抛出的挂载冲突，不依赖中英文错误文案。
            if (_retryExhausted || !IsVisualTargetConflict(error))
            {
                _busy = false;
                reportFailure(error, "主窗口显示失败；未自动重试");
                return;
            }

            writeLog(RuntimeLog.FormatException("主窗口显示", error, false,
                "显示失败，等待下一轮 UI 调度重试一次；尚未恢复"));
            _retryExhausted = true;
            try
            {
                defer(() => Retry(generation, show, canShow));
            }
            catch (Exception schedulingError) when (!RuntimeLog.IsCritical(schedulingError))
            {
                _busy = false;
                reportFailure(schedulingError, "主窗口恢复调度失败；未恢复");
            }
        }
    }

    private void Retry(long generation, Action show, Func<bool> canShow)
    {
        if (generation != _generation) return;
        try
        {
            if (!canShow())
            {
                writeLog("[主窗口恢复] 显示请求已撤销或程序正在关闭，取消重试；未恢复。");
                return;
            }
            show();
            _retryExhausted = false;
            writeLog("[主窗口恢复] 一次延后重试完成，窗口已显示；未重放业务操作。");
        }
        catch (Exception error) when (!RuntimeLog.IsCritical(error))
        {
            reportFailure(error, "主窗口重试仍失败；已停止自动重试，未恢复");
        }
        finally { _busy = false; }
    }

    internal static bool IsVisualTargetConflict(Exception error) =>
        error is ArgumentException && new StackTrace(error).GetFrames()?.Any(frame =>
            frame.GetMethod() is { Name: "SetRootVisual", DeclaringType.FullName:
                "System.Windows.Media.CompositionTarget" }) == true;
}
