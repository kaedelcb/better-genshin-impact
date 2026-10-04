from pathlib import Path
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs')
s=p.read_bytes().decode('utf-8').replace('\r\n','\n')
needle='''            Assert.Empty(ReadAdmissionOperationsForRun(run.RunId));
        }
        finally
        {
            File.WriteAllBytes(leasePath, originalLease);'''
replace='''            Assert.Empty(ReadAdmissionOperationsForRun(run.RunId));
            // Repair only the missing original handoff under the currently qualified owner.
            var currentStore = (ArbitrationLeaseStore)typeof(TaskCenterHost).GetField("_admissionStore",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
            var owner = currentStore.Read().File!.Lease!;
            var originalHandoff = JsonSerializer.Deserialize<LogicalOwnerLeaseFile>(originalLease)!.Handoff;
            Assert.True(currentStore.MutateHandoffLatest(owner.LeaseId, owner.OwnerEpoch, file =>
                { file.Handoff = originalHandoff; return null; }).Success);
            Assert.Equal(HostActionStatus.Effective, (await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)).Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            Assert.Null(host.Runs.Load(run.RunId)!.AdmissionMappings);
        }
        finally
        {
            File.WriteAllBytes(leasePath, originalLease);'''
assert s.count(needle)==1
s=s.replace(needle,replace)
needle='''            if (!finalWindow)
                Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.Accepted, op.RequestState));
        }
        finally'''
replace='''            if (!finalWindow)
                Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.Accepted, op.RequestState));
            host.AdmissionTerminalReadFaultForTest = null;
            File.WriteAllBytes(leasePath, originalLease);
            Assert.Equal(HostActionStatus.Effective, (await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)).Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            Assert.Equal(2, host.Runs.Load(run.RunId)!.AdmissionMappings!.Count);
        }
        finally'''
assert s.count(needle)==1
s=s.replace(needle,replace)
p.write_bytes(s.replace('\n','\r\n').encode('utf-8'))
base=Path(__file__).parent
previous=Path('_workflow/local-wait-admission-gates-20261004/auto-relay-host-writer-qualified-20261005-from-01a107e1/qualified-pfp.py').read_text(encoding='utf-8')
start=previous.index('mutants = [')
end=previous.index('\nargs=sys.argv',start)
new='''def missing_expected(t):
    start=t.index('    private static bool OriginalAdmissionMappingsPresent')
    end=t.index('    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync',start)
    return t[:start]+'    private static bool OriginalAdmissionMappingsPresent(WorkflowRunRecord run, IReadOnlyList<OperationRecord> operations) => true;\\n\\n'+t[end:]

def legacy_no_anchor(t):
    a='            || run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait, Binding: not null }\\n'
    assert t.count(a)==1
    return t.replace(a,'')

def parked_observer(t):
    a='                if (reconcileObservedTerminal)'
    assert t.count(a)==1
    return t.replace(a,'                if (true)')

mutants = [
    ('partial-original-mapping', admission, missing_expected, 'TerminalStop_PartialOriginalMappingLossCannotReleaseRemainingRegistration', 'Expected: Unavailable'),
    ('legacy-missing-original', admission, legacy_no_anchor, 'TerminalStop_LegacyParkedRunWithoutNewMappingAnchorCannotClaimMissingLedgerAsNoMapping', 'Expected: Unavailable'),
    ('parked-observer-terminal-race', host, parked_observer, 'ParkedDriveCompletion_DoesNotStartTerminalReconciliation', 'Expected: 0'),
]
'''
previous=previous[:start]+new+previous[end:]
previous=previous.replace('auto-relay-host-writer-qualified-20261005-from-01a107e1','auto-relay-host-stop-integrity-20261005-from-01a1081b').replace('run-qualified-check.py','run-stop-check.py')
(base/'stop-pfp.py').write_text(previous,encoding='utf-8')
