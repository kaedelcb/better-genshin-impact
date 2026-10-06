from pathlib import Path
template=Path(__file__).parents[1]/'path-runtime-01a10f14/causal.py'
code=template.read_text(encoding='utf-8')
def change(old,new):
 global code
 assert code.count(old)==1,(old,code.count(old))
 code=code.replace(old,new)
change("base=Path(__file__).resolve().parent;out=base/'causal';", "base=Path(__file__).resolve().parent;out=base/'undo-causal';")
change("source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'", "source=root/'MultiplayerHoeingAssistant/Views/ScheduleListView.xaml.cs'")
change("oracle='FullyQualifiedName~RunnerWaitRequest_UsesBoundNodeTimeInsteadOfRootTrigger|FullyQualifiedName~Leaf_ArmsBeforeSend_FreezesAndClosesBeforeRouting'", "oracle='FullyQualifiedName~SharedViews_UndoScheduledMove'")
change("'path-runtime-01a10f14-critical-causal'", "'runtime-accept-01a10f9b-undo-causal'")
start=code.index(" original_tier=")
end=code.index(" subjects=",start)
code=code[:start]+''' guard=b'if (Timeline is null || _drawing || Draft?.IsRestoringSchedule==true) return;'
 assert original.count(guard)==1
 mutant=original.replace(guard,b'if (Timeline is null || _drawing) return;')
 storage.write(out/'mutant-view.bin',mutant)
'''+code[end:]
code=code.replace("out/'original-runner.bin'", "out/'original-view.bin'").replace("out/'mutant-runner.bin'", "out/'mutant-view.bin'")
assert code.count('==3')==3
code=code.replace('==3','==1')
exec(compile(code,str(template)+'[shared-view-undo]','exec'))
