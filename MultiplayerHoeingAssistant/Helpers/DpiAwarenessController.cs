using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MultiplayerHoeingAssistant.Helpers;

/// <summary>
/// 窗口 DPI 适配工具（自包含实现，不依赖 Vanara.PInvoke / 主 BGI 项目）。
///
/// 背景：本助手进程启动时若不声明 DPI 感知，WPF 在高缩放率显示器上会得到错误的
/// 逻辑分辨率（2K 200% → 1280×720），固定 980×860 的窗口高度 860 超出屏幕可用
/// 高度，导致上下被截断。
///
/// 机制：
/// 1. 进程级：静态构造里调用 SetProcessDpiAwareness(PER_MONITOR_DPI_AWARE)，
///    让 WPF 正确识别显示器 DPI，获得真实的逻辑分辨率。
/// 2. 窗口级：Loaded 事件中获取屏幕工作区，按比例限制窗口初始尺寸，确保不超出屏幕。
/// </summary>
internal sealed class DpiAwarenessController : IDisposable
{
    private readonly Window window;
    private bool _disposed;

    static DpiAwarenessController()
    {
        try
        {
            int hr = SetProcessDpiAwareness(2); // PROCESS_PER_MONITOR_DPI_AWARE
            if (hr < 0 && hr != unchecked((int)0x80070005)) // E_ACCESSDENIED = 已设过，可忽略
            {
                Debug.WriteLine($"SetProcessDpiAwareness returned error: 0x{hr:x8}");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set process DPI awareness: {ex.Message}");
        }
    }

    private DpiAwarenessController(Window window)
    {
        this.window = window;
        // 用 SourceInitialized 而非 Loaded：句柄创建后、首次显示前调用，此时已能
        // 获取正确的 DPI 与显示器工作区，且未开始布局，设置 Width/Height/Left/Top
        // 会被首帧直接采纳，避免"启动时尺寸/位置异常、缩放后才正常"的闪烁。
        window.SourceInitialized += OnSourceInitialized;
    }

    /// <summary>
    /// 仅触发进程级 per-monitor DPI 感知（静态构造执行一次），不挂任何窗口。
    /// 应在创建任何窗口之前调用。
    /// </summary>
    public static void EnsureDpiAware() => _ = new DpiAwarenessNoop();

    /// <summary>
    /// 给窗口挂载 DPI 适配（进程级感知 + 窗口自适应尺寸）。
    /// 在窗口构造函数中调用。
    /// </summary>
    public static void Initialize(Window window) => _ = new DpiAwarenessController(window);

    /// <summary>确保 <see cref="DpiAwarenessController"/> 静态构造被触发（仅此而已）。</summary>
    private sealed class DpiAwarenessNoop { }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        if (_disposed) return;
        AdjustWindowSize(window);
    }

    /// <summary>
    /// 根据屏幕工作区调整窗口尺寸，确保不超过屏幕可用空间的 90%。
    /// 在窗口 Loaded 后调用，此时窗口已初始化、可获取正确的工作区信息。
    /// </summary>
    public static void AdjustWindowSize(Window w)
    {
        try
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd == IntPtr.Zero) return;

            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return;

            var monitorInfo = GetMonitorInfo(hMonitor);
            if (monitorInfo == null) return;

            int workWidth = monitorInfo.Value.rcWork.Right - monitorInfo.Value.rcWork.Left;
            int workHeight = monitorInfo.Value.rcWork.Bottom - monitorInfo.Value.rcWork.Top;

            uint dpiX = 96, dpiY = 96;
            if (GetDpiForMonitor(hMonitor, 0, out dpiX, out dpiY) != 0)
            {
                dpiX = 96;
                dpiY = 96;
            }
            double scaleX = dpiX / 96.0;
            double scaleY = dpiY / 96.0;

            double logicalWorkWidth = workWidth / scaleX;
            double logicalWorkHeight = workHeight / scaleY;
            double logicalWorkLeft = monitorInfo.Value.rcWork.Left / scaleX;
            double logicalWorkTop = monitorInfo.Value.rcWork.Top / scaleY;

            // 诊断探针：把真实运行时数值写入日志，便于定位"是否缩小/是否居中"问题。
            LogDpi($"win={w.Width:F1}x{w.Height:F1} min={w.MinWidth:F0}x{w.MinHeight:F0} " +
                   $"dpi={dpiX},{dpiY} scaleF={scaleX:F2} workPhy={workWidth}x{workHeight} " +
                   $"workLog={logicalWorkWidth:F1}x{logicalWorkHeight:F1} at {logicalWorkLeft:F0},{logicalWorkTop:F0}");

            // 启动时把窗口初始尺寸压到工作区的 95% 以内（仅初始，不改 MaxWidth/MaxHeight，
            // 否则会把"最大化"也限制住——最大化仍应允许占满全屏）。
            // SourceInitialized 时机窗口尚未布局，这里直接设 Width/Height 会被首帧采纳。
            const double sizeRatio = 0.95;
            double boundedW = Math.Min(w.Width, logicalWorkWidth * sizeRatio);
            double boundedH = Math.Min(w.Height, logicalWorkHeight * sizeRatio);

            if (w.MinWidth > boundedW) w.MinWidth = boundedW;
            if (w.MinHeight > boundedH) w.MinHeight = boundedH;

            w.Width = boundedW;
            w.Height = boundedH;

            // 手动定位：居中优先，只在会超界时 clamp 到带留白的边界。
            const double margin = 16.0;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            double minLeft = logicalWorkLeft + margin;
            double minTop = logicalWorkTop + margin;
            double maxLeft = logicalWorkLeft + logicalWorkWidth - boundedW - margin;
            double maxTop = logicalWorkTop + logicalWorkHeight - boundedH - margin;

            double centerX = logicalWorkLeft + (logicalWorkWidth - boundedW) / 2;
            double centerY = logicalWorkTop + (logicalWorkHeight - boundedH) / 2;
            w.Left = Clamp(centerX, minLeft, maxLeft);
            w.Top = Clamp(centerY, minTop, maxTop);

            LogDpi($"result W={boundedW:F1} H={boundedH:F1} Left={w.Left:F1} Top={w.Top:F1} " +
                   $"bottom={w.Top + boundedH:F1} (workBottom={logicalWorkTop + logicalWorkHeight:F1})");
        }
        catch (Exception ex)
        {
            LogDpi($"AdjustWindowSize EXCEPTION: {ex}");
        }
    }

    /// <summary>
    /// 诊断探针：把 DPI/尺寸计算写到应用目录 dpi_debug.log，方便确认运行时真实数值。
    /// 问题修复后可移除。
    /// </summary>
    private static void LogDpi(string message)
    {
        try
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "dpi_debug.log");
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss.fff}] {message}\r\n");
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        window.SourceInitialized -= OnSourceInitialized;
    }

    // ---------- Win32 interop ----------

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("shcore.dll")]
    private static extern int SetProcessDpiAwareness(int value);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static MONITORINFO? GetMonitorInfo(IntPtr hMonitor)
    {
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (GetMonitorInfo(hMonitor, ref mi))
            return mi;
        return null;
    }

    /// <summary>
    /// 将 value 限制在 [min, max] 区间内。若区间无效（min > max），返回 max，
    /// 避免窗口被放到负坐标/超出屏幕。
    /// </summary>
    private static double Clamp(double value, double min, double max)
    {
        if (min > max) return max;
        return Math.Max(min, Math.Min(max, value));
    }
}