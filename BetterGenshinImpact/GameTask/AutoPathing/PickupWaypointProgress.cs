using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>一次到点动作的获得列表计数，帧由现有截图调度器提供。</summary>
internal sealed class PickupWaypointProgress : IDisposable
{
    private static readonly object Gate = new();
    private static PickupWaypointProgress? _active;

    private readonly PickupObtainedRowTracker _tracker = new();
    private readonly TaskCompletionSource _baseline = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _reached = new();
    private readonly int _target;
    private readonly CancellationToken _routeToken;
    private readonly ILogger? _logger;
    private readonly string? _pointLabel;
    private long _lastFrameTime;
    private bool _frameErrorReported;
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

    internal async Task<bool> WaitForBaselineAsync(CancellationToken ct)
    {
        try
        {
            await _baseline.Task.WaitAsync(TimeSpan.FromMilliseconds(400), ct);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    internal void ObserveFrame(Mat frame, long frameTime, double interval)
    {
        if (!ReferenceEquals(Capture(), this))
            return;

        try
        {
            // 截图或窗口长时间中断后先重新建立基线，期间出现的行不猜测归属。
            if (_lastFrameTime != 0 && Stopwatch.GetElapsedTime(_lastFrameTime, frameTime).TotalMilliseconds > Math.Max(300, interval * 3))
                Observe(PickupRowObservation.Interrupted);
            _lastFrameTime = frameTime;
            Observe(PickupObtainedRowDetector.Detect(frame));
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

    internal void Observe(PickupRowObservation observation)
    {
        PickupRowUpdate update;
        bool establishedBaseline;
        int count;
        lock (Gate)
        {
            if (!ReferenceEquals(_active, this) || _count >= _target || _routeToken.IsCancellationRequested)
                return;

            update = _tracker.Observe(observation);
            establishedBaseline = update.BaselineReady && _baseline.TrySetResult();
            if (update.AddedRows > 0)
            {
                _count += Math.Min(update.AddedRows, int.MaxValue - _count);
                if (_count >= _target)
                    _reached.Cancel();
            }
            count = _count;
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
