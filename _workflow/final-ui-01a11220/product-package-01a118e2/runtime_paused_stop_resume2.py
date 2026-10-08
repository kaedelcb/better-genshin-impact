"""Fresh owned replay after verified resume1 controller/Job disappearance; retain all old evidence."""
from pathlib import Path
source=Path(__file__).resolve().with_name('runtime_paused_stop_resume.py')
code=source.read_text(encoding='utf-8')
assert code.count('resume1')==2
code=code.replace('resume1','resume2')
exec(compile(code,str(Path(__file__).resolve()),'exec'))
