using System.Text.Json;
using System.Text.Json.Nodes;

namespace MultiplayerHoeingAssistant.Services.Gateway;

/// <summary>
/// 网关传输信封（MHA 客户端镜像，《通信方案》§4.2）——/gateway 上所有消息共用的 DTO。
/// 与 BgiCoordinatorServer/Gateway/GatewayEnvelope.cs 逐字段保持一致，**以服务器为准**；
/// 服务器 SignalR 现网序列化为 System.Text.Json（无 Newtonsoft），故 Payload 用
/// System.Text.Json.Nodes.JsonObject，与现网 camelCase 约定一致。
/// （切片 9 落地，与 BGI 侧 GameTask/AutoHoeing/Multiplayer/Gateway/GatewayEnvelope.cs 同构。）
/// </summary>
public sealed record GatewayEnvelope
{
    /// <summary>协议版本，冻结；破坏性变更才升。</summary>
    public int ProtocolVersion { get; init; } = GatewayProtocol.ProtocolVersion;

    /// <summary>"command" | "event" | "query" | "response" | "hello"</summary>
    public required string Type { get; init; }

    /// <summary>消息名，如 "control.joinRoom"、"log.reportBatch"。</summary>
    public required string Name { get; init; }

    /// <summary>command/query 必填，response 原样带回（客户端侧仅作日志/排查用途——
    /// 响应关联由 SignalR InvokeAsync 天然完成，不依赖本字段匹配）。</summary>
    public string? RequestId { get; init; }

    /// <summary>写类 command 建议必填（幂等键，服务器本切片仅透传不消费）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>房间域消息必填（替代逐方法透传 roomCode）。</summary>
    public string? RoomCode { get; init; }

    /// <summary>event/response 携带：房间 revision（§4.5 顺延，现网恒 null）。</summary>
    public long? StateRevision { get; init; }

    /// <summary>业务数据（camelCase，与现网一致）。</summary>
    public JsonObject? Payload { get; init; }

    public DateTime SentAtUtc { get; init; }

    /// <summary>任意对象 → JsonObject 载荷（camelCase）。null 入 null 出。</summary>
    public static JsonObject? ToPayload(object? obj)
    {
        if (obj == null) return null;
        return JsonSerializer.SerializeToNode(obj, GatewayJson.Options) as JsonObject;
    }

    /// <summary>构造一帧 command（客户端 → Hub.Dispatch）。</summary>
    public static GatewayEnvelope Command(string name, object? payload, string? roomCode = null)
        => New(GatewayProtocol.MessageTypes.Command, name, payload, roomCode);

    /// <summary>构造一帧 query（客户端 → Hub.Query）。</summary>
    public static GatewayEnvelope Query(string name, object? payload, string? roomCode = null)
        => New(GatewayProtocol.MessageTypes.Query, name, payload, roomCode);

    private static GatewayEnvelope New(string type, string name, object? payload, string? roomCode)
        => new()
        {
            Type = type,
            Name = name,
            // N 格式（无连字符）：本字段客户端不用于响应匹配，无需在意服务器回显格式
            RequestId = Guid.NewGuid().ToString("N"),
            RoomCode = roomCode,
            Payload = ToPayload(payload),
            SentAtUtc = DateTime.UtcNow,
        };

    /// <summary>响应 payload 是否携带 error（{ "error": { "code": ..., "message": ... } }）。</summary>
    public bool TryGetError(out string code, out string message)
    {
        code = "";
        message = "";
        if (Payload != null
            && Payload.TryGetPropertyValue("error", out var errNode)
            && errNode is JsonObject err)
        {
            if (err.TryGetPropertyValue("code", out var c) && c != null)
                code = c.GetValue<string>();
            if (err.TryGetPropertyValue("message", out var m) && m != null)
                message = m.GetValue<string>();
            return true;
        }
        return false;
    }

    // ====== payload 取值辅助（服务器发 camelCase 键；TryGetPropertyValue 大小写敏感，按键名原样取）======

    public string GetString(string key, string def = "")
        => Payload != null && Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<string>() : def;

    public int GetInt(string key, int def = 0)
        => Payload != null && Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.GetValue<int>() : def;

    public T? Get<T>(string key) where T : class
        => Payload != null && Payload.TryGetPropertyValue(key, out var n) && n != null
            ? n.Deserialize<T>(GatewayJson.Options) : null;

    /// <summary>整个 payload 即业务对象时使用（如 control.playersUpdated 直传 ControlRoomPlayersUpdate）。</summary>
    public T? DeserializePayload<T>() where T : class
        => Payload != null ? Payload.Deserialize<T>(GatewayJson.Options) : null;
}

/// <summary>网关 JSON 序列化选项（STJ，camelCase + 大小写不敏感读取，与 SignalR 现网默认一致）。</summary>
public static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}

/// <summary>
/// 服务器在响应 payload.error 中返回业务错误（unsupported_operation / bad_request / internal_error 等）时抛出，
/// 对齐旧协议 HubException 被各调用方 catch 的语义位置。
/// </summary>
public sealed class GatewayErrorException : Exception
{
    public string Code { get; }

    public GatewayErrorException(string code, string message)
        : base($"[gateway:{code}] {message}")
    {
        Code = code;
    }
}
