from pathlib import Path
import sys,json,hashlib,os,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;old=root/'_workflow/runtime-unified-01a10cef/candidate-r3'
target=root/'_workflow/runtime-unified-01a10e1b/product'
sha=lambda p:hashlib.sha256(p.read_bytes()).hexdigest()
user=root/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
with s.Session(root,'full-product-complete-runtime-assembly') as budget:
    budget.track(base);budget.track(target)
    assert not target.exists()
    prior=json.loads((root/'_workflow/closeout-01a10cef/final-runtime-manifest.json').read_text(encoding='utf-8-sig'))
    choices={}
    for row in prior:
        rel=Path(row['path']);p=old/rel;assert sha(p)==row['sha256'],str(p)
        source=p
        if len(rel.parts)==1 and rel.name.startswith(('BetterGI.','Fischless.WindowsInput.','Fischless.HotkeyCapture.','Fischless.GameCapture.')):
            p2=root/'_workflow/runtime-unified-01a10cef/single-tests/bgi'/rel
            if p2.is_file():source=p2
        if rel.parts[:2]==('Tools','MultiplayerHoeingAssistant') and rel.name.startswith('MultiplayerHoeingAssistant.'):
            p2=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant'/rel.name
            assert p2.is_file();source=p2
        choices[rel]=source
    for p in [old/'User/config.json',old/'User/OneDragon/验收-统一版本-01a10c87.json']:
        assert p.is_file();choices[p.relative_to(old)]=p
    # A precise registered resource set; no cache, backup, screenshots, or whole User copy.
    resources=['AutoFight','AutoHoeing','AutoPathing','JsScript','ScriptGroup','KeyMouseScript']
    for category in resources:
        for p,st in s.files_under(user/category):choices[Path('User')/p.relative_to(user)]=p
    legacy=[*sorted((user/'OneDragon').glob('*.json')),user/'config.json']
    total=sum(p.stat().st_size for p in choices.values())+sum(p.stat().st_size for p in legacy)
    budget.check(total,location=target)
    original={str(p):sha(p) for p in set(choices.values())|set(legacy)}
    manifest=[]
    for rel,p in choices.items():
        dst=target/rel;data=p.read_bytes();s.write(dst,data);manifest.append(dict(path=rel.as_posix(),bytes=len(data),sha256=hashlib.sha256(data).hexdigest()))
    for p in legacy:s.write(base/'private/legacy-source'/p.relative_to(user),p.read_bytes())
    assert all(sha(Path(p))==h for p,h in original.items())
    assert not any('testhost' in r['path'].lower() or '.UnitTest.' in r['path'] or 'ControlledWriterProbe' in r['path'] for r in manifest)
    inputs=[]
    for folder in ['MultiplayerHoeingAssistant','BetterGenshinImpact','Fischless.WindowsInput','Fischless.HotkeyCapture','Fischless.GameCapture']:
        for p,st in s.files_under(root/folder):
            rel=p.relative_to(root)
            if not any(x in ('bin','obj','User') for x in rel.parts) and p.suffix in ('.cs','.xaml','.csproj','.props','.targets','.resx'):
                inputs.append(dict(path=rel.as_posix(),sha256=sha(p)))
    s.write(base/'runtime-manifest.json',json.dumps(manifest,ensure_ascii=False,indent=2).encode())
    s.write(base/'source-manifest.json',json.dumps(inputs,ensure_ascii=False,indent=2).encode())
    s.write(base/'private/source-original-hashes.json',json.dumps(original,ensure_ascii=False).encode())
    result=dict(directory=str(target),files=len(manifest),bytes=total,bgi_sha=sha(target/'BetterGI.dll'),assistant_sha=sha(target/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.dll'),source_head=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),resources=resources,originals_unchanged=True,private_user_resources=True,complete_goal=False,independent_review=False)
    s.write(base/'assembly.json',json.dumps(result,ensure_ascii=False,indent=2).encode());print(json.dumps(result,ensure_ascii=False),flush=True)
