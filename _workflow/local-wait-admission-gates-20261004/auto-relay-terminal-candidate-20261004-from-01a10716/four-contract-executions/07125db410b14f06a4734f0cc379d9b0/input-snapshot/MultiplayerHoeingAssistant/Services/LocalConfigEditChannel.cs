using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 助手之间的配置编辑通道。跨 Windows 用户只转发配置请求给目标助手，
/// 再由它走所属会话的 BGI IPC；不放宽 BGI 的会话隔离或只读状态管道。
/// 每次连接使用随机挑战和房间密码 HMAC，消息有大小/超时限制。
/// </summary>
internal sealed class LocalConfigEditChannel : IDisposable
{
    internal sealed record Endpoint(string PipeName, int ProcessId, long StartTicks);
    private sealed record Envelope(string CommandJson, string Proof);
    private const int MaxFrameBytes = 10 * 1024 * 1024;
    private readonly Func<string?> _getSecret;
    private readonly Func<RemoteCommand, Task<RemoteCommand?>> _handle;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    internal Endpoint Address { get; }

    public LocalConfigEditChannel(Func<string?> getSecret,
        Func<RemoteCommand, Task<RemoteCommand?>> handle, Action<string> report, string? pipeName = null)
    {
        _getSecret = getSecret;
        _handle = handle;
        _report = report;
        using var process = Process.GetCurrentProcess();
        Address = new Endpoint(pipeName ?? PipeName(process.Id), process.Id, process.StartTime.ToUniversalTime().Ticks);
        // 先创建监听实例，确保构造返回即可连接；IO/权限失败由调用方记录并保留服务器通路。
        var first = CreateServer();
        _listener = ListenAsync(first);
    }

    private static string PipeName(int pid) => $"BetterGI.Assistant.config-p{pid}";

    private NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!,
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(Address.PipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task ListenAsync(NamedPipeServerStream first)
    {
        var pipe = first;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using (pipe)
                {
                    await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                        timeout.CancelAfter(TimeSpan.FromSeconds(12));
                        var nonce = RandomNumberGenerator.GetBytes(32);
                        await WriteFrameAsync(pipe, nonce, timeout.Token).ConfigureAwait(false);
                        var envelope = JsonSerializer.Deserialize<Envelope>(
                            await ReadFrameAsync(pipe, timeout.Token).ConfigureAwait(false));
                        var secret = _getSecret();
                        if (envelope != null && !string.IsNullOrEmpty(secret)
                            && CryptographicOperations.FixedTimeEquals(
                                Sign(secret, nonce, envelope.CommandJson), Convert.FromBase64String(envelope.Proof)))
                        {
                            var command = JsonSerializer.Deserialize<RemoteCommand>(envelope.CommandJson);
                            var reply = command == null ? null : await _handle(command).WaitAsync(timeout.Token).ConfigureAwait(false);
                            await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply), timeout.Token).ConfigureAwait(false);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException or FormatException)
                    {
                        // 探测断开、认证失败、坏帧、超时不会终止监听；不记录含配置/认证信息的原始消息。
                    }
                    catch (Exception ex)
                    {
                        _report($"本地配置请求失败：{ex.Message}");
                    }
                }
                if (!_stop.IsCancellationRequested) pipe = CreateServer();
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex) { _report($"本地配置通道停止：{ex.Message}"); }
        finally { pipe.Dispose(); }
    }

    /// <summary>按目标 UID + 房间认证探测全部本机助手；多个匹配拒绝选择，不能误写另一个执行端。</summary>
    internal static async Task<Endpoint?> FindAsync(RemoteCommand probe, string secret)
    {
        using var current = Process.GetCurrentProcess();
        var processes = Process.GetProcessesByName(current.ProcessName);
        try
        {
            var matches = await Task.WhenAll(processes.Where(p => p.Id != current.Id).Select(async process =>
            {
                try
                {
                    var endpoint = new Endpoint(PipeName(process.Id), process.Id, process.StartTime.ToUniversalTime().Ticks);
                    var reply = await SendAsync(endpoint, probe, secret, TimeSpan.FromSeconds(2));
                    return reply?.Cmd == "remote_config.probe_result" ? endpoint : null;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                    or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    return null;
                }
            }));
            var found = matches.OfType<Endpoint>().ToArray();
            return found.Length switch
            {
                0 => null,
                1 => found[0],
                _ => throw new InvalidOperationException("本机发现多个同 UID 的执行端，无法唯一确定配置编辑目标")
            };
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    internal static async Task<RemoteCommand?> SendAsync(Endpoint endpoint, RemoteCommand command,
        string secret, TimeSpan? timeout = null)
    {
        using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(12));
        using var process = Process.GetProcessById(endpoint.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != endpoint.StartTicks || process.HasExited)
            throw new InvalidOperationException("本地执行端实例已变化，请重新打开配置编辑");
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
        WindowsSessionIdentity.VerifyPipeServer(pipe, endpoint.ProcessId);
        var nonce = await ReadFrameAsync(pipe, limit.Token).ConfigureAwait(false);
        if (nonce.Length != 32) throw new InvalidDataException("本地配置通道握手无效");
        var json = JsonSerializer.Serialize(command);
        var envelope = new Envelope(json, Convert.ToBase64String(Sign(secret, nonce, json)));
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope), limit.Token).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<RemoteCommand>(await ReadFrameAsync(pipe, limit.Token).ConfigureAwait(false));
        if (reply != null && (reply.CommandId != command.CommandId || reply.SenderUid != command.Target.Single()))
            throw new InvalidDataException("本地配置回复与请求不匹配");
        return reply;
    }

    private static byte[] Sign(string secret, byte[] nonce, string json) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), nonce.Concat(Encoding.UTF8.GetBytes(json)).ToArray());

    private static async Task WriteFrameAsync(Stream stream, byte[] data, CancellationToken ct)
    {
        if (data.Length > MaxFrameBytes) throw new InvalidDataException("本地配置消息超过长度上限");
        await stream.WriteAsync(BitConverter.GetBytes(data.Length), ct).ConfigureAwait(false);
        await stream.WriteAsync(data, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadFrameAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var length = BitConverter.ToInt32(header);
        if (length < 0 || length > MaxFrameBytes) throw new InvalidDataException("本地配置消息长度无效");
        var data = new byte[length];
        await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
        return data;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _ = _listener.ContinueWith(_ => _stop.Dispose(), TaskScheduler.Default);
    }
}
