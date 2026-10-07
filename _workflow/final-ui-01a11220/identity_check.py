"""Focused same-name flow acceptance, using the original storage budget and product."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, xml.etree.ElementTree as ET
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'flow-identity'
sys.path.insert(0, str(ROOT / 'tools/mistletoe'))
import storage_limits as s
import process_runner as p
FILES = ['MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs',
         'MultiplayerHoeingAssistant/Views/ScheduleListView.xaml',
         'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalFlowContextTests.cs']
sha = lambda b: hashlib.sha256(b).hexdigest()
phase = sys.argv[1]
with s.Session(ROOT, 'own-root-01a113cc-flow-identity-' + phase) as budget:
    budget.track(BASE)
    out = BASE / phase
    out.mkdir(parents=True, exist_ok=False)
    def write(name, value):
        s.write(out / name, json.dumps(value, ensure_ascii=False, indent=2).encode())
    if phase == 'opening':
        states = {}
        for rel in FILES:
            data = (ROOT / rel).read_bytes()
            s.write(out / 'before' / rel, data)
            states[rel] = dict(sha256=sha(data), bytes=len(data), lines=len(data.splitlines()), crlf=data.count(b'\r\n'), bom=data.startswith(b'\xef\xbb\xbf'))
        write('before.json', states)
        s.write(out / 'git-status.txt', subprocess.run(['git','-c','core.longpaths=true','status','--porcelain=v1'], cwd=ROOT, capture_output=True, check=True).stdout)
        write('admission.json', dict(marker='OWN-ROOT-DEV-MATRIX-20261007-FROM-01a11380', function='C01',
            entry='ScheduleListView flow choice in main/popout, same-name imported active flows',
            gap='Name and activation status alone do not distinguish normal same-name active identities',
            consequence='user may select the other same-name flow, observed in historical run-9a416f351b6d',
            repair='visible stable workflow ID plus full name/ID/revision tooltip; preserve shared selection and draft guards',
            matrix=['two same-name active flows after actual activation','main/popout selection and visible identity','new revision refresh','candidate restriction and dirty draft guard regression'],
            agents='single shared WPF selection chain; direct tracing is sufficient, no extra review request',
            scope='original complete-product Goal limited repair, no new opening or review budget',
            review_requests_new=0, review_budget_remaining=0, product_complete=False,
            next='actual developer entry matrix, resource parameter save/reference update, migration/restart/data retention'))
        print('Identity admission and exact before bytes retained', flush=True)
    else:
        carrier = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
        budget.track(carrier)
        for folder in ['MultiplayerHoeingAssistant/obj', 'Test/MultiplayerHoeingAssistant.UnitTest/obj']:
            budget.track(ROOT / folder)
        hashes = {f.relative_to(ROOT).as_posix():sha(f.read_bytes()) for folder in ['MultiplayerHoeingAssistant','Test/MultiplayerHoeingAssistant.UnitTest'] for f in (ROOT/folder).rglob('*') if f.suffix in ['.cs','.xaml','.csproj'] and not any(q in f.parts for q in ['bin','obj'])}
        write('source-hashes.json', hashes)
        env = os.environ.copy()
        env['FORMAL_UI_EVIDENCE'] = str(out / 'samples')
        env['NEXUSBGI_DATA_ROOT'] = str(out / 'own-data')
        env['TEMP'] = env['TMP'] = str(out / 'temp')
        Path(env['TEMP']).mkdir()
        Path(env['FORMAL_UI_EVIDENCE']).mkdir()
        args = ['C:/Program Files/dotnet/dotnet.exe','build',str(ROOT/'Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj'),'--disable-build-servers','-nodeReuse:false','-p:UseSharedCompilation=false','-p:DeployToBgiTools=false','-t:Rebuild','-maxcpucount:1','-o',str(carrier)]
        build,_,_ = p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='build',timeout=1200)
        result = dict(phase=phase,build=build)
        if build == 0:
            filt = 'FullyQualifiedName~ImportedCandidate_RealActivation_RefreshesPreviewAndRenderedLabel' if phase in ['red','negative'] else 'FullyQualifiedName~Formal|FullyQualifiedName~TaskCenterPanelViewModelTests|FullyQualifiedName~IsolatedDataRoot|FullyQualifiedName~FullProductUiTests|FullyQualifiedName~WorkflowEdit'
            args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(carrier/'MultiplayerHoeingAssistant.UnitTest.dll'),'--TestCaseFilter:'+filt,'--logger:trx;LogFileName=identity.trx','--ResultsDirectory:'+str(out)]
            result['test'],_,_ = p.run(args,cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='test',timeout=600)
            ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
            rows=ET.parse(out/'identity.trx').findall('.//t:UnitTestResult',ns)
            for kind in ['Passed','Failed','NotExecuted']:
                result[kind]=[r.get('testName') for r in rows if r.get('outcome')==kind]
        result['source_drift']=[rel for rel,h in hashes.items() if sha((ROOT/rel).read_bytes())!=h]
        write('result.json',result)
        print(json.dumps(dict(phase=phase,build=build,test=result.get('test'),passed=len(result.get('Passed',[])),failed=result.get('Failed'),source_drift=result['source_drift']),ensure_ascii=False),flush=True)
