using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using BgiCoordinatorServer.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 桌面截图帧（JPEG 字节 + 元数据）。只存压缩后的字节，不长期持有 Bitmap（IDisposable 及时释放）。
/// </summary>
public sealed class ScreenFrame
{
    public DateTime Time { get; init; }
    /// <summary>JPEG 压缩后的图像字节（缩放后，宽度 = 配置的缩略宽度）。</summary>
    public byte[] JpegBytes { get; init; } = [];
    /// <summary>缩放后尺寸（显示布局用）。</summary>
    public int Width { get; init; }
    public int Height { get; init; }
    public string FrameId { get; init; } = "";
    public DesktopCaptureGeometry? Geometry { get; init; }
}

/// <summary>
/// 桌面监控截图服务（嘟嘟可 P4 / F4）。
/// Graphics.CopyFromScreen 截主屏全屏 → 缩放到配置宽度 → JPEG(质量 ~70) 输出字节；
/// 环形保留最近 10 帧，超出丢弃（帧只存字节，无未释放位图）。
///
/// DPI 适配：主屏捕获在同步线程内临时进入 PerMonitorV2，获取实际主屏物理像素；
/// 退出时恢复原上下文，不改变 WPF 窗口及进程的 DPI 行为。
///
/// 截图在调用方线程执行（约几十~几百毫秒），UI 层务必放后台线程调用，不要阻塞 UI。
/// Capture() 的历史帧只在本机使用；CaptureJpeg() 可由房间截图应答服务调用并单播给请求者。
/// </summary>
public sealed class ScreenshotService
{
    /// <summary>历史帧环形容量。</summary>
    public const int HistoryCapacity = 10;
    /// <summary>JPEG 编码质量（~70，控内存）。</summary>
    private const long JpegQuality = 70L;

    private readonly Func<int> _thumbnailWidthProvider;
    private readonly object _lock = new();
    private readonly List<ScreenFrame> _history = [];
    private readonly TimeProvider _time;
    private readonly Func<DesktopCaptureGeometry?>? _geometryProvider;
    private readonly Func<int, long, ScreenFrame?>? _captureOverride;
    private readonly Dictionary<string, FrameRegistration> _controlFrames = new(StringComparer.Ordinal);
    // 别名共享原始捕获时间及消费位；不能通过同一 JPEG 的其他编号再次输入。
    private readonly Dictionary<string, long> _frameIds = new(StringComparer.Ordinal);
    private sealed class ControlFrame(DesktopCaptureGeometry geometry, long captured)
    {
        public readonly DesktopCaptureGeometry Geometry = geometry;
        public readonly long Captured = captured;
        public bool Used;
    }
    private sealed record FrameRegistration(ControlFrame Frame, string? RemoteContext = null);

    /// <param name="thumbnailWidthProvider">缩略宽度提供者（读取统一设置，改宽度后下一帧生效）。</param>
    public ScreenshotService(Func<int> thumbnailWidthProvider) : this(thumbnailWidthProvider, TimeProvider.System) { }

    internal ScreenshotService(Func<int> thumbnailWidthProvider, TimeProvider timeProvider,
        Func<DesktopCaptureGeometry?>? geometryProvider = null, Func<int, long, ScreenFrame?>? captureOverride = null)
    {
        _thumbnailWidthProvider = thumbnailWidthProvider;
        _time = timeProvider;
        _geometryProvider = geometryProvider;
        _captureOverride = captureOverride;
    }

    /// <summary>历史帧（新的在尾部）。线程安全快照。</summary>
    public IReadOnlyList<ScreenFrame> History
    {
        get { lock (_lock) return _history.ToList(); }
    }

    /// <summary>
    /// 截一帧主屏全屏并加入历史。成功返回帧，失败返回 null（如会话未解锁/无显示器）。
    /// 本方法可能耗时，调用方放后台线程。
    /// </summary>
    public ScreenFrame? Capture()
    {
        // 宽度配置非法时兜底 1280
        var targetW = _thumbnailWidthProvider();
        if (targetW is < 320 or > 3840) targetW = 1280;

        var frame = CaptureDesktopFrame(targetW, JpegQuality);
        if (frame == null) return null;

        lock (_lock)
        {
            _history.Add(frame);
            // 环形：超出容量丢弃最旧帧（帧只持字节数组，GC 回收即可，无 Bitmap 泄漏）
            while (_history.Count > HistoryCapacity) _history.RemoveAt(0);
        }
        return frame;
    }

    /// <summary>
    /// 截一帧主屏并编码为 JPEG（不入本地历史，供远程上报等独立用途）。
    /// 返回 null 表示截图失败。调用方放后台线程。
    /// </summary>
    public byte[]? CaptureJpeg(int targetWidth, long quality, out int outWidth, out int outHeight)
    {
        var frame = CaptureDesktopFrame(targetWidth, quality);
        outWidth = frame?.Width ?? 0;
        outHeight = frame?.Height ?? 0;
        return frame?.JpegBytes;
    }

    /// <summary>截主屏并登记可信帧。布局不可确认时仍尽力截图，但不附可控制的 geometry。</summary>
    public ScreenFrame? CaptureDesktopFrame(int targetWidth, long quality)
    {
        var captured = _time.GetTimestamp();
        ScreenFrame? raw;
        if (_captureOverride != null) raw = _captureOverride(targetWidth, quality);
        else
        {
            try
            {
                using var dpi = DesktopDisplayLayout.EnterPerMonitorV2();
                var geometry = DesktopDisplayLayout.GetGeometry();
                var w = geometry?.Width ?? GetSystemMetrics(SM_CXSCREEN);
                var h = geometry?.Height ?? GetSystemMetrics(SM_CYSCREEN);
                if (w <= 0 || h <= 0) return null;
                using var full = CaptureRegion(geometry?.Left ?? 0, geometry?.Top ?? 0, w, h);
                if (full == null) return null;
                // 显示器热插拔、主屏切换或缩放改变时，只提供查看画面。
                if (geometry != null && geometry != DesktopDisplayLayout.GetGeometry()) geometry = null;
                var jpeg = EncodeJpeg(full, targetWidth, quality, out var width, out var height);
                if (jpeg == null) return null;
                raw = new ScreenFrame { JpegBytes = jpeg, Width = width, Height = height, Geometry = geometry };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ScreenshotService] 桌面截图失败: {ex.Message}");
                return null;
            }
        }
        if (raw == null) return null;
        // 每次捕获拥有自己的元数据实例，别名只能关联这一实际捕获，不能关联较新的相同布局帧。
        var geometryCopy = raw.Geometry?.IsValid == true ? raw.Geometry with { } : null;
        var frame = new ScreenFrame { FrameId = Guid.NewGuid().ToString("N"), Time = _time.GetLocalNow().LocalDateTime,
            JpegBytes = raw.JpegBytes, Width = raw.Width, Height = raw.Height, Geometry = geometryCopy };
        if (geometryCopy != null)
        {
            lock (_lock)
            {
                PruneFrames();
                if (_controlFrames.Count < 512 && _frameIds.Count < 4096)
                {
                    _controlFrames.Add(frame.FrameId, new(new(geometryCopy, captured)));
                    _frameIds.Add(frame.FrameId, captured);
                }
            }
        }
        return frame;
    }

    /// <summary>远程应答别名只关联本服务实际捕获的 geometry 实例，且沿用原捕获时间。</summary>
    public void RememberControlFrame(string frameId, DesktopCaptureGeometry geometry)
        => RememberControlFrame(frameId, geometry, null);

    internal void RememberControlFrame(string frameId, DesktopCaptureGeometry geometry, string? remoteContext)
    {
        if (!ValidFrameId(frameId) || geometry?.IsValid != true) return;
        lock (_lock)
        {
            PruneFrames();
            if (_frameIds.ContainsKey(frameId) || _controlFrames.Count >= 512 || _frameIds.Count >= 4096) return;
            var source = _controlFrames.Values.Select(x => x.Frame)
                .FirstOrDefault(f => !f.Used && ReferenceEquals(f.Geometry, geometry));
            if (source == null) return;
            _controlFrames.Add(frameId, new(source, remoteContext));
            _frameIds.Add(frameId, source.Captured);
        }
    }

    public bool TryGetControlFrame(string frameId, out DesktopCaptureGeometry? geometry)
        => TryGetControlFrame(frameId, null, out geometry);

    internal bool TryGetControlFrame(string frameId, string? remoteContext, out DesktopCaptureGeometry? geometry)
    {
        lock (_lock)
        {
            PruneFrames();
            if (frameId != null && _controlFrames.TryGetValue(frameId, out var registration)
                && !registration.Frame.Used && (remoteContext == null || registration.RemoteContext == remoteContext))
            { geometry = registration.Frame.Geometry; return true; }
        }
        geometry = null;
        return false;
    }

    internal bool TryGetControlFrameAge(string frameId, string remoteContext, out TimeSpan age)
    {
        lock (_lock)
        {
            PruneFrames();
            if (_controlFrames.TryGetValue(frameId, out var registration) && !registration.Frame.Used
                && registration.RemoteContext == remoteContext)
            {
                age = _time.GetElapsedTime(registration.Frame.Captured);
                return true;
            }
        }
        age = default;
        return false;
    }

    internal bool TryConsumeControlFrame(string frameId)
    {
        lock (_lock)
        {
            PruneFrames();
            if (!_controlFrames.TryGetValue(frameId, out var registration) || registration.Frame.Used) return false;
            registration.Frame.Used = true;
            return true;
        }
    }

    internal bool IsControlFrameFresh(string frameId, DesktopCaptureGeometry geometry)
    {
        lock (_lock)
        {
            PruneFrames();
            return _controlFrames.TryGetValue(frameId, out var registration)
                && ReferenceEquals(registration.Frame.Geometry, geometry);
        }
    }

    public DesktopCaptureGeometry? GetCurrentGeometry()
        => _geometryProvider != null ? _geometryProvider() : DesktopDisplayLayout.GetGeometry();

    private void PruneFrames()
    {
        var now = _time.GetTimestamp();
        foreach (var item in _controlFrames.Where(x => _time.GetElapsedTime(x.Value.Frame.Captured, now) >= DesktopControlProtocol.FrameLifetime).ToArray())
            _controlFrames.Remove(item.Key);
        foreach (var item in _frameIds.Where(x => _time.GetElapsedTime(x.Value, now) > TimeSpan.FromMinutes(10)).ToArray())
            _frameIds.Remove(item.Key);
    }

    private static bool ValidFrameId(string? id) => Guid.TryParseExact(id, "N", out _)
        || (id is { Length: 72 } && id.StartsWith("screen:", StringComparison.Ordinal)
            && Guid.TryParseExact(id.Substring(7, 32), "N", out _) && id[39] == ':'
            && Guid.TryParseExact(id[40..], "N", out _));

    /// <summary>游戏进程名（国服 Yuanshen / 国际服 GenshinImpact）。</summary>
    private static readonly string[] GameProcessNames = ["Yuanshen", "GenshinImpact"];

    /// <summary>
    /// 截一帧「游戏画面」：找当前 Windows 会话的原神主窗口，用 PrintWindow(PW_RENDERFULLCONTENT)
    /// 直接绘制窗口自身内容到位图——与屏幕区域无关，因此：副屏/多屏无坐标系问题、被其他窗口遮挡
    /// 也不会截到遮挡物、不受 DPI 虚拟化影响（矩形是什么坐标系内容就按什么尺寸渲染）。
    /// 找不到窗口或游戏最小化（PrintWindow 只能得到冻结/黑帧）时返回 null，调用方应回退主屏全屏。
    /// 多用户隔离：只取当前会话的游戏进程（与 BgiProcessMonitor 同策略）。
    /// 注意：不要对全黑帧做"失败回退"——传送加载时游戏画面本来就是黑的，那是真实的现场。
    /// </summary>
    public byte[]? CaptureGameWindowJpeg(int targetWidth, long quality, out int outWidth, out int outHeight)
    {
        outWidth = 0;
        outHeight = 0;
        try
        {
            var hwnd = FindGameWindowHwnd();
            if (hwnd == IntPtr.Zero) return null;
            if (IsIconic(hwnd)) return null; // 最小化时 PrintWindow 只有冻结/黑帧，不如回退主屏
            // 用客户区尺寸（不含标题栏/边框）；坐标系无关，PrintWindow 按位图尺寸渲染内容
            if (!GetClientRect(hwnd, out var rect)) return null;
            var w = rect.Right - rect.Left;
            var h = rect.Bottom - rect.Top;
            if (w < 160 || h < 90) return null;
            using var full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                var hdc = g.GetHdc();
                try
                {
                    if (!PrintWindow(hwnd, hdc, PwRenderFullContent)) return null;
                }
                finally
                {
                    g.ReleaseHdc(hdc);
                }
            }
            // 游戏切分辨率/加载过渡时 PrintWindow 可能把内容画在小一号的缓冲里（右/下剩纯黑带），裁掉再编码
            var cropped = CropBlackBands(full);
            try
            {
                return EncodeJpeg(cropped, targetWidth, quality, out outWidth, out outHeight);
            }
            finally
            {
                if (!ReferenceEquals(cropped, full)) cropped.Dispose();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenshotService] 游戏窗口截图失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>当前会话的游戏主窗口句柄（找不到返回 Zero）。</summary>
    private static IntPtr FindGameWindowHwnd()
    {
        try
        {
            var session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            foreach (var name in GameProcessNames)
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (p)
                {
                    // 会话隔离：只认本会话的游戏（多用户/多开场景不截别人桌面的窗口）
                    if (p.SessionId == session && p.MainWindowHandle != IntPtr.Zero)
                        return p.MainWindowHandle;
                }
            }
        }
        catch { /* 进程枚举失败按未找到处理 */ }
        return IntPtr.Zero;
    }

    /// <summary>截虚拟屏上一块区域为位图（调用方负责 Dispose）。失败返回 null。</summary>
    private static Bitmap? CaptureRegion(int x, int y, int width, int height)
    {
        Bitmap? bmp = null;
        try
        {
            bmp = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(x, y, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            }
            return bmp;
        }
        catch (Exception ex)
        {
            bmp?.Dispose();
            System.Diagnostics.Debug.WriteLine($"[ScreenshotService] 区域截图失败: {ex.Message}");
            return null;
        }
    }

    /// <summary>等比缩放到目标宽度并编码为 JPEG。不接管 source 的所有权（调用方释放）。</summary>
    private static byte[]? EncodeJpeg(Bitmap full, int targetWidth, long quality, out int outWidth, out int outHeight)
    {
        outWidth = 0;
        outHeight = 0;
        Bitmap? scaled = null;
        try
        {
            // 等比缩放到目标宽度
            if (targetWidth is < 160 or > 3840) targetWidth = 1280;
            if (targetWidth >= full.Width)
            {
                scaled = null; // 不缩放，直接编码原图
            }
            else
            {
                var targetH = (int)((long)full.Height * targetWidth / full.Width);
                scaled = new Bitmap(targetWidth, targetH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, targetWidth, targetH);
                }
            }
            var source = scaled ?? full;

            byte[] jpeg;
            using (var ms = new MemoryStream())
            {
                var encoder = GetJpegEncoder();
                if (encoder == null)
                {
                    source.Save(ms, ImageFormat.Png); // 理论上不会走到（JPEG 编码器 Windows 自带）
                }
                else
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                    source.Save(ms, encoder, ep);
                }
                jpeg = ms.ToArray();
            }

            outWidth = source.Width;
            outHeight = source.Height;
            return jpeg;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenshotService] JPEG 编码失败: {ex.Message}");
            return null;
        }
        finally
        {
            scaled?.Dispose();
        }
    }

    /// <summary>裁掉四边的纯黑带（PrintWindow 在加载过渡时内容缩在左上，右/下是纯黑）。
    /// 整帧纯黑（传送加载画面）或内容过小/黑边很小时不裁，返回原图（调用方按引用相等判断是否需要 Dispose）。
    /// 阈值：像素三通道全 &lt;8 视为黑；内容与边缘的 JPEG 噪点靠 4px 步进抽样容忍。</summary>
    private static Bitmap CropBlackBands(Bitmap src)
    {
        var w = src.Width;
        var h = src.Height;
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var stride = Math.Abs(data.Stride);
        var buf = new byte[stride * h];
        Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        src.UnlockBits(data);

        bool PixelBlack(int x, int y)
        {
            var o = y * stride + x * 3;
            return buf[o] < 8 && buf[o + 1] < 8 && buf[o + 2] < 8;
        }
        bool RowBlack(int y) { for (var x = 0; x < w; x += 4) if (!PixelBlack(x, y)) return false; return true; }
        bool ColBlack(int x) { for (var y = 0; y < h; y += 4) if (!PixelBlack(x, y)) return false; return true; }

        var top = 0;
        while (top < h - 1 && RowBlack(top)) top++;
        var bottom = h - 1;
        while (bottom > top && RowBlack(bottom)) bottom--;
        var left = 0;
        while (left < w - 1 && ColBlack(left)) left++;
        var right = w - 1;
        while (right > left && ColBlack(right)) right--;

        var cw = right - left + 1;
        var ch = bottom - top + 1;
        // 整帧黑（加载画面）/ 黑边 <2% / 裁完太小 → 不裁
        if (cw >= w && ch >= h) return src;
        if (cw < 320 || ch < 180) return src;
        if (cw >= w * 0.98 && ch >= h * 0.98) return src;

        var dst = new Bitmap(cw, ch, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(dst))
        {
            g.DrawImage(src, new Rectangle(0, 0, cw, ch), new Rectangle(left, top, cw, ch), GraphicsUnit.Pixel);
        }
        return dst;
    }

    /// <summary>清空历史帧。</summary>
    public void ClearHistory()
    {
        lock (_lock) _history.Clear();
    }

    private static ImageCodecInfo? GetJpegEncoder() =>
        ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.MimeType == "image/jpeg");

    // ---------- Win32 interop ----------
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    /// <summary>PrintWindow 标志：PW_RENDERFULLCONTENT（Win10 1903+，DWM 合成窗口含硬件加速内容也照绘）。</summary>
    private const uint PwRenderFullContent = 2;

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBlt, uint nFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
