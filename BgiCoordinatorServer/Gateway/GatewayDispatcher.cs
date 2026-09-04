using System.Reflection;
using System.Text.Json;
using BgiCoordinatorServer.Services;

namespace BgiCoordinatorServer.Gateway;

/// <summary>
/// 网关分发器（《通信方案》§4.2/§4.3/§4.4）：
/// Dispatch/Query 两个入口 → 消息名路由表 → RoomOperations 强类型操作方法。
/// 路由表按族注册（partial 方法，各族一个文件，随兼容层迁移逐族补齐）。
/// </summary>
public sealed partial class GatewayDispatcher
{
    private readonly GatewaySessionTracker _tracker;
    private readonly ILogger<GatewayDispatcher> _logger;
    private readonly RoomOperations _ops;

    private readonly Dictionary<string, Func<GatewayEnvelope, GatewayHandlerContext, Task<object?>>>
        _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Func<GatewayEnvelope, GatewayHandlerContext, Task<object?>>>
        _queries = new(StringComparer.Ordinal);

    public GatewayDispatcher(GatewaySessionTracker tracker, ILogger<GatewayDispatcher> logger, RoomOperations ops)
    {
        _tracker = tracker;
        _logger = logger;
        _ops = ops;
        RegisterAllHandlers();
    }

    /// <summary>Dispatch 入口：command 类消息（写操作/事件上报）。</summary>
    public Task<GatewayEnvelope> DispatchAsync(GatewayHandlerContext ctx, GatewayEnvelope envelope)
        => RouteAsync(ctx, envelope, isQuery: false);

    /// <summary>Query 入口：query 类消息（只读查询）。</summary>
    public Task<GatewayEnvelope> QueryAsync(GatewayHandlerContext ctx, GatewayEnvelope envelope)
        => RouteAsync(ctx, envelope, isQuery: true);

    private async Task<GatewayEnvelope> RouteAsync(GatewayHandlerContext ctx, GatewayEnvelope env, bool isQuery)
    {
        // DAP 时序：hello 先行；握手完成前拒绝其它消息（§4.4）
        if (env.Name == GatewayProtocol.Names.SessionHello)
            return HandleHello(ctx, env);

        if (!_tracker.IsV3(ctx.ConnectionId))
            return Error(env, GatewayProtocol.ErrorCodes.HandshakeRequired,
                "握手未完成：连接后第一条消息必须是 session.hello");

        var table = isQuery ? _queries : _commands;
        var other = isQuery ? _commands : _queries;
        if (!table.TryGetValue(env.Name, out var handler))
        {
            return other.ContainsKey(env.Name)
                ? Error(env, GatewayProtocol.ErrorCodes.WrongChannel, $"消息 {env.Name} 应走 {(isQuery ? "Dispatch" : "Query")} 通道")
                : Error(env, GatewayProtocol.ErrorCodes.UnsupportedOperation, $"未知消息名：{env.Name}");
        }

        try
        {
            var result = await handler(env, ctx);
            return Respond(env, result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Gateway] 处理消息 {Name} 失败（连接 {ConnId}）", env.Name, ctx.ConnectionId);
            return Error(env, GatewayProtocol.ErrorCodes.InternalError, ex.Message);
        }
    }

    /// <summary>session.hello：能力协商（§4.4）。能力缺省即不支持。</summary>
    private GatewayEnvelope HandleHello(GatewayHandlerContext ctx, GatewayEnvelope env)
    {
        ClientHello? hello;
        try
        {
            hello = env.Payload?.Deserialize<ClientHello>(GatewayJson.Options);
        }
        catch (JsonException)
        {
            return Error(env, GatewayProtocol.ErrorCodes.BadRequest, "hello payload 格式错误");
        }
        if (hello == null)
            return Error(env, GatewayProtocol.ErrorCodes.BadRequest, "hello payload 缺失");

        if (hello.ProtocolVersion < GatewayProtocol.MinimumClientProtocol)
        {
            _logger.LogWarning("[Gateway] 连接 {ConnId} 协议版本 {Version} 低于最低要求 {Min}，拒绝握手",
                ctx.ConnectionId, hello.ProtocolVersion, GatewayProtocol.MinimumClientProtocol);
            return Error(env, GatewayProtocol.ErrorCodes.ProtocolTooOld,
                $"客户端协议版本 {hello.ProtocolVersion} 低于服务端最低要求 {GatewayProtocol.MinimumClientProtocol}");
        }

        _tracker.CompleteHello(ctx.ConnectionId, hello);
        _logger.LogInformation("[Gateway] 连接 {ConnId} 完成握手：kind={Kind} version={Version} capabilities=[{Caps}]",
            ctx.ConnectionId, hello.ClientKind, hello.ClientVersion, string.Join(",", hello.Capabilities));

        return Respond(env, new
        {
            protocolVersion = GatewayProtocol.ProtocolVersion,
            serverVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
            capabilities = GatewayProtocol.ServerCapabilities,
            minimumClientProtocol = GatewayProtocol.MinimumClientProtocol,
        });
    }

    private static GatewayEnvelope Respond(GatewayEnvelope req, object? payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response,
        Name = req.Name,
        RequestId = req.RequestId,
        RoomCode = req.RoomCode,
        Payload = GatewayEnvelope.ToPayload(payload),
        SentAtUtc = DateTime.UtcNow,
    };

    private static GatewayEnvelope Error(GatewayEnvelope req, string code, string message)
        => Respond(req, new { error = new { code, message } });

    // ====== 信封解析辅助（各族路由适配器共用）======

    private static string GetString(GatewayEnvelope env, string key, string def = "")
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<string>() : def;

    private static int GetInt(GatewayEnvelope env, string key, int def = 0)
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<int>() : def;

    private static long GetLong(GatewayEnvelope env, string key, long def = 0)
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<long>() : def;

    private static bool GetBool(GatewayEnvelope env, string key, bool def = false)
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<bool>() : def;

    private static double GetDouble(GatewayEnvelope env, string key, double def = 0)
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<double>() : def;

    private static DateTime GetDateTime(GatewayEnvelope env, string key)
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<DateTime>() : default;

    private static List<string>? GetStringList(GatewayEnvelope env, string key)
        => Get<List<string>>(env, key);

    private static T? Get<T>(GatewayEnvelope env, string key) where T : class
        => env.Payload != null && env.Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.Deserialize<T>(GatewayJson.Options) : null;

    // ====== 路由表注册（各族一个 partial 文件，随兼容层迁移补齐）======

    private void RegisterAllHandlers()
    {
        RegisterRoomLifecycle();
        RegisterRoomQueries();
        RegisterRoomConfig();
        RegisterRouteVerification();
        RegisterSyncPoint();
        RegisterFight();
        RegisterExp();
        RegisterKazuha();
        RegisterWorld();
        RegisterAnomaly();
        RegisterSession();
        RegisterControlRoom();
        RegisterMemberLog();
    }

    partial void RegisterRoomLifecycle();
    partial void RegisterRoomQueries();
    partial void RegisterRoomConfig();
    partial void RegisterRouteVerification();
    partial void RegisterSyncPoint();
    partial void RegisterFight();
    partial void RegisterExp();
    partial void RegisterKazuha();
    partial void RegisterWorld();
    partial void RegisterAnomaly();
    partial void RegisterSession();
    partial void RegisterControlRoom();
    partial void RegisterMemberLog();
}
