import hashlib
import json
import subprocess
from pathlib import Path

root = Path(__file__).resolve().parents[3]
out = Path(__file__).resolve().parent
products = out / 'products'
service = 'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs'
host = 'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'
subjects = [service, host, 'MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs']
original = {p: (root / p).read_bytes() for p in subjects}
sha = lambda b: hashlib.sha256(b).hexdigest()
before = out / 'mutation-before'
before.mkdir(exist_ok=False)
for p, data in original.items():
    target = before / p
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)

def run_build(folder):
    with (folder / 'build.log').open('wb') as log:
        return subprocess.run(['dotnet', 'build', 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj',
                               '-t:Rebuild', '-p:DeployToBgiTools=false', '-o', str(products), '--nologo'],
                              cwd=root, stdout=log, stderr=subprocess.STDOUT).returncode

def run_tests(folder, filter):
    with (folder / 'test.log').open('wb') as log:
        return subprocess.run(['dotnet', 'vstest', str(products / 'MultiplayerHoeingAssistant.UnitTest.dll'),
                               '/TestCaseFilter:' + filter, '/Logger:trx;LogFileName=results.trx',
                               '/ResultsDirectory:' + str(folder)], cwd=root,
                              stdout=log, stderr=subprocess.STDOUT).returncode

cases = [
    ('M1-mirror-detail', service, b'ReasonDetail = currentWinner.LastResult?.ReasonDetail,', b'ReasonDetail = null,',
     'FullyQualifiedName~RejectionDetail_MergedAndReloaded', 'Strings differ'),
    ('M2-unknown-diagnostic', service, b'outcome as SendOutcome.Unknown).ConfigureAwait(false);', b'null).ConfigureAwait(false);',
     'FullyQualifiedName~UnknownDetail_Reloaded', 'Value is null'),
    ('M3-prepare-diagnostic', host,
     b'false, "host:boundary", rej.RejectReason);', b'false, "host:boundary");',
     'FullyQualifiedName~PreparationDiagnostic_OriginalReasonIsPersistedWithoutSending', 'Sub-string not found'),
]
records = []
try:
    baseline = out / 'mutation-baseline'
    baseline.mkdir(exist_ok=False)
    assert run_build(baseline) == 0
    assert run_tests(baseline, '|'.join(c[4] for c in cases)) == 0
    for name, subject, old, new, filter, expected in cases:
        folder = out / name
        folder.mkdir(exist_ok=False)
        assert original[subject].count(old) == 1, name
        mutant = original[subject].replace(old, new)
        (root / subject).write_bytes(mutant)
        try:
            build_exit = run_build(folder)
            assert build_exit == 0, name
            test_exit = run_tests(folder, filter)
            assert test_exit != 0, name
            trx = (folder / 'results.trx').read_text(encoding='utf-8-sig')
            assert expected in trx, name
            records.append({'id': name, 'subject': subject, 'original_sha256': sha(original[subject]),
                            'mutant_sha256': sha(mutant), 'old': old.decode(), 'new': new.decode(),
                            'filter': filter, 'build_exit': build_exit, 'test_exit': test_exit,
                            'failure_contains': expected})
            print(name + ': intended assertion failed', flush=True)
        finally:
            (root / subject).write_bytes(original[subject])
            assert (root / subject).read_bytes() == original[subject]
finally:
    for p, data in original.items():
        (root / p).write_bytes(data)
    restored = out / 'mutation-restored'
    restored.mkdir(exist_ok=False)
    restored_ok = all((root / p).read_bytes() == data for p, data in original.items())
    build_exit = run_build(restored)
    test_exit = run_tests(restored, '|'.join(c[4] for c in cases)) if build_exit == 0 else None
    (out / 'mutation-observation.json').write_text(json.dumps({
        'kind': 'ordinary process mutation observations, not authenticated execution receipt',
        'records': records, 'restored_bytes_equal': restored_ok,
        'restored_build_exit': build_exit, 'restored_test_exit': test_exit,
        'subjects': {p: sha(data) for p, data in original.items()}}, ensure_ascii=False, indent=2), encoding='utf-8')
    print('restored bytes=' + str(restored_ok) + ', build=' + str(build_exit) + ', test=' + str(test_exit), flush=True)
    assert restored_ok and build_exit == 0 and test_exit == 0
