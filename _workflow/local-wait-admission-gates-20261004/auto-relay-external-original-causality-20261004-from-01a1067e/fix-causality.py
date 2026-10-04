from pathlib import Path
import json,hashlib
r=Path.cwd();d=Path(__file__).parent;obs=[]
def edit(path,fn):
 p=r/path;b=p.read_bytes();s=b.decode('utf-8');(d/(p.name+'.before')).write_bytes(b)
 a=fn(s).encode('utf-8');assert len(a)>=len(b);p.write_bytes(a)
 obs.append(dict(path=path,before=dict(bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),crlf=b.count(b'\r\n'),lf=b.count(b'\n')),after=dict(bytes=len(a),lines=len(a.splitlines()),sha256=hashlib.sha256(a).hexdigest(),crlf=a.count(b'\r\n'),lf=a.count(b'\n'))))
def replace(s,x,y,n=1):assert s.count(x)==n,(x,s.count(x));return s.replace(x,y)
def service(s):
 nl='\r\n'
 s=replace(s,'    ExecutionResultKind? TerminalKind = null);','    ExecutionResultKind? TerminalKind = null,'+nl+'    string? CandidateId = null, string? ResourceRef = null, string? ActionId = null, string? TargetBgiEpoch = null);')
 x='AcceptedAtUtc: entry.AcceptedAtUtc, RunId: entry.RunId, OperationType: entry.OperationType);'
 s=replace(s,x,x[:-2]+','+nl+'                    CandidateId: entry.CandidateId, ResourceRef: entry.ResourceRef,'+nl+'                    ActionId: entry.ActionId, TargetBgiEpoch: entry.TargetBgiEpoch);')
 x='                           || agg.SourceFact.OperationType != fact.OperationType'
 s=replace(s,x,x+nl+'                           || !string.Equals(agg.SourceFact.CandidateId, fact.CandidateId, StringComparison.Ordinal)'+nl+'                           || !string.Equals(agg.SourceFact.ResourceRef, fact.ResourceRef, StringComparison.Ordinal)'+nl+'                           || !string.Equals(agg.SourceFact.ActionId, fact.ActionId, StringComparison.Ordinal)'+nl+'                           || !string.Equals(agg.SourceFact.TargetBgiEpoch, fact.TargetBgiEpoch, StringComparison.Ordinal)')
 x='                    && (fact.OperationType != OperationType.ExternalStart'
 s=replace(s,x,'                    && (!ExternalStartLedgerCausalityMatches(op, fact)'+nl+'                        || fact.OperationType != OperationType.ExternalStart')
 helper='''    private static bool ExternalStartLedgerCausalityMatches(OperationRecord op, TakeoverLedgerFact fact)
        => op.OperationType == OperationType.ExternalStart && fact.OperationType == OperationType.ExternalStart
           && !string.IsNullOrWhiteSpace(fact.CandidateId) && !string.IsNullOrWhiteSpace(fact.ResourceRef)
           && !string.IsNullOrWhiteSpace(fact.ActionId) && !string.IsNullOrWhiteSpace(fact.TargetBgiEpoch)
           && string.Equals(op.CandidateId, fact.CandidateId, StringComparison.Ordinal)
           && string.Equals(op.ResourceRef, fact.ResourceRef, StringComparison.Ordinal)
           && string.Equals(op.TargetEpoch, fact.TargetBgiEpoch, StringComparison.Ordinal)
           && string.Equals(op.Candidate?.ActionId ?? ArbitrationOrdering.DeriveActionId(op.CandidateId), fact.ActionId, StringComparison.Ordinal);

'''.replace('\n',nl)
 x='    private bool SettledArchivedTerminalReplayMatches('
 s=replace(s,x,helper+x)
 s=replace(s,'            if (op is null || op.OperationType != OperationType.ExternalStart || op.LastSendSeq < fact.SendSeq)','            if (op is null || !ExternalStartLedgerCausalityMatches(op, fact) || op.LastSendSeq < fact.SendSeq)')
 x='            if (op is null || op.OperationType != OperationType.ExternalStart'+nl+'                || op.LastSendSeq <= fact.SendSeq'
 s=replace(s,x,'            if (op is null || !ExternalStartLedgerCausalityMatches(op, fact)'+nl+'                || op.LastSendSeq <= fact.SendSeq',2)
 s=replace(s,'        if (op.OperationType != OperationType.ExternalStart || op.ConflictPending','        if (!ExternalStartLedgerCausalityMatches(op, fact) || op.ConflictPending')
 s=replace(s,'                return hot.OperationType == OperationType.ExternalStart','                return ExternalStartLedgerCausalityMatches(hot, fact)')
 s=replace(s,'                || operation.OperationType != OperationType.ExternalStart','                || !ExternalStartLedgerCausalityMatches(operation, fact)')
 return s
edit('MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs',service)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',lambda s:replace(s,'                                TerminalKind: e.TerminalKind))','                                TerminalKind: e.TerminalKind,\r\n                                CandidateId: e.CandidateId, ResourceRef: e.ResourceRef,\r\n                                ActionId: e.ActionId, TargetBgiEpoch: e.TargetBgiEpoch))'))
def fixtures(s):
 for prefix in ['e','entry']:
  x=f'RunId: {prefix}.RunId, OperationType: {prefix}.OperationType'
  count=s.count(x);assert count==(2 if prefix=='e' else 1)
  s=s.replace(x,x+f',\r\n                CandidateId: {prefix}.CandidateId, ResourceRef: {prefix}.ResourceRef,\r\n                ActionId: {prefix}.ActionId, TargetBgiEpoch: {prefix}.TargetBgiEpoch')
 return s
edit('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/ArbitrationAdmissionServiceTests.cs',fixtures)
(d/'source-edit-observation.json').write_text(json.dumps(obs,indent=2),encoding='utf-8');print('edited',[(x['path'],x['after']['bytes']-x['before']['bytes']) for x in obs])
