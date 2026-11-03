using System;
using System.Collections.Generic;
using System.Numerics;

namespace BetterGenshinImpact.GameTask.AutoPathing;

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
            // 列表淡入会造成短暂模糊，不因一两帧识别不清而丢失原有基线。
            // 确认断帧或长时间看不清则重新建基线，不猜测期间发生的拾取。
            ResetPending();
            _emptyFrames = 0;
            if (observation.InterruptsContinuity || ++_ambiguousFrames >= DisappearanceFrames)
            {
                _ambiguousFrames = DisappearanceFrames;
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
