from pathlib import Path
import sys,json,hashlib,subprocess,os,xml.etree.ElementTree as ET
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
import process_runner
base=Path(__file__).resolve().parent
prior=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954/auto-standard-install-01a10c1c/01a10c87-deee-7381-a61d-fefce264d537'
old=root/'_workflow/runtime-unified-01a10c87/candidate-r1'
carrier=root/'_workflow/runtime-unified-01a10c87/recovery-restored-full-r1'
candidate=root/'_workflow/runtime-unified-01a10cef/candidate-r2'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
def write(p,v):storage.write(p,json.dumps(v,ensure_ascii=False,indent=2).encode())
with storage.Session(root,'recovery-candidate-assembly-and-joint-regression') as budget:
    budget.track(base);budget.track(candidate);budget.check(location=candidate)
    rows=[json.loads(l) for l in (Path('E:/CodexData/home/sessions/2026/10/06/rollout-2026-10-06T00-39-24-01a10cef-2e70-7a63-9ff4-f3439cc6eb19.jsonl')).read_text(encoding='utf-8').splitlines()]
    meta=next(r['payload'] for r in rows if r['type']=='session_meta');ctx=[r['payload'] for r in rows if r['type']=='turn_context'][-1]
    assert meta['id']=='01a10cef-2e70-7a63-9ff4-f3439cc6eb19' and Path(ctx['cwd'])==root
    assert ctx['model']=='gpt-6.1-sol' and ctx['effort']=='medium' and ctx['collaboration_mode']['settings']['reasoning_effort']=='medium'
    write(base/'01a10cef-new-handshake-observation.json',dict(threadId=meta['id'],marker='AUTO-RECOVERY-VALIDATION-CLOSEOUT-20261006-FROM-01a10c87',cwd=str(root),model=ctx['model'],effort=ctx['effort'],settings_model=ctx['collaboration_mode']['settings']['model'],goal='active native readback',adopted=['mistletoe-release-first-20261005-v2','mistletoe-storage-limits-20261005-v1'],latest_scope='agent performs feasible non-game UI/IPC verification; user performs game and real OS effects'))
    sources=json.loads((prior/'recovery-fix/restored-full-r1/source-after.json').read_text())
    assert all(sha(root/p)==h for p,h in sources.items())
    write(base/'source-readback.json',sources)
    for name,args in [('bundle',[sys.executable,'-B','tools/mistletoe/review_process.py','verify-bundle','--root',str(root)]),('deliveries',[sys.executable,'-B','tools/mistletoe/deliveries.py','--root',str(root),'--registry','Docs/design/mistletoe-parallel-deliveries.json'])]:
        r=subprocess.run(args,capture_output=True);storage.write(base/(name+'.stdout.txt'),r.stdout);storage.write(base/(name+'.stderr.txt'),r.stderr);write(base/(name+'.result.json'),dict(argv=args,exit_code=r.returncode))
    original=json.loads((prior/'runtime-facts/runtime-files-with-routes.json').read_text())
    choices=[];dep_diffs=[]
    for row in original:
        rel=Path(row['path']);p=old/rel
        assert sha(p)==row['sha256'],str(p)
        source=p
        if rel.parts[:2]==('Tools','MultiplayerHoeingAssistant'):
            new=carrier/Path(*rel.parts[2:])
            if new.is_file():
                if new.name.startswith('MultiplayerHoeingAssistant.'):
                    source=new
                elif sha(new)!=sha(p):dep_diffs.append(str(rel))
        choices.append((rel,source))
    assert not dep_diffs,dep_diffs
    estimated=sum(p.stat().st_size for _,p in choices)
    budget.check(estimated,location=candidate)
    assert not candidate.exists()
    manifest=[]
    for rel,p in choices:
        dst=candidate/rel;storage.write(dst,p.read_bytes());manifest.append(dict(path=rel.as_posix(),bytes=dst.stat().st_size,sha256=sha(dst),source=str(p)))
    assert sha(candidate/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll')==sha(carrier/'MultiplayerHoeingAssistant.dll')
    assert not any('testhost' in x['path'].lower() or 'ControlledWriterProbe' in x['path'] or 'TestData' in x['path'] for x in manifest)
    write(base/'runtime-manifest.json',manifest)
    write(base/'assembly-summary.json',dict(candidate=str(candidate),source_head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),files=len(manifest),bytes=estimated,assistant_sha=sha(carrier/'MultiplayerHoeingAssistant.dll'),bgi_sha=sha(candidate/'BetterGI.dll'),nonproduct_dependency_drift=dep_diffs,source_unchanged=True,accepted=False,independent_repair_review=False))
    # Existing terminal assistant carrier has this exact module and full regression.
    # BGI keeps its own 9.x dependency graph; inject only the new MHA product module.
    bgi=root/'_workflow/runtime-unified-01a10c87/regression-r2/bgi'
    replacements=[]
    for p in (candidate/'Tools/MultiplayerHoeingAssistant').glob('MultiplayerHoeingAssistant.*'):
        if p.suffix in ['.dll','.exe','.pdb','.json']:
            target=bgi/p.name;storage.write(target,p.read_bytes(),mode='wb');replacements.append(dict(path=str(target),sha256=sha(target)))
    write(base/'joint-products-before.json',replacements)
    args=['C:/Program Files/dotnet/dotnet.exe','vstest',str(bgi/'BetterGenshinImpact.UnitTest.dll'),'--TestCaseFilter:FullyQualifiedName~StandardMigration|FullyQualifiedName~Migration|FullyQualifiedName~TaskCenter','--logger:trx;LogFileName=joint.trx','--ResultsDirectory:'+str(base)]
    code,_,_=process_runner.run(args,cwd=root,env=os.environ.copy(),directory=base,recovery_directory=base,phase='joint-migration',timeout=900)
    ns={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'};tree=ET.parse(base/'joint.trx')
    write(base/'joint-summary.json',dict(argv=args,exit_code=code,counters=tree.find('.//t:Counters',ns).attrib,failed=[n.attrib['testName'] for n in tree.findall('.//t:UnitTestResult',ns) if n.attrib['outcome']=='Failed']))
    assert all(sha(Path(p['path']))==p['sha256'] for p in replacements)
    assert all(sha(root/p)==h for p,h in sources.items())
    print(json.dumps(dict(candidate=str(candidate),files=len(manifest),bytes=estimated,joint_exit=code)),flush=True)
