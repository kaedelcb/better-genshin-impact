using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>按需获取成员主屏；并发观看者复用一次捕获，每个应答编号登记为该实际帧的别名。</summary>
public sealed class MemberScreenshotRelayService : IDisposable
{
    private const long ShareJpegQuality = 75L;
    private readonly ScreenshotService _screenshotService;
    private readonly DodocoSettingsService _settingsService;
    private readonly Func<SignalRClient?> _clientProvider;
    private readonly object _requestLock = new();
    private readonly List<PendingCapture> _pending = [];
    private SignalRClient? _hooked;
    private string? _hookContext;
    private Action<string, string>? _requestedHandler;
    private Action<MemberScreenshotFrame>? _frameHandler;
    private bool _captureRunning;
    private bool _disposed;
    private sealed record PendingCapture(SignalRClient Client, string Context, string RequestId);

    public event Action<MemberScreenshotFrame>? FrameReceived;

    public MemberScreenshotRelayService(ScreenshotService screenshotService,
        DodocoSettingsService settingsService, Func<SignalRClient?> clientProvider)
        => (_screenshotService, _settingsService, _clientProvider) = (screenshotService, settingsService, clientProvider);

    public async Task RequestFrameAsync(string targetUid, string sessionId)
    {
        try
        {
            EnsureHooked();
            var client = _clientProvider();
            if (_disposed || client?.IsConnected != true) return;
            await client.RequestMemberScreenshotAsync(targetUid, CreateRequestId(sessionId)).ConfigureAwait(false);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 请求截图失败: {ex.Message}"); }
    }

    internal static string CreateRequestId(string sessionId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _))
            throw new ArgumentException("监控会话标识必须为 Guid N。", nameof(sessionId));
        return $"screen:{sessionId}:{Guid.NewGuid():N}";
    }

    public void EnsureHooked()
    {
        var client = _clientProvider();
        lock (_requestLock)
        {
            if (_disposed || (ReferenceEquals(client, _hooked) && client?.DesktopControlContext == _hookContext)) return;
            Unhook();
            _pending.Clear();
            _hooked = client;
            _hookContext = client?.DesktopControlContext;
            if (client == null) return;
            var context = _hookContext!;
            _requestedHandler = (_, id) => HandleRequested(client, context, id);
            _frameHandler = frame =>
            {
                if (!IsCurrent(client, context)) return;
                try { FrameReceived?.Invoke(frame); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 帧分发失败: {ex.Message}"); }
            };
            client.OnMemberScreenshotRequested += _requestedHandler;
            client.OnMemberScreenshot += _frameHandler;
        }
    }

    private bool IsCurrent(SignalRClient client, string context)
        => !_disposed && ReferenceEquals(client, _clientProvider()) && client.IsConnected
            && context == client.DesktopControlContext;

    private void HandleRequested(SignalRClient client, string context, string requestId)
    {
        if (!IsCurrent(client, context) || string.IsNullOrEmpty(requestId)) return;
        lock (_requestLock)
        {
            if (_disposed || _pending.Count >= 64 || _pending.Any(x => x.RequestId == requestId)) return;
            _pending.Add(new(client, context, requestId));
            if (_captureRunning) return;
            _captureRunning = true;
        }
        _ = Task.Run(RespondToPendingAsync);
    }

    private async Task RespondToPendingAsync()
    {
        while (true)
        {
            PendingCapture[] requests;
            lock (_requestLock)
            {
                if (_disposed || _pending.Count == 0) { _captureRunning = false; return; }
                requests = _pending.ToArray();
                _pending.Clear();
            }
            try
            {
                var first = requests[0];
                if (!IsCurrent(first.Client, first.Context)) continue;
                var cfg = _settingsService.Current;
                var frame = _screenshotService.CaptureDesktopFrame(cfg.ShareScreenshotWidth, ShareJpegQuality);
                if (frame?.JpegBytes is not { Length: > 0 }) continue;
                var jpegBase64 = Convert.ToBase64String(frame.JpegBytes);
                foreach (var request in requests)
                {
                    if (!IsCurrent(request.Client, request.Context)) continue;
                    var allowed = _settingsService.Current.AllowDesktopControl && request.Client.DesktopControlReady
                        && frame.Geometry?.IsValid == true;
                    if (allowed)
                    {
                        _screenshotService.RememberControlFrame(request.RequestId, frame.Geometry!, request.Context);
                        allowed = _screenshotService.TryGetControlFrame(request.RequestId, request.Context, out _);
                    }
                    try
                    {
                        await request.Client.ReportMemberScreenshotExAsync(jpegBase64, frame.Width, frame.Height,
                            frame.Time, request.RequestId, frame.Geometry, allowed).ConfigureAwait(false);
                    }
                    catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 应答截图失败: {ex.Message}"); }
                }
            }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MemberScreenshotRelay] 截图失败: {ex.Message}"); }
        }
    }

    private void Unhook()
    {
        if (_hooked != null)
        {
            _hooked.OnMemberScreenshotRequested -= _requestedHandler;
            _hooked.OnMemberScreenshot -= _frameHandler;
        }
        _hooked = null;
        _hookContext = null;
    }

    public void Dispose()
    {
        lock (_requestLock)
        {
            _disposed = true;
            Unhook();
            _pending.Clear();
        }
    }
}
