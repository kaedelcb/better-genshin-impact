from pathlib import Path

def edit(relative, callback):
    p = Path(relative)
    original = p.read_bytes().decode('utf-8')
    nl = '\r\n' if '\r\n' in original else '\n'
    text = original.replace('\r\n', '\n')
    changed = callback(text)
    assert len(changed) > len(text) * .9
    p.write_bytes(changed.replace('\n', nl).encode('utf-8'))

def host(t):
    t = t.replace('    private bool _shutdown;', '    private bool _shutdown;\n    private Task? _shutdownTask;', 1)
    start = t.index('    public async Task ShutdownAsync()')
    end = t.index('    // ================= 内部', start)
    t = t[:start] + '''    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            _shutdown = true;
            var drives = _driveCompletions.ToList();
            // Scheduling guarantees no user cancellation callback executes under _gate.
            // The same task represents cancellation, full observation and bounded convergence.
            _shutdownTask = Task.Run(() => ShutdownCoreAsync(drives));
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(List<DriveEntry> drives)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cancellations = drives.Select(d => CancelIsolatedAsync(d.Cts)).Append(CancelIsolatedAsync(_shutdownCts));
        var complete = Task.WhenAll(cancellations.Concat(drives.Select(d => d.Completion.Task)));
        await Task.WhenAny(complete, Task.Delay(ShutdownConvergeBudget)).ConfigureAwait(false);
        // Both cancellation callbacks and terminal writeback consume the original 10s budget.
        // Late observers retain their original owner and cannot borrow a successor capability.
        ReleaseAdmissionLeaseOnShutdown();
        if (!complete.IsCompleted)
            _ = Task.Run(() => TryLog($"[任务中心] 宿主关闭：{drives.Count} 个运行未在 {ShutdownConvergeBudget.TotalSeconds:0}s 内收敛（在飞事实保留，下次启动恢复扫描标记）"));
    }

    private Task CancelIsolatedAsync(CancellationTokenSource cts)
        => Task.Run(() =>
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* Completed observer disposed its own source. */ }
            catch (Exception ex)
            {
                _ = Task.Run(() => TryLog("[任务中心] 退出取消回调异常（原观察责任保持）：" + ex.GetType().Name));
            }
        });

''' + t[end:]
    start = t.index('        var cts = new CancellationTokenSource();', t.index('    private HostActionResult LaunchDrive'))
    end = t.index('    private async Task ObserveDriveAsync', start)
    t = t[:start] + '''        var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource<WorkflowRunRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entry = new DriveEntry { WorkflowId = workflowId, RunId = knownRunId, Runner = runner, Cts = cts, Task = started.Task };
        lock (_gate)
        {
            if (_shutdown)
            {
                _reservedWorkflows.Remove(workflowId);
                cts.Dispose();
                return HostActionResult.Unavailable("任务中心宿主已关闭（未启动驱动）");
            }
            // Register the full lifecycle before any synchronous prefix of start can run.
            _drives[workflowId] = entry;
            _driveCompletions.Add(entry);
        }
        _ = ObserveDriveAsync(entry);
        try
        {
            var task = start(cts);
            _ = CompleteStartedDriveAsync(task, started);
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            return HostActionResult.Unavailable(ex.Message);
        }
        NotifyStateChanged();
        return HostActionResult.Registered(registeredMessage);
    }

    private static async Task CompleteStartedDriveAsync(Task<WorkflowRunRecord> task,
        TaskCompletionSource<WorkflowRunRecord> started)
    {
        try { started.TrySetResult(await task.ConfigureAwait(false)); }
        catch (Exception ex) { started.TrySetException(ex); }
    }

''' + t[end:]
    # Orphan work no longer exists: every started task has the same full observer.
    start = t.index('    /// <summary>册外观察')
    t = t[:start] + '}\n'
    t = t.replace('        var clock = System.Diagnostics.Stopwatch.StartNew();\n', '', 1)
    return t

def admission(t):
    needle = '    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync'
    helpers = '''    private static bool IsCompleteAdmissionRead(LeaseReadResult read)
        => !read.UncertainResidue && read.Status is ArbitrationLeaseStatus.Valid or ArbitrationLeaseStatus.Expired
            && read.File?.Handoff is { Operations: not null, ArchivedOperations: not null };

    private static bool HasOriginalAdmissionMapping(WorkflowRunRecord run)
        => run.AdmissionParentSource is { Kind: AdmissionParentKind.PanelFlowRegistration }
            || run.CurrentSubmission?.SendPermit?.OriginalSendIdentity is { Length: > 0 }
            || run.NodeHistory.Any(h => h.Submission?.SendPermit?.OriginalSendIdentity is { Length: > 0 })
            || run.RecoveryAssociations.Any(a => !string.IsNullOrEmpty(a.SubmissionIdentity));

'''
    assert t.count(needle) == 1
    t = t.replace(needle, helpers + needle)
    old = '            var operations = AllAdmissionOperations(_admissionStore.Read().File?.Handoff);'
    new = '''            var originalRead = _admissionStore.Read();
            if (!IsCompleteAdmissionRead(originalRead)) return AdmissionTerminalReconciliationOutcome.Pending;
            var operations = AllAdmissionOperations(originalRead.File!.Handoff).ToList();
            if (!operations.Any(o => o.RunBinding == runId) && HasOriginalAdmissionMapping(run))
                return AdmissionTerminalReconciliationOutcome.Pending;'''
    assert t.count(old) == 1
    t = t.replace(old, new)
    t = t.replace('if (leaseRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)', 'if (!IsCompleteAdmissionRead(leaseRead))', 1)
    t = t.replace('if (finalRead.Status is ArbitrationLeaseStatus.Corrupt or ArbitrationLeaseStatus.Unsupported)', 'if (!IsCompleteAdmissionRead(finalRead))', 1)
    t = t.replace('if (current.Count == 0) return AdmissionTerminalReconciliationOutcome.NoMapping;', '''if (current.Count == 0) return HasOriginalAdmissionMapping(run)
                ? AdmissionTerminalReconciliationOutcome.Pending : AdmissionTerminalReconciliationOutcome.NoMapping;''')
    # Release diagnostics cannot block or throw through the bounded shutdown completion.
    start = t.index('    private void ReleaseAdmissionLeaseOnShutdown()')
    end = t.index('    private void StartAdmissionLeaseHeartbeat', start)
    part = t[start:end]
    part = part.replace('_log?.Invoke("[任务中心] 仲裁租约退出释放被拒（" + rel.Reason + "）——留待 TTL 接管路径。");', '_ = Task.Run(() => TryLog("[任务中心] 仲裁租约退出释放被拒（" + rel.Reason + "）——留待 TTL 接管路径。"));')
    part = part.replace('_log?.Invoke("[任务中心] 仲裁租约退出释放异常（留待 TTL 接管路径）：" + ex.Message);', '_ = Task.Run(() => TryLog("[任务中心] 仲裁租约退出释放异常（留待 TTL 接管路径）：" + ex.Message));')
    t = t[:start] + part + t[end:]
    return t

edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs', host)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs', admission)
