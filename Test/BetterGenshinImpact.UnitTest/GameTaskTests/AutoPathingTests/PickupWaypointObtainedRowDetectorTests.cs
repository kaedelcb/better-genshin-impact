using BetterGenshinImpact.GameTask.AutoPathing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public sealed class PickupWaypointObtainedRowDetectorTests
{
    [Fact]
    public void OneObtainedCaptionAndTwoFixedRowSlotsProduceTwoRows()
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 169, 511);
        DrawTextBlocks(frame, 160, 554);
        DrawTextBlocks(frame, 160, 602);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.True(observation.IsAvailable);
        Assert.Equal(3, stats.HeaderComponents);
        Assert.Equal(3, stats.OriginalHeaderComponents);
        Assert.Equal("原框原色", stats.HeaderMatch);
        Assert.Equal(505, stats.HeaderTop);
        Assert.Equal(new[] { 3, 3, 0, 0, 0, 0 }, stats.RowComponents);
        Assert.Collection(observation.Rows!,
            row => Assert.InRange(row.Y, 560, 569),
            row => Assert.InRange(row.Y, 608, 617));
    }

    [Theory]
    [InlineData(474, 213, 215, 218, "邻框原色")]
    [InlineData(511, 245, 245, 245, "原框浅色")]
    [InlineData(474, 154, 154, 154, "邻框浅色")]
    public void NearbyOrPaleCaptionStillRequiresItsOwnConnectedBlocks(
        int top, int blue, int green, int red, string match)
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 169, top, new Scalar(blue, green, red));
        DrawTextBlocks(frame, 160, 554);
        DrawTextBlocks(frame, 160, 602);
        DrawTextBlocks(frame, 160, 650);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.True(observation.IsAvailable);
        Assert.Equal(0, stats.OriginalHeaderComponents);
        Assert.Equal(match, stats.HeaderMatch);
        Assert.InRange(stats.HeaderTop!.Value, 457, 517);
        Assert.True(stats.HeaderComponents >= stats.HeaderThreshold);
        Assert.Equal(3, observation.Rows!.Count);
    }

    [Fact]
    public void BrightRowLikeTextWithoutObtainedCaptionIsAmbiguous()
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 160, 554);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.False(observation.IsAvailable);
        Assert.Equal(0, stats.HeaderComponents);
        Assert.Null(stats.HeaderMatch);
        Assert.Equal(3, stats.RowComponents![0]);
    }

    [Fact]
    public void BroadBrightBackgroundDoesNotActAsCaption()
    {
        using var frame = NewScene();
        Cv2.Rectangle(frame, new Rect(164, 457, 55, 92), new Scalar(245, 245, 245), thickness: -1);
        DrawTextBlocks(frame, 160, 554);
        DrawTextBlocks(frame, 160, 602);
        DrawTextBlocks(frame, 160, 650);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.False(observation.IsAvailable);
        Assert.Null(stats.HeaderMatch);
        Assert.Equal(new[] { 3, 3, 3, 0, 0, 0 }, stats.RowComponents);
    }

    [Fact]
    public void CaptionWithMissingFirstRowIsAmbiguous()
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 169, 511);
        DrawTextBlocks(frame, 160, 602);

        Assert.False(PickupObtainedRowDetector.Detect(frame).IsAvailable);
    }

    [Fact]
    public void EmptyFixedSlotsAndBlackFrameHaveDifferentOutcomes()
    {
        using var frame = NewScene();
        var empty = PickupObtainedRowDetector.Detect(frame);
        Assert.True(empty.IsAvailable);
        Assert.Empty(empty.Rows!);

        using var black = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        Assert.False(PickupObtainedRowDetector.Detect(black).IsAvailable);
    }

    private static Mat NewScene() => new(1080, 1920, MatType.CV_8UC3, new Scalar(92, 80, 71));

    private static void DrawTextBlocks(Mat frame, int left, int top, Scalar? color = null)
    {
        for (var i = 0; i < 3; i++)
        {
            Cv2.Rectangle(frame, new Rect(left + i * 13, top, 8, 18),
                color ?? new Scalar(213, 215, 218), thickness: -1);
        }
    }
}
