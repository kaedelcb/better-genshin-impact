from pathlib import Path
import hashlib,json
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterExternalStartAdmissionTests.cs');b=p.read_bytes();s=b.decode('utf-8').replace('\r\n','\n');d=Path(__file__).parent
old='''    public async Task OriginalLateRound_HostArchivedResponsibility(bool archiveBeforeReceipt, bool archiveAfterTerminal)'''
new='''    [InlineData(true, true, "unconfirmed")]
    [InlineData(true, true, "wrong-job")]
    [InlineData(true, true, "wrong-terminal")]
    [InlineData(true, true, "wrong-source")]
    [InlineData(true, true, "wrong-time")]
    [InlineData(true, true, "missing-terminal")]
    public async Task OriginalLateRound_HostArchivedResponsibility(bool archiveBeforeReceipt, bool archiveAfterTerminal, string fault = "valid")'''
assert s.count(old)==1;s=s.replace(old,new)
needle='''            var replay = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(replay.ScanFactConflicts == 0, "original settled archived terminal replay: " + replay);'''
repl='''            if (fault != "valid")
            {
                var hooks = (AdmissionHooks)typeof(ArbitrationAdmissionService).GetField("_hooks", fields)!.GetValue(facade)!;
                var originalScan = hooks.TakeoverLedgerScan;
                var originalConfirm = hooks.TakeoverTerminalPayloadConfirmed;
                var retainedBytes = System.Text.Json.JsonSerializer.Serialize(Store(currentHost).Read().File!.Handoff);
                try
                {
                    if (fault == "unconfirmed") hooks.TakeoverTerminalPayloadConfirmed = (_, _, _, _, _, _, _, _) => false;
                    else hooks.TakeoverLedgerScan = () =>
                    {
                        var scan = originalScan(); var fact = Assert.Single(scan.Facts);
                        fact = fault switch
                        {
                            "wrong-job" => fact with { JobId = "wrong-original-job" },
                            "wrong-terminal" => fact with { RawTerminal = "cancelled", TerminalKind = ExecutionResultKind.Cancelled },
                            "wrong-source" => fact with { TerminalEvidenceSource = "wrong-original-source" },
                            "wrong-time" => fact with { TerminalObservedAtUtc = fact.TerminalObservedAtUtc!.Value.AddSeconds(1) },
                            "missing-terminal" => fact with { Terminal = false },
                            _ => throw new InvalidOperationException(fault),
                        };
                        return scan with { Facts = new[] { fact } };
                    };
                    var denied = await facade.RecoverExternalStartObservationsAsync();
                    Assert.True(denied.ScanFactConflicts == 1, "unconfirmed/conflicting archived replay must remain unresolved: " + denied);
                    Assert.Equal(retainedBytes, System.Text.Json.JsonSerializer.Serialize(Store(currentHost).Read().File!.Handoff));
                    Assert.Equal(2, sends);
                }
                finally { hooks.TakeoverLedgerScan = originalScan; hooks.TakeoverTerminalPayloadConfirmed = originalConfirm; }
            }
            var replay = await facade.RecoverExternalStartObservationsAsync();
            Assert.True(replay.ScanFactConflicts == 0, "original settled archived terminal replay: " + replay);
            if (archiveAfterTerminal) Assert.Single(Store(currentHost).Read().File!.Handoff!.ArchivedOperations);'''
assert s.count(needle)==1;s=s.replace(needle,repl).replace('$"host-late-{archiveBeforeReceipt}-{archiveAfterTerminal}.json"','$"host-late-{archiveBeforeReceipt}-{archiveAfterTerminal}-{fault}.json"')
p.write_bytes(s.encode('utf-8'));(d/'fault-test-edit-observation.json').write_text(json.dumps(dict(before_sha256=hashlib.sha256(b).hexdigest(),after_sha256=hashlib.sha256(p.read_bytes()).hexdigest(),bytes=len(p.read_bytes())),indent=2),encoding='utf-8')
