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
    /// 取指定窗口所在显示器的逻辑工作区矩形（rcWork，已按该显示器 DPI 折算为 WPF 逻辑单位）。
    /// rcWork 已排除任务栏/停靠栏，所以用它做尺寸上限与位置钳制可同时避免
    /// "窗口高于可用高度被裁" 与 "窗口底部被任务栏盖住" 两类问题。
    /// 取不到真实显示器信息时退化为 <see cref="SystemParameters.WorkArea"/>（只对主显示器精确），
    /// 不返回 null —— 静默跳过钳制正是"弹窗又被任务栏盖住"的成因。
    /// </summary>
    /// <param name="w">用于取句柄的窗口。</param>
    /// <param name="monitorSource">
    /// 可选：用哪个窗口定位显示器。弹窗在 SourceInitialized 阶段自身尚未居中，
    /// 此时传入 Owner（已定位的主窗口）才能取到弹窗最终会落在的那块屏幕；
    /// 不传则用 w 自身（Loaded 之后调用是正确的）。
    /// </param>
    public static Rect? GetLogicalWorkAreaRect(Window w, Window? monitorSource = null)
    {
        try
        {
            var hwnd = new WindowInteropHelper(monitorSource ?? w).Handle;
            if (hwnd == IntPtr.Zero) return FallbackWorkArea("句柄未就绪");

            IntPtr hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return FallbackWorkArea("MonitorFromWindow 失败");

            var monitorInfo = GetMonitorInfo(hMonitor);
            if (monitorInfo == null) return FallbackWorkArea("GetMonitorInfo 失败");

            var rc = monitorInfo.Value.rcWork;
            int workWidth = rc.Right - rc.Left;
            int workHeight = rc.Bottom - rc.Top;

            uint dpiX = 96, dpiY = 96;
            if (GetDpiForMonitor(hMonitor, 0, out dpiX, out dpiY) != 0)
            {
                dpiX = 96;
                dpiY = 96;
            }

            double scaleX = dpiX / 96.0;
            double scaleY = dpiY / 96.0;
            return new Rect(rc.Left / scaleX, rc.Top / scaleY, workWidth / scaleX, workHeight / scaleY);
        }
        catch (Exception ex)
        {
            LogDpi($"GetLogicalWorkAreaRect EXCEPTION: {ex.Message}");
            return FallbackWorkArea("异常: " + ex.Message);
        }
    }

    /// <summary>
    /// 兜底工作区：Win32 路径失败时退化为 WPF 的 SystemParameters.WorkArea。
    /// 它只反映主显示器（多屏/异 DPI 下不精确），但"有个可用的矩形"远好于返回 null 后
    /// 完全跳过钳制——跳过就意味着窗口可能又伸进任务栏被盖住（本函数存在的理由）。
    /// </summary>
    private static Rect FallbackWorkArea(string reason)
    {
        LogDpi($"GetLogicalWorkAreaRect 兜底 SystemParameters.WorkArea（{reason}）");
        return SystemParameters.WorkArea;
    }

    /// <summary>
    /// 把窗口夹进指定工作区矩形内（尺寸不超上限 + 位置不越界），并转为 Manual 定位。
    /// 用于代码构建的弹窗：WindowStartupLocation.CenterOwner 只按属主窗口居中、
    /// 完全不管任务栏，属主靠下时弹窗底部就会伸进任务栏被盖住，故必须显式钳位置。
    /// </summary>
    /// <param name="assumedHeight">
    /// 尚未量测出真实高度时（SourceInitialized）传入预期的最大高度，先摆一个必定合法的位置；
    /// 已量测出高度时（Loaded）传 ActualHeight 即为最终位置。
    /// </param>
    public static void PlaceWithinWorkArea(Window w, Rect work, double assumedHeight, double margin = 12)
    {
        double width = w.ActualWidth > 0 ? w.ActualWidth : w.Width;
        if (double.IsNaN(width) || width <= 0) width = Math.Max(w.MinWidth, 300);
        double height = assumedHeight;
        if (double.IsNaN(height) || height <= 0) height = Math.Max(w.MinHeight, 240);

        // 最大化可用区间；若窗口比工作区还大（极端小屏），区间退化为 margin，由 Math.Max 兜住
        double minLeft = work.Left + margin;
        double maxLeft = Math.Max(minLeft, work.Right - width - margin);
        double minTop = work.Top + margin;
        double maxTop = Math.Max(minTop, work.Bottom - height - margin);

        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = Math.Clamp(work.Left + (work.Width - width) / 2, minLeft, maxLeft);
        w.Top = Math.Clamp(work.Top + (work.Height - height) / 2, minTop, maxTop);
    }

    /// <summary>便利重载：只要工作区尺寸（宽/高）。</summary>
    public static (double Width, double Height)? GetLogicalWorkArea(Window w, Window? monitorSource = null)
        => GetLogicalWorkAreaRect(w, monitorSource) is { } r ? (r.Width, r.Height) : null;

    /// <summary>
    /// 诊断探针（与 <see cref="LogDpi"/> 同一日志文件）：记录弹窗尺寸决策的关键数值，
    /// 便于在高缩放屏上核对"内容高度 / 封顶值 / 最终高度"，定位"底部被裁"类问题。
    /// 纯观测，不影响任何行为。
    /// </summary>
    public static void LogDialogMetrics(string tag, string detail)
        => LogDpi($"[{tag}] {detail}");

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