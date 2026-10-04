from pathlib import Path
import json,hashlib
r=Path.cwd();d=Path(__file__).parent;p=r/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/HistoricalExecutionObservationTests.cs';b=p.read_bytes();(d/'historical-tests.pre-association').write_bytes(b);s=b.decode('utf-8').replace('\r\n','\n')
needle='    [InlineData("result")]';assert s.count(needle)==1
s=s.replace(needle,needle+'\n    [InlineData("duplicate-association")]\n    [InlineData("conflicting-association")]')
needle='        var (store, run, _, op) = Seed(true, true);';assert s.count(needle)==1;s=s.replace(needle,'        var (store, run, evidence, op) = Seed(true, true);')
needle='        if (fault == "result") outcome.Result = "succeeded";\n        else';assert s.count(needle)==1
s=s.replace(needle,'''        if (fault.StartsWith("duplicate-association", StringComparison.Ordinal) || fault == "conflicting-association")
        {
            evidence.HistoryHash = TerminalReleaseEvidence.Hash(sub);
            evidence.OutcomeHash = TerminalReleaseEvidence.Hash(outcome);
            run.RecoveryAssociations.Add(evidence);
            if (fault == "duplicate-association")
                run.RecoveryAssociations.Add(JsonSerializer.Deserialize<RecoveryAssociationRecord>(JsonSerializer.Serialize(evidence))!);
            else evidence.JobId = "foreign-original-job";
        }
        else if (fault == "result") outcome.Result = "succeeded";
        else''')
out=s.replace('\n','\r\n').encode('utf-8');p.write_bytes(out);(d/'association-test-edit.json').write_text(json.dumps(dict(before=hashlib.sha256(b).hexdigest(),after=hashlib.sha256(out).hexdigest(),bytes_before=len(b),bytes_after=len(out)),indent=2),encoding='utf-8')
