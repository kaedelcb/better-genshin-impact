using System;
using System.Collections.Generic;
using System.Numerics;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

internal readonly record struct PickupObtainedRowDetectionStats(
    int HeaderComponents, int[]? RowComponents, int HeaderThreshold, int RowThreshold,
    int OriginalHeaderComponents, string? HeaderMatch, int? HeaderTop);

/// <summary>在固定框内，对两次取色之间的原始像素做四连通计数。</summary>
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

    internal const int TextRedMin = 236, TextRedMax = 242;
    internal const int TextGreenMin = 229, TextGreenMax = 235;
    internal const int TextBlueMin = 216, TextBlueMax = 222;
    private static readonly Scalar TextColorMin = new(TextBlueMin, TextGreenMin, TextRedMin);
    private static readonly Scalar TextColorMax = new(TextBlueMax, TextGreenMax, TextRedMax);
    private const int HeaderThreshold = 5;
    private const int RowThreshold = 5;

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

        // 插值会改变色值，使用原始像素和两次取色给定的窄范围。
        using var mask = new Mat();
        Cv2.InRange(channels == 4 ? bgr : source, TextColorMin, TextColorMax, mask);

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
        // 已完成颜色检测和连通计数；缩小二值掩膜只用于跨帧去重。
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

// Y 使用 1080p 参考坐标。签名只比较行的形状，不识别物品名或数量。
internal readonly record struct PickupVisualRow(double Y, ulong ShapeSignature);

// 界面动画造成的短暂模糊、捕获中断、确认列表为空是三种不同状态。
internal readonly record struct PickupRowObservation(bool IsAvailable, IReadOnlyList<PickupVisualRow>? Rows,
    bool InterruptsContinuity = false)
{
    internal static PickupRowObservation Interrupted => new(false, null, true);
}

internal readonly record struct PickupRowUpdate(int AddedRows, bool Cleared, bool BaselineReady);

internal sealed class PickupObtainedRowTracker
{
    private const int StableFrames = 2;
    private const int DisappearanceFrames = 3;
    private const int MaxRows = 12;
    private const int SignatureTolerance = 10;
    private const double AlignmentTolerance = 6;
    private const double MaxVerticalShift = 150;
    private const double MinRowSeparation = 16;

    private PickupVisualRow[] _visibleRows = [];
    private PickupVisualRow[]? _pendingRows;
    private int _pendingFrames;
    private int _emptyFrames;
    private int _ambiguousFrames;
    private bool _baselineReady;

    internal PickupRowUpdate Observe(PickupRowObservation observation)
    {
        if (observation.InterruptsContinuity || !TryGetOrderedRows(observation, out var rows))
        {
            // 淡入时可能连续多帧只有标题。已确认的空列表仍可作为基线，
            // 否则淡入后的第一行会被当成旧行而漏计。
            // 真正断帧，或已有物品行却长时间看不清时，仍不猜测期间发生的拾取。
            ResetPending();
            _emptyFrames = 0;
            if (observation.InterruptsContinuity || ++_ambiguousFrames >= DisappearanceFrames)
            {
                _ambiguousFrames = DisappearanceFrames;
                if (observation.InterruptsContinuity || _visibleRows.Length != 0)
                    _baselineReady = false;
            }
            return new(0, false, _baselineReady);
        }

        _ambiguousFrames = 0;
        if (rows.Length == 0)
        {
            ResetPending();
            if (++_emptyFrames < DisappearanceFrames)
                return new(0, false, _baselineReady);

            _emptyFrames = DisappearanceFrames;
            var cleared = _visibleRows.Length != 0;
            _visibleRows = [];
            _baselineReady = true;
            return new(0, cleared, true);
        }

        _emptyFrames = 0;
        if (_baselineReady && MatchList(_visibleRows, rows))
        {
            ResetPending();
            _visibleRows = rows;
            return new(0, false, true);
        }

        _pendingFrames = _pendingRows != null && MatchList(_pendingRows, rows) ? _pendingFrames + 1 : 1;
        _pendingRows = rows;

        // 一帧的增行不计；非空列表暂时少一行也不立即移除旧行。
        var requiredFrames = _baselineReady && rows.Length < _visibleRows.Length ? DisappearanceFrames : StableFrames;
        if (_pendingFrames < requiredFrames)
            return new(0, false, _baselineReady);

        var added = _baselineReady ? CountAdditions(_visibleRows, rows) : 0;
        _visibleRows = rows;
        _baselineReady = true;
        ResetPending();
        return new(added, false, true);
    }

    private static bool TryGetOrderedRows(PickupRowObservation observation, out PickupVisualRow[] rows)
    {
        rows = [];
        if (!observation.IsAvailable || observation.Rows is not { Count: <= MaxRows } source)
            return false;
        if (source.Count == 0)
            return true;

        rows = new PickupVisualRow[source.Count];
        for (var index = 0; index < rows.Length; index++)
        {
            if (!double.IsFinite(source[index].Y))
                return false;
            rows[index] = source[index];
        }

        Array.Sort(rows, static (first, second) => first.Y.CompareTo(second.Y));
        for (var index = 1; index < rows.Length; index++)
        {
            if (rows[index].Y - rows[index - 1].Y < MinRowSeparation)
                return false;
        }

        return true;
    }

    private static int CountAdditions(PickupVisualRow[] previous, PickupVisualRow[] current)
    {
        if (previous.Length == 0)
            return current.Length;

        var bestOverlap = 0;
        var bestHasValidTransition = false;
        for (var oldStart = 0; oldStart < previous.Length; oldStart++)
        for (var newStart = 0; newStart < current.Length; newStart++)
        {
            var overlap = MatchingRun(previous, oldStart, current, newStart);
            if (overlap == 0 || overlap < bestOverlap)
                continue;

            var oldEnd = oldStart + overlap;
            var newEnd = newStart + overlap;
            // 未清屏时，只承认旧行连续保留且新行出现在一端；同名行也按位置顺序逐条配对。
            var validTransition = overlap == current.Length
                || ((oldStart == 0 && newEnd == current.Length)
                    || (oldEnd == previous.Length && newStart == 0))
                && !HasAmbiguousReplacement(previous, oldStart, oldEnd, current, newStart, newEnd);
            bestHasValidTransition = overlap == bestOverlap
                ? bestHasValidTransition || validTransition
                : validTransition;
            bestOverlap = overlap;
        }

        // 只用最大的重叠，不用更短的对齐去猜测多出来的行。
        return bestHasValidTransition ? current.Length - bestOverlap : 0;
    }

    private static bool HasAmbiguousReplacement(PickupVisualRow[] previous, int oldStart, int oldEnd,
        PickupVisualRow[] current, int newStart, int newEnd)
    {
        for (var oldIndex = 0; oldIndex < previous.Length; oldIndex++)
        {
            if (oldIndex >= oldStart && oldIndex < oldEnd)
                continue;
            for (var newIndex = 0; newIndex < current.Length; newIndex++)
            {
                if (newIndex >= newStart && newIndex < newEnd)
                    continue;
                if (SignaturesMatch(previous[oldIndex], current[newIndex]))
                    return true;
            }
        }
        return false;
    }

    private static bool MatchList(PickupVisualRow[] previous, PickupVisualRow[] current)
        => previous.Length == current.Length && MatchingRun(previous, 0, current, 0) == current.Length;

    private static int MatchingRun(PickupVisualRow[] previous, int oldStart, PickupVisualRow[] current, int newStart)
    {
        var possible = Math.Min(previous.Length - oldStart, current.Length - newStart);
        if (possible == 0)
            return 0;

        var shift = current[newStart].Y - previous[oldStart].Y;
        if (Math.Abs(shift) > MaxVerticalShift)
            return 0;

        var count = 0;
        for (; count < possible; count++)
        {
            var oldRow = previous[oldStart + count];
            var newRow = current[newStart + count];
            if (Math.Abs(newRow.Y - oldRow.Y - shift) > AlignmentTolerance
                || !SignaturesMatch(oldRow, newRow))
            {
                break;
            }
        }

        return count;
    }

    private static bool SignaturesMatch(PickupVisualRow first, PickupVisualRow second)
        => BitOperations.PopCount(first.ShapeSignature ^ second.ShapeSignature) <= SignatureTolerance;

    private void ResetPending()
    {
        _pendingRows = null;
        _pendingFrames = 0;
    }
}
