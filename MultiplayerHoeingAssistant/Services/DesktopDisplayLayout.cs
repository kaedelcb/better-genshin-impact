using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using BgiCoordinatorServer.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>线程内使用物理像素，不改变 WPF 进程及窗口的 DPI 行为；Scope 不可跨 await。</summary>
internal static class DesktopDisplayLayout
{
    internal static IDisposable EnterPerMonitorV2()
    {
        var previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
        if (previous == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "不能建立主屏物理像素上下文");
        return new Scope(previous);
    }

    private sealed class Scope(IntPtr previous) : IDisposable
    {
        private IntPtr _previous = previous;
        public void Dispose()
        {
            var context = Interlocked.Exchange(ref _previous, IntPtr.Zero);
            if (context != IntPtr.Zero) SetThreadDpiAwarenessContext(context);
        }
    }

    internal static DesktopCaptureGeometry? GetGeometry()
    {
        try
        {
            using var dpi = EnterPerMonitorV2();
            var displays = new List<(MonitorInfo Info, string Signature)>();
            var failed = false;
            MonitorEnum callback = (IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data) =>
            {
                var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" };
                if (!GetMonitorInfo(monitor, ref info) || string.IsNullOrWhiteSpace(info.Device))
                { failed = true; return false; }
                if (GetScaleFactorForMonitor(monitor, out var scale) != 0 || scale <= 0)
                { failed = true; return false; }
                var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), DeviceName = "", DeviceString = "", DeviceId = "", DeviceKey = "" };
                if (!EnumDisplayDevices(info.Device, 0, ref device, 1))
                { failed = true; return false; }
                var mode = new DisplayMode { Size = (ushort)Marshal.SizeOf<DisplayMode>(), DeviceName = "", FormName = "" };
                if (!EnumDisplaySettings(info.Device, -1, ref mode)) { failed = true; return false; }
                var bounds = info.Monitor;
                var id = string.IsNullOrWhiteSpace(device.DeviceId) ? info.Device : device.DeviceId;
                displays.Add((info, $"{info.Device}|{id}|{bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}|{info.Flags}|{scale}|{mode.Orientation}"));
                return true;
            };
            if (!EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero) || failed || displays.Count == 0) return null;
            var primary = displays.Where(x => (x.Info.Flags & 1) != 0).ToArray();
            if (primary.Length != 1) return null;
            var p = primary[0].Info;
            var geometry = new DesktopCaptureGeometry
            {
                Left = p.Monitor.Left, Top = p.Monitor.Top,
                Width = checked(p.Monitor.Right - p.Monitor.Left), Height = checked(p.Monitor.Bottom - p.Monitor.Top),
                DisplayId = p.Device,
                LayoutId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                    string.Join("\n", displays.Select(x => x.Signature).Order(StringComparer.Ordinal)))))
            };
            return geometry.IsValid ? geometry : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DesktopDisplay] 主屏布局不可确认: {ex.Message}");
            return null;
        }
    }

    internal static void RequireInteractiveDesktop()
    {
        // OpenInputDesktop 在锁屏/UAC 下失败，或指向 Winlogon。还需确认调用线程属于交互窗口站。
        if (!ObjectName(GetProcessWindowStation()).Equals("WinSta0", StringComparison.OrdinalIgnoreCase)
            || !ObjectName(GetThreadDesktop(GetCurrentThreadId())).Equals("Default", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("当前线程不在交互桌面");
        var desktop = OpenInputDesktop(0, false, 0x0001); // DESKTOP_READOBJECTS
        if (desktop == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "当前桌面不可输入（可能已锁屏或处于安全桌面）");
        try
        {
            if (!ObjectName(desktop).Equals("Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不支持锁屏或 UAC 安全桌面");
        }
        finally { CloseDesktop(desktop); }
    }

    private static string ObjectName(IntPtr handle)
    {
        var name = new StringBuilder(256);
        if (handle == IntPtr.Zero || !GetUserObjectInformation(handle, 2, name, name.Capacity * 2, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "不能确认交互桌面");
        return name.ToString();
    }

    internal static (int Left, int Top, int Width, int Height) VirtualBounds()
        => (GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor, Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion, DriverVersion, Size, DriverExtra;
        public uint Fields;
        public int PositionX, PositionY;
        public uint Orientation, FixedOutput;
        public short Color, Duplex, YResolution, TTOption, Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPixel, Width, Height, Flags, Frequency, IcmMethod, IcmIntent, MediaType, DitherType,
            Reserved1, Reserved2, PanningWidth, PanningHeight;
    }
    private delegate bool MonitorEnum(IntPtr monitor, IntPtr dc, ref Rect rect, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnum callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplayDevices(string device, uint number, ref DisplayDevice info, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumDisplaySettings(string device, int mode, ref DisplayMode settings);
    [DllImport("shcore.dll")] private static extern int GetScaleFactorForMonitor(IntPtr monitor, out int scale);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
}
