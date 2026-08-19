using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class IpcClient : IDisposable
{
    private NamedPipeClientStream? _pipeClient;
    private readonly string _pipeName;

    public IpcClient()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;
        _pipeName = $"BetterGI.v2.user-{sid}.root";
    }

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
    }

    public async Task<IpcResponse> SendCommandAsync(IpcRequest request)
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
        string? dataJson = null;
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            dataJson = dataEl.GetRawText();

        return new IpcResponse
        {
            Success = success,
            Data = dataJson,
            ErrorMessage = errorMessage
        };
    }

    public void Dispose()
    {
        _pipeClient?.Dispose();
        _pipeClient = null;
    }
}