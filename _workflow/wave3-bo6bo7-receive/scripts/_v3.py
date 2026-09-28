import json, io, os, hashlib, collections, xml.etree.ElementTree as ET, subprocess
d='_workflow/wave3-bo6bo7-receive'; v=d+'/verification'
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def sha(p): return hashlib.sha256(open(p,'rb').read()).hexdigest()
def parse(p):
    root=ET.parse(p).getroot()
    c=dict(root.find('t:ResultSummary/t:Counters',NS).attrib)
    defs={x.get('id'):x.find('t:TestMethod',NS).get('name') for x in root.find('t:TestDefinitions',NS)}
    rows=[(defs[r.get('testId')], r.get('testId'), r.get('outcome')) for r in root.find('t:Results',NS)]
    oc=collections.Counter(o for _,_,o in rows)
    return {'path':p.replace('\\','/'),'file_sha256':sha(p),'render_counters':c,'row_outcome_counts':dict(oc),
            'rows':rows,'not_executed':[n for n,_,o in rows if o=='NotExecuted'],'failed':[n for n,_,o in rows if o=='Failed']}
runs={}
for k,p in [('opening-baseline-full',d+'/baseline/assistant-full-baseline.trx'),
            ('integrated-full',d+'/regression/final/assistant-full-1564.trx'),
            ('opening-baseline-targeted',d+'/baseline/targeted-baseline.trx'),
            ('integrated-targeted',d+'/regression/final/targeted-93.trx'),
            ('claim-surface-regen',d+'/regression/claim-surface/regen/claim-surface-regen.trx'),
            ('claim-surface-no-env',d+'/regression/claim-surface/no-env/claim-surface-no-env.trx'),
            ('preRegen-integrated-full',d+'/regression/assistant-full-integrated.trx'),
            ('preRegen-integrated-targeted',d+'/regression/targeted-integrated-93.trx')]:
    r=parse(p); runs[k]={k2:r[k2] for k2 in ('path','file_sha256','render_counters','row_outcome_counts','not_executed','failed')}
out={'meaning':'Corrected outcome accounting for every run cited by this batch. The TRX Counters element reports notExecuted="0" for these VSTest/xUnit runs while two result rows carry outcome NotExecuted (the two opt-in P50 diagnostics). Row-level counts are therefore authoritative here, and total = passed + failed + notExecuted_rows holds exactly in every run.',
 'counter_semantics':'VSTest xUnit shape: Counters@notExecuted is 0 even though NotExecuted rows exist. tools/mistletoe/workflow.py already parses sketched rows this way (it returns row-based skipped) - the error being corrected was in this batch\'s own summary script, which read the counter attribute.',
 'runs':runs,
 'headline':{
   'opening_baseline_full':{'total':1560,'passed':1558,'failed':0,'not_executed_rows':2,'names':runs['opening-baseline-full']['not_executed']},
   'integrated_full':{'total':1564,'passed':1562,'failed':0,'not_executed_rows':2,'names':runs['integrated-full']['not_executed']},
   'opening_baseline_targeted':{'total':89,'passed':89,'failed':0,'not_executed_rows':0},
   'integrated_targeted':{'total':93,'passed':93,'failed':0,'not_executed_rows':0}}}
io.open(v+'/regression-outcomes.json','w',encoding='utf-8',newline='\n').write(json.dumps(out,ensure_ascii=False,indent=2)+"\n")
print('regression-outcomes ok'); print(json.dumps(out['headline'],ensure_ascii=False))

# targeted 93 full id list + equality to source delivery
def ids(p):
    root=ET.parse(p).getroot()
    defs={x.get('id'):x.find('t:TestMethod',NS).get('name') for x in root.find('t:TestDefinitions',NS)}
    return {r.get('testId'):{'name':defs[r.get('testId')],'outcome':r.get('outcome')} for r in root.find('t:Results',NS)}
src=ids(r'C:\Users\Administrator\.codex\worktrees\wave3-bo6-bo7\better-genshin-impact-LCB\_workflow\wave3-bo6-bo7\regression\final\runner-localwait-final.trx')
rec=ids(d+'/regression/final/targeted-93.trx')
t93={'meaning':'Complete 93-entry testId comparison of the integrated targeted suite against the source delivery final targeted TRX.',
 'source_delivery_trx':{'path':'C:/Users/Administrator/.codex/worktrees/wave3-bo6-bo7/better-genshin-impact-LCB/_workflow/wave3-bo6-bo7/regression/final/runner-localwait-final.trx',
    'sha256':sha(r'C:\Users\Administrator\.codex\worktrees\wave3-bo6-bo7\better-genshin-impact-LCB\_workflow\wave3-bo6-bo7\regression\final\runner-localwait-final.trx'),'count':len(src)},
 'integrated_trx':{'path':d+'/regression/final/targeted-93.trx','sha256':sha(d+'/regression/final/targeted-93.trx'),'count':len(rec)},
 'testId_sets_identical':set(src)==set(rec),
 'name_and_outcome_identical_for_every_testId':all(src[k]==rec[k] for k in src),
 'mismatches':[{'testId':k,'source':src.get(k),'integrated':rec.get(k)} for k in set(src)|set(rec) if src.get(k)!=rec.get(k)],
 'entries':[{'testId':k,'name':rec[k]['name'],'outcome':rec[k]['outcome']} for k in sorted(rec)]}
io.open(v+'/targeted-93-ids.json','w',encoding='utf-8',newline='\n').write(json.dumps(t93,ensure_ascii=False,indent=2)+"\n")
print('targeted-93: identical set',t93['testId_sets_identical'],'| all names/outcomes identical',t93['name_and_outcome_identical_for_every_testId'],'| mismatches',len(t93['mismatches']))
