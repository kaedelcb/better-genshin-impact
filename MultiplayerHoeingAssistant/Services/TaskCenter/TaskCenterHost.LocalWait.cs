using System.Globalization;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed partial class TaskCenterHost
{
    private readonly LocalWaitReevaluationTrigger _localWaitTrigger = new(_ => true, "taskcenter-host");
    private readonly HashSet<string> _recoveredWaitRuns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _announcedWaitGenerations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalWaitReevaluationRequest> _waitReentries = new(StringComparer.Ordinal);
    private readonly HashSet<LocalWaitReevaluationTriggerPoint> _pendingWaitTriggers = [];
    private Task? _localWaitReevaluationTask;
    private Task? _localWaitMonitorTask;
    private readonly SemaphoreSlim _localWaitSelection = new(1, 1);

    // Startup resumes only previously accepted, zero-send waits. Loading a saved plan
    // or recovering an ordinary interrupted/paused run never creates execution intent.
    private void CaptureRecoverableLocalWaitRuns()
    {
        var snapshot = _runs.ListWithIntegrity("local-wait-startup");
        if (snapshot.UnknownFiles.Count != 0) return;
        lock (_gate)
        {
            foreach (var run in snapshot.Records)
                if (run.State is WorkflowRunState.LocalWaitParking or WorkflowRunState.Interrupted
                    && !run.StopRequested && HasValidParkedDecision(run, out var binding) && binding is not null)
                    _recoveredWaitRuns.Add(run.RunId);
        }
    }

    private void StartLocalWaitContinuation()
    {
        if (!_admissionWired || !_successorAdmissionWired) return;
        lock (_gate)
        {
            if (_shutdown || _localWaitMonitorTask is not null) return;
            _localWaitMonitorTask = Task.Run(MonitorLocalWaitAsync);
        }
        QueueLocalWaitReevaluation(LocalWaitReevaluationTriggerPoint.StartupRecovery);
    }

    private async Task MonitorLocalWaitAsync()
    {
        var ct = _shutdownCts.Token;
        var wasIdle = false;
        var nextSafetyNet = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
                var idle = false;
                try
                {
                    var facts = CurrentArbitrationFacts();
                    idle = !facts.ExecutionFactsUnknown && !facts.ExecutionOccupied
                        && facts.RunningOccupant.State == OccupantFactsState.Idle && !facts.F11Active;
                }
                catch (Exception ex) { TryLog("[任务中心] 等待占用事实暂不可读：" + ex.Message); }
                if (idle && !wasIdle)
                    QueueLocalWaitReevaluation(LocalWaitReevaluationTriggerPoint.OccupancyEnded);
                wasIdle = idle;
                if (DateTimeOffset.UtcNow >= nextSafetyNet)
                {
                    nextSafetyNet = DateTimeOffset.UtcNow.AddSeconds(30);
                    QueueLocalWaitReevaluation(LocalWaitReevaluationTriggerPoint.SafetyNet);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { TryLog("[任务中心] 等待状态观察中断：" + ex.Message); }
    }

    private void QueueLocalWaitReevaluation(LocalWaitReevaluationTriggerPoint trigger)
    {
        if (!_admissionWired || !_successorAdmissionWired) return;
        lock (_gate)
        {
            if (_shutdown || _localWaitMonitorTask is null) return;
            _pendingWaitTriggers.Add(trigger);
            _localWaitReevaluationTask ??= Task.Run(DrainLocalWaitReevaluationsAsync);
        }
    }

    private async Task DrainLocalWaitReevaluationsAsync()
    {
        while (true)
        {
            LocalWaitReevaluationTriggerPoint trigger;
            lock (_gate)
            {
                if (_shutdown || _pendingWaitTriggers.Count == 0)
                {
                    _localWaitReevaluationTask = null;
                    return;
                }
                trigger = _pendingWaitTriggers.OrderBy(t => (int)t).First();
                _pendingWaitTriggers.Clear(); // A single current snapshot covers coalesced events.
            }
            try { await ReevaluateLocalWaitAsync(trigger).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdownCts.IsCancellationRequested) { }
            catch (Exception ex) { TryLog("[任务中心] 等待重评暂未完成，保留原运行：" + ex.Message); }
        }
    }

    private async Task ReevaluateLocalWaitAsync(LocalWaitReevaluationTriggerPoint trigger)
    {
        await _localWaitSelection.WaitAsync(_shutdownCts.Token).ConfigureAwait(false);
        try { await ReevaluateLocalWaitCoreAsync(trigger).ConfigureAwait(false); }
        finally { _localWaitSelection.Release(); }
    }

    private async Task ReevaluateLocalWaitCoreAsync(LocalWaitReevaluationTriggerPoint trigger)
    {
        lock (_gate)
            if (_shutdown || _waitReentries.Count != 0 || CapabilityBlockReason() is not null || !ExecutionReadiness().Ready) return;
        var facts = CurrentArbitrationFacts();
        if (facts.F11Active || facts.ExecutionFactsUnknown || facts.ExecutionOccupied
            || facts.RunningOccupant.State != OccupantFactsState.Idle) return;
        var lease = _admissionStore?.Read();
        if (lease is null || !IsCompleteAdmissionRead(lease)
            || lease.File?.Lease?.LeaseId != _admissionLeaseId
            || lease.File.Lease.OwnerEpoch != _admissionOwnerEpoch) return;
        var snapshot = _runs.ListWithIntegrity("local-wait-select");
        if (snapshot.UnknownFiles.Count != 0) return;
        var currentEpoch = CurrentBgiEpoch();
        if (snapshot.Records.Any(run => RunStore.HasUnresolvedExternalFact(run)
            && (run.AdmissionSourceScope == "bgi:local:" + currentEpoch
                || run.StopAuthority?.Epoch == currentEpoch
                || run.CurrentSubmission?.Epoch == currentEpoch
                || run.PendingCompletion?.Epoch == currentEpoch
                || run.PrerequisiteActions.Any(action => action.Epoch == currentEpoch)))) return;
        var queued = LocalWaitQueue.Load();
        lock (_gate)
            foreach (var id in _announcedWaitGenerations.Keys.Except(queued.Where(i => i.State == LocalWaitItemState.Waiting)
                         .Select(i => i.ItemId)).ToList()) _announcedWaitGenerations.Remove(id);
        var eligible = new Dictionary<string, WorkflowRunRecord>(StringComparer.Ordinal);
        var ranked = new List<LocalWaitItem>();
        foreach (var item in queued.Where(i => i.State == LocalWaitItemState.Waiting))
        {
            var run = ReadAutomaticWaitCandidate(item, snapshot.Records);
            if (run is null) continue;
            eligible.Add(item.ItemId, run);
            // Trust comes from the current run, source mapping and exact queue payload;
            // the persisted queue's self-reported flag is never used as authority.
            var view = run.LocalWaitDecision!.Binding!.ToQueueItem();
            view.Generation = item.Generation;
            view.HasTrustedIdentity = true;
            ranked.Add(view);
        }
        var selected = LocalWaitQueuePolicy.SelectNextView(ranked,
            item => eligible.ContainsKey(item.ItemId) ? PrerequisiteReadiness.Ready : PrerequisiteReadiness.Undetermined);
        if (selected is null) return;
        _localWaitTrigger.PruneHandledExceptGeneration(selected.StableIdentity,
            selected.Generation.ToString(CultureInfo.InvariantCulture));
        var decision = _localWaitTrigger.Decide(trigger, [selected], DateTimeOffset.UtcNow, _shutdownCts.Token,
            selected.Generation.ToString(CultureInfo.InvariantCulture));
        if (decision.Requests.Count == 0) return;
        var request = decision.Requests[0];
        var registered = false;
        try
        {
            if (!LocalWaitReevaluationConsumer.Consume(request, LocalWaitQueue).Valid) return;
            var freshQueue = LocalWaitQueue.Load().SingleOrDefault(i => i.ItemId == selected.ItemId);
            var fresh = freshQueue is null ? null : ReadAutomaticWaitCandidate(freshQueue,
                _runs.ListWithIntegrity("local-wait-consume").Records);
            if (fresh is null || !LocalWaitQueuePolicy.RevalidateBeforeSend(selected,
                (Func<LocalWaitItem, PrerequisiteReadiness>)(_ => ReadAutomaticWaitCandidate(freshQueue!,
                    _runs.ListWithIntegrity("local-wait-prerequisite").Records)
                    is not null ? PrerequisiteReadiness.Ready : PrerequisiteReadiness.NotReady)).Passed) return;
            lock (_gate)
            {
                if (_shutdown) return;
                _waitReentries[fresh.RunId] = request;
            }
            var result = await SubmitResumeViaAdmissionAsync(fresh, "local-wait:" + trigger).ConfigureAwait(false);
            registered = result.Status == HostActionStatus.Registered;
            if (registered) TryLog("[任务中心] 已按优先级自动接续等待运行 " + fresh.RunId + "。");
        }
        finally
        {
            if (!registered)
            {
                lock (_gate) _waitReentries.Remove(eligible[selected.ItemId].RunId);
                _localWaitTrigger.ReleaseDeferredRequest(request);
            }
        }
    }

    private WorkflowRunRecord? ReadAutomaticWaitCandidate(LocalWaitItem item, IReadOnlyList<WorkflowRunRecord> records)
    {
        if (_runs.UnknownFiles.Count != 0 || item.State != LocalWaitItemState.Waiting) return null;
        var matches = records.Where(r => r.LocalWaitDecision?.Binding?.ItemId == item.ItemId).ToList();
        if (matches.Count != 1) return null;
        var run = matches[0];
        lock (_gate)
        {
            if (_shutdown || _drives.ContainsKey(run.WorkflowId) || _reservedWorkflows.Contains(run.WorkflowId)
                || run.State != WorkflowRunState.LocalWaitParking
                    && !(run.State == WorkflowRunState.Interrupted && _recoveredWaitRuns.Contains(run.RunId))) return null;
        }
        if (run.StopRequested || RunStore.HasUnresolvedExternalFact(run) || HasUnresolvedPrerequisiteResponsibility(run)
            || !HasValidParkedDecision(run, out var binding) || binding is null
            || !LocalWaitQueueStore.MatchesBindingPayload(binding, item)) return null;
        var parent = TryGetAdmissionParent(run.RunId, run.WorkflowId);
        if (parent is null || parent.Value.Scope != binding.Scope
            || binding.SourceKind == LocalWaitSourceKind.PanelFlowRegistration
                && parent.Value.RequestIdentity != binding.SourceIdentity) return null;
        var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
        if (snapshot.Revision != binding.WorkflowRevision || ResumeDefinitionBlockReason(run) is not null) return null;
        var plan = new WorkflowPlan(snapshot.Document);
        if (!plan.TryLocate(binding.NodeId, binding.Occurrence, binding.LoopIteration, out var occurrence)
            || binding.PrerequisiteReference != LocalWaitPrerequisiteReference(run, occurrence)) return null;
        return run;
    }

    private void ObserveLocalWaitDriveCompletion(string? runId, bool terminal)
    {
        if (!_admissionWired || !_successorAdmissionWired || runId is null) return;
        LocalWaitReevaluationRequest? reentry;
        lock (_gate)
        {
            _waitReentries.Remove(runId, out reentry);
            _recoveredWaitRuns.Remove(runId);
        }
        if (reentry is not null) _localWaitTrigger.ReleaseDeferredRequest(reentry);
        var run = _runs.Load(runId);
        if (run?.State == WorkflowRunState.LocalWaitParking && HasValidParkedDecision(run, out var binding)
            && binding is not null)
        {
            var item = LocalWaitQueue.Load().SingleOrDefault(i => i.ItemId == binding.ItemId);
            if (item is not null)
            {
                lock (_gate)
                {
                    if (_announcedWaitGenerations.TryGetValue(item.ItemId, out var announced)
                        && announced == item.Generation) return;
                    _announcedWaitGenerations[item.ItemId] = item.Generation;
                }
                QueueLocalWaitReevaluation(LocalWaitReevaluationTriggerPoint.NewCandidateArrived);
            }
        }
        else if (terminal) QueueLocalWaitReevaluation(LocalWaitReevaluationTriggerPoint.OccupancyEnded);
    }

    internal Task ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint trigger)
        => ReevaluateLocalWaitAsync(trigger);
}
