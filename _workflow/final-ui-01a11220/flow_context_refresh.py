from pathlib import Path
import hashlib,json,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
def perform(budget):
 green=BASE/'flow-context/causal/restored'
 result=json.loads((green/'result.json').read_text());assert result['build']==result['test']==0 and not result['Failed'] and not result['source_drift']
 hashes=json.loads((green/'source-hashes.json').read_text());sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
 assert all(sha(ROOT/n)==h for n,h in hashes.items())
 for phase in ['negative','restored']:
  for job in ['build','test']:assert json.loads((green.parent/phase/(job+'-tree-terminal.json')).read_text())['active_processes']==0
 product=ROOT/'_workflow/runtime-unified-01a10e1b/product';carrier=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
 out=BASE/'own-runtime/refresh-context';budget.track(out);out.mkdir(parents=True,exist_ok=False)
 budget.track(product/'Tools/MultiplayerHoeingAssistant');budget.track(product/'User')
 previous=json.loads((BASE/'own-runtime/refresh-terminal/result.json').read_text())
 assert sha(product/'BetterGI.dll')==previous['bgi_sha256'] and sha(product/'BetterGI.exe')=='42aa9e13aa823a2796fd96b666360dff29890ed1f4d73798f837fa740ac70795'
 before={p.relative_to(product/'User').as_posix():sha(p) for p,_ in s.files_under(product/'User')}
 s.write(out/'private/product-user-before.json',json.dumps(before,indent=2).encode())
 modules=['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.exe','MultiplayerHoeingAssistant.pdb','MultiplayerHoeingAssistant.deps.json','MultiplayerHoeingAssistant.runtimeconfig.json'];updated=[]
 for name in modules:
  target=product/'Tools/MultiplayerHoeingAssistant'/name
  assert sha(target)==next(m['sha256'] for m in previous['updated'] if m['path'].endswith('/'+name))
  s.write(out/'before'/name,target.read_bytes());s.write(target,(carrier/name).read_bytes(),mode='wb')
  assert sha(target)==sha(carrier/name);updated.append(dict(path=target.relative_to(product).as_posix(),sha256=sha(target)))
 after={p.relative_to(product/'User').as_posix():sha(p) for p,_ in s.files_under(product/'User')};assert before==after
 value=dict(marker='OWN-ROOT-FLOW-SWITCH-20261007-FROM-01a11308',updated=updated,bgi_sha256=sha(product/'BetterGI.dll'),bgi_exe_sha256=sha(product/'BetterGI.exe'),product_user_changed=[],user_files=len(before),source_hashes=str(green/'source-hashes.json'),review_requests_new=0,independent_review=False,product_complete=False)
 s.write(out/'result.json',json.dumps(value,indent=2).encode());print('Five product modules refreshed; BGI and User byte-identical',flush=True)
if __name__=='__main__':
 with s.Session(ROOT,'own-root-01a11380-refresh-context') as budget:perform(budget)
