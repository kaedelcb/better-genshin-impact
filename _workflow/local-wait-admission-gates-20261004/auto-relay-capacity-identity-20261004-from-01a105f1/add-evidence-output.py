from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterSuccessorPathGateTests.cs');b=p.read_bytes();s=b.decode('utf-8');nl='\r\n' if b.count(b'\r\n') else '\n';s=s.replace('\r\n','\n')
a='''                    Assert.Equal(33, reloaded.NodeOutcomes.Count);
                });'''
c='''                    Assert.Equal(33, reloaded.NodeOutcomes.Count);
                    var evidenceDir = Environment.GetEnvironmentVariable("BGI_CAPACITY_EVIDENCE_DIR");
                    if (!string.IsNullOrEmpty(evidenceDir))
                        File.WriteAllText(Path.Combine(evidenceDir, handoff ? "handoff-capacity-original.json" : "panel-capacity-original.json"),
                            JsonSerializer.Serialize(new
                            {
                                source = handoff ? "StartupHandoff" : "PanelFlowRegistration",
                                admissionResults = results.Select(o => new { node = o.Node, kind = o.Kind.ToString(), reason = o.Reason, request = o.Request, identity = o.Identity, seq = o.Seq }),
                                occupiedReadbacks = before.Select((h, i) => new { ordinal = i + 1,
                                    active = h.Operations.Count(o => o.Zone == OperationZone.Active),
                                    pendingTransfer = h.Operations.Count(o => o.Zone == OperationZone.TerminalPendingTransfer),
                                    tombstone = h.Operations.Count(o => o.Zone == OperationZone.Tombstone),
                                    originalNodes = h.Operations.Where(o => o.OperationType == OperationType.NodeExecution).ToArray() }),
                                agedOriginalBytes = agedJson, reopenedArchives = reopened.File.Handoff.ArchivedOperations,
                                run = reloaded, originalSends = sends, finalSends = port.SendCount,
                                limitation = "real Host/Runner/RunStore/LeaseStore, controlled execution port, simulated retention age; not IPC/game/User acceptance"
                            }));
                });'''
assert s.count(a)==1;s=s.replace(a,c)
a='    [Theory]\n    [InlineData(false)]\n    [InlineData(true)]\n    public async Task Capacity_33ActualAdmissions_OriginalIdentitySurvivesArchiveAndReopen(bool handoff)'
c='''    [Fact]
    public async Task AdmissionScalarObserverFailure_PreservesActualSendResult()
    {
        var root = NewRoot("tc-observer-fault-");
        try
        {
            var observations = 0;
            var probe = await ProbeNodeSubmitRoutingAsync(root, true, configureSeams: seams =>
                seams.SuccessorAdmissionObservedForTest = (_, _, _, _, _, _) =>
                {
                    observations++;
                    throw new IOException("test observer failed");
                });
            Assert.Equal(1, observations);
            Assert.True(probe.Converged, Diag("observer failure preserves convergence", probe));
            Assert.Equal(WorkflowRunState.Succeeded, probe.State);
            Assert.Equal(1, probe.SendCount);
        }
        finally { TryDelete(root); }
    }

'''+a
assert s.count(a)==1;s=s.replace(a,c);p.write_bytes(s.replace('\n',nl).encode('utf-8'))
