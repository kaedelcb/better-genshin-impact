using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// L2 会话层：每个命名管道连接一个 ext.* 会话。
/// 职责：ext.hello 握手、请求路由（按操作分发到控制/查询/事件三个平面）、
/// 幂等窗口（§3.5：命中重复键直接重放缓存响应，不再执行）。
/// 会话随连接自然消亡（ConditionalWeakTable），事件订阅由 ExternalInterfaceEventHub 按连接存活状态清理。
/// </summary>
internal sealed class ExternalInterfaceSession
{
    private static readonly ConditionalWeakTable<InstanceConnection, ExternalInterfaceSession> Sessions = new();

    /// <summary>取（或建）指定连接对应的 ext.* 会话。连接对象被回收后会话随之回收，无泄漏。</summary>
    public static ExternalInterfaceSession GetOrCreate(InstanceConnection connection)
        => Sessions.GetValue(connection, static c => new ExternalInterfaceSession(c));

    private readonly InstanceConnection _connection;
    private readonly ILogger _logger;

    /// <summary>幂等窗口：key → (首次执行时间, 缓存的成功响应)。TTL 见 ExternalInterfaceProtocol.IdempotencyWindowSeconds。</summary>
    private readonly ConcurrentDictionary<string, (DateTime SeenAt, InstanceIpcEnvelope Response)> _idempotencyWindow = new();

    /// <summary>握手响应与事件帧共用的会话 ID（GUID），重连去重挂在它上面。</summary>
    public Guid SessionId { get; } = Guid.NewGuid();

    /// <summary>本会话所属的命名管道连接（事件推送写目标）。</summary>
    public InstanceConnection Connection => _connection;

    private ExternalInterfaceSession(InstanceConnection connection)
    {
        _connection = connection;
        try
        {
            _logger = App.GetService<ILogger<ExternalInterfaceSession>>() ?? (Microsoft.Extensions.Logging.ILogger)NullLogger.Instance;
        }
        catch
        {
            _logger = NullLogger.Instance;
        }
    }

    /// <summary>路由 ext.* 请求。所有可预期的请求错误都被转换为失败响应，绝不抛出。</summary>
    public async Task<InstanceIpcEnvelope> RouteAsync(
        InstanceRequestHandler handler,
        InstanceIpcEnvelope request,
        CancellationToken cancellationToken)
    {
        try
        {
            switch (request.Operation)
            {
                case ExternalInterfaceOperations.Hello:
                    return HandleHello(request);
                case ExternalInterfaceOperations.EventSubscribe:
                    return ExternalInterfaceEventPlane.HandleSubscribe(this, request);
                case ExternalInterfaceOperations.EventUnsubscribe:
                    return ExternalInterfaceEventPlane.HandleUnsubscribe(this, request);
            }

            // §3.5：写操作可携带 data.idempotencyKey；缺省按 requestId 自然幂等（同一信封重发去重）。
            string? windowKey = null;
            if (ExternalInterfaceOperations.IsWriteOperation(request.Operation))
            {
                var idempotencyKey = request.Data?["idempotencyKey"]?.ToString();
                windowKey = idempotencyKey is { Length: > 0 }
                    ? $"key:{idempotencyKey}"
                    : $"rid:{request.RequestId}";

                if (_idempotencyWindow.TryGetValue(windowKey, out var cached)
                    && (DateTime.UtcNow - cached.SeenAt) < TimeSpan.FromSeconds(ExternalInterfaceProtocol.IdempotencyWindowSeconds))
                {
                    _logger.LogInformation(
                        "[IDEMPOTENT_REPLAY] {Operation} key={Key} 命中幂等窗口，重放缓存响应，不再执行",
                        request.Operation,
                        windowKey);
                    return cached.Response;
                }
            }

            var response = await DispatchToPlaneAsync(handler, request, cancellationToken).ConfigureAwait(false);

            // 只缓存成功响应：失败多为瞬态（如 task_already_running），重放失败会挡住客户端的合法重试。
            if (windowKey is not null && response.Success == true)
            {
                PruneExpiredWindowEntries();
                _idempotencyWindow[windowKey] = (DateTime.UtcNow, response);
            }

            return response;
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or IOException
                                          or TimeoutException
                                          or JsonException)
        {
            _logger.LogWarning(exception, "处理 ext.* 请求失败：{Operation}", request.Operation);
            return InstanceIpcEnvelope.Failure(
                request,
                "invalid_request",
                exception.GetBaseException().Message);
        }
    }

    private InstanceIpcEnvelope HandleHello(InstanceIpcEnvelope request)
    {
        var data = ExternalInterfaceProtocol.BuildHelloData(
            SessionId,
            _connection.ClientSessionId,
            _connection.ClientProcessId);
        return InstanceIpcEnvelope.Response(request, data);
    }

    private async Task<InstanceIpcEnvelope> DispatchToPlaneAsync(
        InstanceRequestHandler handler,
        InstanceIpcEnvelope request,
        CancellationToken cancellationToken)
    {
        if (ExternalInterfaceQueryPlane.TryDispatch(
                handler,
                _connection,
                request,
                out var queryResponse))
        {
            return queryResponse;
        }

        return await ExternalInterfaceCommandPlane.DispatchAsync(
                handler,
                _connection,
                request,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private void PruneExpiredWindowEntries()
    {
        if (_idempotencyWindow.Count < 128)
        {
            return;
        }

        var expiredBefore = DateTime.UtcNow.AddSeconds(-ExternalInterfaceProtocol.IdempotencyWindowSeconds);
        foreach (var pair in _idempotencyWindow)
        {
            if (pair.Value.SeenAt < expiredBefore)
            {
                _idempotencyWindow.TryRemove(pair.Key, out _);
            }
        }
    }
}

/// <summary>
/// 事件平面：订阅/退订。订阅表挂在 ExternalInterfaceEventHub（进程级单例），
/// 事件推送只在存在订阅者时运行观察器（A1：单机零感知）。
/// </summary>
internal static class ExternalInterfaceEventPlane
{
    public static InstanceIpcEnvelope HandleSubscribe(
        ExternalInterfaceSession session,
        InstanceIpcEnvelope request)
    {
        var events = ParseEventList(request);
        ExternalInterfaceEventHub.Instance.Subscribe(
            session.SessionId,
            session.Connection,
            events);
        return InstanceIpcEnvelope.Response(
            request,
            new
            {
                subscribed = events.Count == 0
                    ? ExternalInterfaceEventNames.All
                    : events.ToArray(),
            });
    }

    public static InstanceIpcEnvelope HandleUnsubscribe(
        ExternalInterfaceSession session,
        InstanceIpcEnvelope request)
    {
        var events = ParseEventList(request);
        ExternalInterfaceEventHub.Instance.Unsubscribe(session.SessionId, events);
        return InstanceIpcEnvelope.Response(request, new { ok = true });
    }

    /// <summary>解析 data.events；空/缺失表示"全部已知事件"。未知事件名直接忽略（宽松语义）。</summary>
    private static List<string> ParseEventList(InstanceIpcEnvelope request)
    {
        var result = new List<string>();
        if (request.Data?["events"] is not JArray array)
        {
            return result;
        }

        foreach (var token in array)
        {
            if (token.Type == JTokenType.String
                && token.ToString() is { } name
                && ExternalInterfaceEventNames.IsKnown(name)
                && !result.Contains(name, StringComparer.Ordinal))
            {
                result.Add(name);
            }
        }

        return result;
    }
}
