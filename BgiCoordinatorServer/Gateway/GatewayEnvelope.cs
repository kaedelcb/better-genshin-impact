using System.Text.Json;
using System.Text.Json.Nodes;

namespace BgiCoordinatorServer.Gateway;

/// <summary>
/// 网关传输信封（《通信方案》§4.2）——GatewayHub 上所有消息共用的 DTO。
/// 注意：服务器现网 SignalR 序列化为 System.Text.Json（无 Newtonsoft 依赖），
/// 故 Payload 用 System.Text.Json.Nodes.JsonObject，与现网 camelCase 约定一致。
/// </summary>
public sealed record GatewayEnvelope
{
    /// <summary>协议版本，冻结；破坏性变更才升。</summary>
    public int ProtocolVersion { get; init; } = GatewayProtocol.ProtocolVersion;

    /// <summary>"command" | "event" | "query" | "response" | "hello"</summary>
    public required string Type { get; init; }

    /// <summary>消息名，如 "syncPoint.reportArrival"、"room.create"。</summary>
    public required string Name { get; init; }

    /// <summary>command/query 必填，response 原样带回。</summary>
    public string? RequestId { get; init; }

    /// <summary>写类 command 建议必填（幂等键，本切片仅透传不消费）。</summary>
    public string? IdempotencyKey { get; init; }

    /// <summary>房间域消息必填（替代逐方法透传 roomCode）。</summary>
    public string? RoomCode { get; init; }

    /// <summary>event/response 携带：产生此消息时的房间 revision（§4.5 顺延，本切片恒 null）。</summary>
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

    /// <summary>构造一帧 evt 事件（服务端 → 客户端广播，§4.2 "evt" 回调）。</summary>
    public static GatewayEnvelope Event(string name, object? payload, string? roomCode)
    {
        return new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event,
            Name = name,
            RoomCode = roomCode,
            Payload = ToPayload(payload),
            SentAtUtc = DateTime.UtcNow
        };
    }
}

/// <summary>网关 JSON 序列化选项（STJ，camelCase，与 SignalR 现网默认一致）。</summary>
public static class GatewayJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
}
