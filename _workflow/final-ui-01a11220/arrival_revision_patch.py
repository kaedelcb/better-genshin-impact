from pathlib import Path
import hashlib, json, os
ROOT = Path(__file__).resolve().parents[2]
BASE = ROOT/'_workflow/final-ui-01a11220/arrival-revision-01a11808'
target = ROOT/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
original = target.read_bytes()
assert original == (BASE/'before/WorkflowRunner.cs').read_bytes()
red = json.loads((BASE/'red/result.json').read_bytes())
assert red['build'] == 0 and len(red['failed']) == 2 and all('RevisionSavedDuringNodeWait_AppliesBeforeConditionChoosesSuccessor' in row for row in red['failed'])
assert len(red['passed']) == 4 and not red['other'] and not red['source_drift']
newline = b'\r\n' if b'\r\n' in original else b'\n'
anchor = b'                if (node.Kind == "control.condition")'+newline
assert original.count(anchor) == 1
block = '\n'.join([
    '                // 定时等待完成是尚未执行节点动作的到达边界，先对账等待期间的新修订。',
    '                // 已完成条件的选中边仍保留在持久游标中，不重新求值或扫描未选路径。',
    '                var arrivalPlan = plan;',
    '                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);',
    '                if (!ReferenceEquals(arrivalPlan, plan)) continue;',
    '',
]).encode('utf-8').replace(b'\n', newline) + newline
updated = original.replace(anchor, block+anchor)
temporary = target.with_name(target.name+'.arrival-revision-patch.tmp')
with temporary.open('xb') as file:
    file.write(updated); file.flush(); os.fsync(file.fileno())
os.replace(temporary, target)
assert target.read_bytes() == updated and len(updated) > len(original)
record = dict(path=str(target), original_bytes=len(original), updated_bytes=len(updated), original_lines=len(original.splitlines()), updated_lines=len(updated.splitlines()),
    original_sha256=hashlib.sha256(original).hexdigest(), updated_sha256=hashlib.sha256(updated).hexdigest(), bom_preserved=original[:3]==updated[:3], newline_preserved=(b'\r\n' in original)==(b'\r\n' in updated),
    new_finding='PATH-ARRIVAL-REVISION-INSERT-1', source_candidate_only=True, independent_review_remaining=0)
(BASE/'source-patch.json').write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(record, ensure_ascii=False))
