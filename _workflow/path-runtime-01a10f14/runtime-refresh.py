from pathlib import Path
import sys,json,hashlib,os,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;out=base/'runtime-refresh'
product=root/'_workflow/runtime-unified-01a10e1b/product';carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
causal=json.loads((base/'causal-repeat/result.json').read_text(encoding='utf-8-sig'))
assert causal['baseline_exit']==0 and causal['negative_exit']!=0 and causal['restored_exit']==0 and not causal['source_drift']
assert sha(carrier/'MultiplayerHoeingAssistant.dll')==causal['assistant_sha']
assert not out.exists() and product.is_dir()
final=json.loads((base/'causal-repeat/affected-final/result.json').read_text(encoding='utf-8-sig'))
failed=[t['name'] for t in final['tests'] if t['outcome']=='Failed']
assert failed==['MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter.TaskCenterSuccessorPathGateTests.ProductionCtor_DoesNotEnableSuccessorPathGate'],failed
rows=json.loads((root/'_workflow/full-product-01a10e1b/source-manifest.json').read_text(encoding='utf-8-sig'))
unchanged=[r for r in rows if r['path'].split('/')[0] in ['BetterGenshinImpact','Fischless.WindowsInput','Fischless.HotkeyCapture','Fischless.GameCapture']]
assert all((root/r['path']).is_file() and sha(root/r['path'])==r['sha256'] for r in unchanged)
with s.Session(root,'path-runtime-01a10f14-complete-product-refresh') as budget:
 budget.track(base);budget.track(product/'Tools/MultiplayerHoeingAssistant');out.mkdir()
 processes=subprocess.run(['pwsh','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object { $_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe') } | Select-Object ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],capture_output=True,text=True,encoding='utf-8',check=True)
 active=json.loads(processes.stdout) if processes.stdout.strip() else []
 if isinstance(active,dict):active=[active]
 assert not any(str(p.get('ExecutablePath') or '').lower().startswith(str(product).lower()+os.sep) for p in active),'Own product still running; no module replacement'
 s.write(out/'processes-before.json',json.dumps(active,ensure_ascii=False,indent=2).encode())
 user={p.relative_to(product/'User').as_posix():sha(p) for p,st in s.files_under(product/'User')}
 s.write(out/'private/product-user-before.json',json.dumps(user,ensure_ascii=False,indent=2).encode())
 names=['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.exe','MultiplayerHoeingAssistant.pdb','MultiplayerHoeingAssistant.deps.json','MultiplayerHoeingAssistant.runtimeconfig.json']
 estimate=sum((carrier/n).stat().st_size+(product/'Tools/MultiplayerHoeingAssistant'/n).stat().st_size for n in names)
 budget.check(estimate,location=product)
 updated=[]
 for name in names:
  target=product/'Tools/MultiplayerHoeingAssistant'/name;data=(carrier/name).read_bytes()
  assert target.resolve().is_relative_to(product.resolve())
  s.write(out/'before'/name,target.read_bytes())
  staging=target.with_name(target.name+'.path-runtime-01a10f14');assert not staging.exists()
  s.write(staging,data);assert sha(staging)==hashlib.sha256(data).hexdigest()
  os.replace(staging,target);assert sha(target)==sha(carrier/name)
  updated.append(dict(path=target.relative_to(product).as_posix(),sha256=sha(target),bytes=target.stat().st_size))
 assert user=={p.relative_to(product/'User').as_posix():sha(p) for p,st in s.files_under(product/'User')}
 proof=dict(directory=str(product),updated=updated,bgi_sha=sha(product/'BetterGI.dll'),assistant_sha=sha(product/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll'),bgi_inputs_unchanged=len(unchanged),product_user_files=len(user),product_user_unchanged=True,third_party_js_edited=False,head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=root).decode().strip(),independent_review=False,game_executed=False,runtime_acceptance=False,goal_complete=False)
 s.write(out/'result.json',json.dumps(proof,ensure_ascii=False,indent=2).encode());print(json.dumps({k:proof[k] for k in ['directory','bgi_sha','assistant_sha','bgi_inputs_unchanged','product_user_files','product_user_unchanged']}),flush=True)
