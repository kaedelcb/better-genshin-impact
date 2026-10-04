from pathlib import Path
r=Path.cwd();p=r/'MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs';b=p.read_bytes();s=b.decode('utf-8');nl='\r\n' if b.count(b'\r\n') else '\n';s=s.replace('\r\n','\n')
a='    public Func<Task>? BeforeSuccessorAdmission { get; set; }\n'
assert s.count(a)==1
s=s.replace(a,a+'\n    // Test-only observation of scalar copies; production leaves this null.\n    internal Action<string, AdmissionResultKind, string, string, string?, int>? SuccessorAdmissionObservedForTest;\n')
a='''        if (result.Kind == AdmissionResultKind.Accepted)
        {
            // 按**完整发送身份**取回 sender 内真实发送结果'''
assert s.count(a)==1
s=s.replace(a,'''        try
        {
            _admissionSeams?.SuccessorAdmissionObservedForTest?.Invoke(
                occ.NodeId, result.Kind, result.ReasonCode, result.RequestIdentity,
                result.SubmissionIdentity, result.SendSeq);
        }
        catch (Exception) { /* A diagnostic observer cannot change an admission result. */ }

'''+a)
out=s.replace('\n',nl).encode('utf-8');assert len(out)>len(b);p.write_bytes(out)
