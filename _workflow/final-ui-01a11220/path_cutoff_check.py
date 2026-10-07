from pathlib import Path
import hashlib, json, os, subprocess, sys, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'path-cutoff-01a1176e'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
import storage_limits as s
import process_runner as p
SOURCES = ['MultiplayerHoeingAssistant/Services/TaskCenter/' + n for n in ['WorkflowPlanner.cs', 'WorkflowPlan.Path.cs', 'WorkflowRunner.cs', 'WorkflowNodeSchedule.cs']]
TEST = 'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/PathLoopCutoffTests.cs'
FULL = '|'.join('FullyQualifiedName~' + n for n in ['PathLoopCutoffTests', 'FormalPathTests', 'FormalScheduleTests', 'WorkflowPlannerTests', 'WorkflowRunnerTests', 'AdapterStopRunnerTests', 'FastLocalTerminalAdmissionTests', 'TerminalReleaseSealTests', 'LocalWaitFinalizationContractTests', 'HistoricalExecutionObservationTests', 'LegacyHistoricalSealIntegrityTests'])
CARRIER = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
phase = sys.argv[1]
out = BASE / phase
def sha(data): return hashlib.sha256(data).hexdigest()
def git(*args): return subprocess.run(['git', '-c', 'core.longpaths=true', *args], cwd=ROOT, capture_output=True, check=True).stdout
with s.Session(ROOT, 'path-cutoff-01a1176e-' + phase) as budget:
    budget.track(BASE)
    out.mkdir(parents=True, exist_ok=False)
    def write(name, value): s.write(out/name, json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))
    hashes = {f.relative_to(ROOT).as_posix(): sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant', 'Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs', '.xaml', '.csproj'] and not any(q in f.parts for q in ['bin', 'obj'])}
    write('source-hashes.json', hashes)
    s.write(out/'git-status.txt', git('status', '--porcelain=v1'))
    s.write(out/'source.diff', git('diff', '--', *SOURCES, TEST))
    if phase == 'opening':
        for rel in SOURCES: s.write(out/('original-' + Path(rel).name), (ROOT/rel).read_bytes())
        rollout = Path('E:/CodexData/home/sessions/2026/10/08/rollout-2026-10-08T01-34-33-01a1176e-6732-7bd2-9b74-38989fbe5fbe.jsonl')
        contexts = [json.loads(line) for line in rollout.read_text(encoding='utf-8').splitlines() if '"turn_context"' in line]
        context = [r['payload'] for r in contexts if r['type'] == 'turn_context'][-1]
        assert context['model'] == context['collaboration_mode']['settings']['model'] == 'gpt-6.1-sol'
        assert context['effort'] == context['collaboration_mode']['settings']['reasoning_effort'] == 'ultra'
        write('adoption.json', dict(marker='OWN-ROOT-REVIEW1-PATH-CUTOFF-FIX-20261008-FROM-01a114ee', thread='01a1176e-6732-7bd2-9b74-38989fbe5fbe', native_goal='active; native create/get proof in current rollout', actual_model=context['model'], actual_effort=context['effort'], cwd=str(ROOT), head=git('rev-parse','HEAD').decode().strip(), branch=git('branch','--show-current').decode().strip(), original_opening_and_counts_unchanged=True, remaining_review=1, policies=['mistletoe-release-first-20261005-v2','mistletoe-complete-usable-delivery-20261006-v1','mistletoe-storage-limits-20261005-v1','handoff-inherit-model-20261004-v1'], bundle='verify-bundle exit1 BLOCKED review bundle drift; preserved, no tool repair', admission='C09 normal structural paths and timed cutoff: earlier secondary array node selects wrong repeat; timed wait crosses cutoff. Minimum repair one canonical entry plus bounded node waits and post-wait deadline/round settlement, then affected regression/PFP, same-product refresh and one consolidated repair review.', matrix=['$end and implicit tail restart primary at nonzero array index', 'two rounds selected primary only with distinct identities', 'nonzero primary scheduled round retains wait', 'sequence/fixed due after deadline zero prerequisite/send; cap wakeup and late wakeup', 'flexible idle only after cutoff zero side effects', 'started node can finish but successor cannot start', 'scheduled skipAcrossDays expiry during node wait skips original round', 'pause/stop, explicit backedges, durable cursor/external facts preserved'], protected=['real User and third-party JS', 'old failures/reports/opening/review budget', 'material-external MigrationReferenceActivation.cs MigrationSwitchTransaction.cs csproj'], agents='one read-only evidence navigation helper; no substantive consultation request', preparations='existing storage-scan A and migration-acceptance B; neither two-root-cause patch; retained', next='finite red cases, minimal shared repair, affected Rebuild/regression/PFP, same product runtime and final authorized review'))
        discovery = subprocess.run([sys.executable,'-B','tools/mistletoe/deliveries.py','--root',str(ROOT)],cwd=ROOT,capture_output=True)
        s.write(out/'deliveries-discovery.json',discovery.stdout)
        write('deliveries-result.json',dict(exit=discovery.returncode,stderr=discovery.stderr.decode('utf-8','replace')))
        print('opening recorded; native identity verified; original ledgers preserved',flush=True)
        sys.exit(0)
    budget.track(CARRIER)
    for folder in ['MultiplayerHoeingAssistant/obj','Test/MultiplayerHoeingAssistant.UnitTest/obj']: budget.track(ROOT/folder)
    env = os.environ.copy()
    env['NEXUSBGI_DATA_ROOT'] = str(ROOT/'_workflow/final-ui-01a11220/own-runtime/assistant-data')
    build_args = ['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(CARRIER)]
    build,_,_ = p.run(build_args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
    result = dict(phase=phase,build=build)
    if build == 0:
        filt = 'FullyQualifiedName~PathLoopCutoffTests' if phase == 'red' or phase.startswith('negative') else FULL
        args = ['C:/Program Files/dotnet/dotnet.exe','vstest',str(CARRIER/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=path-cutoff.trx','--ResultsDirectory:'+str(out)]
        result['test'],_,_ = p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
        ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
        rows=ET.parse(out/'path-cutoff.trx').findall('.//t:UnitTestResult',ns)
        result.update(passed=[r.get('testName') for r in rows if r.get('outcome')=='Passed'],failed=[r.get('testName') for r in rows if r.get('outcome')=='Failed'],other=[r.get('testName') for r in rows if r.get('outcome') not in ['Passed','Failed']])
        write('products.json',{f.name:sha(f.read_bytes()) for f in CARRIER.glob('MultiplayerHoeingAssistant*') if f.is_file()})
    result['source_drift']=[rel for rel,h in hashes.items() if sha((ROOT/rel).read_bytes())!=h]
    write('result.json',result)
    print(json.dumps(dict(phase=phase,build=build,passed=len(result.get('passed',[])),failed=result.get('failed',[]),other=result.get('other',[]),source_drift=result['source_drift']),ensure_ascii=False),flush=True)
