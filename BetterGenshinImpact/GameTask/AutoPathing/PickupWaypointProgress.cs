using System;
using System.Diagnostics;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>一次到点动作的获得列表计数，帧由现有截图调度器提供。</summary>
internal sealed class PickupWaypointProgress : IDisposable
{
    // 定位长时间没有刷新时，不用旧坐标给离点后的新画面判归属。
    private const int DeparturePositionMaxAgeMs = 1500;
    private static readonly object Gate = new();
    private static PickupWaypointProgress? _active;

    private readonly PickupObtainedRowTracker _tracker = new();
    private readonly CancellationTokenSource _reached = new();
    private readonly int _target;
    private readonly CancellationToken _routeToken;
    private readonly ILogger? _logger;
    private readonly string? _pointLabel;
    private long _lastFrameTime;
    private long _lastDetectionLogTime;
    private bool _baselineLogged;
    private bool _departing;
    private long _departurePositionAt;
    private long _firstDeparturePositionAt;
    private bool _frameErrorReported;
    private bool _headerFallbackReported;
    private bool _missingHeaderReported;
    private int _count;

    private PickupWaypointProgress(int target, CancellationToken routeToken, ILogger? logger, string? pointLabel)
    {
        _target = target;
        _routeToken = routeToken;
        _logger = logger;
        _pointLabel = pointLabel;
        ReachedToken = _reached.Token;
    }

    internal CancellationToken ReachedToken { get; }

    internal int Count => Volatile.Read(ref _count);

    internal static PickupWaypointProgress? TryBegin(int target, CancellationToken routeToken = default,
        ILogger? logger = null, string? pointLabel = null)
    {
        lock (Gate)
        {
            // 只有一个节点能使用当前游戏画面，避免把同一条提示算进两个节点。
            if (_active != null || target <= 0 || routeToken.IsCancellationRequested)
                return null;

            var progress = new PickupWaypointProgress(target, routeToken, logger, pointLabel);
            Volatile.Write(ref _active, progress);
            return progress;
        }
    }

    internal static PickupWaypointProgress? Capture()
    {
        var active = Volatile.Read(ref _active);
        return active != null && !active._routeToken.IsCancellationRequested && !active.ReachedToken.IsCancellationRequested
            ? active : null;
    }

    internal bool BeginDeparture()
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_active, this) || _routeToken.IsCancellationRequested)
                return false;

            _departing = true;
            return true;
        }
    }

    internal bool ConfirmDeparturePosition()
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_active, this) || !_departing || _routeToken.IsCancellationRequested
                || (_departurePositionAt != 0 && !IsDeparturePositionFresh()))
                return false;

            _departurePositionAt = Stopwatch.GetTimestamp();
            if (_firstDeparturePositionAt == 0)
                _firstDeparturePositionAt = _departurePositionAt;
            return true;
        }
    }

    private bool IsDeparturePositionFresh() => _departurePositionAt != 0
        && Stopwatch.GetElapsedTime(_departurePositionAt).TotalMilliseconds <= DeparturePositionMaxAgeMs;

    internal void ObserveFrame(PickupRowObservation observation, PickupObtainedRowDetectionStats detection,
        long frameTime, double interval)
    {
        if (!ReferenceEquals(Capture(), this))
            return;

        try
        {
            // 截图或窗口长时间中断后先重新建立基线，期间出现的行不猜测归属。
            if (_lastFrameTime != 0 && Stopwatch.GetElapsedTime(_lastFrameTime, frameTime).TotalMilliseconds > Math.Max(300, interval * 3))
                Observe(PickupRowObservation.Interrupted);
            _lastFrameTime = frameTime;
            Observe(observation, detection, frameTime);
        }
        catch (Exception ex)
        {
            ReportFrameError(ex);
        }
    }

    internal void ReportFrameError(Exception ex)
    {
        Observe(PickupRowObservation.Interrupted);
        if (!_frameErrorReported)
        {
            _frameErrorReported = true;
            _logger?.LogWarning("【拾取达标】{Point} 获得列表检测异常，等待可用画面：{Reason}", _pointLabel, ex.Message);
        }
    }

    internal void Observe(PickupRowObservation observation, PickupObtainedRowDetectionStats detection = default,
        long frameTime = 0)
    {
        PickupRowUpdate update;
        bool establishedBaseline;
        int count;
        lock (Gate)
        {
            if (!ReferenceEquals(_active, this) || _count >= _target || _routeToken.IsCancellationRequested
                || (_departing && !observation.InterruptsContinuity
                    && (!IsDeparturePositionFresh() || (frameTime != 0 && frameTime < _firstDeparturePositionAt))))
                return;

            update = _tracker.Observe(observation);
            establishedBaseline = update.BaselineReady && !_baselineLogged;
            _baselineLogged |= establishedBaseline;
            if (update.AddedRows > 0)
            {
                _count += Math.Min(update.AddedRows, int.MaxValue - _count);
                if (_count >= _target)
                    _reached.Cancel();
            }
            count = _count;
        }

        if (detection.RowComponents is { } rowComponents)
        {
            if (!_headerFallbackReported && detection.HeaderTop is { } top
                && detection.OriginalHeaderComponents < detection.HeaderThreshold)
            {
                _headerFallbackReported = true;
                _logger?.LogInformation("【拾取达标】{Point} 获得标题用 {Match} 找到（Y={Top}，原框 {Original} 块，命中框 {Header} 块，阈值 {Threshold}）",
                    _pointLabel, detection.HeaderMatch, top, detection.OriginalHeaderComponents,
                    detection.HeaderComponents, detection.HeaderThreshold);
            }
            else if (!_missingHeaderReported && detection.HeaderTop == null
                && Array.Exists(rowComponents, components => components >= detection.RowThreshold))
            {
                _missingHeaderReported = true;
                _logger?.LogInformation("【拾取达标】{Point} 检测到物品行候选，但获得标题未确认（原框 {Original} 块，最佳候选 {Header} 块，阈值 {Threshold}），暂不计数",
                    _pointLabel, detection.OriginalHeaderComponents, detection.HeaderComponents, detection.HeaderThreshold);
            }

            if (_logger?.IsEnabled(LogLevel.Debug) == true
                && (establishedBaseline || update.AddedRows > 0 || update.Cleared || _lastDetectionLogTime == 0
                    || Stopwatch.GetElapsedTime(_lastDetectionLogTime).TotalMilliseconds >= 500))
            {
                _lastDetectionLogTime = Stopwatch.GetTimestamp();
                _logger.LogDebug("【拾取达标】{Point} RGB 容错 R={RedMin}–{RedMax} G={GreenMin}–{GreenMax} B={BlueMin}–{BlueMax} 四连通块：标题原框 {Original}，命中或最佳候选 {Header}（至少 {HeaderThreshold}，方式 {Match}，标题框 Y={Top}），各行 [{RowComponents}]（至少 {RowThreshold}），有效行 {VisibleRows}",
                    _pointLabel, PickupObtainedRowDetector.TextRedMin, PickupObtainedRowDetector.TextRedMax,
                    PickupObtainedRowDetector.TextGreenMin, PickupObtainedRowDetector.TextGreenMax,
                    PickupObtainedRowDetector.TextBlueMin, PickupObtainedRowDetector.TextBlueMax,
                    detection.OriginalHeaderComponents, detection.HeaderComponents, detection.HeaderThreshold,
                    detection.HeaderMatch ?? "未命中", detection.HeaderTop, string.Join(", ", rowComponents),
                    detection.RowThreshold, observation.IsAvailable ? observation.Rows?.Count : null);
            }
        }

        if (establishedBaseline)
            _logger?.LogInformation("【拾取达标】{Point} 获得列表基线已就绪，已有 {Rows} 行不计，只计新增行",
                _pointLabel, observation.Rows?.Count ?? 0);
        if (update.Cleared)
            _logger?.LogInformation("【拾取达标】{Point} 获得列表已消失，本点累计保留 {Count}/{Target}",
                _pointLabel, count, _target);
        if (update.AddedRows > 0)
            _logger?.LogInformation("【拾取达标】{Point} 获得列表新增 {Rows} 行，累计 {Count}/{Target}",
                _pointLabel, update.AddedRows, count, _target);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (!ReferenceEquals(_active, this))
                return;

            Volatile.Write(ref _active, null);
            _reached.Dispose();
        }
    }
}
