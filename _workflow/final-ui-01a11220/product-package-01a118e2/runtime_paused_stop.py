"""Reuse the existing owned mode-replacement carrier for the refreshed Stop helper."""
from pathlib import Path
source=Path(__file__).resolve().with_name('runtime_hostfix.py')
code=source.read_text(encoding='utf-8')
for old,new in [('runtime-hostfix','runtime-paused-stop'),('wf-own-mode-hostfix-01a118e2','wf-own-mode-hostseal-01a11940'),('任务中心 宿主冷恢复修复 01a118e2','任务中心 暂停停止终局修复 01a11940'),('current-unified-product-taskcenter-mode-runtime-01a118e2','same-product-cold-paused-stop-terminal-runtime-01a11940')]:
    assert old in code;code=code.replace(old,new)
old=";write(B/'result.json',dict(exit_code=code,runs=runs,"
new=";s.write(B/'private/arbitration-final.json',(D/'arbitration/arbitration-lease.json').read_bytes());lease=load(D/'arbitration/arbitration-lease.json');operations=[o for o in lease['handoff']['operations'] if o.get('runBinding')==runs[0]['runId']];assert len(operations)==1 and operations[0]['requestState']==6 and runs[0]['terminalRelease'] is not None and operations[0]['terminalReleaseEvidence']=='runstore-seal:'+runs[0]['terminalRelease']['id'];write(B/'result.json',dict(exit_code=code,runs=runs,original_operations=operations,"
assert code.count(old)==1;code=code.replace(old,new)
exec(compile(code,str(Path(__file__).resolve()),'exec'))
