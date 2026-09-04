using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>GatewayEnvelope STJ 序列化测试（§4.2：camelCase 与现网一致）。</summary>
public class GatewayEnvelopeTests
{
    [Fact]
    public void Event_SerializationRoundTrip_CamelCaseFields()
    {
        var env = GatewayEnvelope.Event("sync.allArrived", new { syncPointId = "sp-1" }, "ABC123");

        var json = JsonSerializer.Serialize(env, GatewayJson.Options);

        // 信封字段 camelCase
        Assert.Contains("\"type\":\"event\"", json);
        Assert.Contains("\"name\":\"sync.allArrived\"", json);
        Assert.Contains("\"roomCode\":\"ABC123\"", json);
        // payload 内字段 camelCase：syncPointId 而不是 SyncPointId
        Assert.Contains("syncPointId", json);
        Assert.DoesNotContain("SyncPointId", json);

        // 反序列化往返
        var back = JsonSerializer.Deserialize<GatewayEnvelope>(json, GatewayJson.Options)!;
        Assert.Equal(GatewayProtocol.MessageTypes.Event, back.Type);
        Assert.Equal("sync.allArrived", back.Name);
        Assert.Equal("ABC123", back.RoomCode);
        Assert.NotNull(back.Payload);
        Assert.Equal("sp-1", back.Payload["syncPointId"]!.GetValue<string>());
        Assert.Equal(GatewayProtocol.ProtocolVersion, back.ProtocolVersion);
    }

    [Fact]
    public void Event_NullPayload_RoundTrips()
    {
        var env = GatewayEnvelope.Event("room.closed", null, "ABC123");

        var json = JsonSerializer.Serialize(env, GatewayJson.Options);
        var back = JsonSerializer.Deserialize<GatewayEnvelope>(json, GatewayJson.Options)!;

        Assert.Equal("room.closed", back.Name);
        Assert.Equal("ABC123", back.RoomCode);
        Assert.True(back.Payload == null || back.Payload.Count == 0);
    }
}
