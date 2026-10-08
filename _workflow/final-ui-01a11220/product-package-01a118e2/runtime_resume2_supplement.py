"""Read back completed app evidence; retain the carrier's Id/id assertion failure."""
from pathlib import Path
import hashlib,json,os,subprocess,sys,winreg
R=Path(__file__).resolve().parents[3]; W=Path(__file__).resolve().parent
B=W/'runtime-paused-stop-resume2';P=R/'_workflow/runtime-unified-01a10e1b/product'
sys.path.insert(0,str(R/'tools/mistletoe'))
import storage_limits as s
sha=lambda data:hashlib.sha256(data).hexdigest()
load=lambda p:json.loads(p.read_bytes())
session=s.Session(R,'same-product-resume2-terminal-readback-01a1198f')
session.policy=dict(session.policy,operation_bytes=16*1024*1024)
with session:
    old=session.old_roots[:];session.old_roots=list(dict.fromkeys(old));assert set(old)==set(session.old_roots)
    session.track(B)
    assert load(B/'app-lifecycle.json')['exits']==[0,0,0]
    assert load(B/'app-lifecycle.json')['same_bgi_alive_after_second']
    assert load(B/'apps-process-result.json')['exit_code']==0
    terminal=load(B/'apps-tree-terminal.json'); assert terminal['active_processes']==0 and terminal['helper_exit']==0
    assert not (B/'inflight.json').exists() and not (B/'result.json').exists()
    for tag in ['product-user','real-user']:
        assert load(B/'private'/(tag+'-before.json'))==load(B/'private'/(tag+'-after.json'))
    first=B/'private/first-exit/runs/run-064d11070de2.run.json'
    paused=load(first); assert sha(first.read_bytes())=='cc4add79bc815208db8061b2fc69dfd60dd14791964b505aa9cabf438bc7e633'
    run_path=B/'private/after/runs/run-064d11070de2.run.json';run=load(run_path)
    assert run==load(B/'private/second-exit/runs/run-064d11070de2.run.json')
    assert run['state']==6 and run['stopRequested'] and run['terminalRelease']
    assert run['workflowRevision']==paused['workflowRevision'] and run['cursor']==paused['cursor']
    assert not run['nodeOutcomes'] and not run['submissionHistory'] and not run['tailReached']
    ops=[o for o in load(B/'private/arbitration-final.json')['handoff']['operations'] if o.get('runBinding')==run['runId']]
    assert len(ops)==1 and ops[0]['requestState']==6
    assert ops[0]['terminalReleaseEvidence']=='runstore-seal:'+run['terminalRelease']['Id']
    original=load(B/'private/protocol-before.json')
    with winreg.OpenKey(winreg.HKEY_CURRENT_USER,r'Software\Classes\BetterGI\shell\open\command') as key:
        assert winreg.QueryValueEx(key,'')==(original['value'],original['kind'])
    version=load(P/'VERSION.json'); inputs=load(Path(version['compiled_input_hashes']))
    assert len(inputs)==313 and all(sha((R/p).read_bytes())==h for p,h in inputs.items())
    assert all(sha((P/m['path']).read_bytes())==m['sha256'] for m in version['assistant_modules'])
    assert sha((P/'BetterGI.dll').read_bytes())==version['bgi_dll_sha256']
    assert sha((P/'BetterGI.exe').read_bytes())==version['bgi_exe_sha256']
    facts=json.loads(subprocess.check_output(['powershell.exe','-NoProfile','-Command',
        "Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('BetterGI.exe','MultiplayerHoeingAssistant.exe')} | Select-Object Name,ProcessId,SessionId,ExecutablePath | ConvertTo-Json -Compress"],text=True,encoding='utf-8-sig'))
    assert not any(str(P).lower() in f['ExecutablePath'].lower() for f in facts)
    assert {f['ProcessId'] for f in facts}=={43292,54668} and all(f['SessionId']==3 for f in facts)
    result=dict(kind='ordinary completed application and persisted terminal readback; not certified receipt',
        controller_exit=1,carrier_failure="KeyError: 'id'; actual terminalRelease field is Id",original_failure_preserved=True,
        app_exit_codes=[0,0,0],job_terminal=terminal,actual_verified_for_this_scene=True,product_complete=False,
        run=run,original_operations=ops,paused_sha256=sha(first.read_bytes()),
        refused_resume_before_stop_sha256=sha(first.read_bytes()),refused_resume_evidence='private/native-ui-source.jsonl',
        product_user_changed=[],real_user_changed=[],protocol_restored=True,source_modified=False,
        source_inputs_current_verified=313,assistant_modules=version['assistant_modules'],
        native_ui_source_sha256=sha((B/'private/native-ui-source.jsonl').read_bytes()),
        d_user_processes_preserved=facts,new_review_requests=0,
        authorization='OWNER-RESUME-FINAL-ONE-20261008-FROM-01a11963',review_remaining=1,
        old_interrupted_evidence_preserved=['runtime-paused-stop','runtime-paused-stop-resume1'])
    s.write(B/'result-supplement.json',s.encode(result))
    text='''# 同产物冷启暂停停止实际验收

当前助手 DLL 19291863、BGI DLL 0720fe79 / EXE 42aa9e13；313 编译输入及五模块逐 SHA 对应。采用 OWNER-RESUME-FINAL-ONE-20261008-FROM-01a11963，原存储域 128 MiB 预约，无产品/User复制。

正式任务中心启动 run-064d11070de2，未来 gate→end 等待后暂停。原 run SHA cc4add79bc815208db8061b2fc69dfd60dd14791964b505aa9cabf438bc7e633，修订8、游标gate和12:14等待保持。正式删除自有gate/end、追加已有狗粮顺序引用并保存 e261bff7，未启动该资源。

首轮助手28396正常退出0；同BGI19664/epoch冷启助手66972。12:00:00恢复同步返回Unavailable/定义不兼容，原Paused文件逐字节保持、零节点/提交。12:00:16显式Stop返回Effective，Cancelled/耐久Stop、runstore seal 64e74317c96d48c2b872e5829de711a3，原仲裁74d3c11094c044b2b9e10a06aeb02ec9为TerminalCompleted并绑定该seal；无节点/提交/收尾。

两轮助手及BGI正常退出0/0/0，同BGI在末轮助手退出后仍活；Job active0/helper exit0，产品User和真实User前后差集空、协议恢复。原始应用来源、first/second/after及仲裁文件保留。载体最后误读封印字段id而实际为Id，controller exit1 / KeyError原失败保留；result-supplement.json独立核对原始字段、应用终态、来源与数据，未伪改原result或制造认证收据。

上次resume1在工具连接重建后控制器/Job消失，保留原目录及interrupted-terminal.json；不计正常验收。旧用户Escape中断亦保持。本场景实际成立不替代整版独立复核、用户游戏效果反馈或完整产品交付；本次已授权整版Sol/high固定1次尚未派发。
'''
    s.write(B/'ACTUAL.md',text.encode('utf-8'))
    print(json.dumps(dict(application_exits=[0,0,0],job_active=0,controller_exit=1,
        actual_verified_for_this_scene=True,seal=run['terminalRelease']['Id'],original_operation=ops[0]['requestIdentity'],
        unchanged_user=True,review_remaining=1,product_complete=False),ensure_ascii=False))
