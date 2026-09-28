import hashlib, subprocess, io, os
def sha(b): return hashlib.sha256(b).hexdigest()
def low(b): return b.replace(b'\r\n', b'\n')
p='MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowRunner.cs'
blob=subprocess.run(['git','cat-file','blob','e009068e2:'+p],capture_output=True).stdout
wt=open(p,'rb').read()
base=open('_workflow/wave3-bo6bo7-receive/opening.json','rb').read()
print('blob    bytes=%d sha=%s  CRLF_count=%d' % (len(blob), sha(blob), blob.count(b'\r\n')))
print('blob LF-normalized sha=%s' % sha(low(blob)))
print('worktree bytes=%d sha=%s  CRLF_count=%d' % (len(wt), sha(wt), wt.count(b'\r\n')))
print('worktree LF-normalized sha=%s' % sha(low(wt)))
print('blob==worktree bytes:', blob==wt, '| LF-normalized equal:', low(blob)==low(wt))
