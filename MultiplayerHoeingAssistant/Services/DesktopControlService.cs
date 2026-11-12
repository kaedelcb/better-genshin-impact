using BgiCoordinatorServer.Models;
using MultiplayerHoeingAssistant.Services.Gateway;
using System.Windows.Threading;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>低频桌面输入的独立即时通道。单执行闸门不等待、不重试；观看端等待实际执行回执。</summary>
public class DesktopControlService : IDisposable
{
    private const int MaxClaims = 8192;
    private readonly ScreenshotService _screenshots;
    private readonly DodocoSettingsService _settings;
    private readonly Func<SignalRClient?> _clientProvider;
    private readonly DesktopInputSender _sender;
    private readonly TimeProvider _time;
    private readonly IRemoteControlPresence _presence;
    private readonly Dictionary<string, (DesktopControlPresenceRequest Request, TaskCompletionSource<DesktopControlPresenceResult> Completion)> _presencePending = new(StringComparer.Ordinal);
    private Action<DesktopControlPresenceRequest>? _presenceRequested;
    private Action<DesktopControlPresenceResult>? _presenceResult;
    private readonly object _state = new();
    private readonly SemaphoreSlim _execution = new(1, 1);
    private readonly Dictionary<string, Claim> _claims = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _sentIds = new(StringComparer.Ordinal);
    private static readonly TimeSpan ClaimRetention = TimeSpan.FromMinutes(3);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private SignalRClient? _hooked;
    private string? _hookContext;
    private Action<DesktopControlRequest>? _requested;
    private Action<DesktopControlResult>? _result;
    private Action<string>? _cancelled;
    private Action? _contextChanged;
    private long? _lastInput;
    private bool _disposed;
    private sealed class Claim(string? context, long createdAt)
    {
        public readonly string? Context = context;
        public readonly long CreatedAt = createdAt;
        public CancellationTokenSource? Cancellation;
        public bool Cancelled;
    }
    private sealed class Pending(DesktopControlCommand command, SignalRClient client, string context)
    {
        public readonly DesktopControlCommand Command = command;
        public readonly SignalRClient Client = client;
        public readonly string Context = context;
        public readonly TaskCompletionSource<DesktopControlResult> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class Rejected(string status, string message) : Exception(message)
    {
        public readonly string Status = status;
    }

    public DesktopControlService(ScreenshotService screenshots, DodocoSettingsService settings,
        Func<SignalRClient?> clientProvider, DesktopInputSender? inputSender = null, TimeProvider? timeProvider = null,
        IRemoteControlPresence? presence = null)
    {
        (_screenshots, _settings, _clientProvider) = (screenshots, settings, clientProvider);
        _sender = inputSender ?? new DesktopInputSender();
        _time = timeProvider ?? TimeProvider.System;
        _presence = presence ?? new RemoteControlPresence(Dispatcher.CurrentDispatcher, _time);
        _settings.SettingsChanged += SettingsChanged;
        EnsureHooked();
    }

    public virtual void EnsureHooked()
    {
        var client = _clientProvider();
        lock (_state)
        {
            var context = client?.DesktopControlContext;
            if (_disposed || (ReferenceEquals(client, _hooked) && context == _hookContext)) return;
            EndContextLocked();
            UnhookLocked();
            _hooked = client;
            _hookContext = context;
            if (client == null) return;
            _requested = request => { _ = HandleRequestedAsync(client, context!, request); };
            _result = result => HandleResult(client, context!, result);
            _presenceRequested = request => { _ = HandlePresenceRequestedAsync(client, context!, request); };
            _presenceResult = result => HandlePresenceResult(client, context!, result);
            client.OnDesktopControlPresenceRequested += _presenceRequested;
            client.OnDesktopControlPresenceResult += _presenceResult;
            _cancelled = id => { if (IsCurrent(client, context!)) CancelLocal(id); };
            _contextChanged = () =>
            {
                lock (_state)
                    if (ReferenceEquals(_hooked, client) && _hookContext == context) EndContextLocked();
            };
            client.OnDesktopControlRequested += _requested;
            client.OnDesktopControlResult += _result;
            client.OnDesktopControlCancelled += _cancelled;
            client.DesktopControlContextChanged += _contextChanged;
        }
    }

    private static string RemoteOwner(string context, string requester) => $"remote:{context}:{requester}:";
    private static string BrowserOwner(string session) => $"browser:{session}:";

    public virtual Task<DesktopControlPresenceResult> StartPresenceAsync(string targetUid, string frameId, string controlId,
        CancellationToken cancellationToken = default)
        => ChangePresenceAsync(targetUid, frameId, controlId, "start", cancellationToken);

    public virtual Task<DesktopControlPresenceResult> RenewPresenceAsync(string targetUid, string frameId, string controlId,
        CancellationToken cancellationToken = default)
        => ChangePresenceAsync(targetUid, frameId, controlId, "renew", cancellationToken);

    public virtual async Task EndPresenceAsync(string targetUid, string frameId, string controlId)
        => _ = await ChangePresenceAsync(targetUid, frameId, controlId, "end", CancellationToken.None).ConfigureAwait(false);

    internal Task<DesktopControlPresenceResult> ChangeBrowserPresenceAsync(string session, DesktopControlPresenceRequest request,
        CancellationToken cancellationToken = default)
        => ApplyPresenceAsync(BrowserOwner(session), request with { LifetimeMilliseconds = DesktopControlProtocol.PresenceLifetime.TotalMilliseconds }, null, cancellationToken);

    internal Task ClearBrowserPresenceAsync(string session) => _presence.ClearOwnerPrefixAsync(BrowserOwner(session));

    private async Task<DesktopControlPresenceResult> ChangePresenceAsync(string target, string frame, string controlId,
        string action, CancellationToken cancellationToken)
    {
        var request = new DesktopControlPresenceRequest { RequestId = Guid.NewGuid().ToString("N"), TargetUid = target,
            FrameId = frame, ControlId = controlId, Action = action, ExpiresAtUtc = _time.GetUtcNow() + DesktopControlProtocol.RequestLifetime,
            LifetimeMilliseconds = DesktopControlProtocol.PresenceLifetime.TotalMilliseconds };
        if (target == "local") return await ApplyPresenceAsync("local-pc", request, null, cancellationToken).ConfigureAwait(false);
        EnsureHooked();
        var client = _clientProvider();
        if (client == null || !client.DesktopControlReady) return PresenceResult(request, "denied", "控制房间尚未连接");
        var context = client.DesktopControlContext;
        if (action == "end")
        {
            try { using var timeout = new CancellationTokenSource(DesktopControlProtocol.RequestLifetime, _time);
                await client.RequestDesktopControlPresenceAsync(request, timeout.Token).ConfigureAwait(false); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DesktopControl] 结束提示未确认: {ex.Message}"); }
            return PresenceResult(request, "ended", "控制已关闭");
        }
        var completion = new TaskCompletionSource<DesktopControlPresenceResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_state)
        {
            if (!IsCurrent(client, context)) return PresenceResult(request, "denied", "控制连接已变化");
            _presencePending.Add(request.RequestId, (request, completion));
        }
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            _ = SendPresenceRequestAsync(client, context, request, completion, sendCancellation.Token);
            var result = await completion.Task.WaitAsync(DesktopControlProtocol.RequestLifetime, _time, cancellationToken).ConfigureAwait(false);
            return result.ControlId == controlId && result.TargetUid == target ? result : PresenceResult(request, "denied", "控制确认不匹配");
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        { return PresenceResult(request, "denied", "等待对方确认超时或已取消"); }
        finally { sendCancellation.Cancel(); lock (_state) _presencePending.Remove(request.RequestId); }
    }

    private async Task SendPresenceRequestAsync(SignalRClient client, string context, DesktopControlPresenceRequest request,
        TaskCompletionSource<DesktopControlPresenceResult> completion, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrent(client, context)) throw new InvalidOperationException("控制连接已变化");
            await client.RequestDesktopControlPresenceAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { completion.TrySetResult(PresenceResult(request, "denied", ex.Message)); }
    }

    private void HandlePresenceResult(SignalRClient client, string context, DesktopControlPresenceResult result)
    {
        if (!IsCurrent(client, context)) return;
        lock (_state)
            if (_presencePending.TryGetValue(result.RequestId, out var pending)
                && pending.Request.TargetUid == result.TargetUid && pending.Request.ControlId == result.ControlId
                && result.Status is "active" or "denied")
                pending.Completion.TrySetResult(result);
    }

    private async Task HandlePresenceRequestedAsync(SignalRClient client, string context, DesktopControlPresenceRequest request)
    {
        try
        {
            if (!IsCurrent(client, context) || request.RoomCode != client.DesktopControlRoomCode
                || string.IsNullOrWhiteSpace(request.RequesterConnectionId)) return;
            var result = await ApplyPresenceAsync(RemoteOwner(context, request.RequesterConnectionId), request, context, CancellationToken.None).ConfigureAwait(false);
            if (request.Action != "end" && IsCurrent(client, context))
            {
                using var timeout = new CancellationTokenSource(DesktopControlProtocol.RequestLifetime, _time);
                await client.ReportDesktopControlPresenceAsync(result, timeout.Token).ConfigureAwait(false);
            }
            if (!IsCurrent(client, context)) await _presence.ClearOwnerPrefixAsync(RemoteOwner(context, request.RequesterConnectionId)).ConfigureAwait(false);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DesktopControl] 提示请求失败: {ex.Message}"); }
    }

    private async Task<DesktopControlPresenceResult> ApplyPresenceAsync(string owner, DesktopControlPresenceRequest request,
        string? context, CancellationToken cancellationToken)
    {
        if (!Guid.TryParseExact(request.RequestId, "N", out _) || !Guid.TryParseExact(request.ControlId, "N", out _)
            || (context == null ? request.TargetUid != "local" : !TrySession(request.FrameId, out _)))
            return PresenceResult(request, "denied", "控制提示参数无效");
        if (request.Action == "end")
        {
            await _presence.DeactivateAsync(owner, request.ControlId).ConfigureAwait(false);
            return PresenceResult(request, "ended", "控制已关闭");
        }
        var started = _time.GetTimestamp();
        if (request.Action is not ("start" or "renew") || !_settings.Current.AllowDesktopControl || _disposed)
            return PresenceResult(request, "denied", "本机未授权桌面控制");
        var milliseconds = request.LifetimeMilliseconds;
        if (!double.IsFinite(milliseconds) || milliseconds <= 0 || milliseconds > DesktopControlProtocol.PresenceLifetime.TotalMilliseconds)
            return PresenceResult(request, "denied", "控制租约时限无效");
        var lifetime = TimeSpan.FromMilliseconds(milliseconds);
        var budget = lifetime < DesktopControlProtocol.RequestLifetime ? lifetime : DesktopControlProtocol.RequestLifetime;
        if (context == null)
        {
            // 本机浏览器与助手使用同机时钟；HTTP 排队不能重新开始三秒确认时限。
            var remaining = request.ExpiresAtUtc - _time.GetUtcNow();
            if (remaining < budget) budget = remaining;
        }
        if (request.Action == "start")
        {
            if (context == null)
            {
                if (!_screenshots.TryGetControlFrame(request.FrameId, out _))
                    return PresenceResult(request, "denied", "画面已过期，请刷新");
            }
            else
            {
                if (!double.IsFinite(request.FrameAgeMilliseconds) || request.FrameAgeMilliseconds < 0
                    || request.FrameAgeMilliseconds >= DesktopControlProtocol.FrameLifetime.TotalMilliseconds
                    || !_screenshots.TryGetControlFrameAge(request.FrameId, context, out var captureAge))
                    return PresenceResult(request, "denied", "画面已过期，请刷新");
                // 目标的截图捕获早于服务器收到该帧；两段单调帧龄之差保守包含网络延迟。
                var transport = captureAge - TimeSpan.FromMilliseconds(request.FrameAgeMilliseconds);
                if (transport > TimeSpan.Zero) { budget -= transport; lifetime -= transport; }
            }
        }
        budget -= _time.GetElapsedTime(started);
        if (budget <= TimeSpan.Zero || lifetime <= TimeSpan.Zero)
            return PresenceResult(request, "denied", "控制提示确认已超时");
        using var timeout = new CancellationTokenSource(budget, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var active = false;
        try
        {
            active = await _presence.ActivateAsync(owner, request.ControlId, lifetime,
                request.Action == "renew", linked.Token).ConfigureAwait(false);
            if (linked.IsCancellationRequested || !_settings.Current.AllowDesktopControl || _disposed
                || (context != null && (_hooked == null || !IsCurrent(_hooked, context))))
                active = false;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        if (!active) await _presence.DeactivateAsync(owner, request.ControlId).ConfigureAwait(false);
        return PresenceResult(request, active ? "active" : "denied", active ? "控制提示已显示" : "控制提示未显示或已失效");
    }

    private static DesktopControlPresenceResult PresenceResult(DesktopControlPresenceRequest request, string status, string message)
        => new() { RequestId = request.RequestId, ControlId = request.ControlId, TargetUid = request.TargetUid, Status = status, Message = message };

    private bool IsCurrent(SignalRClient client, string context)
        => !_disposed && ReferenceEquals(client, _clientProvider()) && client.DesktopControlReady
            && client.DesktopControlContext == context;

    public virtual async Task<DesktopControlResult> RequestAsync(DesktopControlCommand command,
        CancellationToken cancellationToken = default)
    {
        if (!DesktopControlProtocol.IsValid(command) || command.TargetUid == "local")
            return Result(command, "denied", "无效的远程输入");
        EnsureHooked();
        var client = _clientProvider();
        if (client == null || !client.DesktopControlReady)
            return Result(command, "denied", "控制房间尚未连接");
        var context = client.DesktopControlContext;
        var pending = new Pending(command, client, context);
        lock (_state)
        {
            if (_disposed || !IsCurrent(client, context)) return Result(command, "denied", "控制连接已变化");
            PruneClaimsLocked();
            if (_sentIds.ContainsKey(command.RequestId)) return Result(command, "denied", "该请求编号已使用，不会再次发送");
            if (_sentIds.Count >= MaxClaims || _pending.Count >= 64) return Result(command, "busy", "控制请求数已达上限");
            _sentIds.Add(command.RequestId, _time.GetTimestamp());
            _pending.Add(command.RequestId, pending);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            RemovePending(pending);
            return Result(command, "cancelled", "操作已取消");
        }
        using var sendCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        // 发送与等待共享三秒窗口。ACK 不完成 Completion；只有 result 事件能确认 executed。
        _ = SendRequestAsync(pending, sendCancellation.Token);
        var needsCancel = false;
        try
        {
            var result = await pending.Completion.Task.WaitAsync(DesktopControlProtocol.RequestLifetime, _time, cancellationToken).ConfigureAwait(false);
            needsCancel = result.Status is "unknown" or "cancelled";
            return result;
        }
        catch (TimeoutException)
        {
            needsCancel = true;
            return Result(command, "unknown", "三秒内未收到执行回执，请刷新画面核对；不会重发输入");
        }
        catch (OperationCanceledException)
        {
            needsCancel = true;
            return Result(command, "cancelled", "操作已取消；已发出的输入无法撤回");
        }
        finally
        {
            sendCancellation.Cancel();
            RemovePending(pending);
            if (needsCancel && TrySession(command.FrameId, out var session))
                _ = CancelTransportAsync(client, context, session, command.TargetUid, command.ControlId, command.RequestId);
        }
    }

    private async Task SendRequestAsync(Pending pending, CancellationToken cancellationToken)
    {
        try
        {
            if (!IsCurrent(pending.Client, pending.Context)) throw new InvalidOperationException("控制连接已变化");
            await pending.Client.RequestDesktopControlAsync(pending.Command, cancellationToken).ConfigureAwait(false);
        }
        catch (GatewayErrorException ex)
        {
            pending.Completion.TrySetResult(Result(pending.Command, ex.Code == "transport_unknown" ? "unknown" : "denied", ex.Message));
        }
        catch (Exception ex)
        {
            pending.Completion.TrySetResult(Result(pending.Command, "unknown", "输入发送结果未确认：" + ex.Message));
        }
    }

    private void HandleResult(SignalRClient client, string context, DesktopControlResult result)
    {
        if (!IsCurrent(client, context)) return;
        lock (_state)
        {
            if (!_pending.TryGetValue(result.RequestId, out var pending) || !ReferenceEquals(pending.Client, client)
                || pending.Context != context || pending.Command.TargetUid != result.TargetUid) return;
            if (result.Status is not ("executed" or "denied" or "error" or "expired" or "busy" or "cancelled")) return;
            pending.Completion.TrySetResult(result);
        }
    }

    private void RemovePending(Pending pending)
    {
        lock (_state)
            if (_pending.TryGetValue(pending.Command.RequestId, out var current) && ReferenceEquals(current, pending))
                _pending.Remove(pending.Command.RequestId);
    }

    public virtual Task<DesktopControlResult> ExecuteLocalAsync(DesktopControlCommand command,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, expiresAtUtc, cancellationToken, null, null, owner: "local-pc");

    internal Task<DesktopControlResult> ExecuteBrowserAsync(string sessionId, DesktopControlCommand command,
        DateTimeOffset expiresAtUtc, CancellationToken cancellationToken = default)
        => ExecuteAsync(command, expiresAtUtc, cancellationToken, null, null, owner: BrowserOwner(sessionId));

    private async Task HandleRequestedAsync(SignalRClient client, string context, DesktopControlRequest request)
    {
        try
        {
            if (!IsCurrent(client, context) || request.RoomCode != client.DesktopControlRoomCode
                || string.IsNullOrWhiteSpace(request.RequesterConnectionId)) return;
            var budgetStarted = _time.GetTimestamp();
            DesktopControlResult result;
            if (!DesktopControlProtocol.IsValid(request.Command) || !double.IsFinite(request.FrameAgeMilliseconds) || request.FrameAgeMilliseconds < 0
                || request.FrameAgeMilliseconds >= DesktopControlProtocol.FrameLifetime.TotalMilliseconds
                || !double.IsFinite(request.LifetimeMilliseconds) || request.LifetimeMilliseconds <= 0
                || request.LifetimeMilliseconds > DesktopControlProtocol.RequestLifetime.TotalMilliseconds)
                result = Result(request.Command, "denied", "控制时限无效，请更新服务器并刷新画面");
            else if (!_screenshots.TryGetControlFrameAge(request.Command.FrameId, context, out var captureAge))
                result = Result(request.Command, "denied", "画面过期或不属于当前连接，请刷新");
            else
            {
                // targetCapture <= serverReceived；从目标捕获时刻锚定的截止只会更早，
                // 因而上传、指令传输及调度耗时都计入预算，服务器与目标墙钟偏差不影响判定。
                var budget = TimeSpan.FromMilliseconds(request.FrameAgeMilliseconds + request.LifetimeMilliseconds) - captureAge;
                result = budget <= TimeSpan.Zero
                    ? Result(request.Command, "expired", "输入指令已超过有效期，请刷新画面")
                    : await ExecuteAsync(request.Command, request.ExpiresAtUtc, CancellationToken.None, client, context,
                        budget, budgetStarted, RemoteOwner(context, request.RequesterConnectionId)).ConfigureAwait(false);
            }
            if (IsCurrent(client, context))
            {
                using var timeout = new CancellationTokenSource(DesktopControlProtocol.RequestLifetime, _time);
                await client.ReportDesktopControlAsync(result, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DesktopControl] 回执未确认: {ex.Message}"); }
    }

    private async Task<DesktopControlResult> ExecuteAsync(DesktopControlCommand command, DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken, SignalRClient? source, string? context,
        TimeSpan? relativeBudget = null, long budgetStarted = 0, string owner = "local-pc")
    {
        if (!DesktopControlProtocol.IsValid(command)
            || (source == null ? command.TargetUid != "local" || !Guid.TryParseExact(command.FrameId, "N", out _)
                : command.TargetUid != source.DesktopControlUid || !TrySession(command.FrameId, out _)))
            return Result(command, "denied", "输入目标或参数无效");
        var started = _time.GetTimestamp();
        var remaining = relativeBudget.HasValue
            ? relativeBudget.Value - _time.GetElapsedTime(budgetStarted, started)
            : expiresAtUtc - _time.GetUtcNow();
        if (remaining > DesktopControlProtocol.RequestLifetime) remaining = DesktopControlProtocol.RequestLifetime;
        Claim claim;
        lock (_state)
        {
            if (_disposed) return Result(command, "denied", "桌面控制已关闭");
            PruneClaimsLocked();
            if (_claims.TryGetValue(command.RequestId, out var previous))
                return Result(command, previous.Cancelled ? "cancelled" : "denied", "该请求已取消或处理，不会再次执行");
            if (_claims.Count >= MaxClaims) return Result(command, "busy", "控制请求数已达上限");
            claim = new Claim(context, _time.GetTimestamp()) { Cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) };
            _claims.Add(command.RequestId, claim);
        }
        if (!_execution.Wait(0))
        {
            FinishClaim(claim);
            return Result(command, "busy", "正在执行其他输入，本次不排队");
        }
        try
        {
            return await Task.Run(() =>
            {
                try
                {
                    var token = claim.Cancellation!.Token;
                    Validate();
                    if (!_screenshots.TryGetControlFrame(command.FrameId, context, out var geometry) || geometry?.IsValid != true)
                        throw new Rejected("denied", "画面过期、已使用或不属于当前控制连接，请刷新");
                    if (_screenshots.GetCurrentGeometry() != geometry)
                        throw new Rejected("denied", "显示布局已变化，请刷新画面");
                    Validate();
                    var now = _time.GetTimestamp();
                    if (_lastInput is { } last && _time.GetElapsedTime(last, now) < DesktopControlProtocol.InputInterval)
                        throw new Rejected("busy", "每个被控端每两秒只能操作一次");
                    if (!_screenshots.TryConsumeControlFrame(command.FrameId))
                        throw new Rejected("denied", "画面已过期或使用，请刷新");
                    _lastInput = now;
                    _sender.Send(command, geometry, token, () =>
                    {
                        Validate();
                        if (!_screenshots.IsControlFrameFresh(command.FrameId, geometry))
                            throw new Rejected("expired", "画面已过期，请刷新");
                    });
                    return Result(command, "executed", "系统已接受完整输入，请刷新画面确认效果");

                    void Validate()
                    {
                        if (!_settings.Current.AllowDesktopControl || !_presence.IsActive(owner, command.ControlId))
                            throw new Rejected("denied", "本机未授权或远程控制提示已失效");
                        token.ThrowIfCancellationRequested();
                        if (_disposed || (source != null && !IsCurrent(source, context!)))
                            throw new Rejected("cancelled", "控制连接已变化，操作已取消");
                        if (remaining <= TimeSpan.Zero || _time.GetElapsedTime(started) >= remaining
                            || (!relativeBudget.HasValue && _time.GetUtcNow() >= expiresAtUtc))
                            throw new Rejected("expired", "输入指令已超过三秒有效期");
                    }
                }
                catch (Rejected ex) { return Result(command, ex.Status, ex.Message); }
                catch (OperationCanceledException)
                {
                    return Result(command, _settings.Current.AllowDesktopControl ? "cancelled" : "denied",
                        "操作已取消或授权已撤销；已发出的输入无法撤回");
                }
                catch (Exception ex) { return Result(command, "error", "输入未能完整执行：" + ex.Message); }
            }).ConfigureAwait(false);
        }
        finally { FinishClaim(claim); _execution.Release(); }
    }

    // 去重窗口覆盖整次观看；终态旧帧早已失效，长期运行无需累计到重启才释放。
    // 尚在执行的 claim 永不逐出，取消仍能触达其清理逻辑。
    private void PruneClaimsLocked()
    {
        var now = _time.GetTimestamp();
        foreach (var key in _claims.Where(x => x.Value.Cancellation == null
                     && _time.GetElapsedTime(x.Value.CreatedAt, now) >= ClaimRetention).Select(x => x.Key).ToArray())
            _claims.Remove(key);
        foreach (var key in _sentIds.Where(x => !_pending.ContainsKey(x.Key)
                     && _time.GetElapsedTime(x.Value, now) >= ClaimRetention).Select(x => x.Key).ToArray())
            _sentIds.Remove(key);
    }

    private void FinishClaim(Claim claim)
    {
        lock (_state)
        {
            claim.Cancellation?.Dispose();
            claim.Cancellation = null;
        }
    }

    /// <summary>显式取消也保存墓碑：HTTP input 即使晚于 cancel 到达也不能执行。</summary>
    public virtual void CancelLocal(string requestId)
    {
        if (!Guid.TryParseExact(requestId, "N", out _)) return;
        lock (_state)
        {
            if (_disposed) return;
            PruneClaimsLocked();
            if (!_claims.TryGetValue(requestId, out var claim))
            {
                if (_claims.Count >= MaxClaims) return;
                _claims.Add(requestId, claim = new Claim(null, _time.GetTimestamp()));
            }
            claim.Cancelled = true;
            claim.Cancellation?.Cancel();
        }
    }

    public virtual async Task CancelAsync(string sessionId, string targetUid, string controlId)
    {
        if (!Guid.TryParseExact(sessionId, "N", out _) || !Guid.TryParseExact(controlId, "N", out _)
            || targetUid is not { Length: > 0 and <= 128 }) return;
        EnsureHooked();
        lock (_state)
            foreach (var pending in _pending.Values.Where(x => TrySession(x.Command.FrameId, out var session)
                && session == sessionId && x.Command.TargetUid == targetUid && x.Command.ControlId == controlId))
                pending.Completion.TrySetResult(Result(pending.Command, "cancelled", "监控控制模式已关闭"));
        var client = _clientProvider();
        if (client != null && targetUid != "local")
            await CancelTransportAsync(client, client.DesktopControlContext, sessionId, targetUid, controlId).ConfigureAwait(false);
    }

    private async Task CancelTransportAsync(SignalRClient client, string context, string sessionId, string targetUid, string controlId, string? requestId = null)
    {
        try
        {
            if (!IsCurrent(client, context)) return;
            using var timeout = new CancellationTokenSource(DesktopControlProtocol.RequestLifetime, _time);
            await client.CancelDesktopControlAsync(sessionId, targetUid, controlId, requestId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[DesktopControl] 取消未确认（指令仍受三秒截止限制）: {ex.Message}"); }
    }

    private void SettingsChanged()
    {
        if (_settings.Current.AllowDesktopControl) return;
        _ = _presence.ClearAsync();
        lock (_state)
            foreach (var claim in _claims.Values) claim.Cancellation?.Cancel();
    }

    private void EndContextLocked()
    {
        _ = _presence.ClearOwnerPrefixAsync("remote:");
        foreach (var pending in _presencePending.Values)
            pending.Completion.TrySetResult(PresenceResult(pending.Request, "denied", "控制连接已变化"));
        _presencePending.Clear();
        foreach (var pending in _pending.Values)
            pending.Completion.TrySetResult(Result(pending.Command, "unknown", "控制连接或房间已变化，执行结果未确认，请刷新画面"));
        _pending.Clear();
        foreach (var claim in _claims.Values.Where(x => x.Context != null)) claim.Cancellation?.Cancel();
    }

    private void UnhookLocked()
    {
        if (_hooked == null) return;
        _hooked.OnDesktopControlPresenceRequested -= _presenceRequested;
        _hooked.OnDesktopControlPresenceResult -= _presenceResult;
        _hooked.OnDesktopControlRequested -= _requested;
        _hooked.OnDesktopControlResult -= _result;
        _hooked.OnDesktopControlCancelled -= _cancelled;
        _hooked.DesktopControlContextChanged -= _contextChanged;
        _hooked = null;
        _hookContext = null;
    }

    private static bool TrySession(string? frameId, out string session)
    {
        session = "";
        if (frameId is not { Length: 72 } || !frameId.StartsWith("screen:", StringComparison.Ordinal)
            || frameId[39] != ':' || !Guid.TryParseExact(frameId.Substring(7, 32), "N", out _)
            || !Guid.TryParseExact(frameId[40..], "N", out _)) return false;
        session = frameId.Substring(7, 32);
        return true;
    }

    private static DesktopControlResult Result(DesktopControlCommand? command, string status, string message)
        => new() { RequestId = command?.RequestId ?? "", TargetUid = command?.TargetUid ?? "", Status = status,
            Message = message.Length > 1000 ? message[..1000] : message };

    public virtual void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            _settings.SettingsChanged -= SettingsChanged;
            EndContextLocked();
            foreach (var claim in _claims.Values) claim.Cancellation?.Cancel();
            UnhookLocked();
            _presence.Dispose();
            // 运行中的同步原生序列在 finally 释放合成键及执行闸门。
        }
    }
}
