from pathlib import Path
import json
p=Path('E:/CodexData/home/sessions/2026/10/06/rollout-2026-10-06T00-39-24-01a10cef-2e70-7a63-9ff4-f3439cc6eb19.jsonl')
last=None
for line in p.open(encoding='utf-8'):
    if 'turn_context' not in line: continue
    row=json.loads(line)
    if row.get('type')=='turn_context': last=row
assert last is not None
payload=last['payload']
print(json.dumps({'rollout':str(p),'timestamp':last.get('timestamp'),'model':payload.get('model'),'effort':payload.get('effort'),'reasoning_effort':payload.get('reasoning_effort'),'collaboration_mode':payload.get('collaboration_mode')},ensure_ascii=False))
