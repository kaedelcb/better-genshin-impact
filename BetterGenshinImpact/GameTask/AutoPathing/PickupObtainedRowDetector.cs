using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>检测左侧「获得」列表的文字行。不读取物品名、图标或行尾数量。</summary>
internal static class PickupObtainedRowDetector
{
    private const int ReferenceWidth = 1920;
    private const int ReferenceHeight = 1080;
    private const int RoiX = 150;
    private const int RoiY = 335;
    private const int RoiWidth = 251;
    private const int RoiHeight = 565;
    private const int RowPitch = 48;
    private const int MaxRows = 6;

    // 中文「获得」标题的 16×4 粗略笔画分布。只作为固定 UI 的形状门，不识别内容文字。
    private const ulong HeaderShape = 0x421e_fffe_feee_5cfeUL;

    private readonly record struct Glyph(int X, int Y, int Width, int Height)
    {
        public double CenterY => Y + Height / 2.0;
    }

    internal static PickupRowObservation Detect(Mat frame)
    {
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
        int left = (int)Math.Round(RoiX * scaleX), top = (int)Math.Round(RoiY * scaleY);
        int right = (int)Math.Round((RoiX + RoiWidth) * scaleX), bottom = (int)Math.Round((RoiY + RoiHeight) * scaleY);
        using var source = new Mat(frame, new Rect(left, top, right - left, bottom - top));
        using var normalized = new Mat();
        Cv2.Resize(source, normalized, new Size(RoiWidth, RoiHeight), interpolation: scaleX > 1 ? InterpolationFlags.Area : InterpolationFlags.Linear);

        using var bgr = new Mat();
        if (channels == 4)
            Cv2.CvtColor(normalized, bgr, ColorConversionCodes.BGRA2BGR);
        else
            normalized.CopyTo(bgr);

        using var gray = new Mat();
        using var hsv = new Mat();
        Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
        Cv2.MeanStdDev(gray, out var mean, out var deviation);
        if (mean.Val0 < 25 || deviation.Val0 < 4)
            return default; // 黑帧、过场或没有可用的场景细节不当作列表消失。

        using var contrast = new Mat();
        using var contrastMask = new Mat();
        using var lightTextMask = new Mat();
        using var mask = new Mat();
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(11, 9));
        Cv2.MorphologyEx(gray, contrast, MorphTypes.TopHat, kernel);
        var threshold = Cv2.Threshold(contrast, contrastMask, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        // 白/浅灰字接受一段色值，用局部亮度差过滤亮水面和其他大面积亮色。
        Cv2.Threshold(contrast, contrastMask, Math.Clamp(threshold, 16, 31) - 1, 255, ThresholdTypes.Binary);
        Cv2.InRange(hsv, new Scalar(0, 0, 143), new Scalar(180, 95, 255), lightTextMask);
        Cv2.BitwiseAnd(contrastMask, lightTextMask, mask);

        var glyphs = FindGlyphs(mask);
        var rows = FindRowCandidates(mask, glyphs);
        var headers = FindHeaders(mask, glyphs, out var headerLike);
        AddHeadersFromRows(mask, rows, headers);
        if (headers.Count == 0)
            return rows.Count == 0 && !headerLike
                ? new PickupRowObservation(true, Array.Empty<PickupVisualRow>())
                : default;

        List<PickupVisualRow>? matched = null;
        foreach (var header in headers)
        {
            var group = FindAnchoredRows(rows, header);
            if (group == null)
                continue;
            if (matched != null)
                return default; // 多个可能的列表，不能安全计数。
            matched = group;
        }

        return matched == null ? default : new PickupRowObservation(true, matched);
    }

    private static List<Glyph> FindGlyphs(Mat mask)
    {
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();
        var count = Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids,
            connectivity: PixelConnectivity.Connectivity8, ltype: MatType.CV_32S);
        var glyphs = new List<Glyph>();
        for (var i = 1; i < count; i++)
        {
            var width = stats.At<int>(i, 2);
            var height = stats.At<int>(i, 3);
            if (width is >= 2 and <= 58 && height is >= 5 and <= 30 && stats.At<int>(i, 4) >= 11)
                glyphs.Add(new Glyph(RoiX + stats.At<int>(i, 0), RoiY + stats.At<int>(i, 1), width, height));
        }

        return glyphs;
    }

    private static List<int> FindHeaders(Mat mask, List<Glyph> glyphs, out bool headerLike)
    {
        var headers = new List<int>();
        headerLike = false;
        foreach (var glyph in glyphs)
        {
            if (glyph.X is < 164 or > 177 || glyph.Y is < 350 or > 590
                || glyph.Width is < 13 or > 54 || glyph.Height is < 15 or > 27)
            {
                continue;
            }

            if (Ink(mask, 168, glyph.Y, 23, 24) < 90 || Ink(mask, 193, glyph.Y, 23, 24) < 70
                || Ink(mask, 218, glyph.Y, 17, 24) >= 85)
            {
                continue;
            }

            headerLike = true;
            for (var dy = -2; dy <= 2; dy++)
            {
                var y = glyph.Y + dy;
                var signature = Signature(mask, 166, y - 1, 50, 26, 84);
                if (BitOperations.PopCount(signature ^ HeaderShape) > 16)
                    continue;
                if (!headers.Exists(previous => Math.Abs(previous - y) <= 5))
                    headers.Add(y);
                break;
            }
        }

        return headers;
    }

    private static void AddHeadersFromRows(Mat mask, List<PickupVisualRow> rows, List<int> headers)
    {
        // 亮背景有时会把标题笔画与周围像素连成一个大块；从第一行的相对位置
        // 回找标题形状，不要求标题本身一定被连通域分成两个独立字块。
        foreach (var row in rows)
        {
            var expected = (int)row.Y - 54;
            if (expected is < 344 or > 596 || headers.Exists(header => Math.Abs(header - expected) <= 8))
                continue;

            var bestDistance = 17;
            var bestY = 0;
            for (var y = Math.Max(350, expected - 6); y <= Math.Min(590, expected + 6); y++)
            {
                // 已像物品行的区域不能再当成标题，否则漏掉首行时会误认成新的列表。
                if (rows.Exists(candidate => Math.Abs(candidate.Y - y - 11) <= 13))
                    continue;
                var distance = BitOperations.PopCount(Signature(mask, 166, y - 1, 50, 26, 84) ^ HeaderShape);
                if (distance >= bestDistance)
                    continue;
                bestDistance = distance;
                bestY = y;
            }

            if (bestY != 0)
                headers.Add(bestY);
        }
    }

    private static List<PickupVisualRow> FindRowCandidates(Mat mask, List<Glyph> glyphs)
    {
        var rows = new List<PickupVisualRow>();
        foreach (var seed in glyphs)
        {
            if (seed.X is < 154 or > 165 || seed.Y < RoiY + 2 || seed.Y + 25 > RoiY + RoiHeight
                || seed.Width is < 9 or > 36 || seed.Height is < 13 or > 29)
            {
                continue;
            }

            var parts = glyphs.Where(glyph => glyph.X >= 154 && glyph.Width <= 44
                && glyph.Y >= seed.Y - 4 && glyph.Y <= seed.Y + 14
                && Math.Abs(glyph.CenterY - seed.Y - 10) <= 12).ToArray();
            if (parts.Length < 2 || parts.Max(glyph => glyph.X + glyph.Width) - seed.X < 42)
                continue;

            var y = seed.Y + 11;
            if (rows.Exists(row => Math.Abs(row.Y - y) <= 7))
                continue;

            // 固定起点和宽度，避免滚动或淡入时旧行的轻微形变被当成新行。
            var signature = Signature(mask, 154, seed.Y - 2, 128, 27, 38);
            if (signature != 0)
                rows.Add(new PickupVisualRow(y, signature));
        }

        rows.Sort((left, right) => left.Y.CompareTo(right.Y));
        return rows;
    }

    private static List<PickupVisualRow>? FindAnchoredRows(List<PickupVisualRow> candidates, int header)
    {
        var firstY = header + 54;
        var rows = new List<PickupVisualRow>();
        foreach (var candidate in candidates)
        {
            if (candidate.Y < firstY - 8 || candidate.Y > firstY + (MaxRows - 1) * RowPitch + 8)
                continue;
            // 一定从第一行连续排列；看到缺行、浮动过渡或多余行时先等待下一帧。
            if (Math.Abs(candidate.Y - firstY - rows.Count * RowPitch) > 8)
                return null;
            rows.Add(candidate);
        }

        return rows.Count == 0 ? null : rows;
    }

    private static int Ink(Mat mask, int x, int y, int width, int height)
    {
        using var region = new Mat(mask, new Rect(x - RoiX, y - RoiY, width, height));
        return Cv2.CountNonZero(region);
    }

    private static ulong Signature(Mat mask, int x, int y, int width, int height, int inkThreshold)
    {
        using var region = new Mat(mask, new Rect(x - RoiX, y - RoiY, width, height));
        using var density = new Mat();
        Cv2.Resize(region, density, new Size(16, 4), interpolation: InterpolationFlags.Area);
        ulong bits = 0;
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 16; column++)
        {
            if (density.At<byte>(row, column) >= inkThreshold)
                bits |= 1UL << (row * 16 + column);
        }

        return bits;
    }
}
