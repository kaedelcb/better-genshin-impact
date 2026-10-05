from pathlib import Path
import sys,json,hashlib,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;product=root/'_workflow/runtime-unified-01a10e1b/product';carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(root,'full-product-final-authoring-module-refresh') as budget:
    budget.track(base);budget.track(product)
    processes=subprocess.check_output(['powershell','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe')} | Select-Object -ExpandProperty ProcessId"]).decode().strip();assert not processes
    old=json.loads((base/'final-runtime-manifest.json').read_text(encoding='utf-8-sig'));assert all(sha(product/x['path'])==x['sha256'] for x in old)
    host=root/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs';t=host.read_text(encoding='utf-8-sig')
    a=t.index('    /// <summary>显式连接作者目录');b=t.index('    public LocalWaitQueueStore LocalWaitQueue',a)
    old_host=(base/'before-connect/MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs').read_text(encoding='utf-8-sig')
    assert t[:a]+t[b:]==old_host,'existing execution host body changed'
    changes=[]
    for p in sorted((product/'Tools/MultiplayerHoeingAssistant').glob('MultiplayerHoeingAssistant.*')):
        src=carrier/p.name;assert src.is_file()
        if sha(src)!=sha(p):
            s.write(base/'before-final-authoring-runtime'/p.name,p.read_bytes());changes.append(dict(path=p.relative_to(product).as_posix(),before=sha(p),after=sha(src)));s.write(p,src.read_bytes(),mode='wb')
    prior=json.loads((base/'final-source-manifest.json').read_text(encoding='utf-8-sig'));source=[];changed=[]
    for x in prior:
        h=sha(root/x['path']);source.append(dict(path=x['path'],sha256=h))
        if h!=x['sha256']:changed.append(x['path'])
    assert set(changed)<=set(['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml','MultiplayerHoeingAssistant/Views/MistletoePage.xaml.cs'])
    rows=[dict(path=p.relative_to(product).as_posix(),bytes=st.st_size,sha256=sha(p)) for p,st in s.files_under(product) if p.relative_to(product).parts[0] not in ('User','log') and p.relative_to(product).as_posix()!='Tools/MultiplayerHoeingAssistant/dpi_debug.log' and not p.relative_to(product).as_posix().startswith('Tools/MultiplayerHoeingAssistant/log/')]
    s.write(base/'runtime-manifest-final.json',json.dumps(rows,ensure_ascii=False,indent=2).encode());s.write(base/'source-manifest-final.json',json.dumps(source,ensure_ascii=False,indent=2).encode())
    result=dict(changed_modules=changes,source_changes=changed,existing_execution_host_body_byte_equivalent_after_removing_new_readonly_method=True,head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),bgi_sha=sha(product/'BetterGI.dll'),assistant_sha=sha(product/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll'),independent_review=False)
    s.write(base/'module-binding-final.json',json.dumps(result,ensure_ascii=False,indent=2).encode());print(json.dumps(result),flush=True)
