using System.Windows;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>只验证观看端 WPF Uniform 的图像内容坐标，不调用任何桌面输入 API。</summary>
public sealed class DesktopMonitorCoordinateTests
{
    [Theory]
    [InlineData(1000, 800, 1920, 1080, 500, 400, 0.5, 0.5)]
    [InlineData(1000, 800, 1920, 1080, 750, 400, 0.75, 0.5)]
    [InlineData(1000, 800, 1920, 1080, 0, 118.75, 0, 0)]
    [InlineData(1000, 800, 1920, 1080, 1000, 681.25, 1, 1)]
    [InlineData(1000, 800, 1080, 1920, 275, 0, 0, 0)]
    [InlineData(1000, 800, 1080, 1920, 725, 800, 1, 1)]
    [InlineData(640, 480, 1280, 960, 160, 120, 0.25, 0.25)]
    public void UniformContent_MapsCenterCornersAndInterior(double viewWidth, double viewHeight,
        double imageWidth, double imageHeight, double x, double y, double expectedX, double expectedY)
    {
        Assert.True(ScreenshotViewModel.TryMapControlPoint(new Point(x, y), new Size(viewWidth, viewHeight),
            new Size(imageWidth, imageHeight), out var normalized));
        Assert.Equal(expectedX, normalized.X, 10);
        Assert.Equal(expectedY, normalized.Y, 10);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(2.5)]
    public void DpiChangesDipSizes_WithoutChangingNormalizedTarget(double dpiScale)
    {
        // 同一物理内容在不同观看端缩放率下转换成 DIP；发送端不能再次乘 dpiScale。
        Assert.True(ScreenshotViewModel.TryMapControlPoint(new Point(750 / dpiScale, 400 / dpiScale),
            new Size(1000 / dpiScale, 800 / dpiScale), new Size(1920, 1080), out var normalized));
        Assert.Equal(0.75, normalized.X, 10);
        Assert.Equal(0.5, normalized.Y, 10);
    }

    [Theory]
    [InlineData(1920, 1080, 500, 118.749)]
    [InlineData(1920, 1080, 500, 681.251)]
    [InlineData(1920, 1080, -0.001, 400)]
    [InlineData(1920, 1080, 1000.001, 400)]
    [InlineData(1080, 1920, 274.999, 400)]
    [InlineData(1080, 1920, 725.001, 400)]
    [InlineData(1920, 1080, double.NaN, 400)]
    [InlineData(1920, 1080, 500, double.PositiveInfinity)]
    public void LetterboxAndNonFinitePoints_AreRejected(double imageWidth, double imageHeight, double x, double y)
        => Assert.False(ScreenshotViewModel.TryMapControlPoint(new Point(x, y), new Size(1000, 800),
            new Size(imageWidth, imageHeight), out _));

    [Fact]
    public void MissingOrInvalidLayout_IsRejected()
    {
        Assert.False(ScreenshotViewModel.TryMapControlPoint(new Point(), Size.Empty, new Size(1920, 1080), out _));
        Assert.False(ScreenshotViewModel.TryMapControlPoint(new Point(), new Size(0, 800), new Size(1920, 1080), out _));
        Assert.False(ScreenshotViewModel.TryMapControlPoint(new Point(), new Size(1000, 800), new Size(0, 1080), out _));
        Assert.False(ScreenshotViewModel.TryMapControlPoint(new Point(), new Size(double.PositiveInfinity, 800),
            new Size(1920, 1080), out _));
        Assert.False(ScreenshotViewModel.TryMapControlPoint(new Point(), new Size(1000, 800),
            new Size(1920, double.NaN), out _));
    }
}
