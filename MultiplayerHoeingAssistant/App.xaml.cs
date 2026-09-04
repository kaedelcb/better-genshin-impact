using System.Threading;
using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant;

public partial class App : Application
{
    /// <summary>
    /// 单例互斥体，按会话隔离：不同 Windows 用户会话的助手互不干扰。
    /// 不加 SessionId 过滤时，多用户下会话 B 的助手会阻止会话 A 的助手启动。
    /// </summary>
    private static readonly Mutex _instanceMutex = new(true,
        $"NexusBGI_InstanceMutex_Session{System.Diagnostics.Process.GetCurrentProcess().SessionId}");
    /// <summary>跨进程弹窗通知事件：第二个实例启动时触发，第一个实例收到后弹窗到前台。</summary>
    private static readonly EventWaitHandle _showWindowEvent = new(false, EventResetMode.AutoReset,
        $"NexusBGI_ShowWindowEvent_Session{System.Diagnostics.Process.GetCurrentProcess().SessionId}");
    private TaskbarIcon? _trayIcon;
    private bool _startMinimized;
    private MainWindow? _mainWindow;
    private MainViewModel? _mainViewModel;
    private AssistConfigManager? _configManager;
    private AssistConfig? _appConfig;
    private Timer? _bgiWatchTimer;

    protected override async void OnStartup(StartupEventArgs e)
    {
        // 单例检测：第二个实例时触发事件让第一个实例弹窗，然后退出
        if (!_instanceMutex.WaitOne(TimeSpan.Zero, true))
        {
            try
            {
                _showWindowEvent.Set();
            }
            catch
            {
                // 事件通知失败不影响进程退出
            }
            Shutdown();
            return;
        }

        // 启动后台线程监听跨进程弹窗事件（第二个实例触发时，把本窗口弹窗到前台）
        StartShowWindowEventListener();

        // 检测命令行参数
        _startMinimized = e.Args.Contains("--minimized");

        // 进程级 per-monitor DPI 感知
        DpiAwarenessController.EnsureDpiAware();

        // 加载配置，决定启动行为
        _configManager = new AssistConfigManager();
        _appConfig = _configManager.Load();

        // ② 开机自启动：如果开启，注册开机自启动（已注册则跳过）
        // 注意：开机自启动注册时根据 AutoLaunchOnBootMinimized 决定是否带 --minimized 参数，
        // _startMinimized 始终由命令行参数 --minimized 决定（开机自启动时自动带此参数），
        // 这里不再重复覆盖 _startMinimized，避免手动启动时被误设为静默。
        if (_appConfig.AutoLaunchOnBoot && !e.Args.Contains("--no-auto-launch"))
        {
            RegisterAutoStartup(_appConfig.AutoLaunchOnBootMinimized);
        }

        // 全局异常处理
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            MessageBox.Show($"未处理异常: {ex?.Message ?? args.ExceptionObject.ToString()}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show($"UI线程异常: {args.Exception.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            MessageBox.Show($"任务异常: {args.Exception?.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            args.SetObserved();
        };

        // 创建托盘图标
        CreateTrayIcon();

        _mainViewModel = new MainViewModel();
        var viewModel = _mainViewModel;
        _mainWindow = new MainWindow(viewModel);

        if (_startMinimized)
        {
            // 静默启动：隐藏窗口到系统托盘（不显示在任务栏，仅托盘图标可见）
            _mainWindow.ShowInTaskbar = false;
            _mainWindow.Hide();
        }
        else
        {
            // 弹窗启动：正常显示
            _mainWindow.Show();
        }

        // 窗口关闭时最小化到托盘而不是退出
        _mainWindow.Closing += (_, args) =>
        {
            args.Cancel = true;
            _mainWindow.Hide();
            _mainWindow.ShowInTaskbar = false;
        };

        // ① 随 BGI 启动：若 BGI 已在运行则只启动监控（窗口状态已由 --minimized 参数决定，不需覆盖）；
        // 若 BGI 不在运行，则启动监控等待 BGI 出现后再按配置显示/隐藏。
        // 注意：启动时绝不调用 ShowOrMinimizeWindow，因为窗口已经按 --minimized 参数决定好了显示状态。
        if (_appConfig.AutoLaunchWithBgi)
        {
            if (!IsBgiRunning())
            {
                // BGI 未运行 → 启动监控等待
                StartBgiWatchTimer();
            }
            // BGI 已在运行：窗口状态已由 --minimized 参数决定，不做任何覆盖
        }

        try
        {
            await viewModel.InitializeAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"初始化失败: {ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// 注册开机自启动到 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。已注册则跳过。
    /// 静态方法：供设置页即时生效调用。
    /// </summary>
    /// <param name="minimized">是否带 --minimized 参数静默启动</param>
    internal static void RegisterAutoStartup(bool minimized = true)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return;

            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath)) return;

            // 读取当前注册值
            var existing = key.GetValue("NexusBGI") as string;
            var targetValue = minimized
                ? $"\"{exePath}\" --minimized --no-auto-launch"
                : $"\"{exePath}\" --no-auto-launch";

            if (existing != targetValue)
            {
                key.SetValue("NexusBGI", targetValue);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"注册开机自启动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 取消开机自启动注册。静态方法：供设置页即时生效调用。
    /// </summary>
    internal static void UnregisterAutoStartup()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            key?.DeleteValue("NexusBGI", false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"取消开机自启动失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检测 BGI 进程是否正在运行（仅统计当前 Windows 会话，避免多用户下误判别会话的 BGI）。
    /// </summary>
    private static bool IsBgiRunning()
    {
        return Services.BgiProcessMonitor.GetCurrentSessionBgiProcesses().Length > 0;
    }

    /// <summary>
    /// 按"随 BGI 启动"的启动方式把助手显示到前台或静默托盘。
    /// AutoLaunchWithBgiMinimized=true → 静默缩小到托盘；false → 弹窗启动。
    /// </summary>
    private void ShowOrMinimizeWindow()
    {
        if (_mainWindow == null) return;
        var Minimized = _appConfig?.AutoLaunchWithBgiMinimized == true;
        ProbeLog($"ShowOrMinimizeWindow enter minimized={Minimized} mainWinVisible={_mainWindow.IsVisible}");
        if (Minimized)
        {
            // 静默启动：隐藏窗口到系统托盘
            _mainWindow.ShowInTaskbar = false;
            _mainWindow.Hide();
            ProbeLog($"ShowOrMinimizeWindow -> 已执行 Hide（窗口藏到托盘）。IsVisible={_mainWindow.IsVisible}");
        }
        else
        {
            // 弹窗启动：显示窗口
            _mainWindow.Show();
            _mainWindow.ShowInTaskbar = true;
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            ProbeLog("ShowOrMinimizeWindow -> 已执行 Show/Activate");
        }
    }

    /// <summary>
    /// 启动 BGI 进程监控定时器。当 BGI 启动时，自动启动助手（如果尚未启动到前台）。
    /// 幂等：已有监控则直接返回（避免叠加多个 Timer）。
    /// </summary>
    internal void StartBgiWatchTimer()
    {
        if (_bgiWatchTimer != null) return;
        _bgiWatchTimer = new Timer(_ =>
        {
            if (IsBgiRunning())
            {
                // BGI 已启动：把助手按配置方式启动（弹窗/静默托盘）
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_mainWindow != null && !_mainWindow.IsVisible)
                    {
                        ShowOrMinimizeWindow();
                    }
                });

                // 停止监控
                StopBgiWatchTimer();
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    /// <summary>停止 BGI 监控定时器。</summary>
    internal void StopBgiWatchTimer()
    {
        _bgiWatchTimer?.Dispose();
        _bgiWatchTimer = null;
    }

    /// <summary>
    /// 随 BGI 启动开关的即时生效入口（设置页勾选时调用）。
    /// true：若 BGI 已在运行则立即按配置启动助手；否则启动监控等待 BGI 出现。
    /// false：停止监控。
    /// </summary>
    internal void SetAutoLaunchWithBgi(bool enabled)
    {
        ProbeLog($"SetAutoLaunchWithBgi enabled={enabled} bgiRunning={IsBgiRunning()} minimized={_appConfig?.AutoLaunchWithBgiMinimized}");
        if (enabled)
        {
            if (IsBgiRunning())
            {
                // BGI 已在运行 → 立即按配置处理助手
                Application.Current.Dispatcher.Invoke(ShowOrMinimizeWindow);
            }
            else
            {
                // BGI 未运行 → 启动监控等待
                StartBgiWatchTimer();
            }
        }
        else
        {
            StopBgiWatchTimer();
        }
    }

    /// <summary>
    /// 诊断探针日志：写入助手 exe 目录下 log/assistant_runtime.<date>.s<session>.log。
    /// 用于确认"随 BGI 启动"点击开关时窗口是否被 Hide/Show，定位闪退观感。
    /// 写入失败不影响主流程。
    /// </summary>
    private static void ProbeLog(string message)
    {
        try
        {
            var logDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
            System.IO.Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, $"assistant_runtime.{DateTime.Now:yyyy-MM-dd}.s{System.Diagnostics.Process.GetCurrentProcess().SessionId}.log");
            System.IO.File.AppendAllText(logPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [AUTOLAUNCH_PROBE] {message}\n");
        }
        catch
        {
            // 文件写入失败不影响主流程
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 清理定时器
        StopBgiWatchTimer();

        // 释放托盘图标（未 Dispose 时系统托盘会保留图标资源，可能导致进程无法完全退出）
        _trayIcon?.Dispose();
        _trayIcon = null;

        // 释放 ViewModel 后台资源（SignalR 连接 / 业务定时器 / 进程监控），避免进程残留
        _mainViewModel?.Shutdown();
        _mainViewModel = null;

        // 重新检查开机自启动注册：如果用户关闭了开关，取消注册
        if (_appConfig != null && !_appConfig.AutoLaunchOnBoot)
        {
            UnregisterAutoStartup();
        }

        base.OnExit(e);
    }

    private void CreateTrayIcon()
    {
        _trayIcon = new TaskbarIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location),
            ToolTipText = "Nexus BGI 联机助手",
            Visibility = Visibility.Visible
        };

        // 双击显示窗口
        _trayIcon.TrayMouseDoubleClick += (_, _) => ShowWindow();

        // 右键菜单
        _trayIcon.ContextMenu = new System.Windows.Controls.ContextMenu();
        var showItem = new System.Windows.Controls.MenuItem { Header = "显示窗口" };
        showItem.Click += (_, _) => ShowWindow();
        _trayIcon.ContextMenu.Items.Add(showItem);
        var exitItem = new System.Windows.Controls.MenuItem { Header = "退出" };
        exitItem.Click += (_, _) =>
        {
            _trayIcon.Dispose();
            Shutdown();
        };
        _trayIcon.ContextMenu.Items.Add(exitItem);
    }

    private void ShowWindow()
    {
        if (_mainWindow != null)
        {
            _mainWindow.Show();
            _mainWindow.ShowInTaskbar = true;
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
        }
    }

    /// <summary>托盘气泡通知（嘟嘟可异常告警用）。托盘图标不可用时静默忽略。</summary>
    internal void ShowTrayBalloon(string title, string message)
    {
        try
        {
            _trayIcon?.ShowBalloonTip(title, message, BalloonIcon.Warning);
        }
        catch
        {
            // 气泡显示失败不影响主流程
        }
    }

    /// <summary>
    /// 启动后台线程监听跨进程弹窗事件。
    /// 当用户再次点击"打开助手"时，第二个进程触发 EventWaitHandle，
    /// 本进程收到信号后将窗口弹窗到前台。
    /// </summary>
    private void StartShowWindowEventListener()
    {
        var thread = new Thread(() =>
        {
            try
            {
                while (true)
                {
                    _showWindowEvent.WaitOne();
                    Application.Current.Dispatcher.Invoke(ShowWindow);
                }
            }
            catch (ThreadAbortException)
            {
                // 应用退出时线程被中止，正常
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"弹窗事件监听异常: {ex.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "ShowWindowEventListener"
        };
        thread.Start();
    }
}