from pathlib import Path
import hashlib,json,subprocess
root=Path.cwd();base=Path(__file__).resolve().parent
def git(*args):return subprocess.check_output(['git','-c','core.longpaths=true',*args],cwd=root)
foreign=git('diff','--cached','--binary','--','MultiplayerHoeingAssistant')
probe=json.loads((base/'private-probe/probe-result.json').read_text(encoding='utf-8'))
summary={k:probe[k] for k in ['sourceUnchanged','configCount','configs','blockers','issues']}
summary['scope']='component conversion only; no live installation, execution or acceptance'
(base/'PROBE-SUMMARY.json').write_text(json.dumps(summary,ensure_ascii=False,indent=2),encoding='utf-8')
owned=['BetterGenshinImpact/View/Pages/OneDragonFlowPage.xaml','BetterGenshinImpact/ViewModel/Pages/OneDragonFlowViewModel.cs']
owned += [str((base/x).relative_to(root)).replace('\\','/') for x in ['Probe.csproj','Program.cs','run.py','implement.py','rebuild.py','model-evidence.py','save-checkpoint.py','ADMISSION.md','TOTAL-GOAL.md','PROBE-SUMMARY.json','before-product.json','after-product.json','execution/source-binding.json','execution/result.json','rebuild/source-binding.json','rebuild/result.json']]
assert all((root/p).is_file() for p in owned)
assert not any('private-probe' in p or '/out/' in p or '/obj/' in p for p in owned)
subprocess.run(['git','-c','core.longpaths=true','add','-f','-N','--',*owned],cwd=root,check=True)
subprocess.run(['git','-c','core.longpaths=true','commit','--only','-m','WIP: expose existing legacy migration entry; retain full product delivery obligation','--',*owned],cwd=root,check=True)
assert git('diff','--cached','--binary','--','MultiplayerHoeingAssistant')==foreign
commit=git('rev-parse','HEAD').decode().strip()
files=git('show','--format=','--name-only','HEAD').decode().splitlines()
assert sorted(files)==sorted(owned)
print(json.dumps({'commit':commit,'owned_files':len(files),'foreign_staged_sha256_preserved':hashlib.sha256(foreign).hexdigest(),'product_delivered':False},ensure_ascii=False))
