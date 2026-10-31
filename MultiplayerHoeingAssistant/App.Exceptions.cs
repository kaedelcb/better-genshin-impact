using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Helpers;

namespace MultiplayerHoeingAssistant;

public partial class App
{
    private readonly UiExceptionReporter _uiExceptionReporter;
    private readonly WindowShowRecovery _windowShowRecovery;
    private bool _showRequested;

    public App()
    {
        _uiExceptionReporter = new UiExceptionReporter(RuntimeLog.WriteLine, QueueExceptionNotice);
        _windowShowRecovery = new WindowShowRecovery(
            action => Dispatcher.BeginInvoke(DispatcherPriority.Background, action),
            (error, outcome) => _uiExceptionReporter.Report(error, outcome), RuntimeLog.WriteLine);

        // 在启动监听线程、创建配置/窗口之前接管异常；致命出口只同步记日志，不调度 UI。
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            RuntimeLog.WriteException("AppDomain", args.ExceptionObject, args.IsTerminating,
                args.IsTerminating
                    ? "进程即将终止；未恢复；不弹窗、不重放业务、不清除持久执行意图"
                    : "未处理异常；未自动恢复");

        DispatcherUnhandledException += (_, args) =>
        {
            if (RuntimeLog.IsCritical(args.Exception))
            {
                RuntimeLog.WriteException("Dispatcher", args.Exception, true,
                    "严重异常，不能安全继续；交回运行时终止");
                args.Handled = false;
                return;
            }

            // 捕获只结束失败回调，不代表该操作成功，也不在此猜测/重建未知窗口。
            args.Handled = true;
            _uiExceptionReporter.Report(args.Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            RuntimeLog.WriteException("TaskScheduler", args.Exception, false,
                "后台任务已失败；未自动重放；通知发生于回收时，非原故障时间");
            args.SetObserved();
            QueueExceptionNotice("[后台任务异常] 任务已失败，未自动重试；详情已记录到运行日志。");
        };
    }

    private void QueueExceptionNotice(string message)
    {
        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try { _mainViewModel?.AddLog(message); }
                catch (Exception error) when (!RuntimeLog.IsCritical(error))
                {
                    // 不递归送回 UI 通知，原始异常已经同步落盘。
                    RuntimeLog.WriteException("异常通知", error, false, "界面提示失败；仅保留日志");
                }
            }));
        }
        catch (Exception error) when (!RuntimeLog.IsCritical(error))
        {
            RuntimeLog.WriteException("异常通知调度", error, false, "未能显示非阻塞提示；仅保留日志");
        }
    }

    private void CancelWindowShow()
    {
        _showRequested = false;
        _windowShowRecovery.Cancel();
    }

    private void ShowMainWindowSafely()
    {
        var window = _mainWindow;
        if (window == null) return;
        _showRequested = true;
        _windowShowRecovery.Show(() =>
        {
            window.Show();
            window.ShowInTaskbar = true;
            window.WindowState = WindowState.Normal;
            window.Activate();
            // Show 返回不等于恢复成功，至少要确认窗口已可见并有真实显示源。
            if (!window.IsVisible
                || PresentationSource.FromVisual(window)?.CompositionTarget?.RootVisual != window)
                throw new InvalidOperationException("显示调用结束，但主窗口尚未连接到显示源。");
        }, () => _showRequested && ReferenceEquals(_mainWindow, window)
            && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished);
    }
}
