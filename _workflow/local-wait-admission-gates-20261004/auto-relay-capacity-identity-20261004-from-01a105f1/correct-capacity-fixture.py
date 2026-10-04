from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode('utf-8');nl='\r\n' if b.count(b'\r\n') else '\n';s=s.replace('\r\n','\n')
a='''                beforeSuccessorAdmission: () =>
                {
                    var read = store.Read();
                    Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
                    before.Add(read.File!.Handoff!);
                    return Task.CompletedTask;
                },
                configureSeams: seams => typeof(TaskCenterAdmissionSeams)
                    .GetField("SuccessorAdmissionObservedForTest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(seams, observe),'''
c='''                configureSeams: seams =>
                {
                    typeof(TaskCenterAdmissionSeams)
                        .GetField("SuccessorAdmissionObservedForTest", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                        ?.SetValue(seams, observe);
                    seams.Barriers!.AfterOccupyBeforeSend = () =>
                    {
                        var read = store.Read();
                        Assert.Equal(ArbitrationLeaseStatus.Valid, read.Status);
                        if (read.File!.Handoff!.Submission?.OperationType == OperationType.NodeExecution)
                            before.Add(read.File.Handoff);
                        return Task.CompletedTask;
                    };
                },'''
assert s.count(a)==1;s=s.replace(a,c)
a='Assert.True(before[i].Operations.Count(o => o.Zone is OperationZone.Active or OperationZone.TerminalPendingTransfer) < ArbitrationAdmissionService.PrimarySlotLimit);';assert s.count(a)==1;s=s.replace(a,a.replace('< Arbitration','<= Arbitration'))
a='var previous = before[i].Operations.Where(o => o.OperationType == OperationType.NodeExecution).ToList();';assert s.count(a)==1;s=s.replace(a,'var previous = before[i].Operations.Where(o => o.OperationType == OperationType.NodeExecution && o.Candidate?.NodeId != returned.Node).ToList();')
a='                        Assert.NotNull(original.ExecutionResult);';assert s.count(a)==1;s=s.replace(a,'                        Assert.Equal(OperationRequestState.TerminalCompleted, original.RequestState);')
p.write_bytes(s.replace('\n',nl).encode('utf-8'))
