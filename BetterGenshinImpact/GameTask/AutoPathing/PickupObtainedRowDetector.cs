using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PickupObtainedRowDetectionStats(
    int HeaderComponents, int[]? RowComponents, int HeaderThreshold, int RowThreshold);

/// <summary>用固定区域内的文字色块判断「获得」标题和各物品行是否出现。</summary>
internal static class PickupObtainedRowDetector
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private const int PaneX = 154;
    private const int PaneY = 505;
    private const int PaneWidth = 170;
    private const int FirstRowTop = 549;
    private const int RowPitch = 48;
    private const int RowHeight = 31;
    private const int MaxRows = 6;
    private const int PaneHeight = FirstRowTop - PaneY + (MaxRows - 1) * RowPitch + RowHeight;

    // 固定颜色容差（BGR）；标题和每一行都用同一套颜色范围，不判断笔画形状。
    private static readonly Scalar TextColorMin = new(170, 175, 175);
    private static readonly Scalar TextColorMax = new(230, 240, 248);
    private static readonly Rect HeaderSlot = new(10, 0, 55, 32);
    private const int HeaderThreshold = 3;
    private const int RowThreshold = 3;

    internal static PickupRowObservation Detect(Mat frame) => Detect(frame, out _);

    internal static PickupRowObservation Detect(Mat frame, out PickupObtainedRowDetectionStats stats)
    {
        stats = new(0, null, HeaderThreshold, RowThreshold);
        if (frame.Empty() || frame.Width < 1280 || frame.Height < 720
            || Math.Abs(frame.Width / (double)frame.Height - 16d / 9d) > 0.035)
        {
            return default;
        }

        var channels = frame.Channels();
        if (frame.Depth() != MatType.CV_8U || channels is not (3 or 4))
            return default;

        var scaleX = frame.Width / (double)ReferenceWidth;
        var scaleY = frame.Height / (double)ReferenceHeight;
        var left = (int)Math.Round(PaneX * scaleX);
        var top = (int)Math.Round(PaneY * scaleY);
        var right = (int)Math.Round((PaneX + PaneWidth) * scaleX);
        var bottom = (int)Math.Round((PaneY + PaneHeight) * scaleY);
        using var source = new Mat(frame, new Rect(left, top, right - left, bottom - top));
        using var normalized = new Mat();
        Cv2.Resize(source, normalized, new Size(PaneWidth, PaneHeight),
            interpolation: scaleX > 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);

        using var bgr = new Mat();
        if (channels == 4)
            Cv2.CvtColor(normalized, bgr, ColorConversionCodes.BGRA2BGR);
        var color = channels == 4 ? bgr : normalized;
        var mean = Cv2.Mean(color);
        if (Math.Max(mean.Val0, Math.Max(mean.Val1, mean.Val2)) < 25)
            return default; // 黑帧不是列表已消失的证据。

        using var mask = new Mat();
        Cv2.InRange(color, TextColorMin, TextColorMax, mask);
        using var labels = new Mat();
        using var components = new Mat();
        using var centroids = new Mat();
        using var headerMask = new Mat(mask, HeaderSlot);
        var headerCount = CountTextComponents(headerMask, labels, components, centroids);

        var rowCounts = new int[MaxRows];
        var rows = new List<PickupVisualRow>(MaxRows);
        var detectedRowCount = 0;
        var rowGap = false;
        for (var slot = 0; slot < MaxRows; slot++)
        {
            var rowTop = FirstRowTop + slot * RowPitch;
            using var rowMask = new Mat(mask, new Rect(0, rowTop - PaneY, PaneWidth, RowHeight));
            rowCounts[slot] = CountTextComponents(rowMask, labels, components, centroids);
            if (rowCounts[slot] < RowThreshold)
                continue;

            rowGap |= slot != detectedRowCount;
            detectedRowCount++;
            if (headerCount >= HeaderThreshold)
            {
                // 只在跨帧去重时比较色块分布；标题和行的存在都只由连通块数量决定。
                rows.Add(new PickupVisualRow(rowTop + RowHeight / 2.0, RowSignature(rowMask)));
            }
        }

        stats = new(headerCount, rowCounts, HeaderThreshold, RowThreshold);
        if (headerCount < HeaderThreshold)
            return detectedRowCount != 0
                ? default // 行还可见时不能确认提示已消失。
                : new PickupRowObservation(true, Array.Empty<PickupVisualRow>());

        return rows.Count == 0 || rowGap ? default : new PickupRowObservation(true, rows);
    }

    private static int CountTextComponents(Mat mask, Mat labels, Mat stats, Mat centroids)
    {
        var labelsCount = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        var count = 0;
        for (var index = 1; index < labelsCount; index++) // 0 为背景。
        {
            var width = stats.At<int>(index, 2);
            var height = stats.At<int>(index, 3);
            if (width is >= 2 and <= 46 && height is >= 4 and <= 28 && stats.At<int>(index, 4) >= 8)
                count++;
        }

        return count;
    }

    private static ulong RowSignature(Mat mask)
    {
        using var text = new Mat(mask, new Rect(0, 0, 128, RowHeight));
        using var density = new Mat();
        Cv2.Resize(text, density, new Size(16, 4), interpolation: InterpolationFlags.Area);
        ulong bits = 0;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 16; column++)
        {
            if (density.At<byte>(row, column) >= 38)
                bits |= 1UL << (row * 16 + column);
        }

        return bits;
    }
}
