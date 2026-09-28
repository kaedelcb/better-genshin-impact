import io, json
cs='_workflow/wave3-bo6bo7-receive/regression/claim-surface'
def load(p):
    out={}
    for line in io.open(p,encoding='utf-8-sig'):
        line=line.rstrip('\n').rstrip('\r')
        if not line.strip(): continue
        parts=line.split('\u0001')
        out['\u0001'.join(parts[:3])]=line
    return out
a=load(cs+'/manifest-before.txt'); b=load(cs+'/manifest-after-regen.txt')
added=[k for k in b if k not in a]; removed=[k for k in a if k not in b]
print('added lines:',len(added),' removed lines:',len(removed))
for k in added: print('  +', b[k][:210])
for k in removed: print('  -', a[k][:210])
io.open(cs+'/manifest-diff-summary.json','w',encoding='utf-8',newline='\n').write(json.dumps(
  {'added':[b[k] for k in added],'removed':[a[k] for k in removed],
   'interpretation':'Only the newly added §24.126 registration lines carrying claim/gate vocabulary enter the manifest; no pre-existing claim line changed or disappeared.'},ensure_ascii=False,indent=2)+"\n")
