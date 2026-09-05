using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// IPC 会话校验状态：连接后对端 BGI 所属 Windows 会话与本进程会话的比对结果。
/// 背景：多用户多开场景下命名管道按用户 SID 命名（BetterGI.v2.user-{SID}.root），
/// 第一个启动的 BGI（Primary）独占管道，其他会话的助手会连到 Primary 所在会话的 BGI，
/// 其 task.status / 控制指令都指向"别人会话的 BGI"，不可信。
/// </summary>
public enum IpcSessionCheck
{
    /// <summary>尚未校验（未成功建立连接）。</summary>
    NotChecked,
    /// <summary>对端 BGI 与本进程同一会话，状态与控制指令可信。</summary>
    SameSession,
    /// <summary>对端 BGI 在其他 Windows 会话，其状态与控制指令不可信。</summary>
    CrossSession,
    /// <summary>无法确认（Ping 失败或响应缺少会话字段），按不可信处理（不静默放行）。</summary>
    Unknown
}

public class IpcClient : IDisposable
{
    private NamedPipeClientStream? _pipeClient;
    private readonly string _pipeName;

    /// <summary>连接握手后的会话校验结果（详见 <see cref="IpcSessionCheck"/>）。</summary>
    public IpcSessionCheck SessionCheck { get; private set; } = IpcSessionCheck.NotChecked;

    /// <summary>对端 BGI 所在的 Windows 会话 ID（Ping 响应；未确认时为 null）。</summary>
    public int? RemoteSessionId { get; private set; }

    /// <summary>对端 BGI 的进程 ID（Ping 响应；未确认时为 null）。</summary>
    public int? RemoteProcessId { get; private set; }

    /// <summary>管道是否可信：仅同一会话可信；跨会话或无法确认均不可信。</summary>
    public bool IsSessionTrusted => SessionCheck == IpcSessionCheck.SameSession;

    public IpcClient()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;
        _pipeName = $"BetterGI.v2.user-{sid}.root";
    }

    /// <summary>
    /// [IPC_PROBE] 探针：暴露当前 IPC 管道名，供诊断日志使用
    /// </summary>
    public string GetPipeName() => _pipeName;

    public async Task ConnectAsync(int timeoutMs = 3000)
    {
        _pipeClient = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await _pipeClient.ConnectAsync(timeoutMs);
        }
        catch (TimeoutException)
        {
            _pipeClient.Dispose();
            _pipeClient = null;
            throw new TimeoutException($"连接命名管道超时（{timeoutMs}ms），BGI 可能未运行或无响应");
        }
        catch (Exception)
        {
            _pipeClient?.Dispose();
            _pipeClient = null;
            throw;
        }

        // 连接成功后先做会话校验握手（Ping），确认对端 BGI 与本进程同一会话。
        // 注意：握手失败只置 Unknown，不抛异常——不改变 ConnectAsync 原有的成功/失败语义，
        // 是否采信该连接由调用方根据 SessionCheck 决定。
        await VerifyRemoteSessionAsync();
    }

    /// <summary>
    /// 会话校验握手：发送 ping，解析对端 BGI 返回的端点信息（windowsSessionId/processId），
    /// 与本进程会话比对。Ping 是 v2 协议固有操作，响应 data 为 InstanceEndpoint
    /// （BGI 侧 Newtonsoft camelCase 序列化，windowsSessionId 为非空 int 必然存在）；
    /// 因此 Unknown 只会在管道本身异常时出现，此时按不可信处理（不静默放行）。
    /// </summary>
    private async Task VerifyRemoteSessionAsync()
    {
        try
        {
            var response = await SendCommandAsync(new IpcRequest { OpCode = "ping" });
            if (response.Success && !string.IsNullOrEmpty(response.Data))
            {
                var data = JsonSerializer.Deserialize<JsonElement>(response.Data);
                if (data.TryGetProperty("windowsSessionId", out var sidEl)
                    && sidEl.ValueKind == JsonValueKind.Number
                    && sidEl.TryGetInt32(out var remoteSid))
                {
                    RemoteSessionId = remoteSid;
                    if (data.TryGetProperty("processId", out var pidEl)
                        && pidEl.ValueKind == JsonValueKind.Number
                        && pidEl.TryGetInt32(out var remotePid))
                    {
                        RemoteProcessId = remotePid;
                    }

                    var localSid = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                    SessionCheck = remoteSid == localSid
                        ? IpcSessionCheck.SameSession
                        : IpcSessionCheck.CrossSession;
                    System.Diagnostics.Debug.WriteLine(
                        $"[IPC] 会话校验: 对端 Session={remoteSid} PID={RemoteProcessId?.ToString() ?? "?"}，本进程 Session={localSid} → {SessionCheck}");
                    return;
                }
            }

            SessionCheck = IpcSessionCheck.Unknown;
            System.Diagnostics.Debug.WriteLine(
                $"[IPC] 会话校验无法确认: Ping 未返回 windowsSessionId（Success={response.Success} Error={response.ErrorMessage ?? "无"}）");
        }
        catch (Exception ex)
        {
            SessionCheck = IpcSessionCheck.Unknown;
            System.Diagnostics.Debug.WriteLine($"[IPC] 会话校验握手失败: {ex.Message}");
        }
    }

    /// <summary>单条命令的读写总超时（与 BGI 侧 InstanceService.RequestTimeout=5s 对齐）。
    /// 历史上读响应无超时：BGI 卡顿（游戏高负载）时 ReadAsync 无限阻塞，状态轮询一轮挂死一轮，
    /// 叠加出多条管道连接并引发上报日志风暴。</summary>
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);

    public async Task<IpcResponse> SendCommandAsync(IpcRequest request)
    {
        if (_pipeClient == null || !_pipeClient.IsConnected)
            throw new InvalidOperationException("命名管道未连接");

        var coreTask = SendCommandCoreAsync(request);
        try
        {
            return await coreTask.WaitAsync(CommandTimeout);
        }
        catch (TimeoutException)
        {
            // 超时后管道帧边界已不可信（迟到的响应会被下一条命令误读成自己的响应），
            // 直接断开，后续调用按"命名管道未连接"失败，由调用方回退缓存/下轮重连。
            // 断开会让被遗弃的 coreTask 以 ObjectDisposed/IOException 失败，必须观察掉，
            // 否则冒泡到 TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框
            _ = coreTask.ContinueWith(t => _ = t.Exception,
                TaskContinuationOptions.OnlyOnFaulted);
            _pipeClient?.Dispose();
            _pipeClient = null;
            throw new TimeoutException($"BGI 命名管道命令（{request.OpCode}）响应超时（{CommandTimeout.TotalSeconds:0}s），BGI 可能忙或无响应");
        }
    }

    private async Task<IpcResponse> SendCommandCoreAsync(IpcRequest request)
    {
        if (_pipeClient == null || !_pipeClient.IsConnected)
            throw new InvalidOperationException("命名管道未连接");

        // 构建 InstanceIpcEnvelope 格式的请求（与 BGI 侧 InstanceIpcProtocol 兼容）
        var envelope = new
        {
            version = 2,
            requestId = Guid.NewGuid().ToString("N"),
            operation = request.OpCode,
            data = request.Payload != null
                ? (object?)JsonSerializer.Deserialize<System.Text.Json.JsonElement>(request.Payload)
                : null
        };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        // BGI 帧格式：[4字节 payload length][1字节 payload type (1=Utf8Json)][JSON 字节]
        var header = new byte[4 + 1 + bytes.Length];
        BitConverter.GetBytes(bytes.Length).CopyTo(header, 0);
        header[4] = 1; // InstanceIpcPayloadType.Utf8Json
        Buffer.BlockCopy(bytes, 0, header, 5, bytes.Length);
        await _pipeClient.WriteAsync(header, 0, header.Length);
        await _pipeClient.FlushAsync();

        // 读取响应帧：4 字节长度前缀
        var responseHeader = new byte[4];
        var bytesRead = 0;
        while (bytesRead < 4)
        {
            var n = await _pipeClient.ReadAsync(responseHeader, bytesRead, 4 - bytesRead);
            if (n == 0) throw new EndOfStreamException("管道连接已断开");
            bytesRead += n;
        }

        var totalLen = BitConverter.ToInt32(responseHeader, 0);
        if (totalLen <= 0 || totalLen > 1024 * 1024)
            throw new InvalidDataException($"无效的响应长度: {totalLen}");

        // 流中实际有 1 字节 payload type + totalLen 字节 JSON = totalLen + 1 字节
        var responsePayload = new byte[totalLen + 1];
        bytesRead = 0;
        while (bytesRead < totalLen + 1)
        {
            var n = await _pipeClient.ReadAsync(responsePayload, bytesRead, totalLen + 1 - bytesRead);
            if (n == 0) throw new EndOfStreamException("管道连接已断开");
            bytesRead += n;
        }

        // 跳过第 1 字节（payload type），剩下 totalLen 字节是 JSON
        var responseJson = Encoding.UTF8.GetString(responsePayload, 1, totalLen);

        // 先反序列化为 InstanceIpcEnvelope 格式，再提取关键字段
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;

        var success = root.TryGetProperty("success", out var s) && s.GetBoolean();
        string? errorMessage = null;
        if (root.TryGetProperty("errorMessage", out var err))
            errorMessage = err.GetString();
        string? errorCode = null;
        if (root.TryGetProperty("errorCode", out var code))
            errorCode = code.GetString();
        string? dataJson = null;
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            dataJson = dataEl.GetRawText();

        return new IpcResponse
        {
            Success = success,
            Data = dataJson,
            ErrorMessage = errorMessage,
            ErrorCode = errorCode
        };
    }

    public void Dispose()
    {
        _pipeClient?.Dispose();
        _pipeClient = null;
    }
}