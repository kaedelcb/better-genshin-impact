from pathlib import Path
import json,hashlib
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/auto-relay-four-entry-recovery-20261004-from-01a1061f'
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs'
b=p.read_bytes();s=b.decode('utf-8');nl='\r\n';s=s.replace(nl,'\n')
(d/'test-source-before.json').write_text(json.dumps(dict(path=str(p),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n')),indent=2))
assert s.count('bool requirePanelSourceForProbe = true)')==1
s=s.replace('bool requirePanelSourceForProbe = true)','bool requirePanelSourceForProbe = true,\n        Func<TaskCenterHost, RunStore, RoutingFakePort, Task>? afterRegistered = null)')
anchor='            // 有界等待**路由收敛**：'
assert s.count(anchor)==1
s=s.replace(anchor,'            if (afterRegistered is not null) await afterRegistered(host, runs, port);\n\n'+anchor)
anchor='        public BgiJobInfo? OriginalStatusJob { get; set; }'
s=s.replace(anchor,'''        public TaskCompletionSource FirstStatusEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstStatusRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool HoldFirstStatus { get; set; }
'''+anchor)
s=s.replace('        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)\n        {','''        public async Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            if (HoldFirstStatus && jobId == "job-node-1")
            {
                FirstStatusEntered.TrySetResult();
                await FirstStatusRelease.Task.WaitAsync(ct);
            }''')
# Only this now-async method returns tuples; other Task methods retain their contracts.
start=s.index('        public async Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync')
end=s.index('        public Task CancelOwnedTaskAsync',start)
seg=s[start:end].replace('return Task.FromResult<(string?, BgiJobInfo?)>((OriginalStatusJob.State, OriginalStatusJob));','return (OriginalStatusJob.State, OriginalStatusJob);').replace('return Task.FromResult<(string?, BgiJobInfo?)>(("not_found", null));','return ("not_found", null);').replace('return Task.FromResult<(string?, BgiJobInfo?)>(("succeeded", new BgiJobInfo','return ("succeeded", new BgiJobInfo').replace('                }));','                });')
s=s[:start]+seg+s[end:]
tests='''    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task OriginalLifecycle_NewHostResumePreservesParentAndCompletedNode(bool handoff, bool interrupted)
    {
        var root = NewRoot("tc-lifecycle-");
        try
        {
            var probe = await ProbeNodeSubmitRoutingAsync(root, true, nodeIds: ["n-1", "n-2"], startViaHandoff: handoff,
                configurePort: p => p.HoldFirstStatus = true,
                afterRegistered: async (first, runs, port) =>
                {
                    await port.FirstStatusEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    var original = Assert.Single(runs.List());
                    Assert.Equal(HostActionStatus.Effective, (await first.RequestRunActionAsync(original.RunId, WorkflowRunAction.Pause)).Status);
                    port.FirstStatusRelease.TrySetResult();
                    for (var i = 0; i < 1000 && runs.Load(original.RunId)!.State != WorkflowRunState.Paused; i++) await Task.Delay(10);
                    var paused = runs.Load(original.RunId)!;
                    Assert.Equal(WorkflowRunState.Paused, paused.State);
                    Assert.Equal(1, port.SendCount);
                    Assert.Single(paused.NodeOutcomes);
                    Assert.Equal("n-1", paused.NodeOutcomes[0].NodeId);
                    await first.ShutdownAsync(); // old writer is terminal before any new Host
                    var before = ReadLeaseFileWithRetry(root)!.Handoff!;
                    var nodeBefore = Assert.Single(before.Operations.Where(o => o.OperationType == OperationType.NodeExecution));
                    var identityBefore = JsonSerializer.Serialize(nodeBefore);
                    var parentBefore = JsonSerializer.Serialize(nodeBefore.ParentSource);
                    var outcomesBefore = JsonSerializer.Serialize(paused.NodeOutcomes);
                    var historyBefore = JsonSerializer.Serialize(paused.SubmissionHistory);
                    if (interrupted)
                    {
                        // A crash-state fixture, not a real child-process restart: production new Host performs the actual recovery scan.
                        paused.State = WorkflowRunState.Running;
                        runs.Update(paused);
                    }
                    using var secondClient = new BgiExternalClient();
                    var secondRuns = new RunStore(Path.Combine(root, "runs"));
                    var second = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                        () => secondClient, log: null, runnerFactory: null, readinessOverride: () => (true, null),
                        localExecutionCapability: () => true,
                        statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = RoutingFakePort.Epoch,
                            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
                        admissionWired: true, successorAdmissionWired: true,
                        admissionSeams: new TaskCenterAdmissionSeams { Epoch = RoutingFakePort.Epoch,
                            ProductionBoundaryFactory = (_, store) => new BgiWorkflowExecutionBoundary(port, store) });
                    try
                    {
                        second.EnsureRecovered();
                        var recovered = secondRuns.Load(original.RunId)!;
                        Assert.Equal(interrupted ? WorkflowRunState.Interrupted : WorkflowRunState.Paused, recovered.State);
                        Assert.Equal(outcomesBefore, JsonSerializer.Serialize(recovered.NodeOutcomes));
                        Assert.Equal(historyBefore, JsonSerializer.Serialize(recovered.SubmissionHistory));
                        var resumed = await second.ResumeRunAsync(original.RunId);
                        Assert.Equal(HostActionStatus.Registered, resumed.Status);
                        for (var i = 0; i < 1000 && secondRuns.Load(original.RunId)!.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Unknown or WorkflowRunState.Failed); i++) await Task.Delay(10);
                        var finished = secondRuns.Load(original.RunId)!;
                        Assert.True(finished.State == WorkflowRunState.Succeeded, $"state={finished.State}, note={finished.Note}");
                        Assert.Equal(2, port.SendCount); // completed first node was never reissued
                        Assert.Equal(2, finished.NodeOutcomes.Count);
                        var after = ReadLeaseFileWithRetry(root)!.Handoff!;
                        var oldNode = Assert.Single(after.Operations.Where(o => o.RequestIdentity == nodeBefore.RequestIdentity));
                        Assert.Equal(identityBefore, JsonSerializer.Serialize(oldNode));
                        var newNode = Assert.Single(after.Operations.Where(o => o.Candidate?.NodeId == "n-2"));
                        Assert.Equal(parentBefore, JsonSerializer.Serialize(newNode.ParentSource));
                        Assert.Equal(nodeBefore.ParentRequestIdentity, newNode.ParentRequestIdentity);
                        Assert.Equal(1, newNode.LastSendSeq);
                        Assert.NotEqual(nodeBefore.SubmissionIdentity, newNode.SubmissionIdentity);
                        var evidenceDir = Environment.GetEnvironmentVariable("BGI_LIFECYCLE_EVIDENCE_DIR");
                        if (!string.IsNullOrEmpty(evidenceDir)) File.WriteAllText(Path.Combine(evidenceDir, $"{handoff}-{interrupted}-resume.json"),
                            JsonSerializer.Serialize(new { handoff, interrupted, before, after, recovered, finished, sends = port.SendCount,
                                limitation = "new actual Host/Runner/RunStore/LeaseStore with controlled remote port; interrupted crash state is simulated; no IPC/game/User acceptance" }));
                    }
                    finally { await second.ShutdownAsync(); }
                });
            Assert.Equal(WorkflowRunState.Succeeded, probe.State);
            Assert.Equal(2, probe.SendCount);
        }
        finally { TryDelete(root); }
    }

'''
anchor='    [Theory]\n    [InlineData(false)]\n    [InlineData(true)]\n    public async Task Capacity_33ActualAdmissions'
assert s.count(anchor)==1;s=s.replace(anchor,tests+anchor)
new=s.replace('\n',nl).encode('utf-8');assert len(new)>len(b);p.write_bytes(new)
(d/'test-source-after.json').write_text(json.dumps(dict(bytes=len(new),lines=len(new.splitlines()),sha256=hashlib.sha256(new).hexdigest(),crlf=new.count(b'\r\n')),indent=2))
