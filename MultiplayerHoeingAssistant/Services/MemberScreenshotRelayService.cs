using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 成员截图按需取图（嘟嘟可 P5 pull 模式）：
/// 1) 被查看端——已连接房间时收到 MemberScreenshotRequested，当场截一帧 JPEG
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
    private readonly object _requestLock = new();
    private readonly List<string> _pendingRequestIds = [];
    private bool _captureRunning;

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
    /// 应答帧经 FrameReceived 回来（按 uid 认领）；对方离线/旧版或截图失败时无应答，由调用方超时提示。
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

    /// <summary>被查看端：有房间成员请求桌面截图，已连房时截帧应答。</summary>
    private void HandleRequested(string requesterUid, string requestId)
    {
        try
        {
            var client = _clientProvider();
            if (client?.IsConnected != true) return;
            if (string.IsNullOrEmpty(requestId)) return;
            lock (_requestLock)
            {
                // 同一成员可能被多位房间成员同时观看。合并并发请求到同一帧，
                // 每个 requestId 仍分别走服务端的一次性映射单播给原请求者。
                if (_pendingRequestIds.Count >= 64) return;
                _pendingRequestIds.Add(requestId);
                if (_captureRunning) return;
                _captureRunning = true;
            }
            // CopyFromScreen 可能耗时，放线程池截，避免堵 SignalR 回调线程。
            _ = Task.Run(() => RespondToPendingAsync(client));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 处理截图请求失败: {ex.Message}");
        }
    }

    private async Task RespondToPendingAsync(SignalRClient client)
    {
        while (true)
        {
            string? jpegBase64 = null;
            var width = 0;
            var height = 0;
            var capturedAt = DateTime.Now;
            try
            {
                var cfg = _settingsService.Current;
                var jpeg = _screenshotService.CaptureJpeg(cfg.ShareScreenshotWidth, ShareJpegQuality, out width, out height);
                if (jpeg is { Length: > 0 }) jpegBase64 = Convert.ToBase64String(jpeg);
                capturedAt = DateTime.Now;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 截图失败: {ex.Message}");
            }

            string[] requests;
            lock (_requestLock)
            {
                requests = _pendingRequestIds.ToArray();
                _pendingRequestIds.Clear();
            }
            if (jpegBase64 != null)
            {
                foreach (var requestId in requests)
                {
                    try
                    {
                        await client.ReportMemberScreenshotExAsync(jpegBase64, width, height, capturedAt, requestId);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 应答截图失败: {ex.Message}");
                    }
                }
            }
            lock (_requestLock)
            {
                if (_pendingRequestIds.Count != 0) continue;
                _captureRunning = false;
                return;
            }
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
