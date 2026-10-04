from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode();nl='\r\n'
s=s.replace('    [InlineData("stop-missing")]','    [InlineData("stop-missing")]'+nl+'    [InlineData("history-exited")]'+nl+'    [InlineData("history-settle")]'+nl+'    [InlineData("history-publish")]')
s=s.replace('scenario == "stop-exited" ? "cancelled" : "running"','scenario == "stop-exited" || scenario.StartsWith("history-") ? "cancelled" : "running"')
s=s.replace('ExecutionExitConfirmed = scenario == "stop-exited",','ExecutionExitConfirmed = scenario == "stop-exited" || scenario.StartsWith("history-"),')
s=s.replace('scenario == "stop-exited" ? "execution_exited" : null','scenario == "stop-exited" || scenario.StartsWith("history-") ? "execution_exited" : null')
a='                    if (scenario.StartsWith("stop-", StringComparison.Ordinal))'
z='''                    if (scenario.StartsWith("history-", StringComparison.Ordinal))
                    {
                        var client = (BgiExternalClient)((Func<BgiExternalClient?>)typeof(TaskCenterHost)
                            .GetField("_clientAccessor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                            .GetValue(host)!)()!;
                        typeof(BgiExternalClient).GetProperty("State")!.SetValue(client, BgiExternalLinkState.Ready);
                        sub.Intent = SubmitIntentState.Accepted; sub.JobId = "original-accepted-job";
                        run.SubmissionHistory.Add(sub); run.CurrentSubmission = null;
                        // Simulate the retained old history, preserving the real host's original permit and wire payload.
                        var path = Path.Combine(root, "runs", run.RunId + ".run.json");
                        File.WriteAllText(path, JsonSerializer.Serialize(run));
                        var originalHistory = JsonSerializer.Serialize(run.SubmissionHistory);
                        var originalOutcomes = JsonSerializer.Serialize(run.NodeOutcomes);
                        port.OriginalStatusJob = port.OriginalReconcileSnapshot.Jobs[0];
                        if (scenario == "history-publish") hostRuns!.PublishFaultForTest = r => r.RecoveryAssociations.Count > 0 ? new IOException("historical evidence publication failed") : null;
                        var savedBarrier = originalSeams!.Barriers!.AfterAcceptBeforeLedger;
                        if (scenario == "history-settle") originalSeams.Barriers.AfterAcceptBeforeLedger = () => throw new IOException("historical facade settlement interrupted");
                        HostActionResult first;
                        try { first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop); }
                        finally { hostRuns!.PublishFaultForTest = null; originalSeams.Barriers.AfterAcceptBeforeLedger = savedBarrier; }
                        var after = runs.Load(run.RunId)!;
                        Assert.Equal(originalHistory, JsonSerializer.Serialize(after.SubmissionHistory));
                        Assert.Equal(originalOutcomes, JsonSerializer.Serialize(after.NodeOutcomes));
                        if (scenario != "history-exited")
                        {
                            Assert.Equal(HostActionStatus.Unavailable, first.Status);
                            Assert.Equal(WorkflowRunState.Unknown, after.State);
                            Assert.Null(after.TerminalRelease);
                            Assert.Equal(scenario == "history-settle" ? 1 : 0, after.RecoveryAssociations.Count);
                        }
                        else Assert.Equal(HostActionStatus.Effective, first.Status);
                        // Reopen the host against the same durable stores and original controlled execution port.
                        using var reopenedHost = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
                            Path.Combine(root, "catalog.json"), () => client, log: null, runnerFactory: null,
                            readinessOverride: () => (true, null), localExecutionCapability: () => true,
                            admissionWired: true, admissionSeams: originalSeams, successorAdmissionWired: true);
                        var second = reopenedHost.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
                        Assert.True(second.Status == HostActionStatus.Effective, second.Message);
                        after = new RunStore(Path.Combine(root, "runs")).Load(run.RunId)!;
                        Assert.Equal(WorkflowRunState.Cancelled, after.State);
                        Assert.True(TerminalReleaseEvidence.ValidRunSeal(after));
                        Assert.Single(after.RecoveryAssociations);
                        Assert.Equal(originalHistory, JsonSerializer.Serialize(after.SubmissionHistory));
                        Assert.Equal(originalOutcomes, JsonSerializer.Serialize(after.NodeOutcomes));
                        Assert.Equal(1, port.SendCount);
                        return;
                    }
'''.replace('\n',nl)+a
assert s.count(a)==1;s=s.replace(a,z)
s=s.replace('scenario == "stop-exited" ? WorkflowRunState.Cancelled : WorkflowRunState.Unknown, probe.State','scenario == "stop-exited" || scenario.StartsWith("history-") ? WorkflowRunState.Cancelled : WorkflowRunState.Unknown, probe.State')
p.write_bytes(s.encode())
