using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class WhitelistRoundOrderClientTests
{
    private static PlayerInfo[] Players(params string[] uids) => System.Array.ConvertAll(uids, uid => new PlayerInfo { PlayerUid = uid });

    [Fact]
    public void AcceptsNonHostFirstAndDifferentLocalDisplayOrder()
    {
        var startup = new CoordinatorClient.RoundOrderStartupSnapshot("source", "A", ["A", "B", "C"], 2);
        Assert.True(CoordinatorClient.IsUsableRoundOrder(startup, ["B", "A", "C"], Players("A", "C", "B"), "B", "A", 2));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RequiresAnExplicitAllMemberConfirmationFromTheOriginalRoom(bool ready)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Reply(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync("source-A", "B", "B"));
        var startup = new CoordinatorClient.RoundOrderStartupSnapshot("source-instance", "A", ["A", "B"], 2);
        string[] order = ["B", "A"];
        var confirms = 0;
        gateway._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal("source-A", request.RoomCode);
            if (request.Name == GatewayProtocol.Names.RoomGetRoundHostOrder)
                return Task.FromResult(Reply(request, new { order, startup }));
            Assert.Equal(GatewayProtocol.Names.RoomConfirmRoundOrder, request.Name);
            Assert.Equal("source-instance", request.GetString("sessionId"));
            Assert.Equal(order, request.Get<string[]>("order"));
            confirms++;
            return Task.FromResult(Reply(request, new { ready }));
        };
        var readiness = client.ConfirmWhitelistRoundOrderAsync(Players("A", "B"), "A", 2, CancellationToken.None);
        if (ready) Assert.Equal(order, await readiness);
        else await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => readiness);
        Assert.Equal(1, confirms);
        gateway._testInvokeOverride = null;
    }

    [Fact]
    public async Task CancellationWinsEvenIfTheConfirmationReplySaysReady()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Reply(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync("source-A", "B", "B"));
        using var stop = new CancellationTokenSource();
        var startup = new CoordinatorClient.RoundOrderStartupSnapshot("source-instance", "A", ["A", "B"], 2);
        gateway._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomGetRoundHostOrder)
                return Task.FromResult(Reply(request, new { order = new[] { "B", "A" }, startup }));
            Assert.Equal(GatewayProtocol.Names.RoomConfirmRoundOrder, request.Name);
            stop.Cancel();
            return Task.FromResult(Reply(request, new { ready = true }));
        };
        await Assert.ThrowsAnyAsync<System.OperationCanceledException>(() =>
            client.ConfirmWhitelistRoundOrderAsync(Players("A", "B"), "A", 2, stop.Token));
        gateway._testInvokeOverride = null;
    }

    private static GatewayEnvelope Reply(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name, Payload = GatewayEnvelope.ToPayload(payload),
    };

    [Fact]
    public void RejectsMissingUnidentifiedOrChangedMembersAndDifferentRoundLimits()
    {
        var startup = new CoordinatorClient.RoundOrderStartupSnapshot("source", "A", ["A", "B", "C"], 2);
        var players = Players("A", "B", "C");
        Assert.False(CoordinatorClient.IsUsableRoundOrder(startup, [], players, "B", "A", 2));
        Assert.False(CoordinatorClient.IsUsableRoundOrder(startup, ["B", "A", "B"], players, "B", "A", 2));
        Assert.False(CoordinatorClient.IsUsableRoundOrder(startup, ["B", "A", "C"], Players("A", "B"), "B", "A", 2));
        Assert.False(CoordinatorClient.IsUsableRoundOrder(startup, ["B", "A", "C"], players, null, "A", 2));
        Assert.False(CoordinatorClient.IsUsableRoundOrder(startup, ["B", "A", "C"], players, "B", "C", 2));
        Assert.False(CoordinatorClient.IsUsableRoundOrder(startup, ["B", "A", "C"], players, "B", "A", 3));
    }
}
