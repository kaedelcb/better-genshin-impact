from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode().replace('\r\n','\n')
s=s.replace('OriginalLifecycle_NewHostResumePreservesParentAndCompletedNode','OriginalLifecycle_RealPausedAndNewHostInterruptedPreserveOriginalSend')
s=s.replace('                    await first.ShutdownAsync(); // old writer is terminal before any new Host','''                    for (var i = 0; i < 1000 && first.IsDriving(original.WorkflowId); i++) await Task.Delay(10);
                    Assert.False(first.IsDriving(original.WorkflowId));
                    if (interrupted) await first.ShutdownAsync(); // old writer is terminal before new Host''')
s=s.replace('var identityBefore = JsonSerializer.Serialize(nodeBefore);','var identityBefore = OriginalSendBytes(nodeBefore);')
s=s.replace('                    var second = new TaskCenterHost(Path.Combine(root, "flows"),','                    var second = interrupted ? new TaskCenterHost(Path.Combine(root, "flows"),')
s=s.replace('ProductionBoundaryFactory = (_, store) => new BgiWorkflowExecutionBoundary(port, store) });\n                    try','ProductionBoundaryFactory = (_, store) => new BgiWorkflowExecutionBoundary(port, store) }) : first;\n                    try')
s=s.replace('Assert.Equal(identityBefore, JsonSerializer.Serialize(oldNode));','''Assert.Equal(identityBefore, OriginalSendBytes(oldNode));
                        Assert.Equal(OperationRequestState.TerminalCompleted, oldNode.RequestState);
                        Assert.False(string.IsNullOrEmpty(oldNode.TerminalReleaseEvidence));''')
s=s.replace('                    finally { await second.ShutdownAsync(); }','                    finally { if (interrupted) await second.ShutdownAsync(); }')
anchor='    [Theory]\n    [InlineData(false, false)]\n'
assert s.count(anchor)==1
s=s.replace(anchor,'''    private static string OriginalSendBytes(OperationRecord op) => JsonSerializer.Serialize(new
    {
        op.RequestIdentity, op.CandidateId, op.Candidate, op.RunBinding, op.OperationType,
        op.ParentSource, op.ParentRequestIdentity, op.SubmissionIdentity, op.LastSendSeq,
        op.WireSubmitKey, op.TargetEpoch, op.SendPermit, op.PreObservations,
    });

'''+anchor)
p.write_bytes(s.replace('\n','\r\n').encode())
