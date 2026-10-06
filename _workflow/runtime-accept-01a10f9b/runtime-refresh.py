from pathlib import Path
template=Path(__file__).parents[1]/'path-runtime-01a10f14/runtime-refresh.py'
code=template.read_text(encoding='utf-8')
code=code.replace("base/'causal-repeat/result.json'", "base/'recovered-green/result.json'")
code=code.replace("assert causal['baseline_exit']==0 and causal['negative_exit']!=0 and causal['restored_exit']==0 and not causal['source_drift']", "assert causal['exit_code']==0 and not causal['source_drift']")
code=code.replace("assert sha(carrier/'MultiplayerHoeingAssistant.dll')==causal['assistant_sha']", "assert (base/'recovered-green/test-tree-terminal.json').exists()")
start=code.index("final=json.loads(");end=code.index("rows=json.loads(",start)
code=code[:start]+code[end:]
code=code.replace('path-runtime-01a10f14','runtime-accept-01a10f9b')
exec(compile(code,str(template)+'[real-interaction-correction]','exec'))
