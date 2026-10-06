from pathlib import Path
template=Path(__file__).with_name('runtime-refresh.py')
code=template.read_text(encoding='utf-8').replace('recovered-green','gesture-green')
code=code.replace("exec(compile(code,", "code=code.replace(\"out=base/'runtime-refresh'\",\"out=base/'runtime-refresh-gesture'\")\nexec(compile(code,")
exec(compile(code,str(template)+'[gesture-refresh]','exec'))
