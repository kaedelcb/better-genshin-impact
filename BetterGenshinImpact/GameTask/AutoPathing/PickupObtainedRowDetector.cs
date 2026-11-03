using System;
using System.Collections.Generic;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PickupObtainedRowDetectionStats(
    int HeaderComponents, int[]? RowComponents, int HeaderThreshold, int RowThreshold,
    int OriginalHeaderComponents, string? HeaderMatch, int? HeaderTop);

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

    // 固定颜色容差（BGR）；物品行和标题的原框均保持既有色值。
    private static readonly Scalar TextColorMin = new(170, 175, 175);
    private static readonly Scalar TextColorMax = new(230, 240, 248);
    private const int HeaderX = PaneX + 10;
    private const int HeaderWidth = 55;
    private const int HeaderHeight = 32;
    private const int NearbyHeaderTop = PaneY - RowPitch;
    private static readonly Rect HeaderSlot = new(10, 0, HeaderWidth, HeaderHeight);
    // 仅试标题原框与紧邻固定框，搜索带结束于第一行之前。
    private static readonly int[] CandidateHeaderTops = [505, 497, 513, 489, 517, 481, 473, 465, 457];
    private const int HeaderThreshold = 3;
    private const int RowThreshold = 3;

    internal static PickupRowObservation Detect(Mat frame) => Detect(frame, out _);

    internal static PickupRowObservation Detect(Mat frame, out PickupObtainedRowDetectionStats stats)
    {
        stats = new(0, null, HeaderThreshold, RowThreshold, 0, null, null);
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
        var originalHeaderCount = CountTextComponents(headerMask, labels, components, centroids);

        var rowCounts = new int[MaxRows];
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
        }

        var headerCount = originalHeaderCount;
        string? headerMatch = null;
        int? headerTop = null;
        if (originalHeaderCount >= HeaderThreshold)
        {
            headerMatch = "原框原色";
            headerTop = PaneY;
        }
        else if (detectedRowCount != 0 && !rowGap)
        {
            // 没有连续物品行时不扩大标题探测，避免把无关场景当作列表。
            (headerCount, headerMatch, headerTop) = FindNearbyHeader(frame, scaleX, scaleY,
                labels, components, centroids, originalHeaderCount);
        }

        stats = new(headerCount, rowCounts, HeaderThreshold, RowThreshold, originalHeaderCount, headerMatch, headerTop);
        if (headerCount < HeaderThreshold)
            return detectedRowCount != 0
                ? default // 行还可见时不能确认提示已消失。
                : new PickupRowObservation(true, Array.Empty<PickupVisualRow>());

        if (detectedRowCount == 0 || rowGap)
            return default;

        var rows = new List<PickupVisualRow>(detectedRowCount);
        for (var slot = 0; slot < detectedRowCount; slot++)
        {
            var rowTop = FirstRowTop + slot * RowPitch;
            using var rowMask = new Mat(mask, new Rect(0, rowTop - PaneY, PaneWidth, RowHeight));
            // 只在跨帧去重时比较色块分布；标题和行的存在都只由连通块决定。
            rows.Add(new PickupVisualRow(rowTop + RowHeight / 2.0, RowSignature(rowMask)));
        }
        return new PickupRowObservation(true, rows);
    }

    private static (int Count, string? Match, int? Top) FindNearbyHeader(Mat frame, double scaleX, double scaleY,
        Mat labels, Mat components, Mat centroids, int originalHeaderCount)
    {
        var left = (int)Math.Round(HeaderX * scaleX);
        var top = (int)Math.Round(NearbyHeaderTop * scaleY);
        var right = (int)Math.Round((HeaderX + HeaderWidth) * scaleX);
        var bottom = (int)Math.Round(FirstRowTop * scaleY);
        using var source = new Mat(frame, new Rect(left, top, right - left, bottom - top));
        using var normalized = new Mat();
        Cv2.Resize(source, normalized, new Size(HeaderWidth, FirstRowTop - NearbyHeaderTop),
            interpolation: scaleX > 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);
        using var bgr = new Mat();
        if (frame.Channels() == 4)
            Cv2.CvtColor(normalized, bgr, ColorConversionCodes.BGRA2BGR);
        var color = frame.Channels() == 4 ? bgr : normalized;

        using var strict = new Mat();
        Cv2.InRange(color, TextColorMin, TextColorMax, strict);
        using var hsv = new Mat();
        using var neutral = new Mat();
        Cv2.CvtColor(color, hsv, ColorConversionCodes.BGR2HSV);
        // 标题回退允许接近中性的浅灰或亮白；不改变物品行的颜色掩膜。
        Cv2.InRange(hsv, new Scalar(0, 0, 150), new Scalar(180, 38, 255), neutral);

        var bestCount = originalHeaderCount;
        foreach (var slotTop in CandidateHeaderTops)
        {
            var box = new Rect(0, slotTop - NearbyHeaderTop, HeaderWidth, HeaderHeight);
            using var strictSlot = new Mat(strict, box);
            var strictCount = CountTextComponents(strictSlot, labels, components, centroids, requireTitleBand: true);
            if (strictCount >= HeaderThreshold)
                return (strictCount, slotTop == PaneY ? "原框原色" : "邻框原色", slotTop);

            using var neutralSlot = new Mat(neutral, box);
            var neutralCount = CountTextComponents(neutralSlot, labels, components, centroids, requireTitleBand: true);
            if (neutralCount >= HeaderThreshold)
                return (neutralCount, slotTop == PaneY ? "原框浅色" : "邻框浅色", slotTop);

            bestCount = Math.Max(bestCount, Math.Max(strictCount, neutralCount));
        }

        return (bestCount, null, null);
    }

    private static int CountTextComponents(Mat mask, Mat labels, Mat stats, Mat centroids, bool requireTitleBand = false)
    {
        var labelsCount = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity4, ltype: MatType.CV_32S);
        var count = 0;
        var inkArea = 0;
        var minX = mask.Width;
        var maxX = 0;
        var minCenterY = mask.Height * 2;
        var maxCenterY = 0;
        for (var index = 1; index < labelsCount; index++) // 0 为背景。
        {
            var width = stats.At<int>(index, 2);
            var height = stats.At<int>(index, 3);
            var area = stats.At<int>(index, 4);
            if (width is not (>= 2 and <= 46) || height is not (>= 4 and <= 28) || area < 8)
                continue;
            if (requireTitleBand)
            {
                var x = stats.At<int>(index, 0);
                var y = stats.At<int>(index, 1);
                if (y == 0 || y + height == mask.Height)
                    continue;
                inkArea += area;
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x + width);
                var centerY = y * 2 + height;
                minCenterY = Math.Min(minCenterY, centerY);
                maxCenterY = Math.Max(maxCenterY, centerY);
            }
            count++;
        }

        if (requireTitleBand && count >= HeaderThreshold
            && (maxX - minX < 24 || maxCenterY - minCenterY > 32 || inkArea * 2 > mask.Width * mask.Height))
            return 0; // 不接受拥挤亮底或分散在不同高度的块。

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
