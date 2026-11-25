using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.GameTask.AutoOnline;

/// <summary>进程内单槽：只等待本机助手的接收确认，不等待切房或联机批次的结果。</summary>
internal static class RoomActionMailbox
{
    private static readonly TimeSpan ReceiptTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReceiptRetention = TimeSpan.FromSeconds(30);
    private const int ReceiptLimit = 16;
    private static readonly object Sync = new();
    private static readonly Queue<Receipt> Receipts = new();
    private static Peer? _peer;
    private static Delivery? _pending;

    internal sealed record Signal(Guid Id, Guid RunId, string Kind, string? TargetRoomName, long StopVersion,
        Guid? JobId = null, string? SubmissionKey = null, Guid? WorkflowRunId = null);
    internal sealed record Outcome(Guid? PeerId, string? Error)
    {
        internal bool Accepted => Error is null;
    }

    private sealed record Peer(Guid Id, CancellationToken Lifetime);
    private sealed record Receipt(Guid Id, long StopVersion, bool Accepted, long AcknowledgedAt);

    internal sealed class Delivery(Signal signal, CancellationToken cancellation)
    {
        internal Signal Signal { get; } = signal;
        internal CancellationToken Cancellation { get; } = cancellation;
        internal long StartedAt { get; } = Stopwatch.GetTimestamp();
        internal Guid? PeerId { get; set; }
        internal bool Offered { get; set; }
        internal TaskCompletionSource<Outcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static Delivery Post(Signal signal, Guid? requiredPeer, CancellationToken cancellation)
    {
        // Do not nest ExecutionScope's lock inside Sync: a targeted suspend can consult the mailbox.
        var stopVersion = ExecutionScope.StopVersionNow;
        var delivery = new Delivery(signal, cancellation);
        lock (Sync)
        {
            RetireInvalidLocked(stopVersion);
            var peer = _peer;
            string? error = signal.StopVersion != stopVersion || cancellation.IsCancellationRequested
                ? "原配置组已停止或让位" : requiredPeer is not null && requiredPeer != peer?.Id
                ? "原助手 IPC 会话已断开" : peer is null ? "本机助手房间信号通道尚未就绪"
                : _pending is not null ? "另一条房间信号正在等待本机确认" : null;
            if (error is not null)
                delivery.Completion.SetResult(new(null, error));
            else
            {
                // 即使是第一条，也只能交给发出时已知的会话，不能被之后启动的助手消费。
                delivery.PeerId = peer!.Id;
                _pending = delivery;
            }
            return delivery;
        }
    }

    internal static async Task<Outcome> WaitAsync(Delivery delivery)
    {
        try
        {
            return await delivery.Completion.Task.WaitAsync(ReceiptTimeout, delivery.Cancellation).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return Close(delivery, "等待本机助手接收超时，已停止后续房间信号");
        }
        catch (OperationCanceledException)
        {
            return Close(delivery, "原配置组已停止或让位，房间信号接收未确认");
        }
    }

    // If ACK won the race with timeout or cancellation, retain that exact receipt.
    private static Outcome Close(Delivery delivery, string reason)
    {
        lock (Sync)
        {
            if (ReferenceEquals(_pending, delivery)) FinishLocked(new(null, reason));
            return delivery.Completion.Task.GetAwaiter().GetResult();
        }
    }

    internal static bool IsPeerCurrent(Guid id)
    {
        lock (Sync) return _peer is { } peer && peer.Id == id && !peer.Lifetime.IsCancellationRequested;
    }

    internal static bool TryPeek(Guid sessionId, CancellationToken lifetime, out Signal? signal)
    {
        var stopVersion = ExecutionScope.StopVersionNow;
        lock (Sync)
        {
            signal = null;
            RetireInvalidLocked(stopVersion);
            if (lifetime.IsCancellationRequested || _peer is { } current && current.Id != sessionId) return false;
            if (_peer is null)
            {
                var peer = new Peer(sessionId, lifetime);
                _peer = peer;
                lifetime.UnsafeRegister(static state => Disconnect((Peer)state!), peer);
                if (lifetime.IsCancellationRequested) return false;
            }

            if (_pending is { } pending && pending.PeerId == sessionId && pending.Signal.StopVersion == stopVersion)
            {
                pending.Offered = true;
                signal = pending.Signal;
            }
        }
        // Do not transmit a stale offer if F11 raced the snapshot.
        if (signal is not null && signal.StopVersion != ExecutionScope.StopVersionNow) signal = null;
        return true;
    }

    internal static bool Acknowledge(Guid sessionId, Guid id, bool accepted)
    {
        var stopVersion = ExecutionScope.StopVersionNow;
        lock (Sync)
        {
            RetireInvalidLocked(stopVersion);
            if (_peer?.Id != sessionId) return false;
            if (_pending is { Offered: true } pending && pending.Signal.Id == id
                && pending.PeerId == sessionId && pending.Signal.StopVersion == stopVersion)
            {
                Receipts.Enqueue(new(id, stopVersion, accepted, Stopwatch.GetTimestamp()));
                if (Receipts.Count > ReceiptLimit) Receipts.Dequeue();
                FinishLocked(new(sessionId, accepted ? null : "本机助手拒绝此房间信号"));
            }
            else if (!System.Linq.Enumerable.Any(Receipts, r => r.Id == id && r.StopVersion == stopVersion && r.Accepted == accepted))
            {
                return false;
            }
        }
        return stopVersion == ExecutionScope.StopVersionNow && IsPeerCurrent(sessionId);
    }

    private static void Disconnect(Peer peer)
    {
        lock (Sync)
        {
            if (!ReferenceEquals(_peer, peer)) return;
            _peer = null;
            Receipts.Clear();
            if (_pending?.PeerId == peer.Id) FinishLocked(new(null, "原助手 IPC 会话已断开"));
        }
    }

    private static void RetireInvalidLocked(long stopVersion)
    {
        if (_peer is { Lifetime.IsCancellationRequested: true } peer) Disconnect(peer);
        if (_pending is { } pending)
        {
            if (pending.Signal.StopVersion < stopVersion || pending.Cancellation.IsCancellationRequested)
                FinishLocked(new(null, "原配置组已停止或让位"));
            else if (Stopwatch.GetElapsedTime(pending.StartedAt) >= ReceiptTimeout)
                FinishLocked(new(null, "等待本机助手接收超时，已停止后续房间信号"));
        }
        while (Receipts.TryPeek(out var receipt) && (receipt.StopVersion < stopVersion
                    || Stopwatch.GetElapsedTime(receipt.AcknowledgedAt) >= ReceiptRetention))
            Receipts.Dequeue();
    }

    private static void FinishLocked(Outcome outcome)
    {
        var pending = _pending!;
        _pending = null;
        pending.Completion.SetResult(outcome);
    }
}
