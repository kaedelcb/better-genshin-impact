"""Run the complete own product with a dedicated assistant data root; no user-directory moves."""
from pathlib import Path
import hashlib, json, os, subprocess, sys, winreg
ROOT = Path(__file__).resolve().parents[2]
BASE = Path(__file__).resolve().parent / 'own-runtime'
PRODUCT = ROOT / '_workflow/runtime-unified-01a10e1b/product'
CARRIER = ROOT / '_workflow/runtime-unified-01a10cef/single-tests/assistant'
DATA = BASE / 'assistant-data'
sys.path.insert(0,str(ROOT/'tools/mistletoe'))
import storage_limits as s
import process_runner
sha = lambda path: hashlib.sha256(path.read_bytes()).hexdigest()

if sys.argv[1] == '--apps':
    # Both are owned descendants of process_runner's Job. UI actions and normal
    # exits are performed through Computer Use, never through this launcher.
    assert os.environ.get('NEXUSBGI_DATA_ROOT') == str(DATA)
    bgi = subprocess.Popen([str(PRODUCT/'BetterGI.exe')],cwd=PRODUCT)
    assistant = subprocess.Popen([str(PRODUCT/'Tools/MultiplayerHoeingAssistant/MultiplayerHoeingAssistant.exe')],
        cwd=PRODUCT/'Tools/MultiplayerHoeingAssistant')
    print('OWN_APPS',bgi.pid,assistant.pid,flush=True)
    assistant_exit = assistant.wait()
    bgi_exit = bgi.wait()
    print('NORMAL_APP_EXITS',assistant_exit,bgi_exit,flush=True)
    sys.exit(0 if assistant_exit==bgi_exit==0 else 1)

phase = sys.argv[1]
out = BASE/phase
assert not out.exists(), 'Existing runtime evidence refused'
with s.Session(ROOT,'final-ui-01a11220-own-runtime-'+phase) as budget:
    budget.track(BASE);budget.track(PRODUCT/'Tools/MultiplayerHoeingAssistant');budget.track(PRODUCT/'User')
    out.mkdir(parents=True)
    def write(path,value): s.write(path,json.dumps(value,ensure_ascii=False,indent=2).encode())
    user_before={p.relative_to(PRODUCT/'User').as_posix():sha(p) for p,_ in s.files_under(PRODUCT/'User')}
    write(out/'private/product-user-before.json',user_before)
    if phase in ['refresh','refresh-drag','refresh-polish']:
        green=ROOT/('_workflow/final-ui-01a11220/ui-polish/green' if phase=='refresh-polish' else
            '_workflow/final-ui-01a11220/drag-effects/green' if phase=='refresh-drag' else '_workflow/final-ui-01a11220/isolated-data/green3')
        result=json.loads((green/'result.json').read_text(encoding='utf-8'))
        assert result['exit_code']==0 and result['source_drift']==[]
        for name in ['build','test']:
            assert json.loads((green/(name+'-tree-terminal.json')).read_text(encoding='utf-8'))['active_processes']==0
        hashes=json.loads((green/'source-hashes.json').read_text(encoding='utf-8'))
        assert all(sha(ROOT/rel)==value for rel,value in hashes.items())
        prior=BASE/'refresh-drag/result.json' if phase=='refresh-polish' else BASE/'refresh/result.json' if phase=='refresh-drag' else ROOT/'_workflow/runtime-accept-01a10f9b/runtime-refresh-polish/result.json'
        old=json.loads(prior.read_text(encoding='utf-8'))
        if phase=='refresh-polish':
            second=json.loads((BASE/'second/result.json').read_text(encoding='utf-8'))
            assert second['exit_code']==0 and second['product_user_changed']==[] and second['protocol_restored']
            assert json.loads((BASE/'second/apps-tree-terminal.json').read_text(encoding='utf-8'))['active_processes']==0
            records=[]
            for path in (DATA/'runs').glob('*.run.json'):
                run=json.loads(path.read_text(encoding='utf-8'))
                assert run['state']==6 and run['stopRequested'] and not run['submissionHistory'] and not run['nodeOutcomes'] and not run['completionHistory']
                s.write(out/'before-restart/runs'/path.name,path.read_bytes())
                records.append(dict(run_id=run['runId'],workflow=run['workflowId'],revision=run['workflowRevision'],sha256=sha(path),state=run['state'],stop_requested=run['stopRequested'],submissions=0,outcomes=0,completion=0))
            flows=[]
            for path in (DATA/'flows').glob('*.flow.json'):
                s.write(out/'before-restart/flows'/path.name,path.read_bytes());flows.append(dict(file=path.name,sha256=sha(path)))
            write(out/'before-restart.json',dict(runs=records,flows=flows,apps_terminal=second,source_thread='01a112a0-504d-7f71-b5ba-96f62711bab0',product_acceptance=False))
            fixture=json.loads((BASE/'second/single-export.json').read_text(encoding='utf-8'))
            fixture['workflowId']='wf-own-import-01a112a0';fixture['name']='单流程互导验证 01a112a0'
            write(out/'single-unique.json',fixture)
        assert sha(PRODUCT/'BetterGI.dll')==old.get('bgi_sha256',old.get('bgi_sha'))
        source_rows=json.loads((ROOT/'_workflow/full-product-01a10e1b/source-manifest.json').read_text(encoding='utf-8-sig'))
        bgi_inputs=[row for row in source_rows if row['path'].split('/')[0] in
            ['BetterGenshinImpact','Fischless.WindowsInput','Fischless.HotkeyCapture','Fischless.GameCapture']]
        assert all(sha(ROOT/row['path'])==row['sha256'] for row in bgi_inputs)
        for row in old['updated']:
            assert sha(PRODUCT/row['path'])==row['sha256']
        updated=[]
        for name in ['MultiplayerHoeingAssistant.dll','MultiplayerHoeingAssistant.exe','MultiplayerHoeingAssistant.pdb',
                     'MultiplayerHoeingAssistant.deps.json','MultiplayerHoeingAssistant.runtimeconfig.json']:
            target=PRODUCT/'Tools/MultiplayerHoeingAssistant'/name
            s.write(out/'before'/name,target.read_bytes())
            temp=target.with_name(name+'.isolated-01a11220')
            s.write(temp,(CARRIER/name).read_bytes());os.replace(temp,target)
            assert sha(target)==sha(CARRIER/name)
            updated.append(dict(path=target.relative_to(PRODUCT).as_posix(),sha256=sha(target)))
        assert user_before=={p.relative_to(PRODUCT/'User').as_posix():sha(p) for p,_ in s.files_under(PRODUCT/'User')}
        write(out/'result.json',dict(updated=updated,bgi_sha256=sha(PRODUCT/'BetterGI.dll'),
            bgi_inputs_unchanged=len(bgi_inputs),product_user_unchanged=True,
            product_user_files=len(user_before),apps_launched=False,acceptance=False))
        print('Own product modules refreshed; User untouched',flush=True)
    else:
        if phase == 'first':
            assert not DATA.exists()
            DATA.mkdir()
            write(DATA/'assistant-config.json',dict(serverUrl='',standaloneMode=True,disclaimerAccepted=True,
                bgiPath=str(PRODUCT/'BetterGI.exe'),observerMode=False,autoLaunchOnBoot=False,
                autoLaunchWithBgi=False,guardBgi=False,scheduledOnlineTime=''))
            s.write(DATA/'startup-flow.json',b'{"enabled":false,"steps":[]}')
        else:
            assert DATA.is_dir()
        if phase=='third':
            write(out/'storage-authorization-17.5gib.json',dict(source='本聊天用户明确回复：批准累计17.5 GiB',source_thread='01a112a0-504d-7f71-b5ba-96f62711bab0',retained_before_bytes=18253611008,retained_after_bytes=18790481920,operation_bytes=1610612736,min_free_bytes=8589934592,scope='原Goal剩余验收/复核材料；不增加功能，不删除历史，不移动真实用户目录',original_authorization_preserved='_workflow/full-product-01a10e1b/storage-authorization-17gib.json'))
            local=json.loads((BASE/'second/single-export.json').read_text(encoding='utf-8'))
            local['workflowId']='wf-own-control-01a112a0';local['name']='本机判断和结束验收 01a112a0'
            local['nodes']=[node for node in local['nodes'] if node['kind'] in ['control.condition','control.end']]
            assert len(local['nodes'])==2
            end=next(node['nodeId'] for node in local['nodes'] if node['kind']=='control.end')
            for node in local['nodes']:
                node['strategies']=[dict(kind='flow.route')];node['scheduleLane']=0;node.pop('scheduleSpan',None)
                if node['kind']=='control.condition':node['path']['yes']=end;node['path']['no']='$end'
            local['scheduleLanes']=['主车道']
            write(out/'local-control.json',local)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:
            protocol,kind=winreg.QueryValueEx(key,'')
        write(out/'private/protocol-before.json',dict(value=protocol,kind=kind))
        env=os.environ.copy();env['NEXUSBGI_DATA_ROOT']=str(DATA)
        print('Starting owned software; dedicated data root',DATA,flush=True)
        code,_,_=process_runner.run([sys.executable,'-B',str(Path(__file__).resolve()),'--apps'],
            cwd=ROOT,env=env,directory=out,recovery_directory=out,phase='apps',timeout=3600)
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command',0,winreg.KEY_READ|winreg.KEY_WRITE) as key:
            current,_=winreg.QueryValueEx(key,'')
            if str(PRODUCT/'BetterGI.exe').lower() in current.lower():
                winreg.SetValueEx(key,'',0,kind,protocol)
            current,current_kind=winreg.QueryValueEx(key,'')
            assert current==protocol and current_kind==kind
        user_after={p.relative_to(PRODUCT/'User').as_posix():sha(p) for p,_ in s.files_under(PRODUCT/'User')}
        write(out/'result.json',dict(exit_code=code,assistant_data_root=str(DATA),
            product_user_changed=[p for p in set(user_before)|set(user_after) if user_before.get(p)!=user_after.get(p)],
            protocol_restored=True,game_executed=False,acceptance=False))
        print('Owned apps terminal',code,'protocol restored',flush=True)
