from pathlib import Path
import sys,json,hashlib,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;product=root/'_workflow/runtime-unified-01a10e1b/product';carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
with s.Session(root,'full-product-ui-module-refresh-after-normal-exit') as budget:
    budget.track(base);budget.track(product)
    processes=subprocess.check_output(['powershell','-NoProfile','-Command',"Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe')} | Select-Object -ExpandProperty ProcessId"],stderr=subprocess.DEVNULL).decode().strip()
    assert not processes,'Product still running: '+processes
    old=json.loads((base/'runtime-manifest.json').read_text(encoding='utf-8-sig'))
    for row in old:
        p=product/row['path']
        if row['path'] not in ('User/config.json','Tools/MultiplayerHoeingAssistant/dpi_debug.log') and not row['path'].startswith('Tools/MultiplayerHoeingAssistant/log/'):
            assert sha(p)==row['sha256'],row['path']
    changed=[]
    for p in sorted((product/'Tools/MultiplayerHoeingAssistant').glob('MultiplayerHoeingAssistant.*')):
        src=carrier/p.name;assert src.is_file()
        if sha(src)!=sha(p):
            s.write(base/'before-runtime-ui-refine'/p.name,p.read_bytes())
            changed.append(dict(path=p.relative_to(product).as_posix(),before=sha(p),after=sha(src)))
            s.write(p,src.read_bytes(),mode='wb')
    baseline=json.loads((base/'source-manifest.json').read_text(encoding='utf-8-sig'))
    source_changes=[];current=[]
    for row in baseline:
        h=sha(root/row['path']);current.append(dict(path=row['path'],sha256=h))
        if h!=row['sha256']:source_changes.append(row['path'])
    assert set(source_changes)<=set(['MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml','MultiplayerHoeingAssistant/Views/MistletoePage.xaml.cs']),source_changes
    rows=[dict(path=p.relative_to(product).as_posix(),bytes=st.st_size,sha256=sha(p)) for p,st in s.files_under(product) if p.relative_to(product).parts[0] not in ('User','log') and p.relative_to(product).as_posix()!='Tools/MultiplayerHoeingAssistant/dpi_debug.log' and not p.relative_to(product).as_posix().startswith('Tools/MultiplayerHoeingAssistant/log/')]
    s.write(base/'final-runtime-manifest.json',json.dumps(rows,ensure_ascii=False,indent=2).encode())
    s.write(base/'final-source-manifest.json',json.dumps(current,ensure_ascii=False,indent=2).encode())
    result=dict(changed_modules=changed,only_ui_source_changed=source_changes,head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),bgi_sha=sha(product/'BetterGI.dll'),assistant_sha=sha(product/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll'),full_regression_reuse='core source/conditions unchanged; old full module preserved; final UI Rebuild and 22/22 separate',user_data_not_overwritten=True)
    s.write(base/'final-module-binding.json',json.dumps(result,ensure_ascii=False,indent=2).encode());print(json.dumps(result),flush=True)
