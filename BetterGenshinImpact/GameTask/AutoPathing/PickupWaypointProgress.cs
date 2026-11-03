using System;
using System.Threading;

namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>一次到点动作的拾取日志计数。实时拾取触发器可能在另一条线程上报告。</summary>
internal sealed class PickupWaypointProgress : IDisposable
{
    private static readonly object Gate = new();
    private static PickupWaypointProgress? _active;

    private readonly CancellationTokenSource _reached = new();
    private readonly int _target;
    private readonly object? _scriptSource;
    private int _count;

    private PickupWaypointProgress(int target, object? scriptSource)
    {
        _target = target;
        _scriptSource = scriptSource;
        ReachedToken = _reached.Token;
    }

    internal CancellationToken ReachedToken { get; }

    internal int Count => Volatile.Read(ref _count);

    internal static PickupWaypointProgress? TryBegin(int target, object? scriptSource)
    {
        lock (Gate)
        {
            // 原生拾取触发器共享同一游戏窗口，不能把一条事件分发给两个节点。
            if (_active != null || target <= 0)
                return null;

            var progress = new PickupWaypointProgress(target, scriptSource);
            Volatile.Write(ref _active, progress);
            return progress;
        }
    }

    internal static PickupWaypointProgress? Capture(object? source = null)
    {
        var active = Volatile.Read(ref _active);
        return active != null && (source == null || ReferenceEquals(source, active._scriptSource))
            ? active
            : null;
    }

    internal void ReportHere(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName) || itemName.Trim() == "调查")
            return;

        lock (Gate)
        {
            if (!ReferenceEquals(_active, this) || _count >= _target)
                return;

            if (++_count == _target)
                _reached.Cancel();
        }
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
