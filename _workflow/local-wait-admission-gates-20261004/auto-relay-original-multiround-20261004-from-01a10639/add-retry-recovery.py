from pathlib import Path
r=Path.cwd();p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs';s=p.read_bytes().decode('utf-8')
start=s.index('    [Theory]\r\n    [InlineData(false, "valid")]');end=s.index('    // ── ① 路径启用门',start);t=s[start:end]
t=t.replace('    [InlineData(false, "valid")]','    [InlineData(false, "third-unknown")]\r\n    [InlineData(true, "third-unknown")]\r\n    [InlineData(false, "third-close-fault")]\r\n    [InlineData(true, "third-close-fault")]\r\n    [InlineData(false, "valid")]')
t=t.replace('        RoutingFakePort? originalPort = null;','        RoutingFakePort? originalPort = null;\r\n        TaskCenterAdmissionSeams? originalSeams = null;')
t=t.replace('                configureSeams: seams => seams.SuccessorAdmissionObservedForTest =','                configureSeams: seams => { originalSeams = seams; seams.SuccessorAdmissionObservedForTest =')
t=t.replace('                        if (scenario != "valid")','                        if (scenario != "valid" && !scenario.StartsWith("third-", StringComparison.Ordinal))')
t=t.replace('                        var third = facade.RetryAsync(requestId).GetAwaiter().GetResult();\r\n                        Assert.True(third.Kind == AdmissionResultKind.Accepted, JsonSerializer.Serialize(third));',r'''                        if (scenario == "third-unknown") originalPort.ThrowOnSend = true;
                        var originalBarrier = seams.Barriers!.AfterAcceptBeforeLedger;
                        if (scenario == "third-close-fault") seams.Barriers.AfterAcceptBeforeLedger = () => throw new IOException("original third round closure interrupted");
                        AdmissionResult third;
                        try { third = facade.RetryAsync(requestId).GetAwaiter().GetResult(); }
                        finally { seams.Barriers.AfterAcceptBeforeLedger = originalBarrier; }
                        Assert.True(scenario.StartsWith("third-", StringComparison.Ordinal)
                            ? third.Kind != AdmissionResultKind.Accepted : third.Kind == AdmissionResultKind.Accepted, JsonSerializer.Serialize(third));'''.replace('\n','\r\n'))
t=t.replace('                        Assert.Null(current.Submission);\r\n                        Assert.Equal(third.SubmissionIdentity,','                        if (!scenario.StartsWith("third-", StringComparison.Ordinal)) Assert.Null(current.Submission);\r\n                        Assert.Equal(third.SubmissionIdentity,')
old='                    catch (Exception ex) { failures.Add(ex.ToString()); }\r\n                });'
new=r'''                    catch (Exception ex) { failures.Add(ex.ToString()); }
                }; },
                afterConverged: async (host, runs, port, boundary) =>
                {
                    if (!scenario.StartsWith("third-", StringComparison.Ordinal)) return;
                    var unknown = runs.List().Single();
                    Assert.Equal(WorkflowRunState.Unknown, unknown.State);
                    Assert.Equal(3, port.SendCount);
                    var originalRounds = JsonSerializer.Serialize(unknown.CurrentSubmission!.PreviousSendRounds);
                    var originalPermit = unknown.CurrentSubmission.SendPermit;
                    var client = ((Func<BgiExternalClient?>)typeof(TaskCenterHost).GetField("_clientAccessor", fields)!.GetValue(host)!)()!;
                    typeof(BgiExternalClient).GetProperty("State")!.SetValue(client, BgiExternalLinkState.Ready);
                    using var captured = JsonDocument.Parse(payloads[2]);
                    var payload = captured.RootElement;
                    var job = new BgiJobInfo
                    {
                        JobId = scenario == "third-close-fault" ? "job-node-3" : "original-third-job", State = "cancelled",
                        Epoch = port.ServerEpoch, ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
                        IdempotencyKey = payload.GetProperty("idempotencyKey").GetString(),
                        WorkflowRunId = payload.GetProperty("workflowRunId").GetString(), NodeId = payload.GetProperty("nodeId").GetString(),
                        Iteration = payload.GetProperty("iteration").GetInt32(), Occurrence = payload.GetProperty("occurrence").GetInt32(),
                        Attempt = payload.GetProperty("attempt").GetInt32(), ConfigRevision = payload.GetProperty("expectedConfigRevision").GetString(),
                        TaskId = payload.GetProperty("taskId").ValueKind == JsonValueKind.String ? payload.GetProperty("taskId").GetString() : null,
                        RequestFingerprintVersion = 1, RequestOperation = BgiExternalClient.ExternalOperations.TaskStart,
                        RequestFingerprint = BgiOriginalRequestFingerprint.Compute(BgiExternalClient.ExternalOperations.TaskStart, payloads[2]),
                    };
                    port.OriginalReconcileSnapshot = new BgiJobListSnapshot { Epoch = port.ServerEpoch, Jobs = [job] };
                    port.OriginalStatusJob = job;
                    await host.ShutdownAsync();
                    var reopened = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                        () => client, readinessOverride: () => (true, null), localExecutionCapability: () => true,
                        admissionWired: true, admissionSeams: originalSeams, successorAdmissionWired: true);
                    try
                    {
                        var stopped = await reopened.RequestRunActionAsync(unknown.RunId, WorkflowRunAction.Stop);
                        Assert.True(stopped.Status == HostActionStatus.Effective, stopped.Message);
                        var final = new RunStore(Path.Combine(root, "runs")).Load(unknown.RunId)!;
                        Assert.Equal(WorkflowRunState.Cancelled, final.State);
                        Assert.True(TerminalReleaseEvidence.ValidRunSeal(final));
                        Assert.Equal(originalRounds, JsonSerializer.Serialize(final.CurrentSubmission!.PreviousSendRounds));
                        Assert.Equal(originalPermit, final.CurrentSubmission.SendPermit);
                        Assert.Equal(job.JobId, final.CurrentSubmission.JobId);
                        Assert.True(final.CurrentSubmission.ExecutionExitConfirmed);
                        Assert.Equal(3, port.SendCount);
                        var repeated = await reopened.RequestRunActionAsync(unknown.RunId, WorkflowRunAction.Stop);
                        Assert.True(repeated.Status == HostActionStatus.Effective, repeated.Message);
                        Assert.Equal(final.TerminalRelease, runs.Load(unknown.RunId)!.TerminalRelease);
                    }
                    finally { await reopened.ShutdownAsync(); }
                });'''.replace('\n','\r\n')
assert old in t;t=t.replace(old,new)
t=t.replace('            if (scenario != "valid")','            if (scenario != "valid" && !scenario.StartsWith("third-", StringComparison.Ordinal))')
t=t.replace('            Assert.Equal(WorkflowRunState.Succeeded, probe.State);','            Assert.Equal(scenario.StartsWith("third-", StringComparison.Ordinal) ? WorkflowRunState.Cancelled : WorkflowRunState.Succeeded, probe.State);')
t=t.replace('handoff + "-multiround.json"','handoff + "-" + scenario + "-multiround.json"')
s=s[:start]+t+s[end:];p.write_bytes(s.encode('utf-8'))
