using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 成员截图按需取图（嘟嘟可 P5 pull 模式）：
/// 1) 被查看端——收到 MemberScreenshotRequested 且开启"共享我的桌面截图"时，当场截一帧 JPEG
///    带 requestId 应答（宽度走统一设置 ShareScreenshotWidth，默认 1280，质量 75），服务端按映射单播回请求方；
/// 2) 观看端——RequestFrameAsync(targetUid) 向目标成员请求一帧，应答帧经 OnMemberScreenshot 回来；
/// 3) 接收——懒订阅 SignalRClient.OnMemberScreenshot/OnMemberScreenshotRequested（客户端实例懒解析，
///    实例更换时自动换绑；懒绑定由 DodocoViewModel.FlushPending 200ms 节拍驱动 EnsureHooked，与日志族同款）。
/// 尽力而为通道：截图/上报失败只记 Debug 日志，不影响主流程。
/// </summary>
public sealed class MemberScreenshotRelayService : IDisposable
{
    /// <summary>共享帧 JPEG 质量（75：清晰度够读屏上文字，单帧 1280px 约 100~180KB）。</summary>
    private const long ShareJpegQuality = 75L;

    private readonly ScreenshotService _screenshotService;
    private readonly DodocoSettingsService _settingsService;
    private readonly Func<SignalRClient?> _clientProvider;

    private SignalRClient? _hooked;
    private int _capturing; // 防重入：一帧截图未完成时跳过新请求

    /// <summary>收到成员截图帧（回调线程为 SignalR 线程，订阅方自行 Dispatcher 到 UI）。</summary>
    public event Action<MemberScreenshotFrame>? FrameReceived;

    public MemberScreenshotRelayService(
        ScreenshotService screenshotService,
        DodocoSettingsService settingsService,
        Func<SignalRClient?> clientProvider)
    {
        _screenshotService = screenshotService;
        _settingsService = settingsService;
        _clientProvider = clientProvider;
    }

    /// <summary>
    /// 观看端：请求目标成员的一帧桌面截图（生成一次性 requestId）。
    /// 应答帧经 FrameReceived 回来（按 uid 认领）；对方离线/旧版/未开共享时无应答，由调用方超时提示。
    /// </summary>
    public async Task RequestFrameAsync(string targetUid)
    {
        try
        {
            var client = _clientProvider();
            if (client?.IsConnected != true) return;
            var requestId = Guid.NewGuid().ToString("N");
            await client.RequestMemberScreenshotAsync(targetUid, requestId);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 请求截图失败: {ex.Message}");
        }
    }

    /// <summary>客户端实例懒解析换绑（_signalRClient 可能晚于本服务创建/被更换）。由外部节拍驱动。</summary>
    public void EnsureHooked()
    {
        var client = _clientProvider();
        if (ReferenceEquals(client, _hooked)) return;
        if (_hooked != null)
        {
            _hooked.OnMemberScreenshot -= HandleFrame;
            _hooked.OnMemberScreenshotRequested -= HandleRequested;
        }
        _hooked = client;
        if (_hooked != null)
        {
            _hooked.OnMemberScreenshot += HandleFrame;
            _hooked.OnMemberScreenshotRequested += HandleRequested;
        }
    }

    /// <summary>被查看端：有成员请求我的一帧桌面截图。仅在开启"共享我的桌面截图"且已连房时才截帧应答。</summary>
    private void HandleRequested(string requesterUid, string requestId)
    {
        try
        {
            var cfg = _settingsService.Current;
            if (!cfg.ShareDesktopScreenshot) return;
            var client = _clientProvider();
            if (client?.IsConnected != true) return;
            if (string.IsNullOrEmpty(requestId)) return;
            if (Interlocked.Exchange(ref _capturing, 1) != 0) return;
            // CopyFromScreen 可能耗时（几十~几百毫秒），放线程池截，避免堵 SignalR 回调线程
            Task.Run(async () =>
            {
                try
                {
                    var jpeg = _screenshotService.CaptureJpeg(cfg.ShareScreenshotWidth, ShareJpegQuality, out var w, out var h);
                    if (jpeg == null || jpeg.Length == 0) return;
                    await client.ReportMemberScreenshotExAsync(Convert.ToBase64String(jpeg), w, h, DateTime.Now, requestId);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 应答截图失败: {ex.Message}");
                }
                finally
                {
                    Interlocked.Exchange(ref _capturing, 0);
                }
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 处理截图请求失败: {ex.Message}");
        }
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
        if (_hooked != null)
        {
            _hooked.OnMemberScreenshot -= HandleFrame;
            _hooked.OnMemberScreenshotRequested -= HandleRequested;
            _hooked = null;
        }
    }
}
