"""Finish the actual cold-recovery UI entry: use the existing guarded host recovery."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, time, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent
OUT = BASE / 'cold-resume'
SOURCE = 'MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
sys.path.insert(0, str(ROOT / 'tools/mistletoe')); sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p
sha = lambda data: hashlib.sha256(data).hexdigest()
original_size = s.size
generated = [ROOT / 'MultiplayerHoeingAssistant/obj', ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/obj']
retries = []
def stable_size(roots):
    for attempt in range(5):
        try: return original_size(roots)
        except FileNotFoundError as error:
            missing = Path(error.filename).absolute()
            if attempt == 4 or not any(missing.is_relative_to(folder) for folder in generated): raise
            retries.append(dict(path=str(missing), complete_collection_retried=True)); time.sleep(.1)
s.size = stable_size
with s.Session(ROOT, 'own-root-01a114ee-normal-cold-resume-ui-entry') as budget:
    roots = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(roots)); assert set(roots) == set(budget.old_roots)
    carrier = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
    for folder in [OUT, carrier, *generated, PRODUCT / 'Tools/MultiplayerHoeingAssistant']: budget.track(folder)
    def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode())
    previous = BASE / 'own-runtime/fifteenth-normal-entry'
    assert json.loads((previous / 'result.json').read_bytes())['exit_code'] == 0
    assert json.loads((previous / 'apps-tree-terminal.json').read_bytes())['active_processes'] == 0
    source = ROOT / SOURCE; before = source.read_bytes(); s.write(OUT / 'before.cs', before)
    ending = b'\r\n' if b'\r\n' in before else b'\n'
    old = b'        CanResume = run.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused or WorkflowRunState.LocalWaitParking;'
    new = ending.join([
        '        // 无驱动的旧 Running/Waiting 允许显式恢复；宿主屏障负责 Interrupted/Unknown，界面不改记录。'.encode(),
        b'        CanResume = run.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused or WorkflowRunState.LocalWaitParking',
        b'            || (!driving && run.State is WorkflowRunState.Running or WorkflowRunState.Waiting);'])
    assert before.count(old) == 1
    temp = source.with_name(source.name + '.own-01a114ee.tmp'); assert not temp.exists()
    with temp.open('xb') as stream: stream.write(before.replace(old, new)); stream.flush(); os.fsync(stream.fileno())
    os.replace(temp, source); assert source.read_bytes() == before.replace(old, new)
    write(OUT / 'admission.json', dict(function='normal stop/restart and explicit recovery UI', gap='actual cold run still Running, no driver; Stop unavailable and Resume disabled', evidence='../own-runtime/fifteenth-normal-entry/private/native-ui-source.jsonl', minimal_change='enable existing ResumeRunAsync only for undriven Running/Waiting; host recovery/scope/Unknown/admission guards unchanged', affected_regression='existing FullProductUiTests and TaskCenterPanelViewModelTests only', new_tests=False, reverse_mutation=False, reason='one reversible UI availability condition; actual red entry already observed; no new proof scope', review_requests_new=0, review_budget_remaining=0, product_complete=False))
    hashes = {file.relative_to(ROOT).as_posix(): sha(file.read_bytes()) for folder in ['MultiplayerHoeingAssistant', 'Test/MultiplayerHoeingAssistant.UnitTest'] for file in (ROOT / folder).rglob('*') if file.suffix in ['.cs', '.xaml', '.csproj'] and '_wpftmp' not in file.name and not any(part in file.parts for part in ['bin', 'obj'])}
    write(OUT / 'source-hashes.json', hashes)
    env = os.environ.copy(); env.update(NEXUSBGI_DATA_ROOT=str(OUT / 'own-data'), TEMP=str(OUT / 'temp'), TMP=str(OUT / 'temp')); Path(env['TEMP']).mkdir()
    build, _, _ = p.run(['C:/Program Files/dotnet/dotnet.exe', 'build', str(ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'), '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:DeployToBgiTools=false', '-t:Rebuild', '-maxcpucount:1', '-o', str(carrier)], cwd=ROOT, env=env, directory=OUT, recovery_directory=OUT, phase='build', timeout=1200)
    assert build == 0
    code, _, _ = p.run(['C:/Program Files/dotnet/dotnet.exe', 'vstest', str(carrier / 'MultiplayerHoeingAssistant.UnitTest.dll'), '--TestCaseFilter:FullyQualifiedName~FullProductUiTests|FullyQualifiedName~TaskCenterPanelViewModelTests', '--logger:trx;LogFileName=cold-resume.trx', '--ResultsDirectory:' + str(OUT)], cwd=ROOT, env=env, directory=OUT, recovery_directory=OUT, phase='test', timeout=600)
    rows = ET.parse(OUT / 'cold-resume.trx').findall('.//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}UnitTestResult')
    failed = [r.get('testName') for r in rows if r.get('outcome') != 'Passed']
    drift = [name for name, digest in hashes.items() if sha((ROOT / name).read_bytes()) != digest]
    write(OUT / 'result.json', dict(build=build, test=code, passed=len(rows)-len(failed), failed=failed, source_drift=drift, scan_retries=retries, actual_entry_red=True, actual_entry_green=False, independent_review=False, product_complete=False))
    assert code == 0 and not failed and not drift
    refresh = BASE / 'own-runtime/refresh-cold-resume'; old_modules = json.loads((BASE / 'own-runtime/refresh-backup/result.json').read_bytes())
    assert all(sha((PRODUCT / row['path']).read_bytes()) == row['sha256'] for row in old_modules['updated'])
    before_user = {f.relative_to(PRODUCT / 'User').as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(PRODUCT / 'User')}
    updated = []
    for row in old_modules['updated']:
        target = PRODUCT / row['path']; s.write(refresh / 'before' / target.name, target.read_bytes())
        temp = target.with_name(target.name + '.own-01a114ee.tmp'); s.write(temp, (carrier / target.name).read_bytes()); os.replace(temp, target)
        assert target.read_bytes() == (carrier / target.name).read_bytes(); updated.append(dict(path=row['path'], sha256=sha(target.read_bytes())))
    assert before_user == {f.relative_to(PRODUCT / 'User').as_posix(): sha(f.read_bytes()) for f, _ in s.files_under(PRODUCT / 'User')}
    assert sha((PRODUCT / 'BetterGI.dll').read_bytes()) == old_modules['bgi_sha256'] and sha((PRODUCT / 'BetterGI.exe').read_bytes()) == old_modules['bgi_exe_sha256']
    write(refresh / 'result.json', dict(updated=updated, bgi_sha256=old_modules['bgi_sha256'], bgi_exe_sha256=old_modules['bgi_exe_sha256'], source_hashes=str(OUT / 'source-hashes.json'), user_files=len(before_user), product_user_changed=[], actual_ui=False, product_complete=False))
    print(json.dumps({'build':build,'passed':len(rows),'failed':failed,'source_drift':drift,'product_refreshed':True}), flush=True)
