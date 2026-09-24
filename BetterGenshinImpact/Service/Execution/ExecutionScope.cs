using System;
using System.Collections.Generic;
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
    private static long _stateRevisionCounter;
    /// <summary>[R5 A4 第二步] 执行根**释放顺序号**：在根锁内分配，作为退出凭证的线性化顺序（防迟到旧记录覆盖继任根）。</summary>
    private static long _exitOrderCounter;
    private readonly ExecutionScope? _previous;
    private readonly CancellationTokenSource _stop = new();
    private readonly System.Collections.Generic.Dictionary<string, string> _configurationRevisions = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;
    /// <summary>本次执行根已**被接纳并交给调用方**（Start 成功返回）。只有这类根的生命周期结束才产生退出凭证。</summary>
    private bool _admitted;
    private bool _stopRequested;
    /// <summary>[R5 A4 第二步] 停止/让位的**来源归属**：只有确实由某个入口发起时才赋值，未发起时为 null。
    /// 与 <see cref="StopReason"/> 分开——后者是既有字段（默认 CancelledUser），不能被当作退出凭证的真实原因。</summary>
    private string? _stopAttribution;
    /// <summary>[R5 A4 第二步] 是否观测到过任何终态（<see cref="Observe"/> 至少调用一次）；未观测时凭证不得报结果。</summary>
    private bool _outcomeObserved;
    private readonly Timer? _leaseWatch;
    public string StopReason { get; private set; } = JobErrorCodes.CancelledUser;
    /// <summary>此根作用域的一次性身份；RunId 是工作流身份，可能被多个执行尝试共享。</summary>
    public Guid ExecutionInstanceId { get; } = Guid.NewGuid();
    /// <summary>此执行实例最近一次状态变化的单调修订号，用于调用方 CAS 绑定。</summary>
    private long StateRevision { get; set; }
    public Guid RunId { get; }
    public JobDescriptor Descriptor { get; }

    /// <summary>R4.6 D10/E4'：本次调用收尾权限抑制（随描述符，恢复现场经 checkpoint 携带）。</summary>
    public bool SuppressConfigCompletionAction => Descriptor.SuppressConfigCompletionAction;

    /// <summary>R4.6 E2-9：执行权取得后复验的期望 UID（null=不校验）。</summary>
    public string? ExpectedUid => Descriptor.ExpectedUid;
    public long StopVersion { get; }
    public CancellationToken Token => _stop.Token;
    public TaskRunResult Result { get; private set; } = TaskRunResult.Ran;
    public int FailureCount { get; private set; }
    private string? _dragonTaskId;
    private string? _dragonConfigSnapshotJson;
    internal SuspendContextCapture.Snapshot? Checkpoint { get; private set; }
    public static ExecutionScope? Current => Ambient.Value;
    public static bool HasActive { get { lock (Sync) return _active != null; } }
    public static long StopVersionNow { get { lock (Sync) return _stopVersion; } }

    /// <summary>
    /// 当前逻辑执行根的原子只读快照。它不声称某个 TaskSemaphore 裸锁持有者的身份；
    /// 若任务槽占用但此快照为空，消费者必须将执行身份视为未知。
    /// </summary>
    public static ExecutionScopeSnapshot? GetActiveSnapshot()
    {
        lock (Sync)
        {
            var active = _active;
            if (active is null || active._disposed) return null;
            return new ExecutionScopeSnapshot(active.ExecutionInstanceId, active.StateRevision, active.RunId,
                active.Descriptor.JobId, active.Descriptor.Kind, active.Descriptor.Source,
                active.Descriptor.Name, active._stopRequested, active.Result);
        }
    }

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
                    lock (Sync)
                    {
                        if (_disposed) return;
                        StopReason = "lease_expired";
                        Observe(TaskRunResult.Cancelled);
                        MarkStopRequestedLocked("lease_expired");
                    }
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
            if (descriptor.ExpectedStopVersion is { } expectedStopVersion && expectedStopVersion != _stopVersion)
                throw new OperationCanceledException("启动前已被用户停止");
            if (_active != null) throw new InvalidOperationException("task_busy: 另一流程尚未退出");
            if (!PreemptionGate.Authorize(descriptor.TakeoverTicket))
                throw new InvalidOperationException("takeover_conflict: 执行权属于另一批次或票据已失效");
            scope = new ExecutionScope(descriptor);
            _active = scope;
            scope.AdvanceStateRevisionLocked();
        }
        try
        {
            scope.ThrowIfStopped();
            descriptor.OnAdmitted?.Invoke();
        }
        catch { scope.Dispose(); throw; }
        // [R5 A4 第二步] 接纳握手在根锁内闭合：接纳期间已被释放的根不得再交付（也不产生退出凭证，它从未被交付使用）。
        lock (Sync)
        {
            if (scope._disposed)
            {
                // 该根可能是在**继承上下文的子任务**里被释放的（Dispose 只在那个上下文恢复 Ambient）。
                // 拒绝交付时必须先恢复本上下文的悬挂引用，否则父上下文会一直指向已释放的根。
                if (ReferenceEquals(Ambient.Value, scope)) Ambient.Value = scope._previous;
                throw new InvalidOperationException("task_busy: 执行根在接纳期间已被释放");
            }
            scope._admitted = true;
        }
        return scope;
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
            MarkStopRequestedLocked("cancel_requested");
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
            if (_disposed) return;
            _outcomeObserved = true;
            var previousResult = Result;
            if (result == TaskRunResult.Failed) FailureCount++;
            // R4.7 D6 聚合：Cancelled/Preempted 始终覆盖；Failed 覆盖 Ran/Skipped（失败不被跳过覆盖）；
            // Skipped 只覆盖 Ran（正常跳过分别表达，既不算成功执行也不算失败）。
            if (Result == TaskRunResult.Ran
                || (Result == TaskRunResult.Skipped && result == TaskRunResult.Failed)
                || result is TaskRunResult.Cancelled or TaskRunResult.Preempted)
                Result = result;
            if (Result != previousResult) AdvanceStateRevisionLocked();
        }
    }
    internal void SetCheckpoint(SuspendContextCapture.Snapshot snapshot)
    {
        lock (Sync)
        {
            if (!IsCurrentOwner) return;
            Checkpoint = Descriptor.Kind == JobKind.OneDragon
                ? snapshot with { TaskType = "onedragon", GroupName = Descriptor.Name,
                    OneDragonTaskId = _dragonTaskId, SubTaskGroupName = snapshot.TaskType == "group" ? snapshot.GroupName : null }
                : snapshot;
            Checkpoint = Checkpoint with { RootRunId = RunId, AttemptId = Descriptor.JobId,
                NodeId = Descriptor.NodeId, Iteration = Descriptor.Iteration,
                TaskId = Descriptor.TaskId, ConfigRevision = Descriptor.ConfigRevision,
                SuppressCompletionAction = Descriptor.SuppressConfigCompletionAction, // R4.6 B6：抑制权限随恢复现场携带
                Occurrence = Descriptor.Occurrence, Attempt = Descriptor.Attempt, // R4.6 B1
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
    /// <summary>
    /// R3.2 参数快照：本次龙执行冻结的配置 JSON（修订守卫后的执行副本）。
    /// 间接读配置的辅助任务（合成/尘歌壶）经 DragonConfigSnapshotJson 取同一作用域上下文，
    /// 不再跟全局 UI 选择项、也不读执行期间的磁盘新改动。独立运行（无快照）保持公版原行为。
    /// </summary>
    public string? DragonConfigSnapshotJson { get { lock (Sync) return _dragonConfigSnapshotJson; } }
    public void SetDragonConfigSnapshot(string configJson)
    {
        lock (Sync) _dragonConfigSnapshotJson = configJson;
    }
    /// <summary>R3 原生身份：龙内水位记录任务项稳定字符串 ID（GUID），不再是数字键。</summary>
    public void SetDragonNode(string taskId)
    {
        lock (Sync)
        {
            _dragonTaskId = taskId;
            SetCheckpoint(new SuspendContextCapture.Snapshot("onedragon", Descriptor.Name, 0, null, null, taskId, null, null, false));
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
            if (active != null)
            {
                active.Observe(TaskRunResult.Preempted);
                active.MarkStopRequestedLocked("suspend");
            }
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
            if (active != null)
            {
                active.Observe(manual ? TaskRunResult.Cancelled : TaskRunResult.Preempted);
                active.MarkStopRequestedLocked(manual ? "manual_stop" : "preempt_requested");
            }
        }
        // Cancellation callbacks can reenter execution; never invoke them under the admission lock.
        try { active?._stop.Cancel(); } catch (AggregateException) { }
    }

    /// <summary>
    /// 仅向同一执行实例的同一状态修订请求让位。返回 true 只证明请求已登记，
    /// 不证明业务执行体退出，也不释放物理任务槽。
    /// </summary>
    public static bool TryRequestPreempt(Guid expectedInstanceId, long expectedStateRevision)
    {
        ExecutionScope target;
        lock (Sync)
        {
            if (_active is not { } active || active._disposed
                || active.ExecutionInstanceId != expectedInstanceId
                || active.StateRevision != expectedStateRevision)
                return false;
            target = active;
            target.Observe(TaskRunResult.Preempted);
            target.MarkStopRequestedLocked("directional_stop_requested");
        }
        // 取消回调可能重入，始终在根锁外执行；迟到回调只触及被捕获的旧实例。
        try { target._stop.Cancel(); } catch (AggregateException) { }
        return true;
    }
    public void Dispose()
    {
        ExecutionExitReceipt? receipt = null;
        lock (Sync)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (ReferenceEquals(_active, this)) _active = null;
                // [R5 A4 第二步] 被接纳的执行根在生命周期结束时留下**一次性退出凭证**（未接生产门）。
                // 它只声明"这一颗执行根已结束"，不声明叶子／逃逸任务已退出，也不构成任务槽释放凭证。
                if (_admitted)
                {
                    var exitOrder = ++_exitOrderCounter;
                    var descendantScan = CollectOutstandingRegisteredDescendants(
                        Descriptor.JobId, Descriptor.WorkflowRunId);
                    receipt = new ExecutionExitReceipt(
                        JobRegistry.CurrentEpoch.ProcessId,
                        JobRegistry.CurrentEpoch.StartTicksUtc,
                        ExecutionInstanceId,
                        RunId,
                        Descriptor.JobId,
                        Descriptor.Name,
                        Descriptor.Kind.ToString(),
                        Descriptor.Source.ToString(),
                        ObservedOutcome: _outcomeObserved,
                        // 未观测到终态时不报结果：默认值 Ran 不是事实（例如取得执行权后、首个副作用前抛异常的路径）。
                        Result: _outcomeObserved ? Result : null,
                        StopRequested: _stopRequested,
                        // 未请求停止时来源为 null；已请求时只报**来源归属**，不报默认值 StopReason（默认 CancelledUser 不是事实）。
                        StopAttribution: _stopRequested ? _stopAttribution : null,
                        StopVersion,
                        DateTime.UtcNow,
                        SlotObservedFree: false,
                        Order: exitOrder,
                        DescendantScanAvailable: descendantScan.Available,
                        OutstandingRegisteredDescendantJobIds: descendantScan.OutstandingJobIds);
                }
            }
        }

        if (receipt is not null)
        {
            // 槽位状态是**释放后采样**：在根锁外读取，避免与执行/释放路径互相等待；
            // 采样可能已看到继任根占用，因此既不保证空闲，也不构成释放凭证。台账按顺序号守卫，迟到旧记录不会覆盖继任根。
            ExecutionExitLedger.Record(receipt with
            {
                SlotObservedFree = BetterGenshinImpact.GameTask.Common.TaskControl.TaskSemaphore.CurrentCount != 0
            });
        }

        if (ReferenceEquals(Ambient.Value, this)) Ambient.Value = _previous;
        _leaseWatch?.Dispose();
        // Do not dispose: in-flight token registrations may still unwind after root cancellation.
    }

    /// <summary>登记让位请求并记录**来源归属**（第一个来源生效；重复请求不覆盖原始原因）。</summary>
    private void MarkStopRequestedLocked(string attribution)
    {
        if (_stopRequested) return;
        _stopRequested = true;
        _stopAttribution ??= attribution;
        AdvanceStateRevisionLocked();
    }

    private void AdvanceStateRevisionLocked() => StateRevision = ++_stateRevisionCounter;

    /// <summary>
    /// [R5 批次 7] 执行根释放时收集**已登记**且**未终局**的派生子作业（按 `ParentJobId` 链可达）。
    /// 只读；`JobRegistry` 未创建或读取失败 ⇒ `Available=false`（**不构成"没有派生任务"的证据**）。
    /// 未登记的叶子/逃逸任务不在注册表内，本方法**看不见**它们。
    /// </summary>
    private static (bool Available, IReadOnlyList<Guid> OutstandingJobIds) CollectOutstandingRegisteredDescendants(
        Guid? rootJobId, Guid? rootRunId)
    {
        // 无根作业 ID ⇒ 无法按 ParentJobId 定界 ⇒ **不可判定**（不得给出"没有派生任务"的肯定结论）。
        if (rootJobId is not { } root) return (false, System.Array.Empty<Guid>());
        // 无 run 身份 ⇒ 无法把"派生作业"与无关历史/其它运行区分开 ⇒ **不可判定**（保守，不给肯定结论）。
        if (rootRunId is not { } run) return (false, System.Array.Empty<Guid>());
        if (!JobRegistry.IsCreated) return (false, System.Array.Empty<Guid>());

        try
        {
            // 不可变树快照（锁内复制）：避免锁外逐项读可变对象造成采样错位。
            var snapshot = JobRegistry.Instance.JobTreeSnapshot();
            var present = new System.Collections.Generic.HashSet<Guid>();
            foreach (var node in snapshot) present.Add(node.JobId);

            // 证据范围＝**从本根按 ParentJobId 链可达**的已登记作业（可穿过"无 run 身份"的连接节点：
            // 反例"根 → A(无身份、仍在表) → B(同一 run、未终局)"若按 run 过滤收集会漏掉 B ⇒ 误报 0/0/true）。
            // 链完整性检查另行按**同一 run** 限定（见下），run 身份缺失本身不使结论降级。
            // 链完整性（保守，**不按终局豁免**）：范围内若存在"父链缺失且父不是本次根"的作业 ⇒ 某祖先可能已被
            // 淘汰（注册表只保留有限终态作业），从根遍历可能漏掉更深后代 ⇒ 证据不可用（不可判定）。
            // 反例形态：根 → A(已淘汰) → B(终局仍在表) → C(未终局)。
            foreach (var node in snapshot)
            {
                if (node.WorkflowRunId != run) continue;
                if (node.ParentJobId is not { } parent) continue;
                if (parent == root) continue;
                if (!present.Contains(parent)) return (false, System.Array.Empty<Guid>());
            }

            var childrenByParent = new System.Collections.Generic.Dictionary<Guid, System.Collections.Generic.List<Guid>>();
            var terminalByJob = new System.Collections.Generic.Dictionary<Guid, bool>();
            foreach (var node in snapshot)
            {
                terminalByJob[node.JobId] = node.IsTerminal;
                if (node.ParentJobId is not { } parent) continue;
                if (!childrenByParent.TryGetValue(parent, out var list))
                {
                    list = new System.Collections.Generic.List<Guid>();
                    childrenByParent[parent] = list;
                }

                list.Add(node.JobId);
            }

            var outstanding = new System.Collections.Generic.List<Guid>();
            var queue = new System.Collections.Generic.Queue<Guid>();
            queue.Enqueue(root);
            var visited = new System.Collections.Generic.HashSet<Guid> { root };
            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!childrenByParent.TryGetValue(current, out var children)) continue;
                foreach (var child in children)
                {
                    if (!visited.Add(child)) continue;
                    if (terminalByJob.TryGetValue(child, out var terminal) && !terminal) outstanding.Add(child);
                    queue.Enqueue(child);
                }
            }

            return (true, outstanding);
        }
        catch (Exception)
        {
            // 读取失败 ⇒ 证据不可用（保守：不得据此宣称派生任务已退出）
            return (false, System.Array.Empty<Guid>());
        }
    }
}

/// <summary>进程内执行根的版本化只读身份；无活动根时不产生快照。</summary>
public sealed record ExecutionScopeSnapshot(
    Guid ExecutionInstanceId,
    long StateRevision,
    Guid RunId,
    Guid? JobId,
    JobKind Kind,
    JobSource Source,
    string Name,
    bool StopRequested,
    TaskRunResult Result);
