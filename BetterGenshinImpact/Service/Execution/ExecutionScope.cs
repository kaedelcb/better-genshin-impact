using System;
using System.Threading;
using BetterGenshinImpact.GameTask;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>One logical root owns admission across leaf-task gaps. Never owns TaskSemaphore.</summary>
public sealed class ExecutionScope : IDisposable
{
    private static readonly object Sync = new();
    private static readonly AsyncLocal<ExecutionScope?> Ambient = new();
    private static readonly AsyncLocal<string?> AdmissionTicket = new();
    private static ExecutionScope? _active;
    private static long _stopVersion;
    private readonly ExecutionScope? _previous;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _configurationRevisions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    private readonly Timer? _leaseWatch;
    public string StopReason { get; private set; } = JobErrorCodes.CancelledUser;
    public Guid RunId { get; }
    public JobDescriptor Descriptor { get; }
    public long StopVersion { get; }
    public CancellationToken Token => _stop.Token;
    public TaskRunResult Result { get; private set; } = TaskRunResult.Ran;
    public int FailureCount { get; private set; }
    private int _dragonIndex;
    internal SuspendContextCapture.Snapshot? Checkpoint { get; private set; }
    public static ExecutionScope? Current => Ambient.Value;
    public static bool HasActive { get { lock (Sync) return _active != null; } }
    public static long StopVersionNow { get { lock (Sync) return _stopVersion; } }

    private ExecutionScope(JobDescriptor descriptor)
    {
        Descriptor = descriptor;
        RunId = descriptor.WorkflowRunId ?? Guid.NewGuid();
        StopVersion = _stopVersion;
        _previous = Ambient.Value;
        Ambient.Value = this;
        if (descriptor.TakeoverTicket != null && descriptor.Source != JobSource.Resume)
            _leaseWatch = new Timer(_ =>
            {
                if (!PreemptionGate.Authorize(descriptor.TakeoverTicket))
                {
                    StopReason = "lease_expired";
                    Observe(TaskRunResult.Cancelled);
                    try { _stop.Cancel(); } catch (AggregateException) { }
                }
            }, null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    public static ExecutionScope Start(JobDescriptor descriptor)
    {
        descriptor = descriptor.TakeoverTicket == null && AdmissionTicket.Value != null
            ? descriptor with { TakeoverTicket = AdmissionTicket.Value } : descriptor;
        ExecutionScope scope;
        lock (Sync)
        {
            if (_active != null) throw new InvalidOperationException("task_busy: 另一流程尚未退出");
            if (!PreemptionGate.Authorize(descriptor.TakeoverTicket))
                throw new InvalidOperationException("takeover_conflict: 执行权属于另一批次或票据已失效");
            scope = new ExecutionScope(descriptor);
            _active = scope;
        }
        try
        {
            scope.ThrowIfStopped();
            descriptor.OnAdmitted?.Invoke();
            return scope;
        }
        catch { scope.Dispose(); throw; }
    }

    // Carries only the validated invocation's authority through async hotkey callbacks.
    // It does not grant ownership to unrelated UI events or bypass ticket revocation.
    public static IDisposable UseTakeoverTicket(string? ticket)
    {
        if (!PreemptionGate.Authorize(ticket)) throw new InvalidOperationException("takeover_conflict");
        var previous = AdmissionTicket.Value;
        AdmissionTicket.Value = ticket;
        return new AdmissionRestore(previous);
    }
    private sealed class AdmissionRestore(string? previous) : IDisposable
    {
        public void Dispose() => AdmissionTicket.Value = previous;
    }

    public void Cancel()
    {
        lock (Sync)
        {
            if (_disposed) return;
            Observe(TaskRunResult.Preempted);
        }
        try { _stop.Cancel(); } catch (AggregateException) { }
    }

    public bool IsCurrentOwner { get { lock (Sync) return !_disposed && ReferenceEquals(_active, this)
        && StopVersion == _stopVersion && !_stop.IsCancellationRequested
        && Result is not (TaskRunResult.Cancelled or TaskRunResult.Preempted); } }
    public void ThrowIfStopped()
    {
        if (!IsCurrentOwner) throw new OperationCanceledException("根流程已停止或已让位", Token);
    }
    public void Observe(TaskRunResult result)
    {
        lock (Sync)
        {
            if (result == TaskRunResult.Failed) FailureCount++;
            if (Result == TaskRunResult.Ran || result is TaskRunResult.Cancelled or TaskRunResult.Preempted)
                Result = result;
        }
    }
    internal void SetCheckpoint(SuspendContextCapture.Snapshot snapshot)
    {
        lock (Sync)
        {
            if (!IsCurrentOwner) return;
            Checkpoint = Descriptor.Kind == JobKind.OneDragon
                ? snapshot with { TaskType = "onedragon", GroupName = Descriptor.Name,
                    OneDragonTaskIndex = _dragonIndex, SubTaskGroupName = snapshot.TaskType == "group" ? snapshot.GroupName : null }
                : snapshot;
            Checkpoint = Checkpoint with { RootRunId = RunId, AttemptId = Descriptor.JobId,
                NodeId = Descriptor.NodeId, Iteration = Descriptor.Iteration,
                TaskId = Descriptor.TaskId, ConfigRevision = Descriptor.ConfigRevision,
                ConfigurationRevisions = new System.Collections.Generic.Dictionary<string, string>(_configurationRevisions) };
        }
    }
    internal void TrackConfigurationFile(string path)
    {
        var revision = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(path)));
        lock (Sync)
        {
            _configurationRevisions.TryAdd(path, revision);
            if (Checkpoint != null) Checkpoint = Checkpoint with
                { ConfigurationRevisions = new System.Collections.Generic.Dictionary<string, string>(_configurationRevisions) };
        }
    }
    public void SetDragonNode(int index)
    {
        lock (Sync)
        {
            _dragonIndex = index;
            SetCheckpoint(new SuspendContextCapture.Snapshot("onedragon", Descriptor.Name, 0, null, null, index, null, null, false));
        }
    }
    internal static SuspendContextCapture.Snapshot? Capture()
    {
        lock (Sync) return _active?.IsCurrentOwner == true ? _active.Checkpoint : null;
    }
    internal static SuspendContextCapture.Snapshot? Suspend()
    {
        ExecutionScope? active;
        SuspendContextCapture.Snapshot? checkpoint;
        lock (Sync)
        {
            active = _active;
            checkpoint = active?.IsCurrentOwner == true ? active.Checkpoint : null;
            active?.Observe(TaskRunResult.Preempted);
        }
        try { active?._stop.Cancel(); } catch (AggregateException) { }
        return checkpoint;
    }
    public static void StopActive(bool manual)
    {
        ExecutionScope? active;
        lock (Sync)
        {
            if (manual) _stopVersion++;
            active = _active;
            active?.Observe(manual ? TaskRunResult.Cancelled : TaskRunResult.Preempted);
        }
        // Cancellation callbacks can reenter execution; never invoke them under the admission lock.
        try { active?._stop.Cancel(); } catch (AggregateException) { }
    }
    public void Dispose()
    {
        lock (Sync)
        {
            _disposed = true;
            if (ReferenceEquals(_active, this)) _active = null;
        }
        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
        _leaseWatch?.Dispose();
        // Do not dispose: in-flight token registrations may still unwind after root cancellation.
    }
}
