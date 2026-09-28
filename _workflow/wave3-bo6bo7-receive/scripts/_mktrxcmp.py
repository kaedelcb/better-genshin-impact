import subprocess, json, io
r='_workflow/wave3-bo6bo7-receive'
p=subprocess.run(['python','-B','tools/mistletoe/workflow.py','trx',
                  '--baseline', r+'/baseline/assistant-full-baseline.trx',
                  '--final', r+'/regression/assistant-full-integrated.trx'], capture_output=True)
d=json.loads(p.stdout.decode('utf-8'))
assert 'comparison' in d, d
c=d['comparison']
io.open(r+'/regression/testid-comparison-receive.json','w',encoding='utf-8',newline='\n').write(json.dumps(d,ensure_ascii=False,indent=2)+"\n")
print('unchanged',len(c['unchanged_ids']),'added',len(c['added_ids']),'removed',len(c['removed_ids']),'changed',len(c['changed_ids']))
src=json.load(io.open(r'C:\Users\Administrator\.codex\worktrees\wave3-bo6-bo7\better-genshin-impact-LCB\_workflow\wave3-bo6-bo7\regression\final\testid-comparison-final.json',encoding='utf-8'))
sa={x['testId'] for x in src['testId_difference']['added']}; sr={x['testId'] for x in src['testId_difference']['removed']}
print('added set identical to source batch  :', sa==set(c['added_ids']))
print('removed set identical to source batch:', sr==set(c['removed_ids']))
print('counts match source batch (1556/8/4/0):',
      len(c['unchanged_ids'])==1556 and len(c['added_ids'])==8 and len(c['removed_ids'])==4 and len(c['changed_ids'])==0)
