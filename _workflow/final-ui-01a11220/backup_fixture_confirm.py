"""Finish the existing fixture correction only; preserve the 144 passing source-bound cases."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent
OUT = BASE / 'runstore-backup/fixture-confirm'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
SOURCE = 'MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs'
TEST = 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/RunStoreTests.cs'
sys.path.insert(0, str(ROOT / 'tools/mistletoe')); sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha = lambda data: hashlib.sha256(data).hexdigest()
with s.Session(ROOT, 'own-root-01a114ee-finish-existing-fixture-and-refresh') as budget:
    roots = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(roots)); assert set(roots) == set(budget.old_roots)
    carrier = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
    for folder in [OUT, carrier, ROOT / 'MultiplayerHoeingAssistant/obj', ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/obj', PRODUCT / 'Tools/MultiplayerHoeingAssistant']:
        budget.track(folder)
    def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode())
    recovery = json.loads((BASE / 'runstore-backup/r3/recovery.json').read_bytes())
    assert recovery['source_restored']
    assert all(sha((ROOT / name).read_bytes()) == digest for name, digest in recovery['source_hashes'].items())
    for name in ['red', 'green', 'negative', 'restored']:
        for job in ['build', 'test']:
            assert json.loads((BASE / 'runstore-backup/r3' / name / (job + '-tree-terminal.json')).read_bytes())['active_processes'] == 0
    source_before = (ROOT / SOURCE).read_bytes(); before = (ROOT / TEST).read_bytes()
    s.write(OUT / 'test-before.cs', before)
    ending = b'\r\n' if b'\r\n' in before else b'\n'
    old = b'        Assert.True(hits > 1);'
    new = ending.join([b'        Assert.True(hits > 0);', b'        if (!missingFile) Assert.True(hits > 1);'])
    assert before.count(old) == 1
    temporary = (ROOT / TEST).with_name('RunStoreTests.cs.own-01a114ee.tmp')
    assert not temporary.exists()
    with temporary.open('xb') as stream: stream.write(before.replace(old, new)); stream.flush(); os.fsync(stream.fileno())
    os.replace(temporary, ROOT / TEST)
    assert (ROOT / SOURCE).read_bytes() == source_before
    hashes = {file.relative_to(ROOT).as_posix(): sha(file.read_bytes()) for folder in ['MultiplayerHoeingAssistant', 'Test/MultiplayerHoeingAssistant.UnitTest'] for file in (ROOT / folder).rglob('*') if file.suffix in ['.cs', '.xaml', '.csproj'] and '_wpftmp' not in file.name and not any(part in file.parts for part in ['bin', 'obj'])}
    write(OUT / 'source-hashes.json', hashes)
    write(OUT / 'scope.json', dict(user_correction='2026-10-07: no expansion or endless verification; finish existing safe operation and return to usable product entries', production_source_unchanged=True, correction='FileNotFound is loud once, not a retryable contention; assert no primary/backup publication', reuse='r3 green/restored 144 passed with identical production sources; old negative fails before this retry-count assertion', new_scope=False, review_requests_new=0, product_complete=False))
    env = os.environ.copy(); env.update(NEXUSBGI_DATA_ROOT=str(OUT / 'own-data'), TEMP=str(OUT / 'temp'), TMP=str(OUT / 'temp'))
    Path(env['TEMP']).mkdir()
    build, _, _ = p.run(['C:/Program Files/dotnet/dotnet.exe', 'build', str(ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'), '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:DeployToBgiTools=false', '-t:Rebuild', '-maxcpucount:1', '-o', str(carrier)], cwd=ROOT, env=env, directory=OUT, recovery_directory=OUT, phase='build', timeout=1200)
    assert build == 0
    code, _, _ = p.run(['C:/Program Files/dotnet/dotnet.exe', 'vstest', str(carrier / 'MultiplayerHoeingAssistant.UnitTest.dll'), '--TestCaseFilter:FullyQualifiedName~Persist_ManyGrowingUpdates|FullyQualifiedName~Persist_BackupPublishFailure', '--logger:trx;LogFileName=confirm.trx', '--ResultsDirectory:' + str(OUT)], cwd=ROOT, env=env, directory=OUT, recovery_directory=OUT, phase='test', timeout=600)
    rows = ET.parse(OUT / 'confirm.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
    assert code == 0 and len(rows) == 3 and all(row.get('outcome') == 'Passed' for row in rows)
    drift = [name for name, digest in hashes.items() if sha((ROOT / name).read_bytes()) != digest]; assert not drift
    write(OUT / 'result.json', dict(build=build, test=code, passed=3, source_drift=drift, production_source_unchanged=True, related_144_reused_from='../r3/restored/backup.trx', original_failure_preserved=True, independent_review=False, actual_runtime=False, product_complete=False))
    refresh = BASE / 'own-runtime/refresh-backup'
    old_modules = json.loads((BASE / 'own-runtime/refresh-viewport/result.json').read_bytes())
    assert all(sha((PRODUCT / row['path']).read_bytes()) == row['sha256'] for row in old_modules['updated'])
    assert sha((PRODUCT / 'BetterGI.dll').read_bytes()) == old_modules['bgi_sha256']
    assert sha((PRODUCT / 'BetterGI.exe').read_bytes()) == old_modules['bgi_exe_sha256']
    before_user = {file.relative_to(PRODUCT / 'User').as_posix(): sha(file.read_bytes()) for file, _ in s.files_under(PRODUCT / 'User')}
    updated = []
    for row in old_modules['updated']:
        target = PRODUCT / row['path']; s.write(refresh / 'before' / target.name, target.read_bytes())
        temp = target.with_name(target.name + '.own-01a114ee.tmp'); s.write(temp, (carrier / target.name).read_bytes()); os.replace(temp, target)
        assert target.read_bytes() == (carrier / target.name).read_bytes()
        updated.append(dict(path=row['path'], sha256=sha(target.read_bytes())))
    assert before_user == {file.relative_to(PRODUCT / 'User').as_posix(): sha(file.read_bytes()) for file, _ in s.files_under(PRODUCT / 'User')}
    write(refresh / 'result.json', dict(updated=updated, bgi_sha256=old_modules['bgi_sha256'], bgi_exe_sha256=old_modules['bgi_exe_sha256'], source_hashes=str(OUT / 'source-hashes.json'), user_files=len(before_user), product_user_changed=[], review_requests_new=0, actual_ui=False, product_complete=False))
    print('EXISTING_FIXTURE_3_PASS; OWN_PRODUCT_MODULES_REFRESHED; USER_UNCHANGED', flush=True)
