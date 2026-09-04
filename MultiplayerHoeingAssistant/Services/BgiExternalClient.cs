using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>ext.* 链路状态：Down=未连接/断线中；Legacy=对端是老版本 BGI（无 ext.hello）；Ready=握手成功。</summary>
public enum BgiExternalLinkState
{
    Down,
    Legacy,
    Ready,
}

/// <summary>
/// [切片4] SDK 连接状态机（《通信方案》§3.2 会话层状态机的客户端半边）：
/// Connecting（建立/重试连接）→ Handshaking（ext.hello 协商中）→ Ready（通道可用）
/// → Degraded（曾就绪后断开 / 握手失败降级 / 对端老 BGI，可能恢复）→ Closed（Dispose 终态）。
/// 初始值 Degraded（尚未连接 = 不可用）；跨会话等不可恢复错误也落 Degraded（由调用方退避后重建 client 再探测）。
/// </summary>
public enum BgiExternalConnectionState
{
    Connecting,
    Handshaking,
    Ready,
    Degraded,
    Closed,
}

/// <summary>ext.event 事件名常量（与 BGI 侧 ExternalInterfaceEventNames 对齐）。</summary>
public static class BgiExternalEventNames
{
    public const string TaskStarted = "task.started";
    public const string TaskProgress = "task.progress";
    public const string TaskStopped = "task.stopped";
    public const string HoeingProgress = "hoeing.progress";
    public const string OnlineTriggered = "online.triggered";
    public const string TaskSuspended = "task.suspended";
    public const string TaskResumed = "task.resumed";

    public static readonly string[] All =
    [
        TaskStarted,
        TaskProgress,
        TaskStopped,
        HoeingProgress,
        OnlineTriggered,
        TaskSuspended,
        TaskResumed,
    ];
}

/// <summary>一条 ext.event 事件帧（服务端主动推送，Notification 语义）。</summary>
public sealed class BgiExternalEvent
{
    public required string Name { get; init; }

    /// <summary>与 ext.task.status 快照同源的状态版本号；客户端发现跳号可拉快照补齐。</summary>
    public long StateRevision { get; init; }

    public JsonElement Payload { get; init; }
}

/// <summary>ext.* 请求响应（信封 response 的投影）。</summary>
public sealed class BgiExternalResponse
{
    public bool Success { get; init; }

    /// <summary>data 对象的原始 JSON 文本（无 data 时为 null）。</summary>
    public string? Data { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 模块一助手侧 SDK：封装 BGI 的 ext.* 外部接口（《通信方案》§3.2 分层架构的客户端半边）。
/// 一条长连接：连接后先 ext.hello 握手协商 capabilities，之后命令（request/response 按 requestId 关联）
/// 与事件（ext.event 推送）复用同一管道；断线自动重连并恢复订阅。
/// 对端是老版本 BGI（ext.hello 返回 unsupported_operation）→ State=Legacy，调用方降级回
/// IpcClient/CommandExecutor 旧路径（§3.9 优雅降级链），全程无报错。
/// 帧格式与 IpcClient 完全一致：[4字节 length][1字节 type=1][JSON]，length 不含 type 字节。
/// </summary>
public sealed class BgiExternalClient : IDisposable
{
    private const int MaxPayloadLength = 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstHandshakeWait = TimeSpan.FromSeconds(7);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BgiExternalResponse>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _subscribeLock = new();

    private NamedPipeClientStream? _pipe;
    private Task? _managerTask;
    private List<string> _subscribedEvents = new();
    private bool _subscriptionActive;
    private bool _disposed;

    public BgiExternalLinkState State { get; private set; } = BgiExternalLinkState.Down;

    /// <summary>[切片4] 连接状态机（只读）；变更经 <see cref="ConnectionStateChanged"/> 广播。</summary>
    public BgiExternalConnectionState ConnectionState { get; private set; } = BgiExternalConnectionState.Degraded;

    /// <summary>连接状态机变更通知（在 SDK 管理/读线程触发，Handler 异常不影响连接管理）。</summary>
    public event Action<BgiExternalConnectionState>? ConnectionStateChanged;

    private void SetConnectionState(BgiExternalConnectionState next)
    {
        if (ConnectionState == next)
        {
            return;
        }

        ConnectionState = next;
        try
        {
            ConnectionStateChanged?.Invoke(next);
        }
        catch
        {
            // 状态通知异常不杀死连接管理循环
        }
    }

    public string? BgiVersion { get; private set; }

    public string? SessionId { get; private set; }

    public IReadOnlyDictionary<string, bool>? Capabilities { get; private set; }

    /// <summary>事件通道是否可用于替代轮询（Ready 且已恢复订阅）。</summary>
    public bool IsEventChannelActive =>
        State == BgiExternalLinkState.Ready && _subscriptionActive;

    /// <summary>服务端主动推送事件（在 SDK 读线程触发；Handler 内的异常不会杀死读循环）。</summary>
    public event Action<BgiExternalEvent>? EventReceived;

    public BgiExternalClient()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;
        _pipeName = $"BetterGI.v2.user-{sid}.root";
    }

    /// <summary>
    /// 启动连接管理（首次连接 + 握手）。在首次握手出结果前返回：
    /// Ready=新 BGI 可用；Legacy=老 BGI（调用方降级）；Down=BGI 未启动等（内部继续重连）。
    /// </summary>
    public async Task<BgiExternalLinkState> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_managerTask is not null)
        {
            return State;
        }

        // 注意：不把外部 ct 链接进管理循环（链接 CTS 随 StartAsync 返回即释放），
        // 管理循环只受 _lifetimeCts（Dispose）控制；外部取消通过下方等待循环检查。
        _managerTask = Task.Run(
            () => ConnectionManagerAsync(_lifetimeCts.Token),
            CancellationToken.None);

        var deadline = Task.Delay(FirstHandshakeWait, CancellationToken.None);
        while (State == BgiExternalLinkState.Down
               && !deadline.IsCompleted
               && !_managerTask.IsCompleted
               && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, CancellationToken.None);
        }

        return State;
    }

    /// <summary>发送 ext.* 命令并等待响应。通道未就绪时抛 InvalidOperationException（调用方降级）。</summary>
    public async Task<BgiExternalResponse> SendCommandAsync(
        string operation,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = _pipe;
        if (State != BgiExternalLinkState.Ready || pipe is null || !pipe.IsConnected)
        {
            throw new InvalidOperationException("ext 通道未就绪");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completionSource = new TaskCompletionSource<BgiExternalResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completionSource))
        {
            throw new InvalidOperationException($"重复的 ext 请求 ID：{requestId}");
        }

        try
        {
            await WriteEnvelopeAsync(pipe, operation, requestId, payload, cancellationToken)
                .ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token);
            return await completionSource.Task
                .WaitAsync(timeout ?? CommandTimeout, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>订阅事件。空列表 = 全部已知事件。成功后 IsEventChannelActive 变为 true；重连自动恢复订阅。</summary>
    public async Task SubscribeAsync(
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
                ExternalOperations.EventSubscribe,
                new { events },
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"ext.event.subscribe 失败: {response.ErrorMessage ?? response.ErrorCode ?? "未知错误"}");
        }

        lock (_subscribeLock)
        {
            _subscribedEvents = events.Count == 0
                ? new List<string>(BgiExternalEventNames.All)
                : events.ToList();
            _subscriptionActive = true;
        }
    }

    public async Task UnsubscribeAsync(
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
                ExternalOperations.EventUnsubscribe,
                new { events },
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Success)
        {
            lock (_subscribeLock)
            {
                if (events.Count == 0)
                {
                    _subscribedEvents.Clear();
                    _subscriptionActive = false;
                }
                else
                {
                    foreach (var name in events)
                    {
                        _subscribedEvents.Remove(name);
                    }

                    if (_subscribedEvents.Count == 0)
                    {
                        _subscriptionActive = false;
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts.Cancel();
        SetConnectionState(BgiExternalConnectionState.Closed);
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // 清理路径吞异常
        }

        foreach (var pair in _pendingRequests)
        {
            pair.Value.TrySetCanceled();
        }

        _pendingRequests.Clear();
        _writeLock.Dispose();
        _lifetimeCts.Dispose();
    }

    // ===== 连接管理 =====

    /// <summary>
    /// 连接管理主循环：建立连接 → 握手 → 读循环（挂起）→ 连接死亡 → 退避重连。
    /// Legacy / 不可信（跨会话）视为不可恢复，退出循环由调用方退避再探测。
    /// </summary>
    private async Task ConnectionManagerAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                SetConnectionState(BgiExternalConnectionState.Connecting);
                if (await TryEstablishOnceAsync(cancellationToken).ConfigureAwait(false))
                {
                    delay = TimeSpan.Zero;
                    // 挂起直到连接死亡（读循环退出即连接关闭）
                    if (_readerLoop is { } reader)
                    {
                        await reader.ConfigureAwait(false);
                    }
                }
                else
                {
                    delay = ReconnectDelay;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 单次建立失败（BGI 未启动/管道忙等），退避重试
                delay = ReconnectDelay;
            }

            if (State is BgiExternalLinkState.Legacy || _unrecoverable)
            {
                // 版本/会话问题不会在 BGI 运行期间自愈，退出，交给调用方退避再探测
                break;
            }
        }
    }

    private Task? _readerLoop;
    private bool _unrecoverable;

    /// <summary>单次建立连接 + ext.hello 握手 + 启动读循环。返回 false 表示本次未就绪。</summary>
    private async Task<bool> TryEstablishOnceAsync(CancellationToken cancellationToken)
    {
        DisposePipe();
        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            State = BgiExternalLinkState.Down;
            return false;
        }

        _pipe = pipe;
        SetConnectionState(BgiExternalConnectionState.Handshaking);

        BgiExternalResponse hello;
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            await WriteEnvelopeAsync(
                    pipe,
                    ExternalOperations.Hello,
                    requestId,
                    new
                    {
                        clientName = "MultiplayerHoeingAssistant",
                        clientVersion = System.Reflection.Assembly.GetExecutingAssembly()
                            .GetName().Version?.ToString() ?? "unknown",
                        requiredCapabilities = new[] { ExternalOperations.CapabilityEventPush },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(pipe, cancellationToken).ConfigureAwait(false);
            hello = ToResponse(envelope)
                    ?? throw new IOException("ext.hello 未收到响应");
        }
        catch
        {
            DisposePipe();
            State = BgiExternalLinkState.Down;
            return false;
        }

        if (!hello.Success)
        {
            DisposePipe();
            if (hello.ErrorCode == "unsupported_operation")
            {
                // 老版本 BGI：优雅降级，无报错（§3.4/A2）
                State = BgiExternalLinkState.Legacy;
            }
            else if (hello.ErrorCode == "cross_session_rejected")
            {
                _unrecoverable = true;
                State = BgiExternalLinkState.Down;
            }
            else
            {
                State = BgiExternalLinkState.Down;
            }

            SetConnectionState(BgiExternalConnectionState.Degraded);
            return false;
        }

        // 会话校验：与 IpcClient.VerifyRemoteSessionAsync 同级的跨会话防护（BGI 侧守卫之外的客户端自检）
        if (hello.Data is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(hello.Data);
                var root = doc.RootElement;
                if (root.TryGetProperty("sessionId", out var sidEl)
                    && sidEl.ValueKind == JsonValueKind.String)
                {
                    SessionId = sidEl.GetString();
                }

                if (root.TryGetProperty("bgiVersion", out var verEl)
                    && verEl.ValueKind == JsonValueKind.String)
                {
                    BgiVersion = verEl.GetString();
                }

                if (root.TryGetProperty("capabilities", out var capsEl)
                    && capsEl.ValueKind == JsonValueKind.Object)
                {
                    Capabilities = JsonSerializer.Deserialize<Dictionary<string, bool>>(
                        capsEl.GetRawText());
                }

                if (root.TryGetProperty("windowsSessionId", out var wsEl)
                    && wsEl.ValueKind == JsonValueKind.Number
                    && wsEl.TryGetInt32(out var remoteSession))
                {
                    var localSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                    if (remoteSession != localSession)
                    {
                        // 跨会话：不采信、不使用（与 42b81a04 发送端阻断语义一致）
                        DisposePipe();
                        _unrecoverable = true;
                        State = BgiExternalLinkState.Down;
                        SetConnectionState(BgiExternalConnectionState.Degraded);
                        return false;
                    }
                }
            }
            catch
            {
                // 握手数据解析失败按不可用处理
                DisposePipe();
                State = BgiExternalLinkState.Down;
                SetConnectionState(BgiExternalConnectionState.Degraded);
                return false;
            }
        }

        State = BgiExternalLinkState.Ready;
        SetConnectionState(BgiExternalConnectionState.Ready);
        _readerLoop = Task.Run(() => ReaderLoopAsync(pipe, _lifetimeCts.Token), CancellationToken.None);

        // 重连后恢复订阅（订阅挂在会话上，新连接需要重新声明）
        List<string> eventsToRestore;
        lock (_subscribeLock)
        {
            eventsToRestore = new List<string>(_subscribedEvents);
        }

        if (eventsToRestore.Count > 0)
        {
            try
            {
                await SubscribeAsync(eventsToRestore, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 恢复订阅失败：连接仍可用，下一轮由调用方补订
                lock (_subscribeLock)
                {
                    _subscriptionActive = false;
                }
            }
        }

        return true;
    }

    private async Task ReaderLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await ReadEnvelopeAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    break; // EOF：服务端关闭
                }

                if (envelope.Operation == ExternalOperations.EventPush)
                {
                    DispatchEvent(envelope);
                    continue;
                }

                if (envelope.Operation == ExternalOperations.Response
                    && !string.IsNullOrEmpty(envelope.RequestId)
                    && _pendingRequests.TryGetValue(envelope.RequestId, out var completionSource))
                {
                    completionSource.TrySetResult(ToResponse(envelope)!);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or EndOfStreamException
                                          or OperationCanceledException
                                          or ObjectDisposedException
                                          or InvalidDataException
                                          or JsonException)
        {
            // 连接断开/取消：进入重连流程
        }
        finally
        {
            if (!_disposed)
            {
                State = BgiExternalLinkState.Down;
                SetConnectionState(BgiExternalConnectionState.Degraded);
                FailPendingRequests(new IOException("ext 连接已断开"));
                lock (_subscribeLock)
                {
                    // 订阅声明保留，重连成功后自动恢复
                    _subscriptionActive = false;
                }
            }
        }
    }

    private void DispatchEvent(Envelope envelope)
    {
        if (envelope.DataJson is null || EventReceived is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(envelope.DataJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("event", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String
                && nameEl.GetString() is { } name)
            {
                long revision = 0;
                if (root.TryGetProperty("stateRevision", out var revEl)
                    && revEl.ValueKind == JsonValueKind.Number)
                {
                    revision = revEl.GetInt64();
                }

                JsonElement payload = default;
                if (root.TryGetProperty("payload", out var payloadEl))
                {
                    payload = payloadEl.Clone();
                }

                EventReceived.Invoke(new BgiExternalEvent
                {
                    Name = name,
                    StateRevision = revision,
                    Payload = payload,
                });
            }
        }
        catch
        {
            // 单条事件解析失败不影响读循环
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var pair in _pendingRequests)
        {
            pair.Value.TrySetException(exception);
        }

        _pendingRequests.Clear();
    }

    private void DisposePipe()
    {
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // 清理路径吞异常
        }

        _pipe = null;
    }

    // ===== 帧读写（与 IpcClient / InstanceIpcProtocol 逐字节一致）=====

    private async Task WriteEnvelopeAsync(
        NamedPipeClientStream pipe,
        string operation,
        string requestId,
        object? payload,
        CancellationToken cancellationToken)
    {
        var envelope = new
        {
            version = 2,
            requestId,
            operation,
            data = payload,
        };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        var frame = new byte[4 + 1 + bytes.Length];
        BitConverter.GetBytes(bytes.Length).CopyTo(frame, 0);
        frame[4] = 1; // InstanceIpcPayloadType.Utf8Json
        Buffer.BlockCopy(bytes, 0, frame, 5, bytes.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<Envelope?> ReadEnvelopeAsync(
        NamedPipeClientStream pipe,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadExactAsync(pipe, header, 4, cancellationToken, allowEofOnFirstByte: true)
            .ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null; // EOF
        }

        var payloadLength = BitConverter.ToInt32(header, 0);
        if (payloadLength <= 0 || payloadLength > MaxPayloadLength)
        {
            throw new InvalidDataException($"无效的 ext 响应长度: {payloadLength}");
        }

        // 流中实际占 length + 1 字节（1 字节 type + length 字节 JSON）
        var payload = new byte[payloadLength + 1];
        await ReadExactAsync(pipe, payload, payload.Length, cancellationToken, allowEofOnFirstByte: false)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload, 1, payloadLength));
        var root = doc.RootElement;

        string? operation = null;
        if (root.TryGetProperty("operation", out var opEl) && opEl.ValueKind == JsonValueKind.String)
        {
            operation = opEl.GetString();
        }

        string? requestId = null;
        if (root.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
        {
            requestId = ridEl.GetString();
        }

        var success = root.TryGetProperty("success", out var sEl)
                      && sEl.ValueKind == JsonValueKind.True;

        string? errorCode = null;
        if (root.TryGetProperty("errorCode", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
        {
            errorCode = codeEl.GetString();
        }

        string? errorMessage = null;
        if (root.TryGetProperty("errorMessage", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
        {
            errorMessage = msgEl.GetString();
        }

        string? dataJson = null;
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
        {
            dataJson = dataEl.GetRawText();
        }

        return new Envelope(
            success,
            errorCode,
            errorMessage,
            dataJson,
            operation ?? string.Empty,
            requestId ?? string.Empty);
    }

    private static async Task<int> ReadExactAsync(
        NamedPipeClientStream pipe,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken,
        bool allowEofOnFirstByte)
    {
        var read = 0;
        while (read < count)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (allowEofOnFirstByte && read == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException("ext 管道连接已断开");
            }

            read += n;
        }

        return read;
    }

    private static BgiExternalResponse? ToResponse(Envelope? envelope)
        => envelope is null
            ? null
            : new BgiExternalResponse
            {
                Success = envelope.Success,
                Data = envelope.DataJson,
                ErrorCode = envelope.ErrorCode,
                ErrorMessage = envelope.ErrorMessage,
            };

    private sealed record Envelope(
        bool Success,
        string? ErrorCode,
        string? ErrorMessage,
        string? DataJson,
        string Operation,
        string RequestId);

    /// <summary>ext.* 操作名与 capability 名（与 BGI 侧 ExternalInterfaceOperations 对齐）。</summary>
    private static class ExternalOperations
    {
        public const string Hello = "ext.hello";
        public const string EventSubscribe = "ext.event.subscribe";
        public const string EventUnsubscribe = "ext.event.unsubscribe";
        public const string EventPush = "ext.event";
        public const string Response = "response";
        public const string CapabilityEventPush = "event.push";
    }
}
