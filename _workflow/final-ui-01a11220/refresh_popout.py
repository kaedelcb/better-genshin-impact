"""Refresh only the five assistant modules after the contained C06 cycle ends."""
from pathlib import Path
import hashlib,json,os,sys
ROOT=Path(__file__).resolve().parents[2];BASE=Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'tools/mistletoe'));sys.stdout.reconfigure(encoding='utf-8')
import storage_limits as s
sha=lambda b:hashlib.sha256(b).hexdigest()
PRODUCT=ROOT/'_workflow/runtime-unified-01a10e1b/product'
CARRIER=ROOT/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
with s.Session(ROOT,'own-root-01a11405-refresh-C06-popout') as budget:
    original_monitor_roots=budget.old_roots[:]
    budget.old_roots=list(dict.fromkeys(original_monitor_roots))
    assert set(budget.old_roots)==set(original_monitor_roots)  # Same coverage; ledger, baseline and limits unchanged.
    out=BASE/'own-runtime/refresh-popout';budget.track(out);budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant')
    s.write(out/'monitor-root-set.json',json.dumps(dict(original_count=len(original_monitor_roots),distinct_count=len(budget.old_roots),same_path_set=True,policy=budget.policy),indent=2).encode())
    result=json.loads((BASE/'flow-preview/popout-r3/restored/result.json').read_text())
    assert result['build']==result['test']==0 and not result['Failed'] and not result['NotExecuted'] and not result['source_drift']
    hashes=json.loads((BASE/'flow-preview/popout-r3/restored/source-hashes.json').read_text())
    assert all((ROOT/n).is_file() and sha((ROOT/n).read_bytes())==h for n,h in hashes.items())
    old=json.loads((BASE/'own-runtime/refresh-preview/result.json').read_text())
    assert sha((PRODUCT/'BetterGI.dll').read_bytes())==old['bgi_sha256']
    assert sha((PRODUCT/'BetterGI.exe').read_bytes())==old['bgi_exe_sha256']
    before={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')}
    updated=[]
    for suffix in ['.dll','.exe','.pdb','.deps.json','.runtimeconfig.json']:
        name='MultiplayerHoeingAssistant'+suffix;target=PRODUCT/'Tools/MultiplayerHoeingAssistant'/name;data=(CARRIER/name).read_bytes()
        s.write(out/'before'/name,target.read_bytes());temp=target.with_name(target.name+'.own-01a11405.tmp');assert not temp.exists()
        s.write(temp,data);os.replace(temp,target);assert target.read_bytes()==data and not temp.exists()
        updated.append(dict(path=target.relative_to(PRODUCT).as_posix(),sha256=sha(data)))
    after={f.relative_to(PRODUCT/'User').as_posix():sha(f.read_bytes()) for f,_ in s.files_under(PRODUCT/'User')}
    assert before==after
    value=dict(marker='OWN-ROOT-IDENTITY-UI-20261007-FROM-01a113cc',updated=updated,bgi_sha256=old['bgi_sha256'],bgi_exe_sha256=old['bgi_exe_sha256'],product_user_changed=[],user_files=len(after),source_hashes=str(BASE/'flow-preview/popout-r3/restored/source-hashes.json'),review_requests_new=0,independent_review=False,actual_ui=False,product_complete=False)
    s.write(out/'result.json',json.dumps(value,ensure_ascii=False,indent=2).encode())
    print(json.dumps(value,ensure_ascii=False),flush=True)
