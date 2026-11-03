using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PickupObtainedRowDetectionStats(
    int HeaderComponents, int[]? RowComponents, int HeaderThreshold, int RowThreshold,
    int OriginalHeaderComponents, string? HeaderMatch, int? HeaderTop);

/// <summary>在固定框内，只对指定 RGB 的原始像素做四连通计数。</summary>
internal static class PickupObtainedRowDetector
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private const int PaneX = 154;
    private const int PaneWidth = 170;
    private const int HeaderX = PaneX + 10;
    private const int HeaderTop = 505;
    private const int HeaderWidth = 55;
    private const int HeaderHeight = 32;
    private const int FirstRowTop = 549;
    private const int RowPitch = 48;
    private const int RowHeight = 31;
    private const int MaxRows = 6;
    private const int PaneTop = HeaderTop - RowPitch;
    private const int PaneBottom = FirstRowTop + (MaxRows - 1) * RowPitch + RowHeight;
    private static readonly int[] NearbyHeaderTops = [497, 513, 489, 517, 481, 473, 465, 457];

    internal const int TextRed = 236;
    internal const int TextGreen = 229;
    internal const int TextBlue = 216;
    private static readonly Scalar TextColor = new(TextBlue, TextGreen, TextRed);
    private const int HeaderThreshold = 3;
    private const int RowThreshold = 3;

    internal static PickupRowObservation Detect(Mat frame) => Detect(frame, out _);

    internal static PickupRowObservation Detect(Mat frame, out PickupObtainedRowDetectionStats stats)
    {
        stats = new(0, null, HeaderThreshold, RowThreshold, 0, null, null);
        if (frame.Empty() || frame.Width < 1280 || frame.Height < 720
            || Math.Abs(frame.Width / (double)frame.Height - 16d / 9d) > 0.035)
            return default;

        var channels = frame.Channels();
        if (frame.Depth() != MatType.CV_8U || channels is not (3 or 4))
            return default;

        var scaleX = frame.Width / (double)ReferenceWidth;
        var scaleY = frame.Height / (double)ReferenceHeight;
        var pane = ScaleRect(PaneX, PaneTop, PaneWidth, PaneBottom - PaneTop, scaleX, scaleY);
        using var source = new Mat(frame, pane);
        using var bgr = new Mat();
        if (channels == 4)
            Cv2.CvtColor(source, bgr, ColorConversionCodes.BGRA2BGR);

        // 插值会改变色值，只在原始像素上做等值匹配。
        using var mask = new Mat();
        Cv2.InRange(channels == 4 ? bgr : source, TextColor, TextColor, mask);

        using var labels = new Mat();
        using var components = new Mat();
        using var centroids = new Mat();
        Rect Slot(int x, int y, int width, int height)
        {
            var rect = ScaleRect(x, y, width, height, scaleX, scaleY);
            return new Rect(rect.X - pane.X, rect.Y - pane.Y, rect.Width, rect.Height);
        }

        int CountHeader(int top)
        {
            using var headerMask = new Mat(mask, Slot(HeaderX, top, HeaderWidth, HeaderHeight));
            return CountTextComponents(headerMask, labels, components, centroids);
        }

        var originalHeaderCount = CountHeader(HeaderTop);
        var rowCounts = new int[MaxRows];
        var detectedRowCount = 0;
        var rowGap = false;
        for (var slot = 0; slot < MaxRows; slot++)
        {
            using var rowMask = new Mat(mask, Slot(PaneX, FirstRowTop + slot * RowPitch, PaneWidth, RowHeight));
            rowCounts[slot] = CountTextComponents(rowMask, labels, components, centroids);
            if (rowCounts[slot] < RowThreshold)
                continue;

            rowGap |= slot != detectedRowCount;
            detectedRowCount++;
        }

        var headerCount = originalHeaderCount;
        string? headerMatch = originalHeaderCount >= HeaderThreshold ? "原框指定色" : null;
        int? headerTop = headerMatch == null ? null : HeaderTop;
        if (headerMatch == null && detectedRowCount > 0 && !rowGap)
        {
            foreach (var top in NearbyHeaderTops)
            {
                var candidateCount = CountHeader(top);
                headerCount = Math.Max(headerCount, candidateCount);
                if (candidateCount < HeaderThreshold)
                    continue;

                headerMatch = "邻框指定色";
                headerTop = top;
                break;
            }
        }

        stats = new(headerCount, rowCounts, HeaderThreshold, RowThreshold, originalHeaderCount, headerMatch, headerTop);
        if (headerTop == null)
            return detectedRowCount == 0 ? new PickupRowObservation(true, Array.Empty<PickupVisualRow>()) : default;

        if (detectedRowCount == 0 || rowGap)
            return default;

        var rows = new List<PickupVisualRow>(detectedRowCount);
        for (var slot = 0; slot < detectedRowCount; slot++)
        {
            var rowTop = FirstRowTop + slot * RowPitch;
            using var text = new Mat(mask, Slot(PaneX, rowTop, 128, RowHeight));
            rows.Add(new PickupVisualRow(rowTop + RowHeight / 2.0, RowSignature(text)));
        }
        return new PickupRowObservation(true, rows);
    }

    private static Rect ScaleRect(int x, int y, int width, int height, double scaleX, double scaleY)
    {
        var left = (int)Math.Round(x * scaleX);
        var top = (int)Math.Round(y * scaleY);
        var right = (int)Math.Round((x + width) * scaleX);
        var bottom = (int)Math.Round((y + height) * scaleY);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static int CountTextComponents(Mat mask, Mat labels, Mat stats, Mat centroids)
    {
        var labelsCount = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        var count = 0;
        for (var index = 1; index < labelsCount; index++) // 0 为背景。
        {
            if (stats.At<int>(index, 4) >= 2) // 去掉孤立的单像素。
                count++;
        }
        return count;
    }

    private static ulong RowSignature(Mat mask)
    {
        // 已完成等值检测和连通计数；缩小二值掩膜只用于跨帧去重。
        using var density = new Mat();
        Cv2.Resize(mask, density, new Size(16, 4), interpolation: InterpolationFlags.Area);
        ulong bits = 0;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 16; column++)
        {
            if (density.At<byte>(row, column) >= 8)
                bits |= 1UL << (row * 16 + column);
        }
        return bits;
    }
}
