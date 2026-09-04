using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

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
}

/// <summary>
/// 桌面监控截图服务（嘟嘟可 P4 / F4）。
/// Graphics.CopyFromScreen 截主屏全屏 → 缩放到配置宽度 → JPEG(质量 ~70) 输出字节；
/// 环形保留最近 10 帧，超出丢弃（帧只存字节，无未释放位图）。
///
/// DPI 适配：进程启动时 App.xaml.cs 已调 DpiAwarenessController.EnsureDpiAware()
/// （SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE)），进程为 Per-Monitor 感知，
/// 因此此处用 GetSystemMetrics 取到的就是主屏物理像素，高分屏不会截不全。
///
/// 截图在调用方线程执行（约几十~几百毫秒），UI 层务必放后台线程调用，不要阻塞 UI。
/// 隐私：截图仅本地显示，不上传。
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

    /// <param name="thumbnailWidthProvider">缩略宽度提供者（读取统一设置，改宽度后下一帧生效）。</param>
    public ScreenshotService(Func<int> thumbnailWidthProvider)
    {
        _thumbnailWidthProvider = thumbnailWidthProvider;
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

        var jpeg = CaptureJpeg(targetW, JpegQuality, out var w, out var h);
        if (jpeg == null) return null;

        var frame = new ScreenFrame
        {
            Time = DateTime.Now,
            JpegBytes = jpeg,
            Width = w,
            Height = h
        };

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
        Bitmap? full = null;
        Bitmap? scaled = null;
        outWidth = 0;
        outHeight = 0;
        try
        {
            // Per-Monitor 感知进程下 GetSystemMetrics 返回物理像素（高分屏截全的关键）
            var w = GetSystemMetrics(SM_CXSCREEN);
            var h = GetSystemMetrics(SM_CYSCREEN);
            if (w <= 0 || h <= 0) return null;

            full = new Bitmap(w, h, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(full))
            {
                g.CopyFromScreen(0, 0, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
            }

            // 等比缩放到目标宽度
            if (targetWidth is < 160 or > 3840) targetWidth = 1280;
            if (targetWidth >= w)
            {
                scaled = full;
                full = null; // 不缩放，直接用原图（所有权转给 scaled）
            }
            else
            {
                var targetH = (int)((long)h * targetWidth / w);
                scaled = new Bitmap(targetWidth, targetH, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(full, 0, 0, targetWidth, targetH);
                }
            }

            byte[] jpeg;
            using (var ms = new MemoryStream())
            {
                var encoder = GetJpegEncoder();
                if (encoder == null)
                {
                    scaled.Save(ms, ImageFormat.Png); // 理论上不会走到（JPEG 编码器 Windows 自带）
                }
                else
                {
                    using var ep = new EncoderParameters(1);
                    ep.Param[0] = new EncoderParameter(Encoder.Quality, quality);
                    scaled.Save(ms, encoder, ep);
                }
                jpeg = ms.ToArray();
            }

            outWidth = scaled.Width;
            outHeight = scaled.Height;
            return jpeg;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ScreenshotService] 截图失败: {ex.Message}");
            return null;
        }
        finally
        {
            // Bitmap 是 IDisposable，必须释放（无论成功失败）
            full?.Dispose();
            scaled?.Dispose();
        }
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
}
