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
/// 每次连接使用服务器地址和房间密码认证，消息有大小/超时限制。
/// </summary>
internal sealed class LocalConfigEditChannel : IDisposable
{
    internal sealed record Endpoint(string PipeName, int ProcessId, long StartTicks)
    {
        internal string? ServerUrl { get; init; }
    }

    internal sealed record RoomAuthentication(string? ServerUrl, string? Password);
    private sealed record Envelope(string CommandJson, string Proof);
    private const int MaxFrameBytes = 10 * 1024 * 1024;
    private const int NonceBytes = 32;
    private const string ClientProof = "client", ServerProof = "server";
    internal static string DefaultServerUrl { get; } = new AssistConfig().ServerUrl;
    private readonly Func<RoomAuthentication> _getRoom;
    private readonly Func<RemoteCommand, RoomAuthentication, Task<RemoteCommand?>> _handle;
    private readonly Action<string> _report;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener;
    internal Endpoint Address { get; }

    public LocalConfigEditChannel(Func<string?> getSecret,
        Func<RemoteCommand, Task<RemoteCommand?>> handle, Action<string> report, string? pipeName = null,
        string channel = "config")
        : this(() => new RoomAuthentication(DefaultServerUrl, getSecret()),
            (command, _) => handle(command), report, pipeName, channel)
    {
    }

    internal LocalConfigEditChannel(Func<RoomAuthentication> getRoom,
        Func<RemoteCommand, RoomAuthentication, Task<RemoteCommand?>> handle, Action<string> report,
        string? pipeName = null, string channel = "config")
    {
        _getRoom = getRoom;
        _handle = handle;
        _report = report;
        using var process = Process.GetCurrentProcess();
        Address = new Endpoint(pipeName ?? PipeName(process.Id, channel), process.Id, process.StartTime.ToUniversalTime().Ticks);
        // 先创建监听实例，确保构造返回即可连接；IO/权限失败由调用方记录并保留服务器通路。
        var first = CreateServer();
        _listener = ListenAsync(first);
    }

    private static string PipeName(int pid, string channel) => $"BetterGI.Assistant.{channel}-p{pid}";

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
                        await HandleConnectionAsync(pipe, timeout.Token).ConfigureAwait(false);
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

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var authentication = _getRoom();
        if (string.IsNullOrEmpty(authentication.Password)
            || !RoomServerAddress.TryNormalize(authentication.ServerUrl, out var server)) return;

        // 公告服务器标识；只在客户端证明其持有房间密码后，回复受密码保护的挑战证明。
        var key = ServerKey(authentication.Password, server);
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        await WriteFrameAsync(pipe, nonce.Concat(ServerFingerprint(server)).ToArray(), ct).ConfigureAwait(false);
        var clientProof = await ReadFrameAsync(pipe, ct).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(clientProof, Sign(key, nonce, ClientProof))) return;
        await WriteFrameAsync(pipe, Sign(key, nonce, ServerProof), ct).ConfigureAwait(false);

        var envelope = JsonSerializer.Deserialize<Envelope>(await ReadFrameAsync(pipe, ct).ConfigureAwait(false));
        if (envelope is null || !CryptographicOperations.FixedTimeEquals(
            Sign(key, nonce, envelope.CommandJson), Convert.FromBase64String(envelope.Proof))) return;

        var command = JsonSerializer.Deserialize<RemoteCommand>(envelope.CommandJson);
        var reply = command is null ? null
            : await _handle(command, authentication).WaitAsync(ct).ConfigureAwait(false);
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(reply), ct).ConfigureAwait(false);
    }

    /// <summary>按目标 UID + 房间认证探测全部本机助手；多个匹配拒绝选择，不能误写另一个执行端。</summary>
    internal static async Task<Endpoint?> FindAsync(RemoteCommand probe, string secret,
        string expectedProbeReply = "remote_config.probe_result", string channel = "config", string? serverUrl = null)
    {
        var found = await FindAllAsync(probe, secret, expectedProbeReply, channel, serverUrl);
        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => throw new InvalidOperationException(channel == "center"
                ? "本机发现多个匹配的执行端中心，无法唯一确定目标"
                : "本机发现多个同 UID 的执行端，无法唯一确定配置编辑目标")
        };
    }

    internal static async Task<IReadOnlyList<Endpoint>> FindAllAsync(RemoteCommand probe, string secret,
        string expectedProbeReply = "remote_config.probe_result", string channel = "config", string? serverUrl = null)
    {
        var address = serverUrl ?? DefaultServerUrl;
        if (!RoomServerAddress.TryNormalize(address, out _)) return [];
        using var current = Process.GetCurrentProcess();
        // The pipe namespace is visible across Windows user sessions. Enumerating process
        // start times is not: an ordinary user may be denied access to another user's process.
        // Center endpoints use their PID only as an address hint; SendAsync verifies the
        // connected pipe server's actual PID and the HMAC/target instance before use.
        Endpoint[] candidates;
        if (channel is "center" or "browser")
        {
            try
            {
                var prefix = @"\\.\pipe\BetterGI.Assistant." + channel + "-p";
                candidates = Directory.GetFiles(@"\\.\pipe\")
                    .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(path => (Path: path, Suffix: path[prefix.Length..]))
                    .Where(item => int.TryParse(item.Suffix, out var pid) && pid > 0 && pid != current.Id)
                    .Select(item => new Endpoint(Path.GetFileName(item.Path), int.Parse(item.Suffix), 0))
                    .DistinctBy(endpoint => endpoint.ProcessId)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
            {
                candidates = [];
            }
        }
        else candidates = [];

        if (candidates.Length == 0)
        {
            var processes = Process.GetProcessesByName(current.ProcessName);
            try
            {
                candidates = processes.Where(process => process.Id != current.Id)
                    .Select(process =>
                    {
                        try { return new Endpoint(PipeName(process.Id, channel), process.Id,
                            process.StartTime.ToUniversalTime().Ticks); }
                        catch { return null; }
                    }).OfType<Endpoint>().ToArray();
            }
            finally { foreach (var process in processes) process.Dispose(); }
        }

        var matches = await Task.WhenAll(candidates.Select(async endpoint =>
        {
            try
            {
                var candidate = endpoint with { ServerUrl = address };
                var reply = await SendAsync(candidate, probe, secret, TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                if (reply?.Cmd != expectedProbeReply) return null;
                if (channel != "center") return candidate;
                var json = RemoteCenterProtocol.PayloadOf(reply);
                var centerReply = string.IsNullOrEmpty(json)
                    ? null : JsonSerializer.Deserialize<RemoteCenterReply>(json);
                var expectedInstance = probe.TargetInstanceId;
                return centerReply?.Status == "ok"
                    && (string.IsNullOrEmpty(expectedInstance)
                        || centerReply.TargetInstanceId == expectedInstance)
                    ? candidate : null;
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception
                or JsonException)
            {
                return null;
            }
        }));
        return matches.OfType<Endpoint>().ToArray();
    }

    internal static async Task<RemoteCommand?> SendAsync(Endpoint endpoint, RemoteCommand command,
        string secret, TimeSpan? timeout = null, string? serverUrl = null)
    {
        var address = serverUrl ?? endpoint.ServerUrl ?? DefaultServerUrl;
        if (!RoomServerAddress.TryNormalize(address, out var server)
            || (endpoint.ServerUrl is not null && !RoomServerAddress.Same(endpoint.ServerUrl, address)))
            throw new IOException("本地通道服务器不匹配，请重新扫描执行端");

        using var limit = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(12));
        if (endpoint.StartTicks == 0)
        {
            var prefix = endpoint.PipeName.StartsWith("BetterGI.Assistant.browser-p", StringComparison.Ordinal)
                ? "BetterGI.Assistant.browser-p" : "BetterGI.Assistant.center-p";
            if (!endpoint.PipeName.StartsWith(prefix, StringComparison.Ordinal)
                || !int.TryParse(endpoint.PipeName[prefix.Length..], out var pipePid)
                || pipePid != endpoint.ProcessId)
                throw new InvalidOperationException("本地执行端实例已变化，请重新打开配置编辑");
        }
        else
        {
            using var process = Process.GetProcessById(endpoint.ProcessId);
            if (process.StartTime.ToUniversalTime().Ticks != endpoint.StartTicks || process.HasExited)
                throw new InvalidOperationException("本地执行端实例已变化，请重新打开配置编辑");
        }
        using var pipe = new NamedPipeClientStream(".", endpoint.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(limit.Token).ConfigureAwait(false);
        WindowsSessionIdentity.VerifyPipeServer(pipe, endpoint.ProcessId);
        var challenge = await ReadFrameAsync(pipe, limit.Token).ConfigureAwait(false);
        if (challenge.Length != 2 * NonceBytes
            || !CryptographicOperations.FixedTimeEquals(challenge.AsSpan(NonceBytes), ServerFingerprint(server)))
            throw new IOException("本地通道服务器不匹配或不支持服务器认证");

        var nonce = challenge[..NonceBytes];
        var key = ServerKey(secret, server);
        await WriteFrameAsync(pipe, Sign(key, nonce, ClientProof), limit.Token).ConfigureAwait(false);
        var serverProof = await ReadFrameAsync(pipe, limit.Token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(serverProof, Sign(key, nonce, ServerProof)))
            throw new IOException("本地通道房间密码未通过认证");

        var json = JsonSerializer.Serialize(command);
        var envelope = new Envelope(json, Convert.ToBase64String(Sign(key, nonce, json)));
        await WriteFrameAsync(pipe, JsonSerializer.SerializeToUtf8Bytes(envelope), limit.Token).ConfigureAwait(false);
        var reply = JsonSerializer.Deserialize<RemoteCommand>(await ReadFrameAsync(pipe, limit.Token).ConfigureAwait(false));
        if (reply != null && (reply.CommandId != command.CommandId || reply.SenderUid != command.Target.Single()))
            throw new InvalidDataException("本地配置回复与请求不匹配");
        return reply;
    }

    private static byte[] ServerFingerprint(string server) => SHA256.HashData(Encoding.UTF8.GetBytes(server));

    private static byte[] ServerKey(string password, string server) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(password), Encoding.UTF8.GetBytes(server));

    private static byte[] Sign(byte[] key, byte[] nonce, string message) =>
        HMACSHA256.HashData(key, nonce.Concat(Encoding.UTF8.GetBytes(message)).ToArray());

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
