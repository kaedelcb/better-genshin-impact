from pathlib import Path
import subprocess,sys
root=Path.cwd(); base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-routing-rounds-source-20261005-from-01a10898'
for kind in ['prepare','samekey','history']:
 result=subprocess.run([sys.executable,'-B',str(base/'run-round-pfp.py'),kind,'r3'],cwd=root)
 if result.returncode: raise SystemExit(result.returncode)
print('all final PFP complete with original format and fresh C products',flush=True)
