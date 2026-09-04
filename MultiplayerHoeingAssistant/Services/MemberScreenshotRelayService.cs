using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 成员截图汇聚（嘟嘟可 P5 远程巡检墙）：
/// 1) 上报——开启"共享我的桌面截图"且已连房时，每 10 秒截一帧 JPEG 上报给房间
///    （宽度走统一设置 ShareScreenshotWidth，默认 1280，质量 75）；
/// 2) 接收——懒订阅 SignalRClient.OnMemberScreenshot（客户端实例懒解析，实例更换时自动换绑），
///    收到帧后触发 FrameReceived（线程不保证是 UI 线程，订阅方自行切回 UI）。
/// 尽力而为通道：截图/上报失败只记 Debug 日志，不影响主流程。
/// </summary>
public sealed class MemberScreenshotRelayService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);
    /// <summary>共享帧 JPEG 质量（75：清晰度够读屏上文字，单帧 1280px 约 100~180KB，10s 一帧带宽可忽略）。</summary>
    private const long ShareJpegQuality = 75L;

    private readonly ScreenshotService _screenshotService;
    private readonly DodocoSettingsService _settingsService;
    private readonly Func<SignalRClient?> _clientProvider;
    private readonly Timer _timer;

    private SignalRClient? _hooked;
    private int _capturing; // 防重入：一帧截图未完成时跳过下一拍

    /// <summary>收到成员截图帧（回调线程为 SignalR/定时器线程，订阅方自行 Dispatcher 到 UI）。</summary>
    public event Action<MemberScreenshotFrame>? FrameReceived;

    public MemberScreenshotRelayService(
        ScreenshotService screenshotService,
        DodocoSettingsService settingsService,
        Func<SignalRClient?> clientProvider)
    {
        _screenshotService = screenshotService;
        _settingsService = settingsService;
        _clientProvider = clientProvider;
        // 首拍 10 秒后触发，避免窗口刚打开就抢资源
        _timer = new Timer(Tick, null, Interval, Interval);
    }

    private void Tick(object? state)
    {
        try
        {
            EnsureHooked();

            var client = _clientProvider();
            var cfg = _settingsService.Current;
            if (!cfg.ShareDesktopScreenshot) return;
            if (client?.IsConnected != true) return;
            if (Interlocked.Exchange(ref _capturing, 1) != 0) return;
            try
            {
                // CopyFromScreen 可能耗时，Timer 回调本身就是线程池线程，直接在这里截
                var jpeg = _screenshotService.CaptureJpeg(cfg.ShareScreenshotWidth, ShareJpegQuality, out var w, out var h);
                if (jpeg == null || jpeg.Length == 0) return;
                _ = client.ReportMemberScreenshotAsync(Convert.ToBase64String(jpeg), w, h, DateTime.Now);
            }
            finally
            {
                Interlocked.Exchange(ref _capturing, 0);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 上报失败: {ex.Message}");
        }
    }

    /// <summary>客户端实例懒解析换绑（_signalRClient 可能晚于本服务创建）。</summary>
    private void EnsureHooked()
    {
        var client = _clientProvider();
        if (ReferenceEquals(client, _hooked)) return;
        if (_hooked != null) _hooked.OnMemberScreenshot -= HandleFrame;
        _hooked = client;
        if (_hooked != null) _hooked.OnMemberScreenshot += HandleFrame;
    }

    private void HandleFrame(MemberScreenshotFrame frame)
    {
        try { FrameReceived?.Invoke(frame); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 帧分发失败: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        if (_hooked != null)
        {
            _hooked.OnMemberScreenshot -= HandleFrame;
            _hooked = null;
        }
    }
}
