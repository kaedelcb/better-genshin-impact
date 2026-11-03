using BetterGenshinImpact.GameTask.AutoPathing;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

public sealed class PickupWaypointObtainedRowDetectorTests
{
    [Fact]
    public void BrightRowLikeTextWithoutObtainedCaptionDoesNotCount()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, new Scalar(92, 80, 71));
        for (var row = 0; row < 2; row++)
        {
            for (var glyph = 0; glyph < 4; glyph++)
            {
                // 同区域、同间距的浅色细线轮廓也可能来自游戏场景或别的提示。
                Cv2.Rectangle(frame, new Rect(160 + glyph * 24, 553 + row * 48, 19, 21),
                    new Scalar(213, 215, 218), thickness: 2);
            }
        }

        var observation = PickupObtainedRowDetector.Detect(frame);
        Assert.True(!observation.IsAvailable || observation.Rows is { Count: 0 });
    }

    [Fact]
    public void BlackFrameIsNotEvidenceThatTheListDisappeared()
    {
        using var frame = new Mat(1080, 1920, MatType.CV_8UC3, Scalar.Black);
        Assert.False(PickupObtainedRowDetector.Detect(frame).IsAvailable);
    }
}
