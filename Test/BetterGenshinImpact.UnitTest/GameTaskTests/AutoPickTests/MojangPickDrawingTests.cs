using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.GameTask.AutoPick;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Model.Area.Converter;
using OpenCvSharp;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPickTests;

public sealed class MojangPickDrawingTests
{
    [Fact]
    public void DrawTest_UsesCaptureCoordinatesForBothRegions_AndClearsMissingItem()
    {
        var board = new RecordingBoard();
        using var capture = new GameCaptureRegion(new Mat(500, 1000, MatType.CV_8UC3, Scalar.Black), 0, 0);
        using var fRegion = capture.Derive(210, 130, 24, 28);
        var itemRect = new Rect(286, 125, 140, 26);

        MojangPickLoop.DrawTest(board, capture, fRegion, itemRect);

        var f = Assert.IsType<MaskWindowDrawingRect>(Assert.Single(board.Groups["MojangTestF"]));
        var item = Assert.IsType<MaskWindowDrawingRect>(Assert.Single(board.Groups["MojangTestItem"]));
        Assert.Equal(new System.Windows.Rect(210, 130, 24, 28), f.Bounds);
        Assert.Equal(new System.Windows.Rect(286, 125, 140, 26), item.Bounds);

        MojangPickLoop.DrawTest(board, capture, fRegion, null);

        Assert.True(board.Groups.ContainsKey("MojangTestF"));
        Assert.False(board.Groups.ContainsKey("MojangTestItem"));
    }

    [Fact]
    public void DrawTest_ScaledCapture_ConvertsBothRegionsToGameCaptureCoordinates()
    {
        var board = new RecordingBoard();
        using var root = new GameCaptureRegion(new Mat(1000, 2000, MatType.CV_8UC3, Scalar.Black), 0, 0);
        using var capture = new ImageRegion(new Mat(500, 1000, MatType.CV_8UC3, Scalar.Black), 0, 0,
            root, new ScaleConverter(2));
        using var fRegion = capture.Derive(210, 130, 24, 28);

        MojangPickLoop.DrawTest(board, capture, fRegion, new Rect(286, 125, 140, 26));

        var f = Assert.IsType<MaskWindowDrawingRect>(Assert.Single(board.Groups["MojangTestF"]));
        var item = Assert.IsType<MaskWindowDrawingRect>(Assert.Single(board.Groups["MojangTestItem"]));
        Assert.Equal(new System.Windows.Rect(420, 260, 48, 56), f.Bounds);
        Assert.Equal(new System.Windows.Rect(572, 250, 280, 52), item.Bounds);
    }

    private sealed class RecordingBoard : IMaskWindowDrawingBoard
    {
        public Dictionary<string, IReadOnlyList<MaskWindowDrawingShape>> Groups { get; } = new();

        public void Set(MaskWindowDrawingGroup group, IReadOnlyList<MaskWindowDrawingShape>? shapes)
        {
            if (shapes is null || shapes.Count == 0)
            {
                Clear(group);
                return;
            }

            Groups[group.Name] = shapes;
        }

        public void Clear(MaskWindowDrawingGroup group) => Groups.Remove(group.Name);

        public void ClearAll() => Groups.Clear();
    }
}
