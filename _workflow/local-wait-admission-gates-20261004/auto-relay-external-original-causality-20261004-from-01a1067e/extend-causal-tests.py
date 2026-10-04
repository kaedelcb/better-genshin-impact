from pathlib import Path
import json,hashlib
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterExternalStartAdmissionTests.cs');b=p.read_bytes();s=b.decode('utf-8');d=Path(__file__).parent
needle='                    Assert.Equal(2, sends);\n                }\n                finally { File.WriteAllBytes(ledgerPath, originalBytes); }'
assert s.count(needle)==1
s=s.replace(needle,'''                    Assert.Equal(2, sends);
                    var evidenceDir = Environment.GetEnvironmentVariable("BGI_CAUSAL_MATRIX_DIR");
                    if (!string.IsNullOrEmpty(evidenceDir))
                    {
                        Directory.CreateDirectory(evidenceDir);
                        File.WriteAllText(Path.Combine(evidenceDir, $"causal-{archiveBeforeReceipt}-{archiveAfterTerminal}-{phase}-{field}-{missing}.json"),
                            System.Text.Json.JsonSerializer.Serialize(new { phase, field, missing, read, denied, sends, retained = before }));
                    }
                }
                finally { File.WriteAllBytes(ledgerPath, originalBytes); }''')
needle='            }\n        }\n        try\n        {\n            oldHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });'
assert s.count(needle)==1
s=s.replace(needle,'''            }
            var hooks = (AdmissionHooks)typeof(ArbitrationAdmissionService).GetField("_hooks", fields)!.GetValue(Facade(owner))!;
            var scanHook = hooks.TakeoverLedgerScan;
            try
            {
                var scan = scanHook!(); var fact = Assert.Single(scan.Facts);
                foreach (var corrupt in new[] { fact with { CandidateId = "wrong-candidate" }, fact with { ResourceRef = "wrong-resource" },
                    fact with { ActionId = "wrong-action" }, fact with { TargetBgiEpoch = "wrong-epoch" } })
                foreach (var reversed in new[] { false, true })
                {
                    hooks.TakeoverLedgerScan = () => scan with { Facts = reversed ? new[] { corrupt, fact } : new[] { fact, corrupt } };
                    var denied = await Facade(owner).RecoverExternalStartObservationsAsync();
                    Assert.True(denied.ScanFactConflicts == 1, $"original ledger duplicate causality {phase}/{reversed}: {denied}");
                    Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(Store(owner).Read().File!.Handoff));
                    Assert.Equal(2, sends);
                }
            }
            finally { hooks.TakeoverLedgerScan = scanHook; }
        }
        try
        {
            oldHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });''')
needle='    [Fact]\n    public async Task OriginalLateRound_CurrentTerminalArchiveReplays()'
assert s.count(needle)==1
code='''    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OriginalLedgerCausality_CurrentReceiptAndTerminal(bool terminal, bool reopen)
    {
        var root = NewRoot(); TaskCenterHost? host = null, newHost = null; var sends = 0;
        var fields = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        try
        {
            host = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
            await host.AdmitExternalStartAsync(Request(_ =>
            {
                Interlocked.Increment(ref sends); return Task.FromResult(ExternalStartExecution.AcceptedWith("current-causal-job", "ext:task.queue"));
            }, completion: terminal ? () => ExternalStartCompletion.SucceededWith("completed", "ext:task.event", DateTimeOffset.UtcNow, "current-causal-job") : null));
            var op = Assert.Single(Ops(root));
            Assert.Equal(terminal ? OperationRequestState.TerminalCompleted : OperationRequestState.Accepted, op.RequestState);
            var ledgerPath = Path.Combine(root, "external-start-ledger.json"); var bytes = File.ReadAllBytes(ledgerPath);
            var owner = host;
            if (reopen)
            {
                await host.ShutdownAsync(); newHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });
                await newHost.SubmitExternalStartViaAdmissionAsync(new ExternalStartAdmissionRequest
                {
                    RequestIdentity = op.RequestIdentity, Namespace = "v2", WorkflowId = "group:测试组",
                    TriggerOccurrenceId = "v2:remote:{requestIdentity}", ResourceRef = "group:测试组", SourceDetail = "fixture:current-causality",
                    ExecuteAsync = _ => { Interlocked.Increment(ref sends); return Task.FromResult(ExternalStartExecution.AcceptedWith("must-not-resend")); },
                }, default);
                owner = newHost;
            }
            var store = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore", fields)!.GetValue(owner)!;
            var facade = (ArbitrationAdmissionService)typeof(TaskCenterHost).GetField("_admission", fields)!.GetValue(owner)!;
            foreach (var field in new[] { "candidateId", "resourceRef", "actionId", "targetBgiEpoch" })
            foreach (var missing in new[] { false, true })
            {
                var retained = System.Text.Json.JsonSerializer.Serialize(store.Read().File!.Handoff);
                try
                {
                    var document = System.Text.Json.Nodes.JsonNode.Parse(bytes)!;
                    document["entries"]![0]![field] = missing ? "" : "wrong-original-" + field;
                    File.WriteAllText(ledgerPath, document.ToJsonString());
                    var denied = await facade.RecoverExternalStartObservationsAsync();
                    Assert.True(missing ? denied.LedgerUnreadable : denied.ScanFactConflicts == 1,
                        $"current original ledger causality {terminal}/{reopen}/{field}/{missing}: {denied}");
                    Assert.Equal(retained, System.Text.Json.JsonSerializer.Serialize(store.Read().File!.Handoff));
                    Assert.Equal(1, sends);
                }
                finally { File.WriteAllBytes(ledgerPath, bytes); }
            }
            var valid = await facade.RecoverExternalStartObservationsAsync();
            Assert.False(valid.LedgerUnreadable); Assert.Equal(0, valid.ScanFactConflicts);
            Assert.Equal(1, sends); Assert.Equal(op.SubmissionIdentity, Assert.Single(Ops(root)).SubmissionIdentity);
        }
        finally { if (host is not null) await host.ShutdownAsync(); if (newHost is not null) await newHost.ShutdownAsync(); TryDelete(root); }
    }

'''
s=s.replace(needle,code+needle);a=s.encode('utf-8');p.write_bytes(a);(d/'extended-test-observation.json').write_text(json.dumps(dict(before_sha=hashlib.sha256(b).hexdigest(),after_sha=hashlib.sha256(a).hexdigest(),before_bytes=len(b),after_bytes=len(a),lf=a.count(b'\n'),crlf=a.count(b'\r\n')),indent=2),encoding='utf-8');print(len(a)-len(b))
