from pathlib import Path
import json,hashlib,subprocess
r=Path.cwd(); d=Path(__file__).parent
p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterExternalStartAdmissionTests.cs'
b=p.read_bytes(); s=b.decode('utf-8'); (d/'external-test-before.cs').write_bytes(b)
before=dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),crlf=b.count(b'\r\n'),lf=b.count(b'\n'),bom=b.startswith(b'\xef\xbb\xbf'))
needle='    [InlineData(true, true, "missing-terminal")]'
assert s.count(needle)==1
s=s.replace(needle,needle+'\n    [InlineData(false, false, "causal-matrix")]\n    [InlineData(true, false, "causal-matrix")]\n    [InlineData(false, true, "causal-matrix")]\n    [InlineData(true, true, "causal-matrix")]')
needle='        try\n        {\n            oldHost = NewHost(root, new TaskCenterAdmissionSeams { Epoch = "9:900" });'
helper='''        async Task CheckOriginalLedgerCausality(TaskCenterHost owner, string phase)
        {
            var ledgerPath = Path.Combine(root, "external-start-ledger.json");
            var originalBytes = File.ReadAllBytes(ledgerPath);
            var before = System.Text.Json.JsonSerializer.Serialize(Store(owner).Read().File!.Handoff);
            foreach (var field in new[] { "candidateId", "resourceRef", "actionId", "targetBgiEpoch" })
            foreach (var missing in new[] { false, true })
            {
                try
                {
                    var document = System.Text.Json.Nodes.JsonNode.Parse(originalBytes)!;
                    document["entries"]![0]![field] = missing ? "" : "wrong-original-" + field;
                    File.WriteAllText(ledgerPath, document.ToJsonString());
                    var read = new ExternalStartLedger(root).Read();
                    Assert.Equal(!missing, read.Valid);
                    var denied = await Facade(owner).RecoverExternalStartObservationsAsync();
                    Assert.True(missing ? denied.LedgerUnreadable : denied.ScanFactConflicts == 1,
                        $"original ledger causality {phase}/{field}/missing={missing}: {denied}");
                    Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(Store(owner).Read().File!.Handoff));
                    Assert.Equal(2, sends);
                }
                finally { File.WriteAllBytes(ledgerPath, originalBytes); }
            }
        }
'''
assert s.count(needle)==1;s=s.replace(needle,helper+needle)
needle='            var recovery = await facade.RecoverExternalStartObservationsAsync();\n            Assert.True(recovery.HistoricalAcceptanceReceiptsHeld == 1, recovery.ToString());'
assert s.count(needle)==1;s=s.replace(needle,'            if (fault == "causal-matrix") await CheckOriginalLedgerCausality(currentHost, "late-receipt");\n'+needle)
needle='            var terminal = await facade.RecoverExternalStartObservationsAsync();\n            Assert.True(terminal.HistoricalAcceptanceTerminalsFinalized == 1, terminal.ToString());'
assert s.count(needle)==1;s=s.replace(needle,'            if (fault == "causal-matrix") await CheckOriginalLedgerCausality(currentHost, "late-terminal");\n'+needle)
s=s.replace('            if (fault != "valid")\n','            if (fault == "causal-matrix") await CheckOriginalLedgerCausality(currentHost, "settled-replay");\n            if (fault != "valid" && fault != "causal-matrix")\n')
needle='            var all = Store(reopenedHost).Read().File!.Handoff!;'
assert s.count(needle)==1;s=s.replace(needle,'            if (fault == "causal-matrix") await CheckOriginalLedgerCausality(reopenedHost, "new-host");\n'+needle)
p.write_bytes(s.encode('utf-8')); a=p.read_bytes(); (d/'test-edit-observation.json').write_text(json.dumps(dict(path=str(p.relative_to(r)),before=before,after=dict(bytes=len(a),lines=len(a.splitlines()),sha256=hashlib.sha256(a).hexdigest(),crlf=a.count(b'\r\n'),lf=a.count(b'\n')),kind='red fixture candidate; actual temporary ledger tamper, restored finally'),indent=2),encoding='utf-8')
print('test delta',len(a)-len(b))
