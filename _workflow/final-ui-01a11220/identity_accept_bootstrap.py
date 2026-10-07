"""Read-only reception checks, stored through the existing storage reservation."""
from pathlib import Path
import hashlib,json,os,subprocess,sys
ROOT=Path(__file__).resolve().parents[2]; BASE=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest()
out=BASE/'identity-accept-opening'
rollout=Path('E:/CodexData/home/sessions/2026/10/07/rollout-2026-10-07T09-40-46-01a11405-2f49-7ae2-b0a7-73e423bd691c.jsonl')
with s.Session(ROOT,'own-root-01a11405-identity-reception') as budget:
    budget.track(out)
    def write(name,v):s.write(out/name,json.dumps(v,ensure_ascii=False,indent=2).encode('utf-8'))
    turns=[r for line in rollout.read_bytes().splitlines() if (r:=json.loads(line))['type']=='turn_context']
    turn=turns[-1]; ctx=turn['payload']; settings=ctx['collaboration_mode']['settings']
    assert ctx['model']==settings['model']=='gpt-6.1-sol' and ctx['effort']==settings['reasoning_effort']=='xhigh'
    assert Path(ctx['cwd']).resolve()==ROOT
    write('model.json',dict(thread=os.environ['CODEX_THREAD_ID'],rollout=str(rollout),timestamp=turn['timestamp'],cwd=ctx['cwd'],model=ctx['model'],effort=ctx['effort'],settings_model=settings['model'],settings_effort=settings['reasoning_effort']))
    status=subprocess.check_output(['git','-c','core.longpaths=true','status','--porcelain=v1'],cwd=ROOT)
    s.write(out/'private/git-status.txt',status)
    hashes=json.loads((BASE/'flow-identity/restored-recovery/source-hashes.json').read_text())
    drift=[n for n,h in hashes.items() if '_wpftmp' not in n and (not (ROOT/n).is_file() or sha((ROOT/n).read_bytes())!=h)]
    write('source-check.json',dict(drift=drift,generated_wpftmp=[n for n in hashes if '_wpftmp' in n]))
    assert not drift,drift
    for stage in ['red','green','negative','restored-recovery']:
        result=json.loads((BASE/f'flow-identity/{stage}/result.json').read_text())
        write(f'{stage}-readback.json',dict(build=result['build'],test=result['test'],passed=len(result.get('Passed',[])),failed=result.get('Failed',[]),skipped=result.get('NotExecuted',[]),source_drift=result.get('source_drift')))
    for tool,args in [('bundle',['review_process.py','verify-bundle']),('deliveries',['deliveries.py','--root',str(ROOT),'--registry',str(ROOT/'Docs/design/mistletoe-parallel-deliveries.json')])]:
        run=subprocess.run([sys.executable,'-B',str(ROOT/'tools/mistletoe'/args[0]),*args[1:]],cwd=ROOT,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
        s.write(out/f'{tool}-stdout.txt',run.stdout);s.write(out/f'{tool}-stderr.txt',run.stderr)
        write(f'{tool}-result.json',dict(exit_code=run.returncode))
        print(tool,run.returncode,run.stdout.decode('utf-8',errors='replace')[:1800],flush=True)
    for name in ['wf-own-control-01a112a0','wf-1e7a758a']:
        file=BASE/f'own-runtime/assistant-data/flows/{name}.flow.json'; flow=json.loads(file.read_text(encoding='utf-8-sig'))
        s.write(out/f'private/{file.name}',file.read_bytes())
        print(name,json.dumps(flow,ensure_ascii=False),flush=True)
    write('adoption.json',dict(marker='OWN-ROOT-IDENTITY-UI-20261007-FROM-01a113cc',head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),policies=['mistletoe-release-first-20261005-v2','mistletoe-complete-usable-delivery-20261006-v1','mistletoe-storage-limits-20261005-v1'],agents='one shared UI/process state chain, no useful independent task before identity acceptance',source_drift=drift,review_requests_new=0,review_budget_remaining=0,product_complete=False))
    print('BOOTSTRAP_OK',flush=True)
