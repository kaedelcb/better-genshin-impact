from pathlib import Path
old=Path(__file__).with_name('causal.py');code=old.read_text(encoding='utf-8')
def change(before,after):
 global code
 assert code.count(before)==1,(before,code.count(before))
 code=code.replace(before,after)
change("out=base/'causal';", "out=base/'causal-repeat';")
change("source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'", "source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'\ntiming_source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowNodeSchedule.cs'")
change("oracle='FullyQualifiedName~RunnerWaitRequest_UsesBoundNodeTimeInsteadOfRootTrigger|FullyQualifiedName~Leaf_ArmsBeforeSend_FreezesAndClosesBeforeRouting'", "oracle='FullyQualifiedName~RunnerWaitRequest_UsesBoundNodeTimeInsteadOfRootTrigger|FullyQualifiedName~Leaf_ArmsBeforeSend_FreezesAndClosesBeforeRouting|FullyQualifiedName~ScheduledBackEdge_RepeatsAtSameValidTime_WithoutDayDeduplication'")
change("out.mkdir();original=source.read_bytes();storage.write(out/'original-runner.bin',original)", """out.mkdir();original=source.read_bytes();storage.write(out/'original-runner.bin',original)
 original_timing=timing_source.read_bytes();storage.write(out/'original-timing.bin',original_timing)
 loop_guard=b'if (occurrence.LoopIteration > 0 && timing.Kind != "trigger.time")'
 window_guard=b'while (timing.Kind=="trigger.timeFixed" ? now>=timing.ScheduledAt.AddMinutes(1) : now>=timing.WindowEndsAt)'
 assert original_timing.count(loop_guard)==1 and original_timing.count(window_guard)==1
 mutant_timing=original_timing.replace(loop_guard,b'if (occurrence.LoopIteration > 0)').replace(window_guard,b'while (timing.ScheduledAt < now || previous is not null && timing.ScheduledAt <= previous.ScheduledAt)')
 storage.write(out/'mutant-timing.bin',mutant_timing)""")
change('def put(variant):','def put(variant,target_source=source):')
change('quote(source)', 'quote(target_source)')
change("put(out/'mutant-runner.bin');assert source.read_bytes()==mutant", "put(out/'mutant-runner.bin');put(out/'mutant-timing.bin',timing_source);assert source.read_bytes()==mutant and timing_source.read_bytes()==mutant_timing")
change("put(out/'original-runner.bin');assert source.read_bytes()==original", "put(out/'original-runner.bin');put(out/'original-timing.bin',timing_source);assert source.read_bytes()==original and timing_source.read_bytes()==original_timing")
change('byte_restored=True)', 'byte_restored=True,original_timing_sha=sha(original_timing),mutant_timing_sha=sha(mutant_timing),restored_timing_sha=sha(timing_source.read_bytes()))')
assert code.count('==3')==3
code=code.replace('==3','==6')
exec(compile(code,str(old)+'[same-day-repeat-extension]','exec'))
