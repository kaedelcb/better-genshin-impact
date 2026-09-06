using Microsoft.AspNetCore.SignalR.Client;

namespace MultiplayerHoeingAssistant.Services.Gateway;

/// <summary>
/// MHA 侧 v3 网关传输 SDK（切片 9，《通信方案》§4 模块二客户端层；模式照搬切片 8 的
/// BGI 侧 BgiGatewayClient，因两程序集无共享基建且 BGI 侧耦合 App.GetLogger，不抽公共基类——
/// 与《通信方案》迁移路线第 10 条已记录的实现偏差一致）。
/// 职责：连接 {基地址}/gateway、session.hello 握手与能力协商、evt 信封单订阅透传、
/// Dispatch/Query 两个发送原语、断线重连事件透传。
///
/// 纪律（《通信方案》§4.8/§4.9 沉淀）：
/// - 断线门控 + 不 throw 由调用方（SignalRClient 各方法的既有 try/catch）保留，
///   本类原语在传输异常时原样上抛、响应带 error 时抛 <see cref="GatewayErrorException"/>，
///   落点与旧协议 HubException 一致。
/// - HubConnection 是 IAsyncDisposable：重建连接前必须释放旧实例（双连接并发收发教训）。
/// - Closed/Reconnecting/Reconnected 回调全部透传给订阅方，订阅方自行内捕异常。
/// - 本类不记日志（MHA 无 ILogger 基建）：日志由调用方 SignalRClient 的 OnLog 承担。
/// </summary>
public sealed class MhaGatewayClient : IAsyncDisposable
{
    private HubConnection? _connection;

    /// <summary>evt 信封到达（服务器 → 客户端广播/定向）。在 SignalR 回调线程触发，订阅方不得外抛异常。</summary>
    public event Action<GatewayEnvelope>? EnvelopeReceived;

    /// <summary>自动重连成功（同一连接，新 connectionId）。订阅方须先重新 hello 再恢复业务。</summary>
    public event Func<string?, Task>? Reconnected;

    /// <summary>连接断开，内置自动重连进行中。</summary>
    public event Func<Exception?, Task>? Reconnecting;

    /// <summary>连接最终关闭（内置重连耗尽或 StartAsync 失败）。订阅方必须观察异常参数。</summary>
    public event Func<Exception?, Task>? Closed;

    /// <summary>握手协商到的服务端能力清单（能力缺省即不支持；本切片无任何行为依赖它，仅记录）。</summary>
    public string[] ServerCapabilities { get; private set; } = [];

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    /// <summary>
    /// URL 归一化（《通信方案》§4.8）：配置只填服务器基地址（如 http://xxx:8080），
    /// SDK 内部拼 /gateway。旧配置带 /hub（或 /hub/，大小写不敏感）的剥掉并置 strippedLegacyHub=true，
    /// 由调用方告警。其它路径不做猜测，原样保留。
    /// </summary>
    public static string NormalizeBaseUrl(string configuredUrl, out bool strippedLegacyHub)
    {
        strippedLegacyHub = false;
        var url = (configuredUrl ?? "").Trim().TrimEnd('/');
        if (url.EndsWith("/hub", StringComparison.OrdinalIgnoreCase))
        {
            url = url[..^4];
            strippedLegacyHub = true;
        }
        return url;
    }

    /// <summary>基地址 → 网关地址（{base}/gateway）。</summary>
    public static string BuildGatewayUrl(string baseUrl) => baseUrl.TrimEnd('/') + "/gateway";

    /// <summary>
    /// 建立连接并完成 session.hello 握手。任何失败（传输异常或 error 响应）原样上抛，
    /// 由调用方计为连接失败（SignalRClient.ConnectAsync 的旧语义：异常冒泡给 MainViewModel 重试定时器）。
    /// </summary>
    public async Task ConnectAsync(string baseUrl, CancellationToken ct = default)
    {
        // 重建前必须释放旧实例（避免双连接并发收发）
        if (_connection != null)
        {
            await DisposeConnectionAsync();
        }

        _connection = new HubConnectionBuilder()
            .WithUrl(BuildGatewayUrl(baseUrl))
            .WithAutomaticReconnect(new[] {
                TimeSpan.Zero,
                TimeSpan.FromSeconds(2),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            })
            .Build();

        // evt 单订阅：所有领域事件共用，按 Name 分发给订阅方。
        // 回调内绝不外抛（异常会回流 SignalR 管线），就地捕获。
        // 订阅方 SignalRClient.DispatchEvt 自身已内捕，此处是双保险。
        _connection.On<GatewayEnvelope>(GatewayProtocol.Callbacks.Event, env =>
        {
            try
            {
                EnvelopeReceived?.Invoke(env);
            }
            catch
            {
                // 订阅方异常不得回流 SignalR 管线（见上注）
            }
        });

        _connection.Reconnecting += error => Reconnecting?.Invoke(error) ?? Task.CompletedTask;
        _connection.Reconnected += connectionId => Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;
        _connection.Closed += error => Closed?.Invoke(error) ?? Task.CompletedTask;

        await _connection.StartAsync(ct);

        // DAP 时序：连接后第一条消息必须是 session.hello（握手完成前服务端拒绝其它消息）
        await HelloAsync(ct);
    }

    /// <summary>
    /// session.hello 握手与能力协商（§4.4）。失败（传输异常或 error 响应）原样上抛，
    /// 由调用方决定计为失败（首连 → ConnectAsync 上抛；重连 → 计入该次重连失败）。
    /// </summary>
    public async Task HelloAsync(CancellationToken ct = default)
    {
        var resp = await QueryAsync(GatewayProtocol.Names.SessionHello, new
        {
            clientKind = "assistant",
            clientVersion = typeof(MhaGatewayClient).Assembly.GetName().Version?.ToString() ?? "",
            protocolVersion = GatewayProtocol.ProtocolVersion,
            capabilities = Array.Empty<string>(),
        }, null, ct);

        ServerCapabilities = resp.Get<string[]>("capabilities") ?? [];
    }

    /// <summary>重连自愈用：对已构建的连接重新 StartAsync（与 SignalRClient P1-F 自愈定时器配套；
    /// 绝不重建连接——"自愈与内置重连并存"历史竞态教训）。</summary>
    public Task ReconnectStartAsync(CancellationToken ct = default)
        => _connection?.StartAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// command 调用（Hub.Dispatch）：返回响应信封；传输异常原样上抛；
    /// 响应携带 error 时抛 <see cref="GatewayErrorException"/>（对齐旧 HubException 落点）。
    /// </summary>
    public async Task<GatewayEnvelope> InvokeCommandAsync(string name, object? payload, string? roomCode = null, CancellationToken ct = default)
    {
        var env = GatewayEnvelope.Command(name, payload, roomCode);
        var resp = await _connection!.InvokeAsync<GatewayEnvelope>(GatewayProtocol.HubMethods.Dispatch, env, ct);
        if (resp.TryGetError(out var code, out var message))
            throw new GatewayErrorException(code, message);
        return resp;
    }

    /// <summary>query 调用（Hub.Query）：语义同 <see cref="InvokeCommandAsync"/>（本切片仅 session.hello 使用）。</summary>
    public async Task<GatewayEnvelope> QueryAsync(string name, object? payload, string? roomCode = null, CancellationToken ct = default)
    {
        var env = GatewayEnvelope.Query(name, payload, roomCode);
        var resp = await _connection!.InvokeAsync<GatewayEnvelope>(GatewayProtocol.HubMethods.Query, env, ct);
        if (resp.TryGetError(out var code, out var message))
            throw new GatewayErrorException(code, message);
        return resp;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeConnectionAsync();
    }

    private async Task DisposeConnectionAsync()
    {
        var conn = _connection;
        _connection = null;
        if (conn != null)
            await conn.DisposeAsync();
    }
}
