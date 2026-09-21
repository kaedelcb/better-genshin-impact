using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **[R5.3 §24.4 组合根验收] BGI 实例管道替身（仅替换外部传输）**：
/// 真实组装（`MainViewModel → CommandExecutor → TaskCenterHost(真实门面) → Core`）保持不变，本类在**私有管道名**上
/// 应答 BGI 实例协议帧（v2 `ping`／`task.status`／`task.start` 与 ext `ext.hello`／`ext.task.start`／
/// `ext.task.queueStatus`），并支持按测试脚本**推送 ext 事件**（`task.completed` 等）。
/// 帧格式与生产一致：`[4 字节长度][1 字节 payload 类型=1(Utf8Json)][JSON]`（与 `IpcClient`／`BgiExternalClient` 相同）。
/// **纪律**：只在测试进程内的私有管道名上工作（生产管道名不受影响；夹具用 `PipeNameOverrideForTest` 接线并在 finally 还原）。
/// </summary>
internal sealed class BgiInstancePipeDouble : IAsyncDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _stateGate = new();
    private readonly List<(string Operation, JsonElement? Data)> _received = new();
    private readonly HashSet<string> _subscribedEvents = new(StringComparer.Ordinal);
    private NamedPipeServerStream? _active;
    private Task? _acceptLoop;
    private int _queuedStarts;   // 已应答的 ext.task.start 次数（脚本化句柄命名）
    private bool _subscribedAll; // 客户端订阅了「全部事件」（空数组语义，与服务端一致）

    private void SubscribeAllLocked()
    {
        _subscribedAll = true;
        _subscribedEvents.Clear();
    }

    /// <summary>已成功订阅的事件名集合（`ext.event.subscribe` 载荷 `events[]`；空数组＝订阅全部）。</summary>
    public IReadOnlyCollection<string> SubscribedEvents
    {
        get { lock (_stateGate) return _subscribedEvents.ToList(); }
    }

    public string PipeName { get; } = "Codex.BgiCompositionRoot." + Guid.NewGuid().ToString("N")[..12];

    /// <summary>当前是否有客户端连接（夹具在推送事件前等待它为真）。</summary>
    public bool Connected { get { lock (_stateGate) return _active is { IsConnected: true }; } }

    /// <summary>收到的请求（operation + data），供断言「只发一次」等纪律。</summary>
    public IReadOnlyList<(string Operation, JsonElement? Data)> Received
    {
        get { lock (_stateGate) return _received.ToList(); }
    }

    /// <summary>ext.task.start 的应答脚本：true＝入队（`queued` + 自动句柄），false＝队列满（`queue_full`）。</summary>
    public bool AcceptTaskStart { get; set; } = true;

    /// <summary>`ext.task.queueStatus` 的应答脚本：默认 `pending`（不误判终态）；夹具可改为 `completed` 等。</summary>
    public string QueueStatus { get; set; } = "pending";

    /// <summary>v2 `task.start` 的应答脚本：true＝success，false＝业务拒绝（`task_already_running`）。</summary>
    public bool AcceptV2TaskStart { get; set; } = true;

    /// <summary>ext 握手脚本：false＝对端老 BGI（`unsupported_operation` ⇒ 客户端 `Legacy`，ext 通道不可用）。</summary>
    public bool AcceptHello { get; set; } = true;

    public void Start() => _acceptLoop ??= Task.Run(AcceptLoopAsync);

    public int CountOf(string operation)
    {
        lock (_stateGate) return _received.Count(r => r.Operation == operation);
    }

    /// <summary>向客户端推送一条 ext 事件（终端事件按 `taskHandle` 路由，不带 revision 以跳过状态覆盖线）。</summary>
    public async Task PushEventAsync(string eventName, string taskHandle, object? extra = null)
    {
        // **同构纪律**：只有在客户端**成功订阅**了该事件时才允许推送（否则夹具会掩盖「生产漏订阅/订阅集不含终态事件」）。
        lock (_stateGate)
        {
            if (!_subscribedEvents.Contains(eventName) && !_subscribedAll)
                throw new InvalidOperationException(
                    $"替身拒绝推送未订阅事件 {eventName}（已订阅：{string.Join(",", _subscribedEvents)}）");
        }
        // 终态事件的实际口径（与 `BgiTaskTerminalWaiter.OnEvent` 一致）：句柄/cancelled/errorCode/message
        // 都在 `payload` 内（不是 `data` 根）。
        var terminal = new JsonObject { ["taskHandle"] = taskHandle };
        if (extra is not null)
        {
            var node = JsonSerializer.SerializeToNode(extra)!.AsObject();
            foreach (var kv in node) terminal[kv.Key] = kv.Value?.DeepClone();
        }
        var payload = new JsonObject { ["event"] = eventName, ["payload"] = terminal };
        // 事件推送＝`operation=ext.event` 的 v2 信封（`data` 内为事件本体；客户端按操作名分派为事件）。
        var envelope = new JsonObject
        {
            ["version"] = 2,
            ["requestId"] = Guid.NewGuid().ToString("N"),
            ["operation"] = "ext.event",
            ["data"] = payload,
        };
        await WriteFrameAsync(envelope.ToJsonString()).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                lock (_stateGate) _active = server;
                await ServeAsync(server).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // 客户端断开：继续等待下一次连接（真实 BGI 亦如此）。
            }
            finally
            {
                lock (_stateGate) _active = null;
                try { server?.Dispose(); } catch { }
            }
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server)
    {
        while (!_cts.IsCancellationRequested && server.IsConnected)
        {
            var json = await ReadFrameAsync(server).ConfigureAwait(false);
            if (json is null) return;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(json); }
            catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                var operation = root.TryGetProperty("operation", out var opEl) && opEl.ValueKind == JsonValueKind.String
                    ? opEl.GetString() ?? "" : "";
                JsonElement? data = root.TryGetProperty("data", out var dEl) && dEl.ValueKind == JsonValueKind.Object
                    ? dEl.Clone() : null;
                var requestId = root.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String
                    ? ridEl.GetString() ?? "" : "";
                lock (_stateGate) _received.Add((operation, data));
                var response = BuildResponse(operation, data, requestId);
                await WriteFrameAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// 应答＝**v2 信封**（`version=2`＋`requestId` 回显＋`operation=response`＋`success/errorCode/errorMessage/data`）——
    /// 与 `BgiExternalClient.ReadEnvelopeAsync`／`IpcClient` 的实际解析口径一致（缺 `version` 会被判「不支持协议版本」）。
    /// </summary>
    private Dictionary<string, object?> BuildResponse(string operation, JsonElement? data, string requestId)
    {
        var (success, errorCode, errorMessage, payload) = BuildBody(operation, data);
        var envelope = new Dictionary<string, object?>
        {
            ["version"] = 2,
            ["requestId"] = requestId,
            ["operation"] = "response",
            ["success"] = success,
            ["data"] = payload,
        };
        if (errorCode is not null) envelope["errorCode"] = errorCode;
        if (errorMessage is not null) envelope["errorMessage"] = errorMessage;
        return envelope;
    }

    private (bool Success, string? ErrorCode, string? ErrorMessage, object? Payload) BuildBody(
        string operation, JsonElement? data)
    {
        switch (operation)
        {
            case "ping":
                return (true, null, null, new
                {
                    windowsSessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId,
                    processId = System.Diagnostics.Process.GetCurrentProcess().Id,
                });
            case "task.status":
                // 空闲且无中断上下文（组合根夹具的起点事实）。
                return (true, null, null, new { taskRunning = false, hasSuspendedTaskContext = false });
            case "task.start":
                return AcceptV2TaskStart
                    ? (true, null, null, new { status = "success" })
                    : (false, "task_already_running", "夹具：已有任务在跑", new { });
            case "ext.hello":
                if (!AcceptHello)
                    // 老版本 BGI：`unsupported_operation` ⇒ 客户端优雅降级（Legacy，ext 通道不可用，v2 路径继续）。
                    return (false, "unsupported_operation", "夹具：对端老 BGI", null);
                return (true, null, null, new
                {
                    bgiVersion = "fixture-1.0",
                    sessionId = "fixture-session",
                    capabilities = new Dictionary<string, bool>
                    {
                        ["event.push"] = true,
                        ["task.queue"] = true,
                        ["execution.contract.v1"] = false,
                    },
                });
            case "ext.task.start":
                if (!AcceptTaskStart)
                    return (false, "queue_full", "夹具：队列已满", new { });
                var handle = "fixture-handle-" + Interlocked.Increment(ref _queuedStarts);
                return (true, null, null, new { status = "queued", taskHandle = handle, queuePosition = 0 });
            case "ext.task.queueStatus":
                var queried = data is { } q && q.TryGetProperty("taskHandle", out var h) && h.ValueKind == JsonValueKind.String
                    ? h.GetString() ?? "" : "";
                return (true, null, null, queried.Length == 0
                        ? new { status = QueueStatus }
                        : (object)new { status = QueueStatus, taskHandle = queried });
            case "ext.event.subscribe":
                lock (_stateGate)
                {
                    _subscribedEvents.Clear();
                    if (data is { } sub && sub.TryGetProperty("events", out var evEl) && evEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in evEl.EnumerateArray())
                            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name)
                                _subscribedEvents.Add(name);
                        if (_subscribedEvents.Count == 0) SubscribeAllLocked();
                    }
                    else
                    {
                        SubscribeAllLocked();
                    }
                }
                return (true, null, null, new { });
            default:
                // 其余 ext/v2 操作：成功但空载荷（夹具不覆盖的路径不应因此响亮失败）。
                return (true, null, null, new { });
        }
    }

    private async Task WriteFrameAsync(string json)
    {
        var lease = _active;
        if (lease is null || !lease.IsConnected) return;
        var bytes = Encoding.UTF8.GetBytes(json);
        var frame = new byte[4 + 1 + bytes.Length];
        BitConverter.GetBytes(bytes.Length).CopyTo(frame, 0);
        frame[4] = 1;   // Utf8Json
        Buffer.BlockCopy(bytes, 0, frame, 5, bytes.Length);
        await _writeLock.WaitAsync(_cts.Token).ConfigureAwait(false);
        try
        {
            await lease.WriteAsync(frame, 0, frame.Length, _cts.Token).ConfigureAwait(false);
            await lease.FlushAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (Exception) when (lease.IsConnected == false)
        {
            // 客户端已断开：忽略（夹具断言会体现）。
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<string?> ReadFrameAsync(NamedPipeServerStream server)
    {
        var header = new byte[5];
        var read = 0;
        while (read < 5)
        {
            var n = await server.ReadAsync(header, read, 5 - read).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        var length = BitConverter.ToInt32(header, 0);
        if (length <= 0 || length > 1024 * 1024) return null;
        var payload = new byte[length];
        read = 0;
        while (read < length)
        {
            var n = await server.ReadAsync(payload, read, length - read).ConfigureAwait(false);
            if (n == 0) return null;
            read += n;
        }
        return Encoding.UTF8.GetString(payload);
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await (_acceptLoop ?? Task.CompletedTask).ConfigureAwait(false); } catch { }
        lock (_stateGate) _active = null;
        _writeLock.Dispose();
        _cts.Dispose();
    }
}
