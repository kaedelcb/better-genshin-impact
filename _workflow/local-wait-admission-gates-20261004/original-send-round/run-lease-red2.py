from pathlib import Path
import subprocess,hashlib,json
root=Path.cwd();out=root/'_workflow/local-wait-admission-gates-20261004/original-send-round'
p=root/'Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationLeaseStoreTests.cs';b=p.read_bytes();a=b'        file.Handoff!.Operations.Add(op);';z=b'        (file.Handoff ??= new()).Operations.Add(op);';assert b.count(a)==1;p.write_bytes(b.replace(a,z))
p=root/'MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationLeaseStore.cs';candidate=p.read_bytes();s=candidate.decode('utf-8-sig');start=s.index('            if (op.RejectedSendRounds is { } rejectedRounds');end=s.index('            if (version >= 4 && op.AcceptanceClaim is { } claim',start)
baseline=(b'\xef\xbb\xbf' if candidate.startswith(b'\xef\xbb\xbf') else b'')+(s[:start]+s[end:]).encode()
assert hashlib.sha256(baseline).hexdigest()==json.loads((out/'lease-before.json').read_text())['sha256']
def publish(b):
 t=p.with_suffix('.original-round.tmp');t.write_bytes(b);t.replace(p)
def build(log):
 with log.open('wb') as f:return subprocess.run(['dotnet','build','Test/MultiplayerHoeingAssistant.UnitTest/MultiplayerHoeingAssistant.UnitTest.csproj','-t:Rebuild','-p:DeployToBgiTools=false','-o',str(root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products'),'--nologo'],stdout=f,stderr=subprocess.STDOUT).returncode
try:
 publish(baseline);be=build(out/'lease-red2-build.log');assert be==0
 with (out/'lease-red2-test.log').open('wb') as f:te=subprocess.run(['dotnet','vstest',str(root/'_workflow/local-wait-admission-gates-20261004/g10-completion/products/MultiplayerHoeingAssistant.UnitTest.dll'),'/TestCaseFilter:FullyQualifiedName~OriginalRound_LeaseRead','/Logger:trx;LogFileName=lease-red2.trx','/ResultsDirectory:'+str(out)],stdout=f,stderr=subprocess.STDOUT).returncode
 print('corrected original-baseline test exit',te)
finally:
 publish(candidate);assert p.read_bytes()==candidate
 print('candidate restored bytes',hashlib.sha256(candidate).hexdigest())
 be=build(out/'lease-green-build.log');print('restored rebuild exit',be)
