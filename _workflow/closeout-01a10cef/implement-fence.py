from pathlib import Path
import sys,json,hashlib
root=Path.cwd();sys.path.insert(0,str(root/'tools/mistletoe'));import storage_limits as s
base=Path(__file__).resolve().parent;source=root/'MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStopAuthority.cs'
old=source.read_bytes();nl=b'\r\n' if b'\r\n' in old else b'\n'
needle=nl.join([b'            var timestamp = data.GetProperty("lastManualStopTimestamp");',b'            long? last = timestamp.ValueKind == JsonValueKind.Null ? null : timestamp.GetInt64();'])
replacement=nl.join([b'            // The real BGI wire omits nullable fields; version zero still proves no manual stop.',b'            long? last = data.TryGetProperty("lastManualStopTimestamp", out var timestamp)',b'                && timestamp.ValueKind != JsonValueKind.Null ? timestamp.GetInt64() : null;'])
assert old.count(needle)==1
with s.Session(root,'confirmed-fresh-stop-fence-wire-repair'):
    s.write(base/'fence-implementation-before.cs',old);new=old.replace(needle,replacement);s.write(source,new,mode='wb')
    s.write(base/'fence-implementation.json',json.dumps(dict(source=str(source),before_bytes=len(old),after_bytes=len(new),before_sha=hashlib.sha256(old).hexdigest(),after_sha=hashlib.sha256(new).hexdigest(),actual_red='live-ipc/r3-fence-inspect/manual-stop-fence.json and fence-red targeted 108P/1F',unchanged_guards='explicit version, matching epoch/frequency, positive-version timestamp required, zero-version non-null timestamp rejected',independent_review=False),indent=2).encode())
    plan=root/'_workflow/local-wait-admission-gates-20261004/plan.json';v=json.loads(plan.read_text(encoding='utf-8'))
    v['fresh_stop_fence_wire_admission']=dict(function='all task-center starts and future scheduling',gap='actual BGI version zero omits null timestamp; consumer required property and refuses fresh starts',evidence='_workflow/closeout-01a10cef/live-ipc/r3-fence-inspect/manual-stop-fence.json',minimum='accept absent nullable timestamp only with explicit zero version; preserve guards; red-green, same-candidate UI retry and full MHA regression',severity='important',independent_closed=False,review_budget_unchanged=True,next='same complete candidate validation and delivery')
    s.write(plan,json.dumps(v,ensure_ascii=False,indent=2).encode(),mode='wb')
