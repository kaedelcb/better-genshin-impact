using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

public partial class RemoteControlIndicatorWindow : Window, IRemoteControlIndicator
{
    private const int GwlExStyle = -20;
    private const long RequiredStyles = 0x20L | 0x08000000L | 0x80L;
    private HwndSource? _source;
    private bool _closed;
    private readonly DesktopWidgetWindowGuard _windowGuard;

    void IRemoteControlIndicator.Show()
    {
        Show();
        if (!IsVisible || new WindowInteropHelper(this).Handle == IntPtr.Zero)
            throw new InvalidOperationException("远程控制提示窗口未能显示。");
    }

    public RemoteControlIndicatorWindow()
    {
        InitializeComponent();
        // 与桌宠共用 Win+D 防最小化保护；恢复时使用 SW_SHOWNOACTIVATE。
        _windowGuard = new DesktopWidgetWindowGuard(this, () => !_closed && IsVisible);
        SourceInitialized += OnSourceInitialized;
        Closed += OnClosed;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(handle) ?? throw new InvalidOperationException("无法创建远程控制提示窗口句柄。");
        _source.AddHook(WindowProc);
        var oldStyle = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        Marshal.SetLastPInvokeError(0);
        var previous = SetWindowLongPtr(handle, GwlExStyle, new IntPtr(oldStyle | RequiredStyles));
        var error = Marshal.GetLastPInvokeError();
        if (previous == IntPtr.Zero && error != 0) throw new Win32Exception(error);
        if ((GetWindowLongPtr(handle, GwlExStyle).ToInt64() & RequiredStyles) != RequiredStyles)
            throw new InvalidOperationException("无法设置远程控制提示窗口样式。");
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
        Reposition();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _windowGuard.Dispose();
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        SystemParameters.StaticPropertyChanged -= OnSystemParametersChanged;
        _source?.RemoveHook(WindowProc);
        _source = null;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => ScheduleReposition();
    private void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.WorkArea)) ScheduleReposition();
    }

    private void ScheduleReposition()
    {
        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
        {
            try { Dispatcher.BeginInvoke(new Action(() => { if (!_closed) Reposition(); })); }
            catch (InvalidOperationException) { }
        }
    }

    private void Reposition()
    {
        _windowGuard.BeginUserMove();
        try
        {
            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left, area.Right - Width - 16);
            Top = Math.Max(area.Top, area.Bottom - Height - 16);
        }
        finally { _windowGuard.EndUserMove(); }
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case 0x0084: // WM_NCHITTEST
                handled = true;
                return new IntPtr(-1); // HTTRANSPARENT
            case 0x0021: // WM_MOUSEACTIVATE
                handled = true;
                return new IntPtr(3); // MA_NOACTIVATE
            case 0x02E0: // WM_DPICHANGED
            case 0x007E: // WM_DISPLAYCHANGE
            case 0x001A: // WM_SETTINGCHANGE
                ScheduleReposition();
                break;
        }
        return IntPtr.Zero;
    }

    private static IntPtr GetWindowLongPtr(IntPtr hWnd, int index) => IntPtr.Size == 8
        ? GetWindowLongPtr64(hWnd, index) : new IntPtr(GetWindowLong32(hWnd, index));
    private static IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value) => IntPtr.Size == 8
        ? SetWindowLongPtr64(hWnd, index, value) : new IntPtr(SetWindowLong32(hWnd, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int index, IntPtr value);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int index, int value);
}
