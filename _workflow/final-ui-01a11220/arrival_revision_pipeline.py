"""One finite repair pipeline; original source/reviews/failed runtime remain intact."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT/'_workflow/final-ui-01a11220/arrival-revision-01a11808'
CARRIER = ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sys.path.insert(0, str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
FULL = '|'.join('FullyQualifiedName~'+n for n in ['PathArrivalRevisionTests','PathLoopCutoffTests','FormalPathTests','FormalScheduleTests','WorkflowPlannerTests','WorkflowRunnerTests','AdapterStopRunnerTests','FastLocalTerminalAdmissionTests','TerminalReleaseSealTests','LocalWaitFinalizationContractTests','HistoricalExecutionObservationTests','LegacyHistoricalSealIntegrityTests'])
sha = lambda b: hashlib.sha256(b).hexdigest()
def sources():
    return {f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
def atomic(path, data):
    temporary = path.with_name(path.name+'.arrival-revision-restore.tmp')
    with temporary.open('xb') as file: file.write(data); file.flush(); os.fsync(file.fileno())
    os.replace(temporary, path)
with s.Session(ROOT, 'path-arrival-revision-finite-repair-01a11808') as budget:
    old = budget.old_roots[:]; budget.old_roots = list(dict.fromkeys(old)); assert set(old) == set(budget.old_roots)
    budget.track(BASE); budget.track(CARRIER)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']: budget.track(ROOT/folder)
    resume = sys.argv[1:] == ['--resume-stdin']
    assert not sys.argv[1:] or resume
    BASE.mkdir(exist_ok=resume)
    def write(path, value): s.write(path, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
    runner = ROOT/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
    before = runner.read_bytes()
    if resume:
        assert (BASE/'before/WorkflowRunner.cs').read_bytes() == before
        assert not (BASE/'red').exists()
        write(BASE/'controller-stdin-closed.json', dict(original_controller_exit=1, error='EOFError: EOF when reading a line', location='arrival_revision_pipeline.py input PHASE', original_launch_used_plain_pipes=True, build_or_test_started=False, source_mutated=False, original_before_and_opening_preserved=True, resumed_with_tty=True))
    else:
        s.write(BASE/'before/WorkflowRunner.cs', before)
        write(BASE/'before.json', dict(head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(), runner_bytes=len(before), runner_lines=len(before.splitlines()), runner_sha256=sha(before), runner_bom=before[:3].hex(), runner_crlf=b'\r\n' in before, source_modified=False, new_finding='PATH-ARRIVAL-REVISION-INSERT-1', severity='important', review_remaining=0, original_reports_and_failed_actual_preserved=True))
        s.write(BASE/'opening-status.txt', subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT))
    print('READY: red, green, negative, restored, finish', flush=True)
    def execute(stage):
        out = BASE/stage; out.mkdir(exist_ok=False)
        inputs = sources(); write(out/'source-hashes.json', inputs)
        env = os.environ.copy(); env['NEXUSBGI_DATA_ROOT'] = str(ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data')
        temporary = out/'temp'; temporary.mkdir(); env['TEMP'] = env['TMP'] = str(temporary)
        build_argv = ['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(CARRIER)]
        build,_,_ = p.run(build_argv,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        result = dict(phase=stage,build=build)
        if build == 0:
            filter_text = 'FullyQualifiedName~PathArrivalRevisionTests' if stage.startswith('red') or stage=='negative' else FULL
            argv = ['C:/Program Files/dotnet/dotnet.exe','vstest',str(CARRIER/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filter_text,'--logger:trx;LogFileName=arrival-revision.trx','--ResultsDirectory:'+str(out)]
            result['test'],_,_ = p.run(argv,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
            rows = ET.parse(out/'arrival-revision.trx').findall('.//t:UnitTestResult',{'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'})
            result.update(passed=[r.get('testName') for r in rows if r.get('outcome')=='Passed'],failed=[r.get('testName') for r in rows if r.get('outcome')=='Failed'],other=[r.get('testName') for r in rows if r.get('outcome') not in ['Passed','Failed']])
            write(out/'products.json',{f.name:sha(f.read_bytes()) for f in CARRIER.glob('MultiplayerHoeingAssistant*') if f.is_file()})
        result['source_drift'] = [n for n,h in inputs.items() if sha((ROOT/n).read_bytes()) != h]
        write(out/'result.json', result)
        print(json.dumps(dict(phase=stage,build=build,passed=len(result.get('passed',[])),failed=result.get('failed',[]),other=result.get('other',[]),source_drift=result['source_drift']),ensure_ascii=False), flush=True)
    while True:
        stage = input('PHASE> ').strip()
        if stage == 'finish': break
        assert stage in ['red','red-compile-repair','green','negative','restored']
        if stage != 'negative': execute(stage); continue
        original = runner.read_bytes()
        block = b'                var arrivalPlan = plan;\r\n                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);\r\n                if (!ReferenceEquals(arrivalPlan, plan)) continue;'
        if b'\r\n' not in original: block = block.replace(b'\r\n', b'\n')
        assert original.count(block) == 1
        mutant = original.replace(block, b'                // focused mutation: arrival revision boundary omitted')
        s.write(BASE/'negative-original-WorkflowRunner.cs', original)
        write(BASE/'negative-mutation.json', dict(original_sha256=sha(original), mutant_sha256=sha(mutant)))
        try:
            atomic(runner, mutant); execute(stage)
        finally:
            atomic(runner, original)
            write(BASE/'negative-source-restored.json', dict(exact=runner.read_bytes()==original,sha256=sha(runner.read_bytes())))
            assert runner.read_bytes() == original
            print('EXACT SOURCE RESTORED; rebuild restored before product refresh', flush=True)
    print('FINITE REPAIR PIPELINE TERMINAL; no source mutation in flight', flush=True)
