using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Helpers;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 带倒计时自动关闭的提示弹窗。
/// 用户点击「确定」、按 Enter/Esc 或直接点窗口关闭按钮会立即关闭；
/// 未主动关闭时，倒计时归零后自动关闭，避免阻塞后续自动重启等收尾流程。
/// </summary>
public partial class AutoCloseNoticeWindow : Window
{
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    private readonly DispatcherTimer? _timer;
    private int _remainingSeconds;
    private bool _closed;

    internal bool AutoClosed { get; private set; }
    internal int RemainingSeconds => _remainingSeconds;

    internal AutoCloseNoticeWindow(Window? owner, string title, string message, int countdownSeconds, TimeSpan? tickInterval = null)
    {
        InitializeComponent();
        DpiAwarenessController.EnsureDpiAware();

        Title = title;
        MessageText.Text = message;
        if (owner is { IsLoaded: true, IsVisible: true })
        {
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = true;
        }

        _remainingSeconds = Math.Max(0, countdownSeconds);
        if (_remainingSeconds > 0)
        {
            UpdateCountdownUi();
            _timer = new DispatcherTimer { Interval = tickInterval ?? TimeSpan.FromSeconds(1) };
            _timer.Tick += OnTick;
            // 计时器等窗口真正显示后再启动，避免 ShowDialog 异常时残留倒计时。
            Loaded += (_, _) =>
            {
                if (!_closed && _remainingSeconds > 0)
                    _timer.Start();
            };
        }
        else
        {
            CountdownText.Visibility = Visibility.Collapsed;
            OkButton.Content = "确定";
        }

        Closed += (_, _) =>
        {
            _closed = true;
            _timer?.Stop();
        };
    }

    /// <summary>显示模态提示窗口，返回是否因倒计时结束而自动关闭。</summary>
    public static bool ShowNotice(Window? owner, string title, string message, int countdownSeconds = 10, TimeSpan? tickInterval = null)
    {
        var window = new AutoCloseNoticeWindow(owner, title, message, countdownSeconds, tickInterval);
        window.ShowDialog();
        return window.AutoClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast)
            return;

        var handle = new WindowInteropHelper(this).Handle;
        uint caption = 0x002F1617; // COLORREF: #17162F
        uint text = 0x00FAF2F4;    // COLORREF: #F4F2FA
        if (DwmSetWindowAttribute(handle, DwmCaptionColor, in caption, sizeof(uint)) == 0)
            _ = DwmSetWindowAttribute(handle, DwmTextColor, in text, sizeof(uint));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_closed || !IsVisible)
        {
            _timer?.Stop();
            return;
        }

        _remainingSeconds--;
        if (_remainingSeconds <= 0)
        {
            _remainingSeconds = 0;
            _timer?.Stop();
            AutoClosed = true;
            Close();
            return;
        }

        UpdateCountdownUi();
    }

    private void UpdateCountdownUi()
    {
        CountdownText.Text = $"{_remainingSeconds} 秒后未主动关闭将自动关闭";
        OkButton.Content = $"确定 ({_remainingSeconds}s)";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        _timer?.Stop();
        AutoClosed = false;
        Close();
    }

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in uint value, int size);
}
