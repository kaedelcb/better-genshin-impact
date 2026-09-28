import hashlib, subprocess
def sha(b): return hashlib.sha256(b).hexdigest().upper()
p='Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ClaimSurfaceManifest.txt'
blob=subprocess.run(['git','cat-file','blob','e009068e2:'+p],capture_output=True).stdout
wt=open(p,'rb').read()
print('delivery blob (LF form)  sha=%s crlf=%d bytes=%d' % (sha(blob), blob.count(b'\r\n'), len(blob)))
print('mainline worktree (CRLF) sha=%s crlf=%d bytes=%d' % (sha(wt), wt.count(b'\r\n'), len(wt)))
print('LF-normalized equal      :', blob.replace(b'\r\n',b'\n')==wt.replace(b'\r\n',b'\n'))
