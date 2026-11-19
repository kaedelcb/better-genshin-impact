#nullable enable

using System.Collections.Concurrent;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Microsoft.AspNetCore.SignalR.Client;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class NextRoundTransportRecoveryTests
{
    [Fact]
    public async Task ConnectedClientDoesNotRestartOrRepeatHello()
    {
        await using var link = new TestTransport(HubConnectionState.Connected);

        Assert.True(await link.WaitAsync());
        Assert.Empty(link.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AutomaticReconnectWaitsBeforeCreatingOrListingRooms(bool host)
    {
        await using var link = new TestTransport(HubConnectionState.Reconnecting);
        var ready = link.WaitAsync();
        await link.Polled.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.False(ready.IsCompleted);
        Assert.Empty(link.Calls);

        link.State = HubConnectionState.Connected;
        Assert.True(await ready.WaitAsync(TimeSpan.FromSeconds(3)));
        if (host)
            Assert.Equal("next-room", await link.Client.CreateRoomAsync("host", null, "host-uid", 4));
        else
            Assert.Equal("next-room", Assert.Single(await link.Client.GetOnlineRoomsAsync()).Code);

        Assert.Equal(new[] { "session.hello", host ? "room.create" : "room.listOnline" }, link.Calls);
    }

    [Fact]
    public async Task MemberMissingRoomClosedEventCanRecoverAfterReconnectExhaustion()
    {
        await using var link = new TestTransport(HubConnectionState.Connected);
        Assert.True(await link.Client.JoinRoomAsync("old-room", "member", "member-uid"));
        link.Client.RetirePreviousRoundRoom();
        Assert.False(link.Client.IsInRoom);
        link.State = HubConnectionState.Disconnected;

        Assert.True(await link.WaitAsync());
        var room = Assert.Single(await link.Client.GetOnlineRoomsAsync());
        Assert.True(await link.Client.JoinRoomAsync(room.Code, "member", "member-uid"));
        Assert.True(link.Client.IsInRoom);
        Assert.Equal(new[] { "room.join", "start", "session.hello", "room.listOnline", "room.join" }, link.Calls);
    }

    [Fact]
    public async Task CancellingDuringAutomaticReconnectDoesNotStartOrSendHello()
    {
        await using var link = new TestTransport(HubConnectionState.Reconnecting);
        using var stop = new CancellationTokenSource();
        var ready = link.WaitAsync(stop.Token);
        await link.Polled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task TransportWaitUsesTheSharedRoundDeadline()
    {
        await using var link = new TestTransport(HubConnectionState.Disconnected);
        link.Gateway._testReconnectStartOverride = token => Task.Delay(Timeout.Infinite, token);
        var ready = link.Client.WaitForNextRoundTransportAsync(DateTime.UtcNow.AddMilliseconds(100), CancellationToken.None);

        Assert.False(await ready.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Empty(link.Calls);
    }

    [Fact]
    public async Task AnActiveRoomKeepsItsExistingRecoveryPath()
    {
        await using var link = new TestTransport(HubConnectionState.Connected);
        Assert.True(await link.Client.JoinRoomAsync("original-room", "member", "member-uid"));
        link.State = HubConnectionState.Disconnected;

        Assert.True(await link.WaitAsync());
        Assert.Equal(new[] { "room.join" }, link.Calls);
    }

    private sealed class TestTransport : IAsyncDisposable
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private int _state;
        private int _polls;

        public TestTransport(HubConnectionState state)
        {
            State = state;
            Gateway = Client.GetOrCreateGatewayForTest();
            Gateway._testConnectionStateOverride = ReadState;
            Gateway._testReconnectStartOverride = _ =>
            {
                _calls.Enqueue("start");
                State = HubConnectionState.Connected;
                return Task.CompletedTask;
            };
            Gateway._testInvokeOverride = (_, request, _) =>
            {
                _calls.Enqueue(request.Name);
                object payload = request.Name switch
                {
                    "room.create" => new { roomCode = "next-room" },
                    "room.listOnline" => new { rooms = new[] { new { code = "next-room" } } },
                    _ => new { success = true },
                };
                return Task.FromResult(new GatewayEnvelope
                {
                    Name = request.Name,
                    Type = GatewayProtocol.MessageTypes.Response,
                    Payload = GatewayEnvelope.ToPayload(payload),
                });
            };
        }

        public CoordinatorClient Client { get; } = new() { _testWaitRetryIntervalMs = 5 };
        public BgiGatewayClient Gateway { get; }
        public string[] Calls => _calls.ToArray();
        public TaskCompletionSource<bool> Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HubConnectionState State { set => Volatile.Write(ref _state, (int)value); }

        private HubConnectionState ReadState()
        {
            if (Interlocked.Increment(ref _polls) == 2) Polled.TrySetResult(true);
            return (HubConnectionState)Volatile.Read(ref _state);
        }

        public Task<bool> WaitAsync(CancellationToken ct = default) =>
            Client.WaitForNextRoundTransportAsync(DateTime.UtcNow.AddMinutes(1), ct);

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}
