#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;

/// <summary>
/// BGI 侧 v3 网关传输 SDK（切片 8，《通信方案》§4 模块二客户端层）。
/// 职责：连接 {基地址}/gateway、session.hello 握手与能力协商、evt 信封单订阅分发、
/// Dispatch/Query/fire-and-forget 三个发送原语、断线重连事件透传。
///
/// 纪律（《通信方案》§4.8/§4.9 沉淀）：
/// - 断线门控 + 不 throw 由调用方（CoordinatorClient 各方法的既有 try/catch）保留，
///   本类原语在传输异常时原样上抛、响应带 error 时抛 <see cref="GatewayErrorException"/>，
///   落点与旧协议 HubException 一致。
/// - HubConnection 是 IAsyncDisposable：重建连接前必须释放旧实例（双连接并发收发教训）。
/// - Closed/Reconnecting/Reconnected 回调全部透传给订阅方，订阅方自行内捕异常。
/// </summary>
public sealed class BgiGatewayClient : IAsyncDisposable
{
    private readonly ILogger<BgiGatewayClient> _logger = App.GetLogger<BgiGatewayClient>();

    private HubConnection? _connection;

    // === 测试种子（沿用 CoordinatorClient._invokeHubAsync 的既有模式）===
    // 注入后绕过真实 HubConnection，让单测可断言信封线形（名称 + payload）而不依赖网络。
    // 仅在测试中赋值；生产路径保持 null。注意：HubConnection 在 SignalR.Client 8.0 中
    // State/SendAsync 均为不可重写成员，Moq 无法 mock，故用 Func 种子代替。
    internal Func<string, GatewayEnvelope, CancellationToken, Task>? _testSendOverride;
    internal Func<string, GatewayEnvelope, CancellationToken, Task<GatewayEnvelope>>? _testInvokeOverride;

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
    /// 建立连接并完成 session.hello 握手。成功返回 true；任何失败记日志并返回 false
    /// （对齐旧 CoordinatorClient.ConnectAsync 的 bool 语义）。
    /// </summary>
    public async Task<bool> ConnectAsync(string serverUrl, CancellationToken ct)
    {
        try
        {
            var baseUrl = NormalizeBaseUrl(serverUrl, out var stripped);
            if (stripped)
            {
                _logger.LogWarning("[联机] 配置的服务器地址带旧格式 /hub 尾巴，已自动归一化为基地址（新协议固定走 /gateway）");
            }

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
            // 回调内绝不外抛（异常会回流 SignalR 管线），就地捕获记日志。
            _connection.On<GatewayEnvelope>(GatewayProtocol.Callbacks.Event, env =>
            {
                try
                {
                    EnvelopeReceived?.Invoke(env);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[联机] evt 事件分发失败（已吞掉）: {Name}", env.Name);
                }
            });

            _connection.Reconnecting += error => Reconnecting?.Invoke(error) ?? Task.CompletedTask;
            _connection.Reconnected += connectionId => Reconnected?.Invoke(connectionId) ?? Task.CompletedTask;
            _connection.Closed += error => Closed?.Invoke(error) ?? Task.CompletedTask;

            await _connection.StartAsync(ct);

            // DAP 时序：连接后第一条消息必须是 session.hello（握手完成前服务端拒绝其它消息）
            await HelloAsync(ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BgiGatewayClient 连接/握手失败");
            return false;
        }
    }

    /// <summary>
    /// session.hello 握手与能力协商（§4.4）。失败（传输异常或 error 响应）原样上抛，
    /// 由调用方决定计为失败（首连 → ConnectAsync 返回 false；重连 → 计入该次重连失败）。
    /// </summary>
    public async Task HelloAsync(CancellationToken ct = default)
    {
        var resp = await QueryAsync(GatewayProtocol.Names.SessionHello, new
        {
            clientKind = "bgi",
            clientVersion = BetterGenshinImpact.Core.Config.Global.Version ?? "",
            protocolVersion = GatewayProtocol.ProtocolVersion,
            capabilities = Array.Empty<string>(),
        }, null, ct);

        ServerCapabilities = resp.Get<string[]>("capabilities") ?? [];
        _logger.LogInformation("[联机] 网关握手完成：serverVersion={Version} capabilities=[{Caps}]",
            resp.GetString("serverVersion", "unknown"), string.Join(",", ServerCapabilities));
    }

    /// <summary>重连自愈用：对已构建的连接重新 StartAsync（与旧 OnConnectionClosed 退避循环配套）。</summary>
    public Task ReconnectStartAsync(CancellationToken ct = default)
        => _connection?.StartAsync(ct) ?? Task.CompletedTask;

    /// <summary>
    /// command 调用（Hub.Dispatch）：返回响应信封；传输异常原样上抛；
    /// 响应携带 error 时抛 <see cref="GatewayErrorException"/>（对齐旧 HubException 落点）。
    /// </summary>
    public async Task<GatewayEnvelope> InvokeCommandAsync(string name, object? payload, string? roomCode = null, CancellationToken ct = default)
    {
        var env = GatewayEnvelope.Command(name, payload, roomCode);
        var resp = _testInvokeOverride != null
            ? await _testInvokeOverride(GatewayProtocol.HubMethods.Dispatch, env, ct)
            : await _connection!.InvokeAsync<GatewayEnvelope>(GatewayProtocol.HubMethods.Dispatch, env, ct);
        if (resp.TryGetError(out var code, out var message))
            throw new GatewayErrorException(code, message);
        return resp;
    }

    /// <summary>query 调用（Hub.Query）：语义同 <see cref="InvokeCommandAsync"/>。</summary>
    public async Task<GatewayEnvelope> QueryAsync(string name, object? payload, string? roomCode = null, CancellationToken ct = default)
    {
        var env = GatewayEnvelope.Query(name, payload, roomCode);
        var resp = _testInvokeOverride != null
            ? await _testInvokeOverride(GatewayProtocol.HubMethods.Query, env, ct)
            : await _connection!.InvokeAsync<GatewayEnvelope>(GatewayProtocol.HubMethods.Query, env, ct);
        if (resp.TryGetError(out var code, out var message))
            throw new GatewayErrorException(code, message);
        return resp;
    }

    /// <summary>
    /// fire-and-forget command（不等 ACK）：与旧 SendAsync("WaitForAllPlayers", ...) 逐字等价——
    /// 服务端处理异常不达客户端，调用方只感知发送失败。
    /// </summary>
    public async Task SendCommandFireAndForgetAsync(string name, object? payload, string? roomCode = null, CancellationToken ct = default)
    {
        var env = GatewayEnvelope.Command(name, payload, roomCode);
        if (_testSendOverride != null)
        {
            await _testSendOverride(GatewayProtocol.HubMethods.Dispatch, env, ct);
            return;
        }
        await _connection!.SendAsync(GatewayProtocol.HubMethods.Dispatch, env, ct);
    }

    public async Task StopAsync()
    {
        if (_connection != null)
            await _connection.StopAsync();
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
