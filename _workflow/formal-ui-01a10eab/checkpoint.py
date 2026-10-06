from pathlib import Path
import sys,json,hashlib,xml.etree.ElementTree as ET,subprocess
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'))
import storage_limits as storage
base=Path(__file__).resolve().parent
owned=['MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowPlanner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowNodeSchedule.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.cs','MultiplayerHoeingAssistant/ViewModels/TaskCenterPanelViewModel.EditParts.cs','MultiplayerHoeingAssistant/ViewModels/WorkflowScheduleViewModel.cs','MultiplayerHoeingAssistant/Views/MistletoePage.xaml','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml','MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalScheduleTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/FormalScheduleRenderTests.cs','Docs/design/mistletoe-ui-design-intake-20261006.md']
sha=lambda data:hashlib.sha256(data).hexdigest()
with storage.Session(root,'formal-ui-checkpoint-evidence') as budget:
    budget.track(base)
    assert json.loads((base/'causal-result.json').read_text())['restored_exit']==0
    carrier=root/'_workflow/runtime-unified-01a10cef/single-tests/assistant/MultiplayerHoeingAssistant.dll'
    source=[dict(path=rel,bytes=len(data),lines=data.count(b'\n'),sha256=sha(data)) for rel in owned for data in [(root/rel).read_bytes()]]
    storage.write(base/'checkpoint-source.json',json.dumps(source,indent=2).encode())
    trx=ET.parse(base/'causal-restored/causal.trx');results=[e for e in trx.iter() if e.tag.endswith('UnitTestResult')]
    assert all(e.get('outcome')=='Passed' for e in results)
    counters={word:sum(e.get('outcome')==word for e in results) for word in ['Passed','Failed','NotExecuted']}
    previous=json.loads((root/'_workflow/full-product-01a10e1b/private/source-original-hashes.json').read_text(encoding='utf-8-sig'))
    user=root/'BetterGenshinImpact/bin/x64/Debug/net8.0-windows10.0.22621.0/User'
    changes=[];count=0;js=0
    for name,old in previous.items():
        p=Path(name)
        if not p.is_relative_to(user):continue
        count+=1;data=p.read_bytes();h=sha(data)
        if p.suffix.lower()=='.js':js+=1;assert h==old,'third party source changed'
        if h!=old:
            changes.append(dict(path=name,before=old,current=h,bytes=len(data)))
            storage.write(base/'private'/('user-current-'+str(len(changes))+p.suffix),data)
    storage.write(base/'private/user-current-drift.json',json.dumps(changes,ensure_ascii=False,indent=2).encode())
    recovery=json.loads((root/'_workflow/full-product-01a10e1b/private/ui-recovery.json').read_text(encoding='utf-8-sig'))
    live=Path(recovery['preserved']).parent/'NexusBGI'
    current={p.relative_to(live).as_posix():sha(p.read_bytes()) for p in live.rglob('*') if p.is_file()}
    assert current==recovery['before']
    facts=dict(marker='UI-RESEARCH-FUNCTION-FIRST-20261006-FROM-01a10e1b',head_before_commit=subprocess.check_output(['git','rev-parse','HEAD']).decode().strip(),level='source candidate and real offscreen WPF/component checks',tests=counters,assistant_candidate_sha256=sha(carrier.read_bytes()),protected_user_files=count,protected_user_drift=len(changes),third_party_js_sources_unchanged=js,original_assistant_files_unchanged=len(current),user_runtime_progress_retained=True,independent_review=False,product_delivered=False,game_executed=False,runtime_product_refreshed=False,review_bundle_check='BLOCKED: review bundle drift',parallel_discovery_error='r61-distribution-candidate missing report; no unregistered discoveries',reviews_sent=0,readonly_subagent_decision='not used: tightly coupled UI/draft/clock changes and user clarification; unique writer retained')
    storage.write(base/'checkpoint-evidence.json',json.dumps(facts,ensure_ascii=False,indent=2).encode())
    print(json.dumps(facts,ensure_ascii=False),flush=True)
