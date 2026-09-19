using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// BgiJobTerminalPolling.ParseAcceptance 受理回执词汇夹具（R4 真实段 2026-09-19 实锤缺口）：
/// BGI ext.task.start 真实线路受理=queued/adopted（带 taskHandle），早期假设词 accepted 保留兼容；
/// 缺 taskHandle 的受理回执仍是协议违例（Accepted=true、TaskHandle=null，由边界判 Unknown）；
/// already_executed/未知状态/坏 JSON 一律不受理。
/// </summary>
public class BgiJobTerminalPollingTests
{
    [Theory]
    [InlineData("accepted")]
    [InlineData("queued")]
    [InlineData("adopted")]
    public void Acceptance_WithTaskHandle_IsAccepted(string status)
    {
        var (accepted, handle, already) = BgiJobTerminalPolling.ParseAcceptance(
            $$"""{"status":"{{status}}","taskHandle":"abc123","queuePosition":1}""");
        Assert.True(accepted);
        Assert.Equal("abc123", handle);
        Assert.False(already);
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("adopted")]
    public void Acceptance_WithoutTaskHandle_StillProtocolViolation(string status)
    {
        // Accepted=true + TaskHandle=null：边界据此判「受理回执缺 taskHandle（协议违例）」Unknown，不猜成功
        var (accepted, handle, already) = BgiJobTerminalPolling.ParseAcceptance(
            $$"""{"status":"{{status}}","queuePosition":1}""");
        Assert.True(accepted);
        Assert.Null(handle);
        Assert.False(already);
    }

    [Fact]
    public void AlreadyExecuted_ParsedSeparately()
    {
        var (accepted, handle, already) = BgiJobTerminalPolling.ParseAcceptance(
            """{"status":"already_executed","generation":0}""");
        Assert.True(accepted);
        Assert.Null(handle);
        Assert.True(already);
    }

    [Theory]
    [InlineData("""{"status":"rejected","errorCode":"capability_required"}""")]
    [InlineData("""{"status":"failed"}""")]
    [InlineData("""{"taskHandle":"abc"}""")]
    [InlineData("not-json")]
    public void NonAcceptance_NotAccepted(string json)
    {
        var (accepted, handle, already) = BgiJobTerminalPolling.ParseAcceptance(json);
        Assert.False(accepted);
        Assert.Null(handle);
        Assert.False(already);
    }

    [Fact]
    public void Null_NotAccepted()
    {
        var (accepted, handle, already) = BgiJobTerminalPolling.ParseAcceptance(null);
        Assert.False(accepted);
        Assert.Null(handle);
        Assert.False(already);
    }
}