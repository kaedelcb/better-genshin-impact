using System;
using System.Diagnostics;
using System.Threading;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using Mat = OpenCvSharp.Mat;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>一次到点动作的获得列表计数，帧由现有截图调度器提供。</summary>
internal sealed class PickupWaypointProgress : IDisposable
{
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
    private bool _counting;
    private long _countingStartedAt;
    private bool _disposed;

    private PickupWaypointProgress(int target, CancellationToken routeToken, ILogger? logger, string? pointLabel, bool counting)
    {
        _target = target;
        _counting = counting;
        _routeToken = routeToken;
        _logger = logger;
        _pointLabel = pointLabel;
        ReachedToken = _reached.Token;
    }

    internal CancellationToken ReachedToken { get; }

    internal int Count => Volatile.Read(ref _count);

    internal bool IsDisposed => Volatile.Read(ref _disposed);

    internal bool ActivateCounting()
    {
        lock (Gate)
        {
            if (_counting || _disposed || !ReferenceEquals(_active, this) || _routeToken.IsCancellationRequested)
                return false;

            _counting = true;
            _countingStartedAt = Stopwatch.GetTimestamp();
            return true;
        }
    }

    internal bool IsOwnCancellation(OperationCanceledException error, CancellationToken operationToken,
        CancellationToken routeToken)
        => !routeToken.IsCancellationRequested && ReachedToken.IsCancellationRequested
           && (error.CancellationToken == operationToken || error.CancellationToken == ReachedToken);

    internal void LogPointCompleted(bool departing)
        => _logger?.LogInformation("【拾取达标】{Point} 计数 {Count}/{Target}：{Result}",
            _pointLabel, Count, _target, ReachedToken.IsCancellationRequested ? "达标，已完成动作收尾和松键"
                : departing ? "未达标，继续观察首个补捡路段" : "未达标，后续点照常执行");

    internal static PickupWaypointProgress? TryBegin(int target, CancellationToken routeToken = default,
        ILogger? logger = null, string? pointLabel = null, bool counting = true)
    {
        lock (Gate)
        {
            // 只有一个节点能使用当前游戏画面，避免把同一条提示算进两个节点。
            if (_active != null || target <= 0 || routeToken.IsCancellationRequested)
                return null;

            var progress = new PickupWaypointProgress(target, routeToken, logger, pointLabel, counting);
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
            if (!ReferenceEquals(_active, this) || !_departing || _routeToken.IsCancellationRequested)
                return false;

            _departurePositionAt = Stopwatch.GetTimestamp();
            if (_firstDeparturePositionAt == 0)
                _firstDeparturePositionAt = _departurePositionAt;
            return true;
        }
    }

    private bool HasDeparturePosition() => _departurePositionAt != 0;

    internal (PickupRowObservation Observation, PickupObtainedRowDetectionStats Detection)? SampleRawFrame(Mat bitmap)
    {
        try
        {
            var observation = PickupObtainedRowDetector.Detect(bitmap, out var detection);
            return (observation, detection);
        }
        catch (Exception ex)
        {
            ReportFrameError(ex);
            return null;
        }
    }

    internal void AcceptSample(
        (PickupRowObservation Observation, PickupObtainedRowDetectionStats Detection)? sample,
        ImageRegion gameView, bool interrupted, long frameTime, double interval)
    {
        try
        {
            if (interrupted)
                Observe(PickupRowObservation.Interrupted);
            else if (sample is { } rawFrame)
            {
                if (Bv.IsInMainUi(gameView))
                    ObserveFrame(rawFrame.Observation, rawFrame.Detection, frameTime, interval);
                else
                    Observe(PickupRowObservation.Interrupted);
            }
        }
        catch (Exception ex)
        {
            ReportFrameError(ex);
        }
    }

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
        bool counting;
        int count;
        lock (Gate)
        {
            if (!ReferenceEquals(_active, this) || _count >= _target || _routeToken.IsCancellationRequested
                || (_counting && frameTime != 0 && _countingStartedAt != 0 && frameTime < _countingStartedAt)
                || (_departing && !observation.InterruptsContinuity
                    && (!HasDeparturePosition() || (frameTime != 0 && frameTime < _firstDeparturePositionAt))))
                return;

            update = _tracker.Observe(observation);
            establishedBaseline = update.BaselineReady && !_baselineLogged;
            _baselineLogged |= establishedBaseline;
            if (_counting && update.AddedRows > 0)
            {
                _count += Math.Min(update.AddedRows, int.MaxValue - _count);
                if (_count >= _target)
                    _reached.Cancel();
            }
            count = _count;
            counting = _counting;
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
        if (counting && update.AddedRows > 0)
            _logger?.LogInformation("【拾取达标】{Point} 获得列表新增 {Rows} 行，累计 {Count}/{Target}",
                _pointLabel, update.AddedRows, count, _target);
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (_disposed)
                return;

            _disposed = true;
            if (ReferenceEquals(_active, this))
                Volatile.Write(ref _active, null);
            _reached.Dispose();
        }
    }
}
