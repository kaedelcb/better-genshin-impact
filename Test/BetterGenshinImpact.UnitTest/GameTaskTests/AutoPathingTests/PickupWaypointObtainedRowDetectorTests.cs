using BetterGenshinImpact.GameTask.AutoPathing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public sealed class PickupWaypointObtainedRowDetectorTests
{
    private static readonly Scalar ExactColor = new(216, 229, 236);

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    [InlineData(2560, 1440)]
    public void OneExactTitleAndThreeExactRowsProduceThreeRows(int width, int height)
    {
        using var frame = NewScene(width, height);
        DrawTextBlocks(frame, 169, 511, ExactColor);
        for (var row = 0; row < 3; row++)
            DrawTextBlocks(frame, 160, 554 + 48 * row, ExactColor);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.True(observation.IsAvailable);
        Assert.Equal(3, stats.OriginalHeaderComponents);
        Assert.Equal("原框指定色", stats.HeaderMatch);
        Assert.Equal(new[] { 3, 3, 3, 0, 0, 0 }, stats.RowComponents);
        Assert.Equal(3, observation.Rows!.Count);
    }

    [Theory]
    [InlineData(217, 229, 236)]
    [InlineData(216, 230, 236)]
    [InlineData(216, 229, 237)]
    [InlineData(222, 235, 242)]
    public void SimilarColorsDoNotCountAsTitleOrItemText(int blue, int green, int red)
    {
        using var frame = NewScene();
        var other = new Scalar(blue, green, red);
        DrawTextBlocks(frame, 169, 511, other);
        DrawTextBlocks(frame, 160, 554, other);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.True(observation.IsAvailable);
        Assert.Equal(0, stats.HeaderComponents);
        Assert.Equal(0, stats.RowComponents![0]);
        Assert.Empty(observation.Rows!);
    }

    [Fact]
    public void SameExactColorWorksInNeighboringTitleBox()
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 169, 474, ExactColor);
        DrawTextBlocks(frame, 160, 554, ExactColor);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.True(observation.IsAvailable);
        Assert.Equal(0, stats.OriginalHeaderComponents);
        Assert.Equal("邻框指定色", stats.HeaderMatch);
        Assert.Single(observation.Rows!);
    }

    [Fact]
    public void LargeExactBlockAndSinglePixelsDoNotConfirmTitle()
    {
        using var frame = NewScene();
        Cv2.Rectangle(frame, new Rect(164, 457, 55, 92), ExactColor, thickness: -1);
        DrawTextBlocks(frame, 160, 554, ExactColor);
        for (var i = 0; i < 5; i++)
            Cv2.Rectangle(frame, new Rect(170 + i * 4, 605, 1, 1), ExactColor, thickness: -1);

        var observation = PickupObtainedRowDetector.Detect(frame, out var stats);

        Assert.False(observation.IsAvailable);
        Assert.Equal(1, stats.HeaderComponents);
        Assert.Equal(3, stats.RowComponents![0]);
        Assert.Equal(0, stats.RowComponents[1]);
    }

    [Fact]
    public void CaptionWithMissingFirstRowIsAmbiguous()
    {
        using var frame = NewScene();
        DrawTextBlocks(frame, 169, 511, ExactColor);
        DrawTextBlocks(frame, 160, 602, ExactColor);

        Assert.False(PickupObtainedRowDetector.Detect(frame).IsAvailable);
    }

    private static Mat NewScene(int width = 1920, int height = 1080)
        => new(height, width, MatType.CV_8UC3, new Scalar(92, 80, 71));

    private static void DrawTextBlocks(Mat frame, int left, int top, Scalar color)
    {
        var xScale = frame.Width / 1920d;
        var yScale = frame.Height / 1080d;
        for (var i = 0; i < 3; i++)
        {
            var x = (int)Math.Round((left + i * 13) * xScale);
            var y = (int)Math.Round(top * yScale);
            Cv2.Rectangle(frame, new Rect(x, y, (int)Math.Round(8 * xScale), (int)Math.Round(18 * yScale)),
                color, thickness: -1);
        }
    }
}
