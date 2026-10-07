from pathlib import Path
import hashlib, json, os, subprocess, sys, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent/'path-cutoff-01a1176e'
CARRIER = ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner as p
FULL = '|'.join('FullyQualifiedName~'+n for n in ['PathLoopCutoffTests','FormalPathTests','FormalScheduleTests','WorkflowPlannerTests','WorkflowRunnerTests','AdapterStopRunnerTests','FastLocalTerminalAdmissionTests','TerminalReleaseSealTests','LocalWaitFinalizationContractTests','HistoricalExecutionObservationTests','LegacyHistoricalSealIntegrityTests'])
def sha(b): return hashlib.sha256(b).hexdigest()
def sources(): return {f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
def atomic(path,data):
    temp = path.with_name(path.name+'.path-cutoff-restore.tmp')
    with temp.open('xb') as f: f.write(data); f.flush(); os.fsync(f.fileno())
    os.replace(temp,path)
with s.Session(ROOT,'path-cutoff-01a1176e-finite-pipeline') as budget:
    # Reuse the existing runtime-entry template's same-set normalization. Every
    # registered root remains measured; duplicate paths need only one traversal.
    original_roots = budget.old_roots[:]
    budget.old_roots = list(dict.fromkeys(original_roots))
    assert set(budget.old_roots) == set(original_roots)
    budget.track(BASE); budget.track(CARRIER)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']: budget.track(ROOT/folder)
    print('READY: red, green, negative-entry, restored-entry, negative-cutoff, restored, finish',flush=True)
    def execute(stage):
        out = BASE/stage; out.mkdir(exist_ok=False)
        def write(name,value): s.write(out/name,json.dumps(value,ensure_ascii=False,indent=2).encode('utf-8'))
        hashes = sources(); write('source-hashes.json',hashes)
        env = os.environ.copy(); env['NEXUSBGI_DATA_ROOT'] = str(ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data')
        argv = ['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(CARRIER)]
        build,_,_ = p.run(argv,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        result = dict(phase=stage,build=build)
        if build == 0:
            filt = 'FullyQualifiedName~PathLoopCutoffTests' if stage.startswith('red') or stage.startswith('negative') else FULL
            argv = ['C:/Program Files/dotnet/dotnet.exe','vstest',str(CARRIER/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=path-cutoff.trx','--ResultsDirectory:'+str(out)]
            result['test'],_,_ = p.run(argv,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
            rows = ET.parse(out/'path-cutoff.trx').findall('.//t:UnitTestResult',{'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'})
            result.update(passed=[r.get('testName') for r in rows if r.get('outcome')=='Passed'],failed=[r.get('testName') for r in rows if r.get('outcome')=='Failed'],other=[r.get('testName') for r in rows if r.get('outcome') not in ['Passed','Failed']])
            write('products.json',{f.name:sha(f.read_bytes()) for f in CARRIER.glob('MultiplayerHoeingAssistant*') if f.is_file()})
        result['source_drift'] = [n for n,h in hashes.items() if sha((ROOT/n).read_bytes())!=h]
        write('result.json',result)
        print(json.dumps(dict(phase=stage,build=build,passed=len(result.get('passed',[])),failed=result.get('failed',[]),other=result.get('other',[]),source_drift=result['source_drift']),ensure_ascii=False),flush=True)
        return result
    while True:
        stage = input('PHASE> ').strip()
        if stage == 'finish': break
        assert stage and all(c.isalnum() or c=='-' for c in stage)
        if not stage.startswith('negative'):
            execute(stage)
            continue
        rels = ['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlanner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlan.Path.cs'] if stage=='negative-entry' else ['MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowNodeSchedule.cs']
        original = {n:(ROOT/n).read_bytes() for n in rels}
        baseline = json.loads((BASE/'green/source-hashes.json').read_text(encoding='utf-8'))
        assert sources()==baseline, 'source differs from green; no mutation performed'
        mutants={}
        if stage=='negative-entry':
            n=rels[0]; old=b'StructuralRoundEntry(0);'; assert original[n].count(old)==1
            mutants[n]=original[n]  # First entry stays correct: reverse only restart and runner-entry root cause.
            n=rels[1]; old=b'StructuralRoundEntry(checked(current.LoopIteration+1))'; assert original[n].count(old)==2
            mutants[n]=original[n].replace(old,b'OccurrenceAt(0,checked(current.LoopIteration+1))')
        else:
            n=rels[0]; old=b'if (TrySettleLoopDeadline(run, plan)) break;'; assert original[n].count(old)==2
            first=original[n].find(old); second=original[n].find(old,first+len(old))
            mutants[n]=original[n][:second]+b'// focused mutant: post-wait deadline guard removed'+original[n][second+len(old):]
            n=rels[1]; old=b'var cutoff = NodeScheduleCutoff(run, plan, occurrence);'; assert original[n].count(old)==1
            mutants[n]=original[n].replace(old,b'DateTimeOffset? cutoff = null; // focused mutant: cutoff cap removed')
        identity=BASE/(stage+'-mutation.json')
        s.write(identity,json.dumps({n:dict(original_sha=sha(original[n]),mutant_sha=sha(mutants[n])) for n in rels},indent=2).encode())
        for n,b in original.items(): s.write(BASE/(stage+'-original-'+Path(n).name),b)
        try:
            for n,b in mutants.items(): atomic(ROOT/n,b)
            execute(stage)
        finally:
            for n,b in original.items(): atomic(ROOT/n,b)
            restored={n:dict(sha256=sha((ROOT/n).read_bytes()),exact=(ROOT/n).read_bytes()==b) for n,b in original.items()}
            s.write(BASE/(stage+'-source-restored.json'),json.dumps(restored,indent=2).encode())
            assert all(r['exact'] for r in restored.values())
            print('EXACT SOURCE RESTORED; rebuild restored phase before product refresh',flush=True)
    print('finite pipeline ended; no source mutation in flight',flush=True)
