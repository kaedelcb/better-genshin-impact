using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[Collection("GlobalUserRoot")]
public sealed class PickupWaypointProgressTests
{
    [Fact]
    public void ThresholdCountsOnlyNewMatchingLogsAndKeepsIdenticalItemsDistinct()
    {
        var engine = new object();
        Assert.Null(PickupWaypointProgress.Capture(engine));
        var point = PickupWaypointProgress.TryBegin(2, engine)!;
        using (point)
        {
            PickupWaypointProgress.Capture()?.ReportHere(" 调查 ");
            PickupWaypointProgress.Capture(new object())?.ReportHere("游医");
            PickupWaypointProgress.Capture(engine)?.ReportHere("游医");
            Assert.False(point.ReachedToken.IsCancellationRequested);
            PickupWaypointProgress.Capture()?.ReportHere("游医");
            Assert.True(point.ReachedToken.IsCancellationRequested);
        }

        using var next = PickupWaypointProgress.TryBegin(1, engine)!;
        point.ReportHere("上一点迟到的记录");
        Assert.False(next.ReachedToken.IsCancellationRequested);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"pickup_target_count\":0")]
    [InlineData(",\"pickup_target_count\":-1")]
    [InlineData(",\"pickup_target_count\":2.5")]
    [InlineData(",\"pickup_target_count\":\"2\"")]
    [InlineData(",\"pickup_target_count\":{\"invalid\":true}")]
    [InlineData(",\"pickup_target_count\":[2]")]
    [InlineData(",\"pickup_target_count\":\"2/\"")]
    [InlineData(",\"pickup_target_count\":\"/3\"")]
    [InlineData(",\"pickup_target_count\":\"0/3\"")]
    [InlineData(",\"pickup_target_count\":\"2/-3\"")]
    [InlineData(",\"pickup_target_count\":\"2/3/4\"")]
    [InlineData(",\"pickup_target_count\":\"2147483648/3\"")]
    [InlineData(",\"pickup_target_count\":\"2/2147483648\"")]
    [InlineData(",\"pickup_target_count\":\"2/3\\u0000\"")]
    public void MissingOrInvalidCountStillLoadsExistingAction(string field)
    {
        var point = JsonSerializer.Deserialize<Waypoint>("{\"type\":\"target\",\"action\":\"combat_script\","
            + "\"action_params\":\"keypress(f);万叶 e;\"" + field + "}", PathRecorder.JsonOptions)!;

        Assert.Null(point.PickupTargetCount);
        Assert.Equal("keypress(f);万叶 e;", point.ActionParams);
        Assert.DoesNotContain("pickup_target_count", JsonSerializer.Serialize(point, PathRecorder.JsonOptions));
    }

    [Theory]
    [InlineData("2", 0, "2")]
    [InlineData("\"2/0\"", 0, "2")]
    [InlineData("\"2/3\"", 3, "\"2/3\"")]
    [InlineData("\"2/2147483647\"", int.MaxValue, "\"2/2147483647\"")]
    public void ValidCountRoundTripsThroughTheSameField(string input, int skip, string output)
    {
        var point = JsonSerializer.Deserialize<Waypoint>("{\"pickup_target_count\":" + input + "}", PathRecorder.JsonOptions)!;
        Assert.Equal(new PickupTarget(2, skip), point.PickupTargetCount);
        var saved = JsonSerializer.Serialize(point, PathRecorder.JsonOptions);
        using var document = JsonDocument.Parse(saved);
        Assert.Equal(output, document.RootElement.GetProperty("pickup_target_count").GetRawText());
        Assert.Equal(point.PickupTargetCount, JsonSerializer.Deserialize<Waypoint>(saved, PathRecorder.JsonOptions)!.PickupTargetCount);
    }

    [Fact]
    public void AnOldConfigStillDefaultsOff()
    {
        Assert.False(JsonSerializer.Deserialize<PathingPartyConfig>("{}")!.EnablePickupTargetCount);
        var enabled = new PathingPartyConfig { EnablePickupTargetCount = true }.CloneForSoloTask();
        Assert.True(JsonSerializer.Deserialize<PathingPartyConfig>(JsonSerializer.Serialize(enabled))!.EnablePickupTargetCount);
    }
}
