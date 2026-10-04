import json, hashlib
from pathlib import Path
base = Path(__file__).parent
p = Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs')
b = p.read_bytes()
(base/'stop-test-before.json').write_text(json.dumps(dict(path=str(p),bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b)),encoding='utf-8')
marker = '    [Theory]\n    [InlineData("remove")]'
test = '''    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TerminalStop_PartialOriginalMappingLossCannotReleaseRemainingRegistration(bool finalWindow)
    {
        var (host, workflowId, run) = await StartAdmissionWiredParkedRun();
        var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
        byte[]? originalLease = null;
        try
        {
            Assert.Equal(HostActionStatus.Registered, (await host.ResumeRunAsync(run.RunId)).Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(10)));
            run = host.Runs.Load(run.RunId)!;
            Assert.Equal(2, run.AdmissionMappings!.Count);
            originalLease = File.ReadAllBytes(leasePath);
            var missingIdentity = run.AdmissionMappings[0].RequestIdentity;
            void RemoveOriginal()
            {
                var document = JsonNode.Parse(File.ReadAllBytes(leasePath))!.AsObject();
                var operations = document["handoff"]!["operations"]!.AsArray();
                var original = Assert.Single(operations.Where(o => o!["requestIdentity"]!.GetValue<string>() == missingIdentity));
                operations.Remove(original);
                File.WriteAllBytes(leasePath, JsonSerializer.SerializeToUtf8Bytes(document));
                var read = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
                Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
                Assert.Single(ReadAdmissionOperationsForRun(run.RunId));
            }
            if (!finalWindow) RemoveOriginal();
            else host.AdmissionTerminalReadFaultForTest = attempt =>
            {
                if (attempt == 2) RemoveOriginal();
                return null;
            };
            var stop = await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Unavailable, stop.Status);
            Assert.Equal(2, host.Runs.Load(run.RunId)!.AdmissionMappings!.Count);
            Assert.DoesNotContain(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestIdentity == missingIdentity);
            if (!finalWindow)
                Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.Accepted, op.RequestState));
        }
        finally
        {
            host.AdmissionTerminalReadFaultForTest = null;
            if (originalLease is not null) File.WriteAllBytes(leasePath, originalLease);
            await host.ShutdownAsync();
        }
    }

'''
s=b.decode('utf-8').replace('\r\n','\n')
assert s.count(marker)==1
p.write_bytes(s.replace(marker,test+marker).replace('\n','\r\n').encode('utf-8'))
old=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-host-writer-qualified-20261005-from-01a107e1/run-qualified-check.py').read_text(encoding='utf-8')
(base/'run-stop-check.py').write_text(old.replace('auto-relay-host-writer-qualified-20261005-from-01a107e1','auto-relay-host-stop-integrity-20261005-from-01a1081b'),encoding='utf-8')
