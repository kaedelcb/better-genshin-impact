from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs')
s=p.read_bytes().decode('utf-8').replace('\r\n','\n')
marker='    [Theory]\n    [InlineData(false)]\n    [InlineData(true)]\n    public async Task TerminalStop_PartialOriginalMappingLoss'
test='''    [Fact]
    public async Task ParkedDriveCompletion_DoesNotStartTerminalReconciliation()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        var reconciliations = 0;
        host.AdmissionTerminalReconciliationCompletedForTest = () => Interlocked.Increment(ref reconciliations);
        try
        {
            Assert.Equal(HostActionStatus.Registered, (await host.StartWorkflowAsync(workflowId)).Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(10)));
            await host.ShutdownAsync();
            Assert.Equal(WorkflowRunState.LocalWaitParking, Assert.Single(host.Runs.List()).State);
            Assert.Equal(0, reconciliations);
        }
        finally { await host.ShutdownAsync(); }
    }

'''
assert s.count(marker)==1
p.write_bytes(s.replace(marker,test+marker).replace('\n','\r\n').encode('utf-8'))
