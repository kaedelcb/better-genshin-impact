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
        Assert.Equal(new[] { 3, 3, 0, 0, 0, 0 }, stats.RowComponents);
        Assert.Collection(observation.Rows!,
            row => Assert.InRange(row.Y, 560, 569),
            row => Assert.InRange(row.Y, 608, 617));
    }

    [Fact]
    public void BrightRowLikeTextWithoutObtainedCaptionIsAmbiguous()
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 160, 554);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.False(observation.IsAvailable);
        Assert.Equal(0, stats.HeaderComponents);
        Assert.Equal(3, stats.RowComponents![0]);
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

    private static void DrawTextBlocks(Mat frame, int left, int top)
    {
        for (var i = 0; i < 3; i++)
        {
            Cv2.Rectangle(frame, new Rect(left + i * 13, top, 8, 18),
                new Scalar(213, 215, 218), thickness: -1);
        }
    }
}
