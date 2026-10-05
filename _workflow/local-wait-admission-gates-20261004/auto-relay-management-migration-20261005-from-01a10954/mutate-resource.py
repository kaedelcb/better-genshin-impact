from pathlib import Path
import hashlib,json,sys,subprocess,os
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
source=root/'BetterGenshinImpact/Service/ExternalInterface/ExternalResourceEditor.cs'
original=source.read_bytes(); original_hash=hashlib.sha256(original).hexdigest()
needle=b'if (current.Revision != revision)';assert original.count(needle)==1
mutant=original.replace(needle,b'if (false && current.Revision != revision)')
def replace(data):
    tmp=source.with_suffix('.resource-restoring');tmp.write_bytes(data);os.replace(tmp,source)
record={'purpose':'本版过期引用不能跳到错误版本；验证实际拒绝断言能检出revision守卫缺失','source':str(source),'original_sha256':original_hash,'mutant_sha256':hashlib.sha256(mutant).hexdigest(),'baseline':'resource-bgi-r4/bgi.trx','certification':False}
try:
    replace(mutant)
    record['negative_exit']=subprocess.run([sys.executable,'-B',str(base/'check-resource-entry.py'),'resource-revision-negative','bgi'],cwd=root).returncode
finally:
    replace(original);record['restored_sha256']=hashlib.sha256(source.read_bytes()).hexdigest();assert record['restored_sha256']==original_hash
    (base/'resource-mutation.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps(record,ensure_ascii=False),flush=True)
