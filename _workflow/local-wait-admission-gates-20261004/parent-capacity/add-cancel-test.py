from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationAdmissionServiceTests.cs')
b=p.read_bytes(); needle=b'public class ArbitrationAdmissionServiceTests : IDisposable\r\n{\r\n'
test='''    [Fact]
    public async Task TerminalWriteback_CancelledGateWaitKeepsOriginalResponsibilityAndCanRetry()
    {
        var (svc, store, ledger, hooks) = BuildFacade(h => h.TakeoverTerminalConfirmed = (_, _) => true);
        var request = Req();
        Assert.Equal(AdmissionResultKind.Accepted, (await svc.AdmitAsync(request)).Kind);
        var original = store.Read().File!.Handoff!.Operations.Single();
        var gate = (SemaphoreSlim)typeof(ArbitrationAdmissionService)
            .GetField("_gate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(svc)!;
        using var cts = new CancellationTokenSource();
        await gate.WaitAsync();
        try
        {
            var waiting = svc.MarkOperationTerminalAsync(original.RequestIdentity, "original-terminal", cts.Token);
            Assert.False(waiting.IsCompleted);
            cts.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            var retained = store.Read().File!.Handoff!.Operations.Single();
            Assert.Equal(OperationRequestState.Accepted, retained.RequestState);
            Assert.Equal(original.SubmissionIdentity, retained.SubmissionIdentity);
            Assert.Equal(original.LastSendSeq, retained.LastSendSeq);
            Assert.Equal(original.Zone, retained.Zone);
            Assert.Null(retained.TerminalReleaseEvidence);
        }
        finally { gate.Release(); }
        Assert.Equal(AdmissionResultKind.Accepted,
            (await svc.MarkOperationTerminalAsync(original.RequestIdentity, "original-terminal")).Kind);
        Assert.Equal(OperationRequestState.TerminalCompleted, store.Read().File!.Handoff!.Operations.Single().RequestState);
    }

'''
assert b.count(needle)==1
p.write_bytes(b.replace(needle,needle+test.replace('\n','\r\n').encode()))
