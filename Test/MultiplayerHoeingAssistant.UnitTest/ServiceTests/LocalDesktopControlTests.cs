using System.Text.Json;
using System.Text.Json.Nodes;
using BgiCoordinatorServer.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class LocalDesktopControlTests
{
    [Fact]
    public async Task LocalCaptureReturnsRegisteredGeometryAndIndependentPermission()
    {
        using var f = new DesktopControlFixture();
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, f.Root, () => f.Root);
        using var relay = new DodocoWebRelayService(f.Settings, watch, new DailyReportService(() => f.Root), () => null, () => f.Root);
        using var local = new DodocoLocalService(tail, relay, f.Screenshots, f.Settings, () => f.Root, _ => new { },
            desktopControl: f.Control);
        var first = Json((await local.ExecuteAsync(Request("screenshot.capture"), default)).Data);
        Assert.False(first["controlAllowed"]!.GetValue<bool>());
        f.Allow();
        var next = Json((await local.ExecuteAsync(Request("screenshot.capture"), default)).Data);
        Assert.True(next["controlAllowed"]!.GetValue<bool>());
        var id = next["frameId"]!.GetValue<string>();
        Assert.True(f.Screenshots.TryGetControlFrame(id, out var geometry));
        Assert.Equal(3840, geometry!.Width);
        Assert.Equal(1280, next["width"]!.GetValue<int>());
        var command = f.Command() with { FrameId = id };
        var input = await local.ExecuteAsync(Request("desktop.control.input", Json(command)), default);
        Assert.Equal("ok", input.Status);
        Assert.Equal("executed", Json(input.Data)["status"]!.GetValue<string>());
        Assert.Equal(1, f.Sender.Calls);
    }

    // HTTP 处理延迟不能把点击时的三秒有效期重新开始，旧图仍新鲜也必须拒绝。
    [Fact]
    public async Task BridgePreservesClickDeadline_InsteadOfStartingANewWindow()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, f.Root, () => f.Root);
        using var relay = new DodocoWebRelayService(f.Settings, watch, new DailyReportService(() => f.Root), () => null, () => f.Root);
        using var local = new DodocoLocalService(tail, relay, f.Screenshots, f.Settings, () => f.Root, _ => new { }, desktopControl: f.Control);
        var args = Json(f.Command());
        args["expiresAtUtc"] = f.Clock.GetUtcNow().AddSeconds(-1).ToString("O");
        var response = await local.ExecuteAsync(Request("desktop.control.input", args), default);
        Assert.Equal("expired", Json(response.Data)["status"]!.GetValue<string>());
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task ExplicitBridgeCancelBeforeInputCreatesTombstone()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, f.Root, () => f.Root);
        using var relay = new DodocoWebRelayService(f.Settings, watch, new DailyReportService(() => f.Root), () => null, () => f.Root);
        using var local = new DodocoLocalService(tail, relay, f.Screenshots, f.Settings, () => f.Root, _ => new { }, desktopControl: f.Control);
        var command = f.Command();
        Assert.Equal("ok", (await local.ExecuteAsync(Request("desktop.control.cancel", new JsonObject { ["requestId"] = command.RequestId }), default)).Status);
        var response = await local.ExecuteAsync(Request("desktop.control.input", Json(command)), default);
        Assert.Equal("cancelled", Json(response.Data)["status"]!.GetValue<string>());
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task BridgeInputUsesCommandDirectlyAndRejectsRemoteTarget()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, f.Root, () => f.Root);
        using var relay = new DodocoWebRelayService(f.Settings, watch, new DailyReportService(() => f.Root), () => null, () => f.Root);
        using var local = new DodocoLocalService(tail, relay, f.Screenshots, f.Settings, () => f.Root, _ => new { }, desktopControl: f.Control);
        var command = f.Command();
        Assert.Equal("error", (await local.ExecuteAsync(Request("desktop.control.input", new JsonObject { ["command"] = Json(command) }), default)).Status);
        Assert.Equal("error", (await local.ExecuteAsync(Request("desktop.control.input", Json(command with { TargetUid = "remote-uid" })), default)).Status);
        Assert.Equal(0, f.Sender.Calls);
    }

    [Fact]
    public async Task ClosingPairedSessionCancelsItsPendingInput()
    {
        using var f = new DesktopControlFixture(); f.Allow();
        using var tail = new BgiLogTailService(() => null);
        using var watch = new KeywordWatchService(tail, null, null, null, f.Root, () => f.Root);
        using var relay = new DodocoWebRelayService(f.Settings, watch, new DailyReportService(() => f.Root), () => null, () => f.Root);
        using var local = new DodocoLocalService(tail, relay, f.Screenshots, f.Settings, () => f.Root, _ => new { }, desktopControl: f.Control);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        f.Sender.BeforeSend = (_, _) => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); };
        var task = local.ExecuteAsync(Request("desktop.control.input", Json(f.Command())), default);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            local.ReleaseSession("paired-test");
        }
        finally { release.Set(); }
        Assert.Equal("cancelled", Json((await task).Data)["status"]!.GetValue<string>());
        Assert.Equal(0, f.Sender.Calls);
    }

    private static JsonObject Json(object? value) => JsonSerializer.SerializeToNode(value, GatewayJson.Options)!.AsObject();
    private static LocalBrowserRequest Request(string operation, JsonObject? args = null)
    {
        args ??= new JsonObject();
        if (operation == "desktop.control.input" && args["expiresAtUtc"] == null)
            args["expiresAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(3).ToString("O");
        return new("paired-test", Guid.NewGuid().ToString("N"), operation, args, "fake-bridge");
    }
}
