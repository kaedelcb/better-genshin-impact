using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;
using Connection = BgiCoordinatorServer.Services.RoomFollowRelay.Connection;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomFollowRelayTests
{
    [Fact]
    public async Task DemandAndStateAreScopedAndUndemandedStateIsNeverCached()
    {
        var fixture = new Fixture();
        var relay = fixture.Relay;
        var grant = Grant.Create();
        var other = Grant.Create();
        var publisher = new Connection("publisher");
        var reader = new Connection("reader");
        var unrelated = new Connection("unrelated");
        Assert.False((await grant.BindPublisher(relay, publisher)).Demand);
        await relay.PublishAsync(publisher, State("before demand"));
        Assert.Null((await other.BindSubscriber(relay, unrelated)).State);
        Assert.Empty(fixture.Events);

        var binding = await grant.BindSubscriber(relay, reader);
        Assert.True(binding.Demand);
        Assert.Null(binding.State);
        AssertDemand(fixture, publisher, true);
        var state = State("first confirmed room");
        await relay.PublishAsync(publisher, state);
        var sent = AssertState(fixture, reader, state);
        Assert.True(sent > binding.Version);
        await relay.PublishAsync(publisher, state);
        Assert.Empty(fixture.Events); // 相同 JSON 内容不重复转发或推进版本。
        var duplicate = await grant.BindSubscriber(relay, reader);
        Assert.Equal(sent, duplicate.Version);
        Assert.Equal(state, duplicate.State);
        Assert.Empty(fixture.Events);

        await relay.DisconnectAsync(reader);
        AssertDemand(fixture, publisher, false);
        await relay.PublishAsync(publisher, State("while no reader"));
        Assert.Empty(fixture.Events);
        reader = new Connection("reconnected reader");
        var reconnected = await grant.BindSubscriber(relay, reader);
        Assert.Null(reconnected.State);
        Assert.True(reconnected.Version > sent);
        AssertDemand(fixture, publisher, true);
        await relay.PublishAsync(publisher, state);
        AssertState(fixture, reader, state);
        await relay.DisconnectAsync(publisher);
        AssertState(fixture, reader, null);
        await relay.DisconnectAsync(reader);
        await relay.DisconnectAsync(unrelated);
    }

    [Fact]
    public async Task AConnectionHasOneCapabilityRoleAndOnlyCurrentPublisherCanSendMetadata()
    {
        var fixture = new Fixture();
        var relay = fixture.Relay;
        var grant = Grant.Create();
        var other = Grant.Create();
        var publisher = new Connection("publisher");
        var reader = new Connection("reader");
        await grant.BindPublisher(relay, publisher);
        await grant.BindSubscriber(relay, reader);
        fixture.Clear();

        await Assert.ThrowsAsync<HubException>(() => relay.PublishAsync(reader, State("reader")));
        await Assert.ThrowsAsync<HubException>(() => relay.PublishAsync(new Connection("unbound"), State("unbound")));
        await Assert.ThrowsAsync<HubException>(() => grant.BindPublisher(relay, reader));
        await Assert.ThrowsAsync<HubException>(() => other.BindSubscriber(relay, reader));
        await Assert.ThrowsAsync<HubException>(() => grant.BindSubscriber(relay, publisher));
        Assert.Empty(fixture.Events);
        await relay.PublishAsync(publisher, State("legitimate"));
        AssertState(fixture, reader, State("legitimate"));
        await relay.DisconnectAsync(reader);
        await relay.DisconnectAsync(publisher);
    }

    [Fact]
    public async Task ReplacementFencesOldConnectionAndClearsItsStateBeforeNewPublication()
    {
        var fixture = new Fixture(); var relay = fixture.Relay; var grant = Grant.Create();
        var old = new Connection("old"); var current = new Connection("new"); var reader = new Connection("reader");
        await grant.BindSubscriber(relay, reader);
        await grant.BindPublisher(relay, old);
        await relay.PublishAsync(old, State("old room"));
        long previous = AssertState(fixture, reader, State("old room"));
        var binding = await grant.BindPublisher(relay, current);
        Assert.True(binding.Demand);
        Assert.Null(binding.State);
        var deliveries = fixture.Events.ToArray();
        Assert.Equal(2, deliveries.Length);
        Assert.Equal(reader.Id, deliveries[0].Target);
        Assert.Equal("FollowState", deliveries[0].Method);
        Assert.True(Assert.IsType<long>(deliveries[0].Arguments[0]) > previous);
        Assert.Null(deliveries[0].Arguments[1]);
        Assert.Equal(old.Id, deliveries[1].Target);
        Assert.Equal("FollowDemand", deliveries[1].Method);
        Assert.False(Assert.IsType<bool>(deliveries[1].Arguments[0]));
        fixture.Clear();

        await Assert.ThrowsAsync<HubException>(() => grant.BindPublisher(relay, old));
        await Assert.ThrowsAsync<HubException>(() => relay.PublishAsync(old, State("old room")));
        await relay.PublishAsync(current, State("new room"));
        var sent = AssertState(fixture, reader, State("new room"));
        await relay.DisconnectAsync(old);
        Assert.Empty(fixture.Events);
        Assert.Equal(sent, (await grant.BindSubscriber(relay, reader)).Version);
        await relay.DisconnectAsync(current);
        Assert.True(AssertState(fixture, reader, null) > sent);
        await relay.DisconnectAsync(reader);
    }

    [Fact]
    public async Task ClosedPublisherDoesNotSupplyCachedStateWhileItsDisconnectIsStillQueued()
    {
        var fixture = new Fixture(); var relay = fixture.Relay; var grant = Grant.Create();
        using var aborted = new CancellationTokenSource();
        var publisher = new Connection("publisher", aborted.Token);
        var first = new Connection("first"); var second = new Connection("second");
        await grant.BindSubscriber(relay, first);
        await grant.BindPublisher(relay, publisher);
        await relay.PublishAsync(publisher, State("confirmed"));
        var previous = AssertState(fixture, first, State("confirmed"));
        aborted.Cancel();
        var binding = await grant.BindSubscriber(relay, second);
        Assert.Null(binding.State);
        Assert.True(binding.Version > previous);
        AssertState(fixture, first, null);
        await relay.DisconnectAsync(publisher);
        await relay.DisconnectAsync(first);
        await relay.DisconnectAsync(second);
    }

    [Theory]
    [InlineData("unknown field")]
    [InlineData("nested field")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("wrong version")]
    [InlineData("wrong type")]
    [InlineData("invalid active")]
    [InlineData("duplicate profile")]
    [InlineData("credential-bearing url")]
    [InlineData("invalid expected")]
    [InlineData("changed identity")]
    [InlineData("too large")]
    public async Task InvalidMetadataIsNotForwardedAndConfirmedOldViewIsWithdrawn(string scenario)
    {
        var fixture = new Fixture(); var relay = fixture.Relay; var grant = Grant.Create();
        var publisher = new Connection("publisher"); var reader = new Connection("reader");
        await grant.BindSubscriber(relay, reader);
        await grant.BindPublisher(relay, publisher);
        var valid = State("valid");
        await relay.PublishAsync(publisher, valid);
        AssertState(fixture, reader, valid);
        var invalid = scenario switch
        {
            "unknown field" => valid.Insert(1, "\"unexpected\":\"fixture\","),
            "nested field" => valid.Replace("\"name\":", "\"unexpected\":{},\"name\":"),
            "duplicate" => valid.Insert(1, "\"confirmed\":false,"),
            "missing" => valid.Replace("\"confirmed\":true,", ""),
            "wrong version" => valid.Replace("\"version\":1", "\"version\":2"),
            "wrong type" => valid.Replace("\"confirmed\":true", "\"confirmed\":\"true\""),
            "invalid active" => valid.Replace("\"activeId\":\"profile\"", "\"activeId\":\"\""),
            "duplicate profile" => DuplicateProfile(valid),
            "credential-bearing url" => valid.Replace("https://rooms.example.test", "https://rooms.example.test/?unexpected=fixture"),
            "invalid expected" => valid.Replace("\"expectedHoeingPlayers\":4", "\"expectedHoeingPlayers\":5"),
            "changed identity" => valid.Replace("fixture-machine", "other-machine"),
            "too large" => valid.Replace("\"note\":\"valid\"", "\"note\":\"" + new string('界', RoomFollowRelay.MaxStateBytes / 3) + "\""),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
        await Assert.ThrowsAsync<HubException>(() => relay.PublishAsync(publisher, invalid));
        AssertState(fixture, reader, null);
        Assert.Null((await grant.BindSubscriber(relay, reader)).State);
        await relay.PublishAsync(publisher, valid);
        AssertState(fixture, reader, valid);
    }

    [Fact]
    public async Task ChannelAndSubscriberBoundsReleaseWhenConnectionsLeave()
    {
        var fixture = new Fixture(); var relay = fixture.Relay;
        var readers = new List<Connection>();
        for (var i = 0; i < RoomFollowRelay.MaxChannels; i++)
        {
            var connection = new Connection("reader-" + i);
            await Grant.Create().BindSubscriber(relay, connection);
            readers.Add(connection);
        }
        var newGrant = Grant.Create();
        var next = new Connection("next");
        await Assert.ThrowsAsync<HubException>(() => newGrant.BindSubscriber(relay, next));
        await relay.DisconnectAsync(readers[0]);
        Assert.True((await newGrant.BindSubscriber(relay, next)).Demand);
        var sameChannel = new List<Connection>();
        for (var i = 1; i < RoomFollowRelay.MaxSubscribersPerChannel; i++)
        {
            var connection = new Connection("same-" + i);
            await newGrant.BindSubscriber(relay, connection);
            sameChannel.Add(connection);
        }
        var extra = new Connection("extra");
        await Assert.ThrowsAsync<HubException>(() => newGrant.BindSubscriber(relay, extra));
        await relay.DisconnectAsync(next);
        Assert.True((await newGrant.BindSubscriber(relay, extra)).Demand);
        foreach (var connection in readers.Skip(1).Concat(sameChannel).Append(extra))
            await relay.DisconnectAsync(connection);
        Assert.Empty(fixture.Events);
    }

    [Fact]
    public async Task FailedDeliveryDropsSubscriberAndClearsCachedState()
    {
        var fixture = new Fixture(); var relay = fixture.Relay; var grant = Grant.Create();
        var publisher = new Connection("publisher"); var reader = new Connection("reader");
        await grant.BindPublisher(relay, publisher);
        await grant.BindSubscriber(relay, reader);
        fixture.Clear();
        fixture.FailedTarget = reader.Id;
        await relay.PublishAsync(publisher, State("valid"));
        Assert.True(reader.IsClosed);
        AssertDemand(fixture, publisher, false);
        var replacement = new Connection("replacement");
        Assert.Null((await grant.BindSubscriber(relay, replacement)).State);
        AssertDemand(fixture, publisher, true);
    }

    [Theory]
    [InlineData("/room-follow", "http", false)]
    [InlineData("/ROOM-FOLLOW/negotiate", "http", false)]
    [InlineData("/room-follow", "https", true)]
    [InlineData("/gateway", "http", true)]
    public async Task HttpsBoundaryAppliesOnlyToFollowEndpoint(string path, string scheme, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Scheme = scheme;
        context.Request.Headers["X-Forwarded-Proto"] = "https"; // 只有已配置的受信代理中间件可改变 Scheme。
        bool called = false;
        await RoomFollowHub.RequireHttpsAsync(context, _ => { called = true; return Task.CompletedTask; });
        Assert.Equal(allowed, called);
        Assert.Equal(allowed ? 200 : 400, context.Response.StatusCode);
    }

    private static string DuplicateProfile(string state)
    {
        var root = JsonNode.Parse(state)!.AsObject();
        var profiles = root["profiles"]!.AsArray();
        profiles.Add(profiles[0]!.DeepClone());
        return root.ToJsonString();
    }

    internal static string State(string note) => JsonSerializer.Serialize(new
    {
        version = 1, uid = "fixture-uid", instanceId = "fixture-instance", machineId = "fixture-machine",
        confirmed = true,
        profiles = new[] { new { id = "profile", name = "Test room", note, serverUrl = "https://rooms.example.test",
            roomCode = "fixture-room", teamUids = new[] { "fixture-uid" }, expectedHoeingPlayers = 4 } },
        activeId = "profile", viewedId = "__all_rooms__", showOrchestration = false
    });

    private static long AssertState(Fixture fixture, Connection reader, string? state)
    {
        var delivery = Assert.Single(fixture.Events);
        Assert.Equal(reader.Id, delivery.Target);
        Assert.Equal("FollowState", delivery.Method);
        Assert.Equal(state, delivery.Arguments[1]);
        fixture.Clear();
        return Assert.IsType<long>(delivery.Arguments[0]);
    }

    private static void AssertDemand(Fixture fixture, Connection publisher, bool demand)
    {
        var delivery = Assert.Single(fixture.Events);
        Assert.Equal(publisher.Id, delivery.Target);
        Assert.Equal("FollowDemand", delivery.Method);
        Assert.Equal(demand, Assert.IsType<bool>(Assert.Single(delivery.Arguments)));
        fixture.Clear();
    }

    internal sealed class Grant
    {
        internal readonly string WriteSecret = Secret();
        internal readonly string ReadSecret = Secret();
        internal string WriteHash => Hash(WriteSecret);
        internal string ReadHash => Hash(ReadSecret);
        internal static Grant Create() => new();
        internal Task<RoomFollowBindingResult> BindPublisher(RoomFollowRelay relay, Connection connection) =>
            relay.BindPublisherAsync(connection, WriteSecret, ReadHash);
        internal Task<RoomFollowBindingResult> BindSubscriber(RoomFollowRelay relay, Connection connection) =>
            relay.BindSubscriberAsync(connection, ReadSecret, WriteHash);
        private static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }

    private sealed record Delivery(string Target, string Method, object?[] Arguments);

    private sealed class Fixture
    {
        internal readonly ConcurrentQueue<Delivery> Events = new();
        internal string? FailedTarget;
        internal RoomFollowRelay Relay { get; }
        internal Fixture()
        {
            var hub = new Mock<IHubContext<RoomFollowHub>>();
            hub.Setup(h => h.Clients.Client(It.IsAny<string>())).Returns((string id) =>
            {
                var client = new Mock<ISingleClientProxy>();
                client.Setup(p => p.SendCoreAsync(It.IsAny<string>(), It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
                    .Returns((string method, object?[] args, CancellationToken ct) =>
                    {
                        if (id == FailedTarget) return Task.FromException(new IOException("fixture delivery failed"));
                        Events.Enqueue(new(id, method, args));
                        return Task.CompletedTask;
                    });
                return client.Object;
            });
            Relay = new RoomFollowRelay(hub.Object);
        }
        internal void Clear() => Events.Clear();
    }
}
