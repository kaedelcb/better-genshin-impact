from pathlib import Path
d=Path(__file__).parent;s=(d/'mutate-history.py').read_text(encoding='utf-8');begin=s.index('start=text.index');end=s.index('try:\n for ident',begin)
middle='''old = """        var sendIdentity = s.AcceptedSendIdentity ?? s.SendPermit?.OriginalSendIdentity;
        if (run.RecoveryAssociations.Any(a => (a.SubmissionKey == s.Key
                || !string.IsNullOrEmpty(sendIdentity) && a.SubmissionIdentity == sendIdentity)
            && (a.ObservedExecution is not null || !string.IsNullOrEmpty(s.AcceptedSendIdentity)))) return false;

""".replace('\\n','\\r\\n')
mutations=[('M5-conflicting-recovery-observation',old,'','FullyQualifiedName~HistoricalObservation_DirectBoundSealsRejectConflictingOutcomeSet','Assert.Null() Failure')]
'''
s=s[:begin]+middle+s[end:];s=s.replace("out=d/'mutations'","out=d/'mutations-m5'").replace("d/'mutation-observations.json'","d/'mutation-observations-m5.json'").replace("d/'post-mutation-byte-observation.json'","d/'post-mutation-byte-observation-m5.json'")
(d/'mutate-history-m5.py').write_text(s,encoding='utf-8')
