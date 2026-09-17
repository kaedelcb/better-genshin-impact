using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// 保护桌宠类透明工具窗口不被 Win+D / Show Desktop 移到异常坐标或最小化。
/// 锁定值始终使用 Win32 物理像素；调用方在用户拖动或从左/上边缩放期间显式放行。
/// </summary>
internal sealed class DesktopWidgetWindowGuard : IDisposable
{
    private readonly Window _window;
    private readonly Func<bool> _shouldRemainVisible;
    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private bool _allowPositionChange;
    private bool _positionLocked;
    private int _lockedX;
    private int _lockedY;
    private bool _restoreQueued;
    private bool _positionRestoreQueued;
    private bool _disposed;

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private const int WM_WINDOWPOSCHANGED = 0x0047;
    private const int WM_SIZE = 0x0005;
    private const int WM_SYSCOMMAND = 0x0112;
    private const int SC_MINIMIZE = 0xF020;
    private const int SIZE_MINIMIZED = 1;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X;
        public int Y;
        public int Cx;
        public int Cy;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public DesktopWidgetWindowGuard(Window window, Func<bool> shouldRemainVisible)
    {
        _window = window;
        _shouldRemainVisible = shouldRemainVisible;
        window.SourceInitialized += OnSourceInitialized;
        window.Closed += OnClosed;
    }

    /// <summary>在 DragMove 或会改变 Left/Top 的手动缩放开始前调用。</summary>
    public void BeginUserMove() => _allowPositionChange = true;

    /// <summary>用户移动结束后重新锁定实际物理像素位置。</summary>
    public void EndUserMove()
    {
        _allowPositionChange = false;
        CaptureCurrentPosition();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(_window).Handle;
        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WindowProc);
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, CaptureCurrentPosition);
    }

    private IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_WINDOWPOSCHANGING when _positionLocked && !_allowPositionChange:
            {
                var pos = Marshal.PtrToStructure<WindowPos>(lParam);
                pos.X = _lockedX;
                pos.Y = _lockedY;
                Marshal.StructureToPtr(pos, lParam, false);
                break;
            }

            case WM_WINDOWPOSCHANGED when _positionLocked && !_allowPositionChange:
            {
                // SWP_NOSENDCHANGING 可绕过预移动通知，事后再检查一次。
                var pos = Marshal.PtrToStructure<WindowPos>(lParam);
                if ((pos.Flags & SWP_NOMOVE) == 0 && (pos.X != _lockedX || pos.Y != _lockedY))
                    QueuePositionRestore();
                break;
            }

            case WM_SYSCOMMAND when ((long)wParam & 0xFFF0) == SC_MINIMIZE:
                handled = true;
                return IntPtr.Zero;

            case WM_SIZE when (long)wParam == SIZE_MINIMIZED:
                QueueRestoreFromSystemMinimize();
                break;
        }

        return IntPtr.Zero;
    }

    private void CaptureCurrentPosition()
    {
        if (_hwnd != IntPtr.Zero && GetWindowRect(_hwnd, out var rect))
        {
            _lockedX = rect.Left;
            _lockedY = rect.Top;
            _positionLocked = true;
        }
    }

    private void QueuePositionRestore()
    {
        if (_positionRestoreQueued || !_shouldRemainVisible()) return;
        _positionRestoreQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _positionRestoreQueued = false;
            if (_disposed || _allowPositionChange || _hwnd == IntPtr.Zero ||
                !_positionLocked || !_shouldRemainVisible()) return;
            SetWindowPos(_hwnd, IntPtr.Zero, _lockedX, _lockedY, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        });
    }

    private void QueueRestoreFromSystemMinimize()
    {
        if (_restoreQueued || !_shouldRemainVisible()) return;
        _restoreQueued = true;
        _window.Dispatcher.BeginInvoke(DispatcherPriority.Send, () =>
        {
            _restoreQueued = false;
            if (_disposed || _hwnd == IntPtr.Zero || !_shouldRemainVisible()) return;
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            if (_positionLocked)
            {
                SetWindowPos(_hwnd, IntPtr.Zero, _lockedX, _lockedY, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        });
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _window.SourceInitialized -= OnSourceInitialized;
        _window.Closed -= OnClosed;
        _hwndSource?.RemoveHook(WindowProc);
        _hwndSource = null;
        _hwnd = IntPtr.Zero;
    }
}
