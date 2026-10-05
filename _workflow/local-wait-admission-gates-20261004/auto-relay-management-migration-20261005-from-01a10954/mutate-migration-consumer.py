from pathlib import Path
import json,hashlib,sys,subprocess,os
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-management-migration-20261005-from-01a10954'
variants=[('MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs',
    'throw new WorkflowQuarantinedException("迁移提交未确认，禁止启动或编辑；请使用迁移回退恢复原候选。");',';'),
    ('MultiplayerHoeingAssistant/Services/TaskCenter/MigrationReferenceActivation.cs',
    ' || Sha256Hex(input) != target.ExpectedContentHash','')]
before={};record={'purpose':'本版未提交激活不能执行、替换只能消费原输入版本；两具名断言判别力','baseline':'migration-consumer-r2/assistant.trx','sources':[],'certification':False}
def replace(path,data):
    tmp=path.with_suffix('.migration-restoring');tmp.write_bytes(data);os.replace(tmp,path)
try:
    for rel,old,new in variants:
        path=root/rel;data=path.read_bytes();before[rel]=data;assert data.count(old.encode())==1
        mutant=data.replace(old.encode(),new.encode());replace(path,mutant)
        record['sources'].append(dict(path=rel,original_sha256=hashlib.sha256(data).hexdigest(),mutant_sha256=hashlib.sha256(mutant).hexdigest()))
    record['negative_exit']=subprocess.run([sys.executable,'-B',str(base/'check-resource-entry.py'),'migration-consumer-negative','assistant'],cwd=root).returncode
finally:
    for rel,data in before.items():
        replace(root/rel,data);assert (root/rel).read_bytes()==data
    record['restored']=True
    (base/'migration-consumer-mutation.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf-8')
print('negative exit',record.get('negative_exit'),'restored',record['restored'],flush=True)
