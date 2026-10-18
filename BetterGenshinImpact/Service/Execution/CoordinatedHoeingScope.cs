using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// Evidence belonging to one dispatched job. Started/Active cover every native hoeing invocation;
/// Completed counts only invocations whose actual multiplayer setting was true and which returned
/// successfully. Only a frozen root-group plan can additionally bound future native invocations.
/// JobResult is the independent result of the complete group/one-dragon invocation.
/// </summary>
public sealed record CoordinatedHoeingEvidence(int Started, int Completed, int Failed, int Cancelled,
    int Active, bool Sealed, TaskRunResult? JobResult, long Revision = 0, int? PlannedNativeCount = null);

/// <summary>Flows with one dispatched job, including nested script/one-dragon tasks; never a process switch.</summary>
public sealed class CoordinatedHoeingScope : IDisposable
{
    private static readonly AsyncLocal<CoordinatedHoeingScope?> Ambient = new();
    private readonly CoordinatedHoeingScope? _previous = Ambient.Value;
    private readonly object _gate = new();
    private readonly object _publishGate = new();
    private readonly Action<CoordinatedHoeingEvidence>? _observer;
    private CoordinatedHoeingEvidence _evidence = new(0, 0, 0, 0, 0, false, null);
    private long _publishedRevision = -1;
    private bool _nativePlanRegistered;
    private bool _incomplete;
    private bool _disposed;
    public bool Incomplete { get { lock (_gate) return _incomplete; } }
    public string? BatchId { get; }
    public IReadOnlyList<string> ExpectedParticipantUids { get; }
    public static CoordinatedHoeingScope? Current => Ambient.Value;
    public static bool IsActive => Ambient.Value != null;
    public static void MarkIncomplete()
    {
        if (Ambient.Value is not { } scope) return;
        lock (scope._gate) if (!scope._disposed && !scope._evidence.Sealed) scope._incomplete = true;
    }
    public CoordinatedHoeingScope(bool enabled, string? batchId = null,
        IEnumerable<string>? expectedParticipantUids = null, Action<CoordinatedHoeingEvidence>? observer = null)
    {
        _observer = enabled ? observer : null;
        BatchId = batchId;
        ExpectedParticipantUids = Array.AsReadOnly((expectedParticipantUids ?? Array.Empty<string>())
            .Where(uid => !string.IsNullOrWhiteSpace(uid)).Select(uid => uid.Trim())
            .Distinct(StringComparer.Ordinal).ToArray());
        if (enabled) Ambient.Value = this;
        Publish(_evidence);
    }

    public CoordinatedHoeingEvidence Snapshot { get { lock (_gate) return _evidence; } }

    /// <summary>Freeze only the first root-group plan, before any native invocation starts.</summary>
    public bool TryRegisterNativePlan(int? plannedNativeCount)
    {
        CoordinatedHoeingEvidence snapshot;
        lock (_gate)
        {
            if (_disposed || _evidence.Sealed || _nativePlanRegistered || _evidence.Started != 0
                || _evidence.Active != 0 || plannedNativeCount is < 0) return false;
            _nativePlanRegistered = true;
            if (_evidence.PlannedNativeCount == plannedNativeCount) return true;
            snapshot = _evidence = _evidence with
            {
                PlannedNativeCount = plannedNativeCount, Revision = _evidence.Revision + 1
            };
        }
        Publish(snapshot);
        return true;
    }

    /// <summary>Dynamic/nested work cannot replace a root plan or restore its former certainty.</summary>
    public void InvalidateNativePlan()
    {
        CoordinatedHoeingEvidence snapshot;
        lock (_gate)
        {
            if (_disposed || _evidence.Sealed) return;
            _nativePlanRegistered = true;
            if (_evidence.PlannedNativeCount is null) return;
            snapshot = _evidence = _evidence with
            {
                PlannedNativeCount = null, Revision = _evidence.Revision + 1
            };
        }
        Publish(snapshot);
    }

    /// <summary>Open before configuration/resource validation, so an early failure cannot disappear.</summary>
    public NativeAttempt BeginNative()
    {
        CoordinatedHoeingEvidence snapshot;
        NativeAttempt attempt;
        lock (_gate)
        {
            if (_disposed || _evidence.Sealed) return new NativeAttempt(null);
            attempt = new NativeAttempt(this);
            snapshot = _evidence = _evidence with
            {
                Started = _evidence.Started + 1, Active = _evidence.Active + 1,
                Revision = _evidence.Revision + 1
            };
        }
        Publish(snapshot);
        return attempt;
    }

    /// <summary>Called only with the complete dispatched job's actual result; never by a native task.</summary>
    public CoordinatedHoeingEvidence Seal(TaskRunResult result)
    {
        CoordinatedHoeingEvidence snapshot;
        lock (_gate)
        {
            if (_disposed || _evidence.Sealed) return _evidence;
            snapshot = _evidence = _evidence with
            {
                Sealed = true, JobResult = result, Revision = _evidence.Revision + 1
            };
        }
        Publish(snapshot);
        return snapshot;
    }

    private void EndNative(NativeAttempt attempt, bool failed, bool cancelled)
    {
        CoordinatedHoeingEvidence snapshot;
        lock (_gate)
        {
            if (attempt.Ended || _disposed || _evidence.Sealed) return;
            attempt.Ended = true;
            snapshot = _evidence = _evidence with
            {
                Active = _evidence.Active - 1,
                Completed = _evidence.Completed + (!failed && !cancelled && attempt.Multiplayer == true ? 1 : 0),
                Failed = _evidence.Failed + (failed ? 1 : 0),
                Cancelled = _evidence.Cancelled + (cancelled ? 1 : 0),
                Revision = _evidence.Revision + 1
            };
        }
        Publish(snapshot);
    }

    private void Publish(CoordinatedHoeingEvidence snapshot)
    {
        if (_observer == null) return;
        // Concurrent async continuations may publish out of order. Drop old revisions, and never
        // allow an observation failure to change the game's execution result.
        lock (_publishGate)
        {
            if (snapshot.Revision <= _publishedRevision) return;
            _publishedRevision = snapshot.Revision;
            try { _observer(snapshot); } catch { }
        }
    }

    public sealed class NativeAttempt : IDisposable
    {
        private readonly CoordinatedHoeingScope? _owner;
        internal bool Ended;
        internal bool? Multiplayer;
        internal NativeAttempt(CoordinatedHoeingScope? owner) => _owner = owner;
        public void SetMultiplayerMode(bool enabled)
        {
            if (_owner == null) return;
            lock (_owner._gate)
                if (!Ended && !_owner._disposed && !_owner._evidence.Sealed) Multiplayer = enabled;
        }
        public void Complete() => _owner?.EndNative(this, false, false);
        public void Fail() => _owner?.EndNative(this, true, false);
        public void Cancel() => _owner?.EndNative(this, false, true);
        // An abandoned invocation carries no success proof.
        public void Dispose() => Fail();
    }

    public void Dispose()
    {
        CoordinatedHoeingEvidence snapshot;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            // An exception escaping the dispatcher before Seal must remain an unknown job result.
            snapshot = _evidence = _evidence.Sealed ? _evidence : _evidence with
            {
                Sealed = true, Revision = _evidence.Revision + 1
            };
        }
        Ambient.Value = _previous;
        Publish(snapshot);
    }
}

public sealed class HoeingIncompleteException() : InvalidOperationException("hoeing_incomplete: 联机世界尚未全部完成");
