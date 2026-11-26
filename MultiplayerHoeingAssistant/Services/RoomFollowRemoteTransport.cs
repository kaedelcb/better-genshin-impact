using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

internal static class RoomFollowRemoteTransport
{
    private static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);

    internal static async Task PublishAsync(RoomFollowGrant grant, bool bypassProxy, RoomFollowOutbox outbox,
        CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var http = CreateHttpInvoker(bypassProxy);
        await using var connection = Connect(grant.Invitation, http);
        connection.Closed += _ => EndSession(session);
        var demand = new RoomFollowDemand(outbox.Wake);
        using var handler = connection.On<bool>("FollowDemand", demand.OnEvent);

        try
        {
            using (var budget = Budget(session.Token))
            {
                await connection.StartAsync(budget.Token);
                var binding = await connection.InvokeAsync<BindResult>("BindPublisher", grant.WriteSecret, grant.ReadHash, budget.Token);
                if (binding is null || binding.Version < 0) throw InvalidResponse();
                demand.OnBound(binding.Demand);
            }

            long sentRevision = -1, sentDemand = -1;
            while (!session.IsCancellationRequested)
            {
                var currentDemand = demand.Capture();
                var current = outbox.Capture();
                if (currentDemand.Required && (current.Revision != sentRevision || currentDemand.Epoch != sentDemand))
                {
                    // 每个新绑定及需求上升沿发送最新状态，不重播历史或定时查询状态。
                    using var budget = Budget(session.Token);
                    await connection.InvokeAsync("Publish", current.State, budget.Token);
                    sentRevision = current.Revision;
                    sentDemand = currentDemand.Epoch;
                    continue;
                }
                await outbox.WaitAsync(session.Token);
            }
        }
        catch (HubException ex) when (ex.Message.Contains("Follow publisher superseded.", StringComparison.Ordinal))
        { throw new RoomFollowFencedException(); }
    }

    internal static async Task SubscribeAsync(RoomFollowInvitation invitation, bool bypassProxy, Action<string?> deliver,
        CancellationToken token)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var http = CreateHttpInvoker(bypassProxy);
        await using var connection = Connect(invitation, http);
        connection.Closed += _ => EndSession(session);
        // 每次连接的版本基线独立；中继重启后版本可以从头开始。
        var inbox = new RoomFollowStateInbox();
        using var handler = connection.On<long, string?>("FollowState", inbox.Offer);
        using (var budget = Budget(session.Token))
        {
            await connection.StartAsync(budget.Token);
            var binding = await connection.InvokeAsync<BindResult>("BindSubscriber", invitation.ReadSecret, invitation.WriteHash, budget.Token);
            if (binding is null || binding.Version < 0) throw InvalidResponse();
            inbox.Offer(binding.Version, binding.State);
        }

        long delivered = -1;
        while (!session.IsCancellationRequested)
        {
            var current = inbox.Capture();
            if (current.Version > delivered)
            {
                session.Token.ThrowIfCancellationRequested();
                deliver(current.State);
                delivered = current.Version;
                continue;
            }
            await inbox.WaitAsync(session.Token);
        }
    }

    internal static Uri RelayWebSocketUri(RoomFollowInvitation invitation)
    {
        invitation.Validate();
        if (invitation.Local) throw new ArgumentException("本机跟随邀请不能连接网络中继。");
        return new UriBuilder(invitation.ServerUrl + "/room-follow") { Scheme = "wss" }.Uri;
    }

    // 使用固定 HTTPS 基地址的直接 WebSocket。无协商重定向、HTTP 跳转、Cookie 或默认登录凭据。
    // TLS 验证保持系统默认；不从房间快照中取地址，也不把能力放到 URL 或 HTTP 头中。
    private static HubConnection Connect(RoomFollowInvitation invitation, HttpMessageInvoker http)
    {
        var socketUri = RelayWebSocketUri(invitation);
        return new HubConnectionBuilder()
            .WithUrl(invitation.ServerUrl + "/room-follow", HttpTransportType.WebSockets, options =>
            {
                options.SkipNegotiation = true;
                options.WebSocketFactory = async (_, ct) =>
                {
                    var socket = new ClientWebSocket();
                    try
                    {
                        await socket.ConnectAsync(socketUri, http, ct);
                        return socket;
                    }
                    catch { socket.Dispose(); throw; }
                };
            }).Build();
    }

    private static HttpMessageInvoker CreateHttpInvoker(bool bypassProxy) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = !bypassProxy
    });

    private static CancellationTokenSource Budget(CancellationToken token)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(OperationTimeout);
        return budget;
    }

    private static Task EndSession(CancellationTokenSource session)
    {
        try { session.Cancel(); }
        catch (ObjectDisposedException) { }
        return Task.CompletedTask;
    }

    private static InvalidOperationException InvalidResponse() => new("无效的房间跟随绑定响应。");

    private sealed class BindResult
    {
        public BindResult() { }
        [JsonPropertyName("version"), JsonRequired] public long Version { get; set; }
        [JsonPropertyName("state"), JsonRequired] public string? State { get; set; }
        [JsonPropertyName("demand"), JsonRequired] public bool Demand { get; set; }
    }
}

// 同一通道的需求事件按顺序发送，绑定完成的基线不能覆盖已经收到的事件。
internal sealed class RoomFollowDemand(Action wake)
{
    private readonly object _gate = new();
    private bool _hasEvent, _required;
    private long _epoch;

    internal void OnEvent(bool demand)
    {
        lock (_gate) { _hasEvent = true; Set(demand); }
        wake();
    }

    internal void OnBound(bool demand)
    {
        lock (_gate) { if (!_hasEvent) Set(demand); }
        wake();
    }

    internal (bool Required, long Epoch) Capture()
    {
        lock (_gate) return (_required, _epoch);
    }

    private void Set(bool demand)
    {
        if (demand == _required) return;
        _required = demand;
        if (demand) _epoch++;
    }
}

internal sealed class RoomFollowStateInbox
{
    private readonly object _gate = new();
    private readonly Channel<byte> _signal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private long _version = -1;
    private string? _state;

    internal void Offer(long version, string? state)
    {
        lock (_gate)
        {
            if (version <= _version) return;
            _version = version;
            // 错误或超限的状态不能使收到它之前的视图继续显示为当前状态。
            try
            {
                _state = state is not null && (state.Length > RoomFollowFormat.MaxStateBytes
                    || RoomFollowFormat.Utf8.GetByteCount(state) > RoomFollowFormat.MaxStateBytes) ? null : state;
            }
            catch (System.Text.EncoderFallbackException) { _state = null; }
        }
        _signal.Writer.TryWrite(0);
    }

    internal (long Version, string? State) Capture()
    {
        lock (_gate) return (_version, _state);
    }

    internal async ValueTask WaitAsync(CancellationToken token) { _ = await _signal.Reader.ReadAsync(token); }
}
