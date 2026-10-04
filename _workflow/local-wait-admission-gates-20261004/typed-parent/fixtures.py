from pathlib import Path
import hashlib,json
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationAdmissionServiceTests.cs');b=p.read_bytes();s=b.decode('utf-8-sig').replace('\r\n','\n');Path('_workflow/local-wait-admission-gates-20261004/typed-parent/arbitration-tests-before.json').write_text(json.dumps(dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest())))
for name,wf,oldrun in [('RestartAfterUnknown_SameCandidateStillBlocked_NoNewPermitNoKeyChange','wf-r1','run-r1'),('UnresolvedSubmission_BlocksRedrive_NoSendNoSeqAdvance','wf-x','run-1')]:
 start=s.index('    public async Task '+name+'()');end=s.index('\n    [Fact]',start) if '\n    [Fact]' in s[start:] else s.rfind('\n}')
 block=s[start:end];pos=block.index('    {')+len('    {');block=block[:pos]+f'''
        var sourceStore = new RunStore(Path.Combine(_dir, "original-parent-runs"));
        var sourceRun = sourceStore.CreateRun("{wf}", "rev-1", handoff: new HandoffIdentity
        {{ IntentKey = "original:{name}", Mode = StartupHandoffModes.Start }}, admissionSourceScope: "bgi:local:ep1");
        AdmissionParentSource? OriginalSource(string id, string workflow)
            => TaskCenterHost.ResolveAdmissionParent([], sourceStore.Load(id), id, workflow);
'''+block[pos:]
 block=block.replace(f'RunBinding = "{oldrun}"','RunBinding = sourceRun.RunId,\n            ParentSource = sourceRun.AdmissionParentSource').replace(f'RunId = "{oldrun}"','RunId = sourceRun.RunId').replace('Scope = "bgi:inst:ep1"','Scope = "bgi:local:ep1"')
 block=block.replace('h.Sender = _ => { Interlocked.Increment(ref sendsA); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")); }','{ h.HandoffParentProvider = OriginalSource; h.Sender = _ => { Interlocked.Increment(ref sendsA); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")); }; }')
 block=block.replace('h.Sender = _ => { Interlocked.Increment(ref sendsB); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null, "job-new")); }','{ h.HandoffParentProvider = OriginalSource; h.Sender = _ => { Interlocked.Increment(ref sendsB); return Task.FromResult<SendOutcome>(new SendOutcome.Accepted("ext:accepted", null, "job-new")); }; }')
 block=block.replace('h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")); }','{ h.HandoffParentProvider = OriginalSource; h.Sender = _ => { Interlocked.Increment(ref sends); return Task.FromResult<SendOutcome>(new SendOutcome.Unknown("fixture_unknown")); }; }')
 s=s[:start]+block+s[end:]
assert len(s)>len(b.decode())*.95;p.write_bytes(s.replace('\n','\r\n').encode())
