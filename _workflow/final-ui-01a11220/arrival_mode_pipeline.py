"""Bounded repair for the actual fixed-one finding; no additional review dispatch."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT / '_workflow/final-ui-01a11220/arrival-mode-downgrade-01a11897'
CARRIER = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
TARGET = ROOT / 'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlan.Path.cs'
TEST = ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/PathArrivalRevisionTests.cs'
INTERRUPTED = ROOT / '_workflow/final-ui-01a11220/own-runtime/startup-supplement-01a11897'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
import process_runner as p

FULL = '|'.join('FullyQualifiedName~' + n for n in ['PathArrivalRevisionTests', 'PathLoopCutoffTests', 'FormalPathTests', 'FormalScheduleTests', 'WorkflowPlannerTests', 'WorkflowRunnerTests', 'AdapterStopRunnerTests', 'FastLocalTerminalAdmissionTests', 'TerminalReleaseSealTests', 'LocalWaitFinalizationContractTests', 'HistoricalExecutionObservationTests', 'LegacyHistoricalSealIntegrityTests'])
sha = lambda b: hashlib.sha256(b).hexdigest()
load = lambda path: json.loads(path.read_bytes())
def sources():
    return {f.relative_to(ROOT).as_posix(): sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant', 'Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT / folder).rglob('*') if f.suffix in ['.cs', '.xaml', '.csproj'] and not any(x in f.parts for x in ['bin', 'obj'])}
def atomic(path, data):
    temporary = path.with_name(path.name + '.mode-restore-01a11897.tmp')
    with temporary.open('xb') as stream: stream.write(data); stream.flush(); os.fsync(stream.fileno())
    os.replace(temporary, path)

with s.Session(ROOT, 'arrival-mode-downgrade-fixed-one-finding-repair-01a11897') as budget:
    old = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old)); assert set(old) == set(budget.old_roots)
    budget.track(BASE); budget.track(CARRIER); budget.track(INTERRUPTED)
    for folder in ['MultiplayerHoeingAssistant/obj', 'Test/MultiplayerHoeingAssistant.UnitTest/obj']: budget.track(ROOT / folder)
    BASE.mkdir(exist_ok=False)
    def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
    for name, path in [('WorkflowPlan.Path.cs', TARGET), ('PathArrivalRevisionTests.cs', TEST)]:
        s.write(BASE / 'before' / name, path.read_bytes())
    write(BASE / 'before.json', dict(head=subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(), source_metadata={str(path.relative_to(ROOT)): dict(bytes=len(path.read_bytes()), lines=len(path.read_bytes().splitlines()), sha256=sha(path.read_bytes()), bom=path.read_bytes().startswith(b'\xef\xbb\xbf'), crlf=b'\r\n' in path.read_bytes()) for path in [TARGET, TEST]}, finding='PATH-ARRIVAL-MODE-DOWNGRADE-1', severity='important', fixed_one_request='8c795c4e92274393b1d148a722787014', new_reviews=0, authorized_review_used=1, review_remaining=0, original_opening_history_and_failures_preserved=True))
    s.write(BASE / 'opening-status.txt', subprocess.check_output(['git', '-c', 'core.longpaths=true', 'status', '--porcelain=v1'], cwd=ROOT))
    data = ROOT / '_workflow/final-ui-01a11220/own-runtime/assistant-data'
    interrupted_phase = INTERRUPTED / 'edit-timer-false-branch-enable-auto'
    assert load(interrupted_phase / 'app-cleanup.json')['active_processes'] == 0
    assert (data / 'startup-flow.json').read_bytes() == (INTERRUPTED / 'private/original-startup-flow.json').read_bytes()
    assert (data / 'assistant-config.json').read_bytes() == (INTERRUPTED / 'private/original-assistant-config.json').read_bytes()
    write(INTERRUPTED / 'interrupted-terminal.json', dict(reason='Physical Escape stopped Computer Use; user asked why startup work was introduced; supplement abandoned, not acceptance', own_job_active_processes=0, controller_exit=1, own_startup_config_exactly_restored=True, own_assistant_config_exactly_restored=True, startup_actions_executed=False, source_modified=False, no_normal_exit_or_acceptance_claim=True, review_request_unchanged=True))
    rollout = Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T06-58-33-01a11897-06ae-7f83-a925-438e173e79f4.jsonl')
    raw = b'\n'.join(line for line in rollout.read_bytes().splitlines() if (row := json.loads(line)).get('timestamp', '') >= '2026-10-07T23:12:00Z' and row.get('type') in ['response_item', 'event_msg']) + b'\n'
    s.write(INTERRUPTED / 'private/native-interrupted-ui-source.jsonl', raw)
    print('READY: add bounded red cases, then red, green, negative, restored, finish', flush=True)
    def execute(stage):
        out = BASE / stage; out.mkdir(exist_ok=False)
        inputs = sources(); write(out / 'source-hashes.json', inputs)
        env = os.environ.copy(); env['NEXUSBGI_DATA_ROOT'] = str(data)
        temporary = out / 'temp'; temporary.mkdir(); env['TEMP'] = env['TMP'] = str(temporary)
        build = ['C:/Program Files/dotnet/dotnet.exe', 'build', str(ROOT / 'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'), '--disable-build-servers', '-nodeReuse:false', '-p:UseSharedCompilation=false', '-p:DeployToBgiTools=false', '-t:Rebuild', '-maxcpucount:1', '-o', str(CARRIER)]
        code, _, _ = p.run(build, cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='build', timeout=1200)
        result = dict(phase=stage, build=code)
        if code == 0:
            filter_text = 'FullyQualifiedName~PathArrivalRevisionTests' if stage.startswith('red') or stage == 'negative' else FULL
            argv = ['C:/Program Files/dotnet/dotnet.exe', 'vstest', str(CARRIER / 'MultiplayerHoeingAssistant.UnitTest.dll'), '--TestCaseFilter:' + filter_text, '--logger:trx;LogFileName=arrival-mode.trx', '--ResultsDirectory:' + str(out)]
            result['test'], _, _ = p.run(argv, cwd=ROOT, env=env, directory=out, recovery_directory=out, phase='test', timeout=600)
            rows = ET.parse(out / 'arrival-mode.trx').findall('.//t:UnitTestResult', {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'})
            result.update(passed=[x.get('testName') for x in rows if x.get('outcome') == 'Passed'], failed=[x.get('testName') for x in rows if x.get('outcome') == 'Failed'], other=[x.get('testName') for x in rows if x.get('outcome') not in ['Passed', 'Failed']])
            write(out / 'products.json', {f.name: sha(f.read_bytes()) for f in CARRIER.glob('MultiplayerHoeingAssistant*') if f.is_file()})
        result['source_drift'] = [name for name, h in inputs.items() if sha((ROOT / name).read_bytes()) != h]
        write(out / 'result.json', result)
        print(json.dumps(dict(phase=stage, build=code, passed=len(result.get('passed', [])), failed=result.get('failed', []), other=result.get('other', []), source_drift=result['source_drift']), ensure_ascii=False), flush=True)
    while True:
        stage = input('PHASE> ').strip()
        if stage == 'finish': break
        assert stage in ['red', 'red-compile-repair', 'green', 'negative', 'restored']
        if stage != 'negative': execute(stage); continue
        original = TARGET.read_bytes()
        block = b'            if(found)throw new InvalidOperationException("PATH_MODE_MESSAGE");\n'
        # Freeze the exact new guard line; message is located by the stable prefix, not rewritten.
        guard = next(line for line in original.splitlines(keepends=True) if b'if(found)throw new InvalidOperationException' in line and '顺序'.encode('utf-8') in line)
        assert original.count(guard) == 1
        mutant = original.replace(guard, b'            // focused mutation: original path mode binding check omitted\n')
        s.write(BASE / 'negative-original-WorkflowPlan.Path.cs', original)
        write(BASE / 'negative-mutation.json', dict(original_sha256=sha(original), mutant_sha256=sha(mutant), removed_guard_sha256=sha(guard)))
        try:
            atomic(TARGET, mutant); execute(stage)
        finally:
            atomic(TARGET, original)
            write(BASE / 'negative-source-restored.json', dict(exact=TARGET.read_bytes() == original, sha256=sha(TARGET.read_bytes())))
            assert TARGET.read_bytes() == original
            print('EXACT MODE SOURCE RESTORED; run restored before candidate binding', flush=True)
    print('MODE REPAIR SAFE TERMINAL; fixed-one grant remains exhausted; no product refresh/actual acceptance', flush=True)
