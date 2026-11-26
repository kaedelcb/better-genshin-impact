using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RoomFollowTransportTests
{
    [Fact]
    public void InvitationsContainOnlyReadCapabilityAndPinOneRelayAndIdentity()
    {
        var grant = CreateGrant(local: false);
        var another = CreateGrant(local: false);
        var invitation = RoomFollowInvitation.Parse(grant.Invitation.Encode());
        Assert.NotEqual(grant.Invitation.ChannelId, another.Invitation.ChannelId);
        Assert.NotEqual(grant.WriteSecret, invitation.ReadSecret);
        Assert.Equal(RoomFollowFormat.Hash(grant.WriteSecret), invitation.WriteHash);
        Assert.Equal(RoomFollowFormat.Hash(invitation.ReadSecret), grant.ReadHash);
        Assert.Equal(RoomFollowFormat.Channel(invitation.WriteHash, grant.ReadHash), invitation.ChannelId);
        Assert.Equal(grant.Invitation.Uid, invitation.Uid);
        Assert.Equal(grant.Invitation.InstanceId, invitation.InstanceId);
        Assert.Equal(grant.Invitation.MachineId, invitation.MachineId);
        Assert.Equal("https://relay.example.test", invitation.ServerUrl);
        Assert.Equal("wss://relay.example.test/room-follow", RoomFollowRemoteTransport.RelayWebSocketUri(invitation).AbsoluteUri);
        Assert.False(invitation.Local);
        var json = DecodeInvitation(grant.Invitation.Encode());
        using var document = JsonDocument.Parse(json);
        Assert.Equal(9, document.RootElement.EnumerateObject().Count());
        Assert.DoesNotContain(grant.WriteSecret, json);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("browserId", json);
        Assert.Equal(invitation.Encode(), grant.Invitation.Encode());
        var local = CreateGrant(local: true).Invitation;
        Assert.True(local.Local);
        Assert.Equal("", local.ServerUrl);
        Assert.Throws<ArgumentException>(() => RoomFollowRemoteTransport.RelayWebSocketUri(local));
    }

    [Theory]
    [InlineData("http://relay.example.test")]
    [InlineData("http://127.0.0.1:5000")]
    [InlineData("https://relay.example.test/?fixture=value")]
    [InlineData("https://relay.example.test/#fixture")]
    [InlineData("https://fixture@relay.example.test")]
    public void RemoteGrantRequiresAnUncredentialedHttpsAddress(string url)
        => Assert.Throws<FormatException>(() => RoomFollowGrant.Create(url, false, "fixture-uid", "fixture-instance", "fixture-machine"));

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("missing")]
    [InlineData("wrong version")]
    [InlineData("bad channel")]
    [InlineData("noncanonical hex")]
    [InlineData("same capability")]
    [InlineData("insecure url")]
    public void InvitationRejectsInvalidSchemaAndCapabilityPairs(string scenario)
    {
        var grant = CreateGrant(local: false);
        var source = DecodeInvitation(grant.Invitation.Encode());
        var root = JsonNode.Parse(source)!.AsObject();
        switch (scenario)
        {
            case "unknown": root["fixtureUnknown"] = "value"; break;
            case "duplicate":
                Assert.Throws<FormatException>(() => RoomFollowInvitation.Parse(EncodeInvitation(source.Insert(1, "\"version\":1,"))));
                return;
            case "missing": root.Remove("machineId"); break;
            case "wrong version": root["version"] = 2; break;
            case "bad channel": root["channelId"] = new string('0', 64); break;
            case "noncanonical hex": root["readSecret"] = new string('A', 64); break;
            case "same capability":
                root["writeHash"] = grant.ReadHash;
                root["channelId"] = RoomFollowFormat.Channel(grant.ReadHash, grant.ReadHash);
                break;
            case "insecure url": root["serverUrl"] = "http://relay.example.test"; break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        Assert.Throws<FormatException>(() => RoomFollowInvitation.Parse(EncodeInvitation(root.ToJsonString())));
    }

    [Fact]
    public void SnapshotContainsExactlyAllowedFieldsAndFreezesMutableInput()
    {
        var grant = CreateGrant();
        var source = Snapshot(grant.Invitation, "first");
        var state = RoomFollowSnapshotCodec.Encode(source, grant.Invitation)!;
        source.Profiles[0].Note = "changed after publication";
        source.Profiles[0].TeamUids.Clear();
        Assert.True(RoomFollowSnapshotCodec.TryDecode(state, grant.Invitation, out var decoded));
        Assert.Equal("first", decoded!.Profiles[0].Note);
        Assert.Single(decoded.Profiles[0].TeamUids);
        using var doc = JsonDocument.Parse(state);
        Assert.Equal(9, doc.RootElement.EnumerateObject().Count());
        Assert.Equal(7, doc.RootElement.GetProperty("profiles")[0].EnumerateObject().Count());
        Assert.DoesNotContain("password", state, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("writeSecret", state);
        Assert.DoesNotContain("readSecret", state);
        Assert.True(RoomFollowSnapshotCodec.TryDecode(null, grant.Invitation, out var none));
        Assert.Null(none);
    }

    [Theory]
    [InlineData("unknown field")]
    [InlineData("nested unknown")]
    [InlineData("duplicate")]
    [InlineData("missing field")]
    [InlineData("wrong uid")]
    [InlineData("wrong instance")]
    [InlineData("wrong machine")]
    [InlineData("invalid active")]
    [InlineData("invalid viewed")]
    [InlineData("duplicate profile")]
    [InlineData("invalid count")]
    [InlineData("oversize")]
    public void InvalidStateNeverDecodesAsConfirmedMetadata(string scenario)
    {
        var grant = CreateGrant();
        var source = RoomFollowSnapshotCodec.Encode(Snapshot(grant.Invitation, "fixture"), grant.Invitation)!;
        var root = JsonNode.Parse(source)!.AsObject();
        var profile = root["profiles"]![0]!.AsObject();
        switch (scenario)
        {
            case "unknown field": root["fixtureUnknown"] = "value"; break;
            case "nested unknown": profile["fixtureUnknown"] = "value"; break;
            case "duplicate":
                Assert.False(RoomFollowSnapshotCodec.TryDecode(source.Insert(1, "\"confirmed\":false,"), grant.Invitation, out _));
                return;
            case "missing field": root.Remove("showOrchestration"); break;
            case "wrong uid": root["uid"] = "other-uid"; break;
            case "wrong instance": root["instanceId"] = "other-instance"; break;
            case "wrong machine": root["machineId"] = "other-machine"; break;
            case "invalid active": root["activeId"] = ""; break;
            case "invalid viewed": root["viewedId"] = "missing-profile"; break;
            case "duplicate profile": root["profiles"]!.AsArray().Add(profile.DeepClone()); break;
            case "invalid count": profile["expectedHoeingPlayers"] = 5; break;
            case "oversize": profile["note"] = new string('界', RoomFollowFormat.MaxStateBytes / 3); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }
        Assert.False(RoomFollowSnapshotCodec.TryDecode(root.ToJsonString(), grant.Invitation, out var decoded));
        Assert.Null(decoded);
    }

    [Fact]
    public void GrantStoreRoundTripsCurrentUserEncryptedDataAndDoesNotOverwriteCorruption()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "room-follow-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "grants.protected");
            var store = new RoomFollowGrantStore(path);
            Assert.Empty(store.Load());
            var grant = CreateGrant();
            store.Save([grant]);
            var encrypted = Encoding.UTF8.GetString(File.ReadAllBytes(path));
            Assert.DoesNotContain(grant.WriteSecret, encrypted);
            Assert.DoesNotContain(grant.Invitation.ReadSecret, encrypted);
            var recovered = Assert.Single(store.Load());
            Assert.Equal(grant.Invitation.Encode(), recovered.Invitation.Encode());
            Assert.Equal(grant.WriteSecret, recovered.WriteSecret);
            Assert.Equal(grant.ReadHash, recovered.ReadHash);
            Assert.Equal(grant.BrowserId, recovered.BrowserId);
            store.Save([]);
            Assert.Empty(store.Load());
            byte[] corrupt = [1, 2, 3, 4];
            File.WriteAllBytes(path, corrupt);
            Assert.Throws<InvalidDataException>(() => store.Load());
            Assert.Throws<InvalidDataException>(() => store.Save([grant]));
            Assert.Equal(corrupt, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LatestOutboxDeduplicatesAndCoalescesAndDemandTracksNewSubscriptions()
    {
        var outbox = new RoomFollowOutbox();
        outbox.Update("first");
        outbox.Update("first");
        outbox.Update("latest");
        Assert.Equal((2L, "latest"), outbox.Capture());
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await outbox.WaitAsync(timeout.Token);
        var demand = new RoomFollowDemand(outbox.Wake);
        demand.OnEvent(true);
        demand.OnBound(false); // The delayed bind result cannot undo a more recent callback.
        Assert.Equal((true, 1L), demand.Capture());
        demand.OnEvent(true);
        Assert.Equal((true, 1L), demand.Capture());
        demand.OnEvent(false);
        demand.OnEvent(true);
        Assert.Equal((true, 2L), demand.Capture());
    }

    [Fact]
    public void StateVersionRejectsStaleBindBaselineButNewConnectionMayStartAtLowerVersion()
    {
        var inbox = new RoomFollowStateInbox();
        inbox.Offer(12, "new version");
        inbox.Offer(11, "late baseline");
        inbox.Offer(12, "duplicate");
        Assert.Equal((12L, "new version"), inbox.Capture());
        inbox.Offer(13, null);
        Assert.Equal(13L, inbox.Capture().Version);
        Assert.Null(inbox.Capture().State);
        var reconnected = new RoomFollowStateInbox();
        reconnected.Offer(1, "fresh connection");
        Assert.Equal((1L, "fresh connection"), reconnected.Capture());
        reconnected.Offer(2, new string('a', RoomFollowFormat.MaxStateBytes + 1));
        Assert.Null(reconnected.Capture().State);
    }

    [Fact]
    public async Task LocalPipeTransfersChangesClearsOnDisconnectAndRecoversWithSameInvitation()
    {
        var grant = CreateGrant();
        await using var reader = new RoomFollowSubscriber(grant.Invitation, bypassProxy: true);
        var events = Channel.CreateUnbounded<RoomFollowSnapshot?>();
        reader.Changed += snapshot => events.Writer.TryWrite(snapshot);
        await using var publisher = new RoomFollowPublisher(grant, bypassProxy: true);
        var first = Snapshot(grant.Invitation, "first");
        publisher.Update(first);
        Assert.False(events.Reader.TryRead(out _)); // Construction and Update do not start transport (default off).
        await publisher.StartAsync();
        await reader.StartAsync();
        await ReadUntilAsync(events.Reader, s => s?.Profiles[0].Note == "first");

        publisher.Update(first);
        await AssertNoEventAsync(events.Reader);
        first.Profiles[0].Note = "second";
        publisher.Update(first);
        first.Profiles[0].Note = "mutated without update";
        var second = await ReadUntilAsync(events.Reader, s => s?.Profiles[0].Note == "second");
        Assert.Equal("second", second!.Profiles[0].Note);
        publisher.Update(null);
        await ReadUntilAsync(events.Reader, s => s is null);
        publisher.Update(Snapshot(grant.Invitation, "before disconnect"));
        await ReadUntilAsync(events.Reader, s => s?.Profiles[0].Note == "before disconnect");
        await publisher.DisposeAsync();
        await ReadUntilAsync(events.Reader, s => s is null);
        await using var replacement = new RoomFollowPublisher(grant, bypassProxy: true);
        replacement.Update(Snapshot(grant.Invitation, "recovered"));
        await replacement.StartAsync();
        await ReadUntilAsync(events.Reader, s => s?.Profiles[0].Note == "recovered");
        await reader.DisposeAsync();
        await ReadUntilAsync(events.Reader, s => s is null);
        replacement.Update(Snapshot(grant.Invitation, "disabled"));
        await AssertNoEventAsync(events.Reader);
    }

    private static async Task<RoomFollowSnapshot?> ReadUntilAsync(ChannelReader<RoomFollowSnapshot?> reader, Func<RoomFollowSnapshot?, bool> matches)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (true)
        {
            var state = await reader.ReadAsync(timeout.Token);
            if (matches(state)) return state;
        }
    }

    private static async Task AssertNoEventAsync(ChannelReader<RoomFollowSnapshot?> reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { _ = await reader.ReadAsync(timeout.Token); });
    }

    private static RoomFollowGrant CreateGrant(bool local = true) => RoomFollowGrant.Create("https://relay.example.test/gateway/", local,
        "fixture-uid", "fixture-instance", "fixture-machine", "fixture-browser");

    private static RoomFollowSnapshot Snapshot(RoomFollowInvitation invitation, string note) => new()
    {
        Uid = invitation.Uid, InstanceId = invitation.InstanceId, MachineId = invitation.MachineId, Confirmed = true,
        Profiles = [new RoomFollowProfile { Id = "fixture-profile", Name = "Test room", Note = note, ServerUrl = "https://rooms.example.test",
            RoomCode = "fixture-room", TeamUids = [invitation.Uid], ExpectedHoeingPlayers = 4 }],
        ActiveId = "fixture-profile", ViewedId = "__all_rooms__", ShowOrchestration = false
    };

    private static string DecodeInvitation(string invitation)
    {
        var b64 = invitation[RoomFollowInvitation.Prefix.Length..].Replace('-', '+').Replace('_', '/');
        return Encoding.UTF8.GetString(Convert.FromBase64String(b64.PadRight((b64.Length + 3) / 4 * 4, '=')));
    }

    private static string EncodeInvitation(string json) => RoomFollowInvitation.Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
