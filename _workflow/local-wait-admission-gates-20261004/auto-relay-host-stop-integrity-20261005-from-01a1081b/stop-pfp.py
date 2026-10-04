import os, sys, json, hashlib, subprocess, xml.etree.ElementTree as ET
from pathlib import Path

root = Path.cwd()
base = root / '_workflow/local-wait-admission-gates-20261004/auto-relay-host-stop-integrity-20261005-from-01a1081b'
host = 'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs'
admission = 'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'
def shared_shutdown(t):
    a = 'if (_shutdownTask is not null) return _shutdownTask;'
    assert t.count(a) == 1
    return t.replace(a, 'if (_shutdownTask is not null) return Task.CompletedTask;')
def original_mapping(t):
    a = t.index('    private static bool HasOriginalAdmissionMapping')
    b = t.index('    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync', a)
    return t[:a] + '    private static bool HasOriginalAdmissionMapping(WorkflowRunRecord run) => false;\n\n' + t[b:]
def absent_shortcut(t):
    a = '        WorkflowRunRecord? originalRun;'
    assert t.count(a) == 1
    return t.replace(a, '        if (_admissionStore is { } absent && absent.Read().Status == ArbitrationLeaseStatus.Absent)\n            return AdmissionTerminalReconciliationOutcome.NoMapping;\n' + a)
def late_registration(t):
    a = '            _driveCompletions.Add(entry);'
    assert t.count(a) == 1
    return t.replace(a, '            // Mutant omits complete startup observation responsibility.')
def lock_cancel(t):
    a = '            try { cts.Cancel(); }'
    assert t.count(a) == 1
    return t.replace(a, '            try { lock (_gate) cts.Cancel(); }')
def mapping_guard(t):
    a=t.index('        if (authorizedMapping is null)')
    b=t.index('        Require(current.AdmissionSourceScope',a)
    return t[:a]+t[b:]
def new_mapping_guard(t):
    a='        if (currentText is null && rec.AdmissionMappings is not null)\n            throw new RunRecordConflictException("新记录不能补造原准入映射。");\n'
    assert t.count(a)==1
    return t.replace(a,'')

def unqualified_constructor(t):
    a = '_runs = new RunStore(runsDir, requireOwnership: admissionWired);'
    assert t.count(a) == 1
    return t.replace(a, '_runs = new RunStore(runsDir);')

def diagnostic_escalation(t):
    a = 'if (rec.NonExecutingDiagnostic && !authorizedDiagnostic)'
    b = 'if (current?.NonExecutingDiagnostic == true)'
    assert t.count(a) == t.count(b) == 1
    return t.replace(a, 'if (false)').replace(b, 'if (false)')

def late_owner_publish(t):
    a = 'using var ownerFence = authorizedDiagnostic ? null : _owner?.Store.AcquireOwnerFence(_owner);'
    b = 'if (!authorizedDiagnostic) _owner?.Store.VerifyOwnerFence(_owner);'
    assert t.count(a) == t.count(b) == 1
    return t.replace(a, '// Mutant omits original-owner physical fence.').replace(b, '// Mutant omits original-owner final publication check.')

def missing_expected(t):
    start=t.index('    private static bool OriginalAdmissionMappingsPresent')
    end=t.index('    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync',start)
    return t[:start]+'    private static bool OriginalAdmissionMappingsPresent(WorkflowRunRecord run, IReadOnlyList<OperationRecord> operations) => true;\n\n'+t[end:]

def legacy_no_anchor(t):
    a='            || run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait, Binding: not null }\n'
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

args=sys.argv[1:]
revision=''
if '--revision' in args:
    at=args.index('--revision'); revision='-'+args[at+1]; del args[at:at+2]
if args: mutants = [m for m in mutants if m[0] in args]
mutants=[(m[0]+revision,*m[1:]) for m in mutants]
records = []
observation_path = base / ('host-pfp-' + '-'.join(m[0] for m in mutants) + '-observation.json')
assert not observation_path.exists(), 'Original mutation observation must not be overwritten'
def save():
    observation_path.write_text(json.dumps(dict(kind='ordinary causal P/F/P; not authenticated mutation receipt', records=records), ensure_ascii=False, indent=2), encoding='utf-8')
def atomic(p, data):
    temp = p.with_name(p.name + '.host-pfp-restore.tmp')
    temp.write_bytes(data)
    os.replace(temp, p)
def leg(id, phase, source, filter, sha):
    name = 'host-pfp-' + id + '-' + phase
    code = subprocess.call([sys.executable, '-B', str(base / 'run-stop-check.py'), name, 'FullyQualifiedName~' + filter], cwd=root)
    out = base / name
    ns = {'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
    trx = out / 'results.trx'
    results = []
    if trx.exists():
        tree = ET.parse(trx)
        for r in tree.findall('.//t:UnitTestResult', ns):
            results.append(dict(id=r.attrib['testId'], name=r.attrib['testName'], outcome=r.attrib['outcome'], error=r.findtext('t:Output/t:ErrorInfo/t:Message', default='', namespaces=ns), stack=r.findtext('t:Output/t:ErrorInfo/t:StackTrace', default='', namespaces=ns)))
    row = dict(mutation=id, leg=phase, source=source, source_sha256=sha, exit=code, directory=str(out.relative_to(root)), results=results)
    records.append(row)
    save()
    return row
for id, source, patch, filter, assertion in mutants:
    p = root / source
    original = p.read_bytes()
    sha = hashlib.sha256(original).hexdigest()
    nl = '\r\n' if b'\r\n' in original else '\n'
    changed = patch(original.decode('utf-8').replace('\r\n', '\n')).replace('\n', nl).encode('utf-8')
    row = leg(id, 'baseline', source, filter, sha)
    assert row['exit'] == 0 and row['results'] and all(r['outcome']=='Passed' for r in row['results']), row
    try:
        atomic(p, changed)
        row = leg(id, 'negative', source, filter, hashlib.sha256(changed).hexdigest())
        assert row['exit'] != 0 and any(r['outcome']=='Failed' and assertion.lower() in r['error'].lower() for r in row['results']), row
    finally:
        atomic(p, original)
        assert hashlib.sha256(p.read_bytes()).hexdigest() == sha
        records.append(dict(mutation=id, leg='source-restoration', source=source, restored_sha256=sha))
        save()
    row = leg(id, 'restored', source, filter, sha)
    assert row['exit'] == 0 and row['results'] and all(r['outcome']=='Passed' for r in row['results']), row
    print('verified P/F/P', id, flush=True)
