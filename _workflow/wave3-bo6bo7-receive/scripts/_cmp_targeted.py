import xml.etree.ElementTree as ET, json, collections
NS={'t':'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
def load(p):
    root=ET.parse(p).getroot()
    defs={d.get('id'):(d.find('t:TestMethod',NS).get('className'),d.find('t:TestMethod',NS).get('name')) for d in root.find('t:TestDefinitions',NS)}
    out={}
    for r in root.find('t:Results',NS):
        tid=r.get('testId'); cls,m=defs[tid]
        out[tid]={'name':m,'class':cls.split('.')[-1],'outcome':r.get('outcome')}
    return out
src=load(r'C:\Users\Administrator\.codex\worktrees\wave3-bo6-bo7\better-genshin-impact-LCB\_workflow\wave3-bo6-bo7\regression\final\runner-localwait-final.trx')
rec=load('_workflow/wave3-bo6bo7-receive/regression/targeted-integrated-93.trx')
print('source batch final targeted ids =',len(src),' outcomes=',dict(collections.Counter(v['outcome'] for v in src.values())))
print('receive integrated targeted ids =',len(rec),' outcomes=',dict(collections.Counter(v['outcome'] for v in rec.values())))
print('same testId set :', set(src)==set(rec))
print('same name per id :', all(src[k]['name']==rec[k]['name'] for k in src))
diff=[k for k in src if src[k]['outcome']!=rec[k]['outcome']]
print('outcome diffs    :',diff)
json.dump({'source_final_targeted_ids':sorted(src),'receive_integrated_targeted_ids':sorted(rec),
           'identity_equal':set(src)==set(rec)},
          open('_workflow/wave3-bo6bo7-receive/regression/targeted-testid-identity.json','w',encoding='utf-8'),ensure_ascii=False,indent=2)
