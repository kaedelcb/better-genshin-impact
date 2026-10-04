from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs')
b=p.read_bytes()
assert b'\r\n' not in b, 'keep original LF'
s=b.decode('utf-8')
start=s.index('    [Theory]\n    [InlineData("corrupt")]\n    [InlineData("unsupported")]\n    public async Task LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping')
end=s.index('    [Fact]\n    public async Task PersistentHoldStopWithAdmissionWired',start)
body=s[start:end]
body=body.replace('LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping','UnknownOwnerStop_PreservesOriginalRunAndQueueThenLegitimateOwnerRetries')
body=body.replace('        var originalLeaseBytes = File.ReadAllBytes(leasePath);','        var originalLeaseBytes = File.ReadAllBytes(leasePath);\n        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");\n        var originalRunBytes = File.ReadAllBytes(runPath);\n        var originalQueueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);')
body=body.replace('            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);','            Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);\n            Assert.Equal(originalRunBytes, File.ReadAllBytes(runPath));\n            Assert.Equal(originalQueueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));')
body=body.replace('            Assert.Equal(HostActionStatus.Effective, retried.Status);','            Assert.Equal(HostActionStatus.Effective, retried.Status);\n            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);\n            Assert.True(host.Runs.Load(run.RunId)!.StopRequested);')
body='    // Candidate safety contract; the conflicting original Cancelled assertion remains unchanged\n    // and open for the single full independent adjudication, not declared superseded here.\n'+body
p.write_bytes((s[:end]+body+s[end:]).encode('utf-8'))
