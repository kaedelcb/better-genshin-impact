using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class LocalBrowserBridgeTests
{
    private static HttpClient Client(LocalBrowserBridge bridge) => new(new HttpClientHandler { UseProxy = false })
    { BaseAddress = new Uri(bridge.Origin), Timeout = TimeSpan.FromSeconds(10) };
    private static string PairToken(LocalBrowserBridge bridge) => new Uri(bridge.CreatePairingUrl()).Fragment[7..];
    private static async Task<HttpResponseMessage> Post(HttpClient client, string path, object body, string? origin = null, string? session = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
        if (origin != null) request.Headers.TryAddWithoutValidation("Origin", origin);
        if (session != null) request.Headers.TryAddWithoutValidation("X-BGI-Session", session);
        return await client.SendAsync(request);
    }
    private static async Task<string> Pair(HttpClient client, LocalBrowserBridge bridge, string token)
    {
        using var response = await Post(client, "/api/pair", new { token }, bridge.Origin);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.False(string.IsNullOrEmpty(body["instanceId"]?.GetValue<string>()));
        return body["sessionId"]!.GetValue<string>();
    }

    [Fact]
    public async Task Pairing_RequiresLocalOriginAndOneTimeToken_AndAssetsAreEmbedded()
    {
        using var bridge = new LocalBrowserBridge((_, _) => Task.FromResult(new LocalBrowserResult("ok")));
        using var client = Client(bridge);
        using var page = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("frame-ancestors 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Contains("no-store", page.Headers.CacheControl!.ToString());
        using var script = await client.GetAsync("/local-dodoco.js");
        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        using var starStyle = await client.GetAsync("/project-star.css");
        Assert.Equal(HttpStatusCode.OK, starStyle.StatusCode);
        var token = PairToken(bridge);
        using var noOrigin = await Post(client, "/api/pair", new { token });
        Assert.Equal(HttpStatusCode.Forbidden, noOrigin.StatusCode);
        using var externalOrigin = await Post(client, "/api/pair", new { token }, "https://attacker.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, externalOrigin.StatusCode);
        using var missing = await Post(client, "/api/execute", new { requestId = "r", operation = "local.identity", args = new { } }, bridge.Origin);
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        var session = await Pair(client, bridge, token);
        Assert.NotEmpty(session);
        using var replay = await Post(client, "/api/pair", new { token }, bridge.Origin);
        Assert.Equal(HttpStatusCode.Forbidden, replay.StatusCode);
        using var wrongHost = new HttpRequestMessage(HttpMethod.Get, "/");
        wrongHost.Headers.Host = "attacker.invalid";
        using var blocked = await client.SendAsync(wrongHost);
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
    }

    [Fact]
    public async Task RequestId_DeduplicatesConcurrentMutations_AndRejectsChangedPayload()
    {
        var calls = 0;
        using var bridge = new LocalBrowserBridge(async (_, ct) =>
        {
            Interlocked.Increment(ref calls); await Task.Delay(80, ct); return new LocalBrowserResult("ok", Data: new { saved = true });
        });
        using var client = Client(bridge); var session = await Pair(client, bridge, PairToken(bridge));
        var body = new { requestId = "same-id", operation = "watch.settings", args = new { value = 1 } };
        var responses = await Task.WhenAll(Post(client, "/api/execute", body, bridge.Origin, session),
            Post(client, "/api/execute", body, bridge.Origin, session));
        Assert.Equal(1, calls);
        foreach (var response in responses)
        {
            using (response)
            {
                var result = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
                Assert.Equal("ok", result["status"]!.GetValue<string>());
                Assert.False(string.IsNullOrWhiteSpace(result["instanceId"]?.GetValue<string>()));
            }
        }
        using var changed = await Post(client, "/api/execute", new { requestId = "same-id", operation = "watch.settings", args = new { value = 2 } }, bridge.Origin, session);
        Assert.Equal("error", JsonNode.Parse(await changed.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task End_CancelsInFlightBeforeReleasingSession_AndDisposeClosesListener()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var released = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bridge = new LocalBrowserBridge(async (_, ct) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new LocalBrowserResult("ok");
        }, _ => released.TrySetResult(cancelled.Task.IsCompleted));
        using var client = Client(bridge); var session = await Pair(client, bridge, PairToken(bridge));
        var active = Post(client, "/api/execute", new { requestId = "r1", operation = "watch.clear", args = new { } }, bridge.Origin, session);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var ending = await Post(client, "/api/end", new { }, bridge.Origin, session);
        Assert.True(await released.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        using var reply = await active;
        Assert.Equal("unknown", JsonNode.Parse(await reply.Content.ReadAsStringAsync())!["status"]!.GetValue<string>());
        using var after = await Post(client, "/api/execute", new { requestId = "r2", operation = "local.identity", args = new { } }, bridge.Origin, session);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        bridge.Dispose();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync("/"));
    }

    [Theory]
    [InlineData("Content-Length: 2\r\nContent-Length: 2")]
    [InlineData("Content-Length: 2\r\nTransfer-Encoding: chunked")]
    public async Task AmbiguousHttpLength_IsRejected(string framing)
    {
        using var bridge = new LocalBrowserBridge((_, _) => Task.FromResult(new LocalBrowserResult("ok")));
        using var socket = new TcpClient();
        var uri = new Uri(bridge.Origin); await socket.ConnectAsync(uri.Host, uri.Port);
        using var stream = socket.GetStream();
        var bytes = Encoding.ASCII.GetBytes($"POST /api/pair HTTP/1.1\r\nHost: {uri.Authority}\r\nOrigin: {bridge.Origin}\r\nContent-Type: application/json\r\n{framing}\r\n\r\n{{}}");
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.ASCII);
        Assert.Contains("400", await reader.ReadLineAsync());
    }
}
