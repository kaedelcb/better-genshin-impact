"""Resume the owned replay only after explicit user restoration; preserve interrupted output."""
from pathlib import Path
base=Path(__file__).resolve().with_name('runtime_paused_stop.py')
code=base.read_text(encoding='utf-8')
for old,new in [("('runtime-hostfix','runtime-paused-stop')","('runtime-hostfix','runtime-paused-stop-resume1')"),("'wf-own-mode-hostseal-01a11940'","'wf-own-mode-hostseal-resume1-01a11940'"),("'任务中心 暂停停止终局修复 01a11940'","'任务中心 暂停停止终局重演 01a11940'")]:
    assert code.count(old)==1;code=code.replace(old,new)
exec(compile(code,str(Path(__file__).resolve()),'exec'))
