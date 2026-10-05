from pathlib import Path
import json,subprocess,time,hashlib
root=Path.cwd();base=root/'_workflow/local-wait-admission-gates-20261004/auto-relay-c17-effects-20261005-from-01a1093a';out=base/'in-repo-inventory-products';ev=base/'restored-linked-r1';ev.mkdir(exist_ok=False)
sources=[r['path'] for r in json.loads((base/'source-before.json').read_text())+json.loads((base/'additional-before.json').read_text())]+['BetterGenshinImpact/Service/ExternalInterface/TerminalEffectJournal.cs','BetterGenshinImpact/Service/ExternalInterface/TerminalEffectDispatch.cs','MultiplayerHoeingAssistant/Services/TaskCenter/WindowsTerminalEffectObserver.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TerminalEffectJournalTests.cs','Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/BgiWorkflowTerminalEffectTests.cs','Test/BetterGenshinImpact.UnitTest/ServiceTests/ExternalInterface/TerminalEffectDispatchTests.cs']
def source():return [dict(path=p,bytes=len(b:=(root/p).read_bytes()),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b[:3].hex(),crlf=b'\r\n' in b) for p in sources]
before=source();(ev/'source-before.json').write_text(json.dumps(before,indent=2),encoding='utf-8')
for name,prior in [('MultiplayerHoeingAssistant','restored-final-r1'),('BetterGenshinImpact','green-final-r1')]:
 r=json.loads((base/prior/(name+'-test-process.json')).read_text());args=r['argv'].copy();args[2]=str(out/(name+'.UnitTest.dll'));args[-1]='--ResultsDirectory:'+str(ev)
 row=dict(argv=args,started=time.time(),level='same immutable binary test/read observation, no authenticated receipt')
 with (ev/(name+'-test.log')).open('w',encoding='utf-8') as log:
  p=subprocess.Popen(args,cwd=root,stdout=log,stderr=subprocess.STDOUT);row['pid']=p.pid;row['exit_code']=p.wait();row['ended']=time.time()
 (ev/(name+'-process.json')).write_text(json.dumps(row,indent=2),encoding='utf-8');print(name,row['exit_code'],flush=True)
 if row['exit_code']:raise RuntimeError('restored regression failed')
after=source();(ev/'source-after.json').write_text(json.dumps(after,indent=2),encoding='utf-8');assert before==after
links=json.loads((base/'inventory-immutable-links.json').read_text(encoding='utf-8-sig'));drifts=[r['link'] for r in links if hashlib.sha256(Path(r['link']).read_bytes()).hexdigest().upper()!=r['sha256'] or hashlib.sha256(Path(r['source']).read_bytes()).hexdigest().upper()!=r['sha256']];assert not drifts
(ev/'input-comparison.json').write_text(json.dumps(dict(source_equal=True,link_hashes_equal=True,link_count=len(links)),indent=2),encoding='utf-8')
