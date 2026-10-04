from pathlib import Path
import hashlib,json
r=Path.cwd();d=Path(__file__).parent;p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreTerminalRelease.cs';b=p.read_bytes();(d/'terminal-release.pre-association').write_bytes(b);s=b.decode('utf-8').replace('\r\n','\n')
needle='        if (recovery is not null) return true;';assert s.count(needle)==1
s=s.replace(needle,needle+'''
        var sendIdentity = s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity;
        if (run.RecoveryAssociations.Any(a => (a.SubmissionKey == s.Key
                || !string.IsNullOrEmpty(sendIdentity) && a.SubmissionIdentity == sendIdentity)
            && (a.ObservedExecution is not null || !string.IsNullOrEmpty(s.AcceptedSendIdentity)))) return false;
''')
needle='        var related = run.NodeOutcomes.Where(o => o.SubmissionKey == submission.Key';assert s.count(needle)==1
s=s.replace(needle,'''        var associations = run.RecoveryAssociations.Where(a => a.SubmissionKey == submission.Key
            || a.SubmissionIdentity == sendIdentity).ToList();
        if (associations.Count > 1 || associations.Any(a => a.SubmissionKey != submission.Key
            || a.SubmissionIdentity != sendIdentity)) return false;
'''+needle)
out=s.replace('\n','\r\n').encode('utf-8');p.write_bytes(out);(d/'association-source-edit.json').write_text(json.dumps(dict(before=hashlib.sha256(b).hexdigest(),after=hashlib.sha256(out).hexdigest(),bytes_before=len(b),bytes_after=len(out),lines_before=len(b.splitlines()),lines_after=len(out.splitlines())),indent=2),encoding='utf-8')
