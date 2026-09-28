import json, io, os
import xml.etree.ElementTree as ET
d='_workflow/wave3-bo6bo7-receive'
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def counters(p):
    root=ET.parse(p).getroot()
    c=root.find('t:ResultSummary/t:Counters',NS)
    return {'path':p.replace('\\','/'),'total':int(c.get('total')),'passed':int(c.get('passed')),'failed':int(c.get('failed')),'skipped':int(c.get('notExecuted','0'))}
def names(p):
    root=ET.parse(p).getroot()
    defs={x.get('id'):x.find('t:TestMethod',NS).get('name') for x in root.find('t:TestDefinitions',NS)}
    return {r.get('testId'):defs[r.get('testId')] for r in root.find('t:Results',NS)}
full=json.load(io.open(d+'/regression/testid-comparison-receive.json',encoding='utf-8'))
c=full['comparison']
nm=names(d+'/regression/final/assistant-full-1564.trx'); nm.update({k:v for k,v in names(d+'/baseline/assistant-full-baseline.trx').items() if k not in nm})
slim={'meaning':'testId-level structural comparison of the opening baseline (pre-intake HEAD 6fd6207e5) against the integrated mainline run. Identity basis is testId; names are carried for readability only and do not prove assertion equality.',
 'baseline':counters(d+'/baseline/assistant-full-baseline.trx'),
 'final':counters(d+'/regression/final/assistant-full-1564.trx'),
 'counts':{'unchanged':len(c['unchanged_ids']),'added':len(c['added_ids']),'removed':len(c['removed_ids']),'changed':len(c['changed_ids'])},
 'added':[{'testId':i,'name':nm.get(i)} for i in c['added_ids']],
 'removed':[{'testId':i,'name':nm.get(i)} for i in c['removed_ids']],
 'changed':c['changed_ids'],
 'semantics_changed_with_retained_testIds':[
   {'testId':'ec3b4575-60e7-a499-c37f-c9ae12b11e0e','test':'RecomputeSuccessor_ParkingConflictNotFallenBackToCandidate','change':'expected null (conservative chain tail) -> expected parked marker n2'},
   {'testId':'14addbd1-4d7d-814d-ec3d-c070098441ad','test':'RecomputeSuccessor_AllValidParkingChecked_EarlierUnsafeOneNotSilentlySwallowed','change':'expected null -> expected earliest valid marker P1'}],
 'note':'The generator output (testid-comparison-receive.json, ~1.0 MB) additionally embeds both full parsed TRX row sets, which is why it is not attached as review material; this summary plus findings.md carry the reviewable substance. The source delivery comparison is _workflow/wave3-bo6-bo7/regression/final/testid-comparison-final.json; its added/removed testId sets were independently checked to be identical to the ones above.'}
io.open(d+'/regression/testid-comparison-summary.json','w',encoding='utf-8',newline='\n').write(json.dumps(slim,ensure_ascii=False,indent=2)+"\n")
print('summary bytes', os.path.getsize(d+'/regression/testid-comparison-summary.json'))
bud=io.open(d+'/budget.md',encoding='utf-8').read()
old='''## 本批实际发出记录

（收口时回填：发出的请求数、模型/强度、渠道、附件/字节与 token 估算、退出码、结论与逐项处置。）'''
new='''## 本批实际发出记录

本批发出的实质性会诊请求记录（渠道、模型/强度、附件与 token 估算、退出码、结论与逐项处置）写入同目录 `consultation/review-outcome-v1.md`，并在并行成果台账与 R5.3 §24.126 的收口材料中回填；本节不自引用该结果，以免送审材料随结果变化而失效。

`consultation/preflight-v1.json` 记录送审前的容量预检（文件清单、字节数、token 估算方法、占有效窗口比例，以及本次为何仍使用既有 GPT 会诊工具）。'''
assert old in bud
io.open(d+'/budget.md','w',encoding='utf-8',newline='\n').write(bud.replace(old,new))
print('budget bytes', os.path.getsize(d+'/budget.md'))
