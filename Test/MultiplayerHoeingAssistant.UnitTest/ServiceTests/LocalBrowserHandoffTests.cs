using System.Diagnostics;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class LocalBrowserHandoffTests
{
    private static LocalBrowserHandoff.Identity Identity(bool executor = true) => new(
        "handoff-test-owner", "测试执行端", "handoff-test-room", "instance-a", "test-only-secret", executor,
        Process.GetCurrentProcess().SessionId);

    private static RemoteCommand Request(string operation = "local_browser.open", string uid = "handoff-test-owner",
        string room = "handoff-test-room", string instance = "instance-a") => new()
    {
        Cmd = operation, CommandId = Guid.NewGuid().ToString("N"), SenderUid = uid,
        RoomCode = room, Target = [uid], TargetInstanceId = instance,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(10)
    };

    [Fact]
    public async Task SelectedExecutorOwnsPairing_AndMonitorDoesNotCreateAnotherBridge()
    {
        using var bridge = new LocalBrowserBridge((_, _) => Task.FromResult(new LocalBrowserResult("ok")));
        var created = 0;
        using var handoff = new LocalBrowserHandoff(() => Identity(), expected =>
        {
            Assert.Equal(Identity(), expected); created++; return bridge.CreatePairingUrl();
        }, _ => { });
        var probe = await LocalConfigEditChannel.SendAsync(handoff.Address, Request("local_browser.probe"), Identity().Secret);
        Assert.Equal("local_browser.probe_result", probe!.Cmd);
        Assert.Equal(0, created);
        var target = new LocalBrowserHandoff.Target(handoff.Address, Identity().Uid, Identity().Name,
            Identity().RoomCode, Identity().InstanceId, Identity().SessionId);
        var url = await LocalBrowserHandoff.GetPairingUrlAsync(Identity(false), target);
        Assert.StartsWith(bridge.Origin + "/#token=", url);
        Assert.Equal(1, created);
        using var client = new HttpClient(new HttpClientHandler { UseProxy = false });
        using var request = new HttpRequestMessage(HttpMethod.Post, bridge.Origin + "/api/pair")
        {
            Content = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { token = new Uri(url).Fragment[7..] }),
                System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Origin", bridge.Origin);
        using var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode);
    }

    [Theory]
    [InlineData("other-owner", "handoff-test-room", "instance-a")]
    [InlineData("handoff-test-owner", "other-room", "instance-a")]
    [InlineData("handoff-test-owner", "handoff-test-room", "stale-instance")]
    public async Task WrongOwnerRoomOrInstanceCannotReceivePairing(string uid, string room, string instance)
    {
        using var handoff = new LocalBrowserHandoff(() => Identity(), _ => throw new Exception("不得创建链接"), _ => { });
        Assert.Null(await LocalConfigEditChannel.SendAsync(handoff.Address, Request(uid: uid, room: room, instance: instance), Identity().Secret));
    }

    [Fact]
    public async Task ExpiredRequestAndObserverAreRejected_AndModeChangeInvalidatesSelectedTarget()
    {
        var identity = Identity();
        using var handoff = new LocalBrowserHandoff(() => identity, _ => throw new Exception("不得创建链接"), _ => { });
        var expired = Request(); expired.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Null(await LocalConfigEditChannel.SendAsync(handoff.Address, expired, identity.Secret));
        identity = Identity(false);
        Assert.Null(await LocalConfigEditChannel.SendAsync(handoff.Address, Request("local_browser.probe"), identity.Secret));
        var target = new LocalBrowserHandoff.Target(handoff.Address, identity.Uid, identity.Name,
            identity.RoomCode, identity.InstanceId, identity.SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBrowserHandoff.GetPairingUrlAsync(identity, target));
    }

    [Fact]
    public async Task WrongPasswordIsRejectedBeforePairing()
    {
        using var handoff = new LocalBrowserHandoff(() => Identity(), _ => throw new Exception("不得创建链接"), _ => { });
        await Assert.ThrowsAnyAsync<Exception>(() => LocalConfigEditChannel.SendAsync(handoff.Address, Request(), "wrong-password"));
    }

    [Fact]
    public async Task UnauthenticatedServerCannotSupplyAWebAddress()
    {
        using var fake = new LocalConfigEditChannel(() => Identity().Secret, command => Task.FromResult<RemoteCommand?>(new()
        {
            Cmd = "local_browser.open_result", CommandId = command.CommandId, RoomCode = Identity().RoomCode,
            SenderUid = Identity().Uid, Target = [Identity().Uid], TargetInstanceId = Identity().InstanceId,
            Params = new() { ["url"] = "http://127.0.0.1:1234/#token=" + new string('a', 64) }
        }), _ => { }, pipeName: "handoff-fake-" + Guid.NewGuid().ToString("N"));
        var target = new LocalBrowserHandoff.Target(fake.Address, Identity().Uid, Identity().Name,
            Identity().RoomCode, Identity().InstanceId, Identity().SessionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalBrowserHandoff.GetPairingUrlAsync(Identity(false), target));
    }

    [Theory]
    [InlineData("https://127.0.0.1:1234/#token=")]
    [InlineData("http://example.com/#token=")]
    [InlineData("http://127.0.0.1:1234/other#token=")]
    [InlineData("http://127.0.0.1:1234/?token=x#token=")]
    public void OnlyLoopbackPairingLinksCanBeOpened(string prefix)
        => Assert.False(LocalBrowserHandoff.IsPairingUrl(prefix + new string('a', 64)));
}
