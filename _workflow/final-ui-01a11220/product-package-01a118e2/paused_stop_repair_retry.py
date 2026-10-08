"""Preserve the first fixture failure; correct its expected revision before causal replay."""
from pathlib import Path
base=Path(__file__).resolve().with_name('paused_stop_repair.py')
code=base.read_text(encoding='utf-8')
old="B=Path(__file__).resolve().parent/'paused-stop-repair'"
assert code.count(old)==1;code=code.replace(old,"B=Path(__file__).resolve().parent/'paused-stop-repair'/'retry1'")
old="    assert hb.count(old)==1;assert tb.count(b'    [Theory]')==2"
new="""    previous=(B.parent/'before/TaskCenterHost.cs').read_bytes()
    assert hb==previous.replace(old,new),'host changed outside the completed first attempt'
    atomic(H,previous);hb=previous
    assert hb.count(old)==1;assert tb.count(b'    [Theory]')==3"""
assert code.count(old)==1;code=code.replace(old,new)
old="    assert tb.count(anchor)==2;patched=tb.replace(anchor,test.encode().replace(b'\\n',te)+anchor,1);atomic(T,patched)"
new="""    assert tb.count(anchor)==2
    first=b'var flows=new WorkflowStore(Path.Combine(root,"flows"));var doc=flows.Load(flow);'
    corrected=b'var flows=new WorkflowStore(Path.Combine(root,"flows"));var snapshot=flows.LoadSnapshot(flow);var doc=snapshot.Document;'
    assert tb.count(first)==1
    patched=tb.replace(first,corrected)
    first=b'flows.Save(doc,null);var host=Host(root,client);string runId;'
    assert patched.count(first)==1
    patched=patched.replace(first,b'flows.Save(doc,snapshot.Revision);var host=Host(root,client);string runId;')
    atomic(T,patched)"""
assert code.count(old)==1;code=code.replace(old,new)
old="    assert red['total']==2 and len(red['failures'])==2 and all('ColdPausedStop' in f['name'] for f in red['failures'])"
new=old+"\n    assert any('Paused Stop must seal' in f['message'] for f in red['failures'])\n    assert any('Expected: Unavailable' in f['message'] for f in red['failures'])\n    assert all('WorkflowRevisionConflictException' not in f['message'] for f in red['failures'])"
assert code.count(old)==1;code=code.replace(old,new)
exec(compile(code,str(Path(__file__).resolve()),'exec'))
