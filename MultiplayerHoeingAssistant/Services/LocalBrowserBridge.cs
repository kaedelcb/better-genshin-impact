using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NexusBGI.Legal;

namespace MultiplayerHoeingAssistant.Services;

// This transport is deliberately independent of Dodoco: other local products can register
// operations on the same authenticated session without creating another listener or scheduler.
internal sealed record LocalBrowserRequest(string SessionId, string RequestId, string Operation,
    JsonObject Args, string InstanceId);
internal sealed record LocalBrowserResult(string Status, string Message = "", object? Data = null);

internal sealed class LocalBrowserBridge : IDisposable
{
    private const int MaxHeader = 16 * 1024, MaxBody = 3 * 1024 * 1024, MaxResponse = 6 * 1024 * 1024;
    private const int MaxConnections = 24, MaxSessions = 16, MaxClaims = 512;
    private static readonly TimeSpan PairLifetime = TimeSpan.FromMinutes(2), SessionLifetime = TimeSpan.FromMinutes(30);
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly SemaphoreSlim _connections = new(MaxConnections, MaxConnections);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _pairings = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<LocalBrowserRequest, CancellationToken, Task<LocalBrowserResult>> _handler;
    private readonly Action<string>? _sessionEnded;
    private readonly Action<string>? _report;
    private readonly string _instanceId = NewSecret();
    private readonly Task _acceptLoop;
    private int _disposed;

    private sealed class BrowserSession
    {
        public readonly CancellationTokenSource Cancel = new();
        public readonly ConcurrentDictionary<string, Claim> Claims = new(StringComparer.Ordinal);
        public long LastSeenTicks = DateTimeOffset.UtcNow.UtcTicks;
    }
    private sealed record Claim(string Fingerprint, Task<LocalBrowserResult> Task, bool ReadOnly, DateTimeOffset Created);
    private sealed record HttpRequest(string Method, string Path, Dictionary<string, string> Headers, byte[] Body);

    public LocalBrowserBridge(Func<LocalBrowserRequest, CancellationToken, Task<LocalBrowserResult>> handler,
        Action<string>? sessionEnded = null, Action<string>? report = null)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _sessionEnded = sessionEnded;
        _report = report;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(MaxConnections);
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Origin = $"http://127.0.0.1:{port}";
        Host = $"127.0.0.1:{port}";
        _acceptLoop = AcceptAsync();
    }

    internal string Origin { get; }
    internal string Host { get; }

    // Pairing is an explicit user action; the secret is sent in a URL fragment, not in HTTP requests.
    public string CreatePairingUrl()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Expire();
        if (_pairings.Count >= MaxSessions) throw new InvalidOperationException("待配对窗口过多，请稍后重试");
        var token = NewSecret();
        if (!_pairings.TryAdd(token, DateTimeOffset.UtcNow.Add(PairLifetime))) throw new InvalidOperationException("无法创建配对码");
        return $"{Origin}/#token={token}";
    }

    private static string NewSecret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stopping.Token).ConfigureAwait(false);
                if (!_connections.Wait(0)) { client.Dispose(); continue; }
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _report?.Invoke("本机网页监听已停止：" + ex.Message); }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        try
        {
            using (client)
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(40));
                if (client.Client.RemoteEndPoint is not IPEndPoint endpoint || !IPAddress.IsLoopback(endpoint.Address)) return;
                using var stream = client.GetStream();
                var request = await ReadRequestAsync(stream, timeout.Token).ConfigureAwait(false);
                if (request == null) { await WriteAsync(stream, 400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("请求无效"), timeout.Token); return; }
                if (!request.Headers.TryGetValue("host", out var host) || !string.Equals(host, Host, StringComparison.OrdinalIgnoreCase))
                { await WriteAsync(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("来源无效"), timeout.Token); return; }
                Expire();
                if (request.Method == "GET")
                {
                    var resource = StaticResource(request.Path);
                    if (resource == null) { await WriteAsync(stream, 404, "text/plain; charset=utf-8", [], timeout.Token); return; }
                    await WriteAsync(stream, 200, resource.Value.ContentType, resource.Value.Bytes, timeout.Token); return;
                }
                if (request.Method != "POST" || !request.Headers.TryGetValue("origin", out var origin) || !SameOrigin(origin, request.Headers) ||
                    !request.Headers.TryGetValue("content-type", out var type) ||
                    !(type.Equals("application/json", StringComparison.OrdinalIgnoreCase) || type.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase)))
                { await WriteJsonAsync(stream, 403, new { status = "error", message = "来源或请求格式无效" }, timeout.Token); return; }
                JsonObject? body;
                try { body = JsonNode.Parse(request.Body) as JsonObject; }
                catch (JsonException) { body = null; }
                if (body == null) { await WriteJsonAsync(stream, 400, new { status = "error", message = "请求数据无效" }, timeout.Token); return; }
                if (request.Path == "/api/pair")
                {
                    var token = body["token"]?.GetValue<string>() ?? "";
                    if (!_pairings.TryRemove(token, out var expires) || expires <= DateTimeOffset.UtcNow || _sessions.Count >= MaxSessions)
                    { await WriteJsonAsync(stream, 403, new { status = "error", message = "配对码无效或已过期，请从助手重新打开" }, timeout.Token); return; }
                    var id = NewSecret();
                    if (!_sessions.TryAdd(id, new BrowserSession())) throw new InvalidOperationException("无法创建本机会话");
                    await WriteJsonAsync(stream, 200, new { sessionId = id, instanceId = _instanceId }, timeout.Token); return;
                }
                if (!request.Headers.TryGetValue("x-bgi-session", out var sessionId) || !_sessions.TryGetValue(sessionId, out var session))
                { await WriteJsonAsync(stream, 401, new { status = "error", message = "本机会话已失效，请从助手重新打开" }, timeout.Token); return; }
                Interlocked.Exchange(ref session.LastSeenTicks, DateTimeOffset.UtcNow.UtcTicks);
                if (request.Path == "/api/end")
                {
                    Revoke(sessionId);
                    await WriteJsonAsync(stream, 200, new { status = "ok" }, timeout.Token); return;
                }
                if (request.Path != "/api/execute")
                { await WriteJsonAsync(stream, 404, new { status = "error", message = "接口不存在" }, timeout.Token); return; }
                var result = await ExecuteAsync(sessionId, session, body, timeout.Token).ConfigureAwait(false);
                var resultBody = JsonSerializer.SerializeToNode(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)) as JsonObject
                    ?? throw new InvalidOperationException("本机响应无效");
                resultBody["instanceId"] = _instanceId;
                await WriteJsonAsync(stream, 200, resultBody, timeout.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (Exception ex) { _report?.Invoke("本机网页请求失败：" + ex.Message); }
        finally { _connections.Release(); }
    }

    private async Task<object> ExecuteAsync(string sessionId, BrowserSession session, JsonObject body, CancellationToken waitToken)
    {
        var requestId = body["requestId"]?.GetValue<string>() ?? "";
        var operation = body["operation"]?.GetValue<string>() ?? "";
        var args = body["args"] as JsonObject;
        if (!ValidToken(requestId) || !ValidToken(operation) || args == null)
            return new { requestId, status = "error", message = "请求编号或操作无效", data = (object?)null };
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(operation + "\n" + args.ToJsonString())));
        if (session.Claims.Count >= 256)
            foreach (var item in session.Claims)
                if (item.Value.ReadOnly && item.Value.Task.IsCompleted && DateTimeOffset.UtcNow - item.Value.Created > TimeSpan.FromSeconds(30))
                    ((ICollection<KeyValuePair<string, Claim>>)session.Claims).Remove(item);
        if (session.Claims.Count >= MaxClaims && !session.Claims.ContainsKey(requestId))
            return new { requestId, status = "error", message = "本机会话请求数已达上限，请重新打开", data = (object?)null };
        // Create the actual task only after winning the claim; ConcurrentDictionary.GetOrAdd can run
        // a factory multiple times, which would execute a mutating request more than once.
        var completion = new TaskCompletionSource<LocalBrowserResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var candidate = new Claim(fingerprint, completion.Task, IsReadOnly(operation), DateTimeOffset.UtcNow);
        if (session.Claims.TryAdd(requestId, candidate))
            _ = RunClaimAsync(sessionId, session, requestId, operation, (JsonObject)args.DeepClone(), completion);
        var claim = session.Claims[requestId];
        if (!string.Equals(claim.Fingerprint, fingerprint, StringComparison.Ordinal))
            return new { requestId, status = "error", message = "请求编号已用于不同操作", data = (object?)null };
        try
        {
            var result = await claim.Task.WaitAsync(TimeSpan.FromSeconds(30), waitToken).ConfigureAwait(false);
            var status = result.Status is "ok" or "error" or "unknown" ? result.Status : "error";
            return new { requestId, status, message = result.Message, data = result.Data };
        }
        catch (TimeoutException)
        { return new { requestId, status = "unknown", message = "本机操作尚未确认，请查询状态，不要重复执行", data = (object?)null }; }
    }

    private async Task RunClaimAsync(string sessionId, BrowserSession session, string requestId, string operation,
        JsonObject args, TaskCompletionSource<LocalBrowserResult> completion)
    {
        try
        {
            session.Cancel.Token.ThrowIfCancellationRequested();
            var result = await _handler(new LocalBrowserRequest(sessionId, requestId, operation, args, _instanceId), session.Cancel.Token).ConfigureAwait(false);
            completion.TrySetResult(result);
        }
        catch (OperationCanceledException) { completion.TrySetResult(new("unknown", "本机会话已结束，操作结果未确认")); }
        catch (Exception ex) { completion.TrySetResult(new("unknown", "本机操作未能确认：" + ex.Message)); }
    }

    private static bool IsReadOnly(string operation) => operation is "local.identity" or "log.poll" or "log.files" or "log.chunk"
        or "screenshot.capture" or "watch.read" or "watch.records" or "watch.incidents" or "watch.frame"
        or "report.dates" or "report.day" or "watch.diagnostic.chunk" or "mistletoe.read";

    private static bool ValidToken(string value) => value.Length is > 0 and <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    // 部分浏览器扩展会把 Origin 改写成不带端口的 http://127.0.0.1；此时只接受浏览器自身标注的同源请求。
    // Sec-Fetch-Site 由浏览器设置，网页脚本无法伪造；其他端口或站点的页面仍被拒绝。
    private bool SameOrigin(string origin, Dictionary<string, string> headers) => origin == Origin ||
        (origin == "http://127.0.0.1" && headers.TryGetValue("sec-fetch-site", out var site) && site == "same-origin");

    private void Expire()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _pairings) if (pair.Value <= now) _pairings.TryRemove(pair.Key, out _);
        foreach (var pair in _sessions)
            if (now.UtcTicks - Interlocked.Read(ref pair.Value.LastSeenTicks) > SessionLifetime.Ticks) Revoke(pair.Key);
    }

    private void Revoke(string sessionId)
    {
        if (!_sessions.TryRemove(sessionId, out var session)) return;
        session.Cancel.Cancel();
        _ = FinishRevokeAsync(sessionId, session);
    }
    private async Task FinishRevokeAsync(string sessionId, BrowserSession session)
    {
        try { await Task.WhenAll(session.Claims.Values.Select(c => c.Task)).ConfigureAwait(false); }
        catch { }
        try { _sessionEnded?.Invoke(sessionId); } catch (Exception ex) { _report?.Invoke("清理本机会话失败：" + ex.Message); }
        session.Cancel.Dispose();
    }

    private static (string ContentType, byte[] Bytes)? StaticResource(string target)
    {
        var path = target.Split('?', 2)[0];
        if (path == "/") path = "/local-dodoco.html";
        if (path == "/api/legal-info")
        {
            const string prefix = "LocalBrowser/legal/licenses/";
            var names = Assembly.GetExecutingAssembly().GetManifestResourceNames()
                .Where(n => n.StartsWith(prefix, StringComparison.Ordinal)).Select(n => n[prefix.Length..]);
            return ("application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(OpenSourceInfo.ForWeb("联机助手", names)));
        }
        var name = path.TrimStart('/');
        if (name.Contains("..", StringComparison.Ordinal) || name.Contains('\\') || name.Contains('%') || name.Length > 160) return null;
        var extension = Path.GetExtension(name);
        var contentType = extension switch
        {
            ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8", ".png" => "image/png",
            ".txt" or ".md" => "text/plain; charset=utf-8", _ => null
        };
        if (contentType == null) return null;
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("LocalBrowser/" + name);
        if (resource == null) return null;
        using var copy = new MemoryStream(); resource.CopyTo(copy);
        return (contentType, copy.ToArray());
    }

    private static async Task<HttpRequest?> ReadRequestAsync(NetworkStream stream, CancellationToken token)
    {
        using var data = new MemoryStream();
        var buffer = new byte[4096]; var headerEnd = -1;
        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0) return null;
            data.Write(buffer, 0, read);
            var bytes = data.GetBuffer(); var length = (int)data.Length;
            for (var i = Math.Max(0, length - read - 3); i + 3 < length; i++)
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10) { headerEnd = i + 4; break; }
            if (headerEnd < 0 && length > MaxHeader || headerEnd > MaxHeader) return null;
        }
        var header = Encoding.ASCII.GetString(data.GetBuffer(), 0, headerEnd - 4);
        var lines = header.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || first[2] != "HTTP/1.1" || first[1].Length > 2048 || !first[1].StartsWith('/')) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0 || char.IsWhiteSpace(line[0])) return null;
            var key = line[..colon].Trim().ToLowerInvariant(); var value = line[(colon + 1)..].Trim();
            if (!headers.TryAdd(key, value) && key is "host" or "origin" or "content-length" or "transfer-encoding" or "x-bgi-session" or "content-type") return null;
        }
        if (headers.ContainsKey("transfer-encoding")) return null;
        var lengthText = headers.GetValueOrDefault("content-length");
        if (lengthText != null && (!int.TryParse(lengthText, out _) || lengthText.Any(c => !char.IsAsciiDigit(c)))) return null;
        var bodyLength = lengthText == null ? 0 : int.Parse(lengthText);
        if (bodyLength > MaxBody || (first[0] == "POST" && lengthText == null) || data.Length - headerEnd > bodyLength) return null;
        var body = new byte[bodyLength];
        var buffered = (int)data.Length - headerEnd;
        if (buffered > 0) Buffer.BlockCopy(data.GetBuffer(), headerEnd, body, 0, buffered);
        var offset = buffered;
        while (offset < body.Length)
        {
            var count = await stream.ReadAsync(body.AsMemory(offset), token).ConfigureAwait(false);
            if (count == 0) return null;
            offset += count;
        }
        return new HttpRequest(first[0], first[1], headers, body);
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int code, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (bytes.Length > MaxResponse) bytes = JsonSerializer.SerializeToUtf8Bytes(new { status = "unknown", message = "本机响应过大，操作结果未确认" });
        await WriteAsync(stream, code, "application/json; charset=utf-8", bytes, token);
    }
    private static async Task WriteAsync(NetworkStream stream, int code, string contentType, byte[] body, CancellationToken token)
    {
        var reason = code switch { 200 => "OK", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", _ => "Not Found" };
        var header = $"HTTP/1.1 {code} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' http: https: ws: wss:; object-src 'none'; base-uri 'none'; frame-ancestors 'none'\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
        if (body.Length > 0) await stream.WriteAsync(body, token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stopping.Cancel();
        _listener.Stop();
        foreach (var id in _sessions.Keys) Revoke(id);
        _pairings.Clear();
    }
}
