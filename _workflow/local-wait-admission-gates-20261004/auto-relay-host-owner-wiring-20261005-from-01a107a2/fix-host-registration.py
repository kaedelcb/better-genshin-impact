from pathlib import Path
def edit(name, f):
    p=Path(name); raw=p.read_bytes(); nl='\r\n' if b'\r\n' in raw else '\n'
    t=raw.decode('utf-8').replace('\r\n','\n'); changed=f(t)
    assert len(changed)>.9*len(t)
    p.write_bytes(changed.replace('\n',nl).encode('utf-8'))
def model(t):
    needle='    public AdmissionParentSource? AdmissionParentSource { get; set; }'
    assert t.count(needle)==1
    t=t.replace(needle,needle+'''

    /// <summary>原真实分派前冻结的运行准入映射；用于停止/恢复识别丢失账本，不能授予新发送能力。</summary>
    [JsonPropertyName("admissionMappings")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<RunAdmissionMapping>? AdmissionMappings { get; set; }
''')
    return t+'\npublic sealed record RunAdmissionMapping(int Version, string RequestIdentity, string SubmissionIdentity, int SendSeq, OperationType OperationType);\n'
def store(t):
    needle='    private bool UpdateMergingCore(string runId'
    method='''    internal bool BindOriginalAdmissionMapping(string runId, OperationRecord op)
    {
        if (op.RunBinding != runId || op.Candidate is null || string.IsNullOrWhiteSpace(op.RequestIdentity)
            || op.LastSendSeq < 1 || op.SubmissionIdentity != $"sub:{op.RequestIdentity}:{op.LastSendSeq.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            || op.OperationType is not (OperationType.FlowRegistration or OperationType.Recovery or OperationType.Handoff))
            return false;
        var mapping = new RunAdmissionMapping(1, op.RequestIdentity, op.SubmissionIdentity, op.LastSendSeq, op.OperationType);
        UpdateMergingCore(runId, run =>
        {
            if (run.WorkflowId != op.Candidate.WorkflowId) return false;
            if (run.AdmissionMappings?.Contains(mapping) == true) return false;
            (run.AdmissionMappings ??= []).Add(mapping);
            return true;
        }, out var latest, authorizedMapping: mapping);
        return latest?.AdmissionMappings?.Contains(mapping) == true;
    }

'''
    assert t.count(needle)==1
    t=t.replace(needle,method+needle)
    old='out WorkflowRunRecord? latest, PreparedSendPermit? authorizedPermit = null)'
    assert t.count(old)==1
    t=t.replace(old,'out WorkflowRunRecord? latest, PreparedSendPermit? authorizedPermit = null, RunAdmissionMapping? authorizedMapping = null)')
    old='Persist(current, current.RecordRevision, authorizedPermit: authorizedPermit);'
    assert t.count(old)==1
    t=t.replace(old,'Persist(current, current.RecordRevision, authorizedPermit: authorizedPermit, authorizedMapping: authorizedMapping);')
    old='AdmissionParentSource? authorizedParent = null)'
    assert t.count(old)==1
    t=t.replace(old,'AdmissionParentSource? authorizedParent = null, RunAdmissionMapping? authorizedMapping = null)')
    old='        if (currentText is null && rec.RecoveryAssociations.Count != 0)'
    assert t.count(old)==1
    t=t.replace(old,'        if (currentText is null && rec.AdmissionMappings is not null)\n            throw new RunRecordConflictException("新记录不能补造原准入映射。");\n'+old)
    old='RunStoreEvidenceGuard.Validate(current, rec, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation, authorizedPermit);'
    assert t.count(old)==1
    return t.replace(old,'RunStoreEvidenceGuard.Validate(current, rec, authorizedNoSend, authorizedSeal, authorizedRecoveryAssociation, authorizedPermit, authorizedMapping);')
def guard(t):
    old='PreparedSendPermit? authorizedPermit = null)'
    assert t.count(old)==1
    t=t.replace(old,'PreparedSendPermit? authorizedPermit = null, RunAdmissionMapping? authorizedMapping = null)')
    old='        Require(current.AdmissionSourceScope == next.AdmissionSourceScope);'
    addition='''        if (authorizedMapping is null)
            Require(JsonSerializer.Serialize(current.AdmissionMappings) == JsonSerializer.Serialize(next.AdmissionMappings));
        else
        {
            var originalMappings = current.AdmissionMappings ?? [];
            var nextMappings = next.AdmissionMappings ?? [];
            Require(nextMappings.Count == originalMappings.Count + 1 && nextMappings[^1] == authorizedMapping);
            for (var index = 0; index < originalMappings.Count; index++) Require(originalMappings[index] == nextMappings[index]);
        }
'''
    assert t.count(old)==1
    return t.replace(old,addition+old)
def host(t):
    old='var started = new TaskCompletionSource<WorkflowRunRecord>(TaskCreationOptions.RunContinuationsAsynchronously);'
    assert t.count(old)==1
    return t.replace(old,'var started = new TaskCompletionSource<WorkflowRunRecord>(); // Preserve synchronous fault convergence outside the Host gate.')
def admission(t):
    t=t.replace('(read.Status is ArbitrationLeaseStatus.Valid or ArbitrationLeaseStatus.Expired)', '(read.Status is ArbitrationLeaseStatus.Valid or ArbitrationLeaseStatus.Expired or ArbitrationLeaseStatus.Absent)',1)
    t=t.replace('=> run.AdmissionParentSource is { Kind: AdmissionParentKind.PanelFlowRegistration }', '=> run.AdmissionMappings is { Count: > 0 }\n            || run.AdmissionParentSource is { Kind: AdmissionParentKind.PanelFlowRegistration }',1)
    # Flow/recovery actual Sender must persist the exact original expectation before any drive can run.
    needle='        var workflowId = d.Candidate.WorkflowId;\n        lock (_gate)'
    insertion='''        try
        {
            if (!_runs.BindOriginalAdmissionMapping(runId, op)) return new SendOutcome.Unknown("original_mapping_not_persisted");
        }
        catch (Exception ex) { return new SendOutcome.Unknown("original_mapping_publish_" + ex.GetType().Name); }

'''
    assert t.count(needle)==1
    t=t.replace(needle,insertion+needle)
    needle='        var workflowId = run.WorkflowId!;\n        lock (_gate)'
    assert t.count(needle)==1
    t=t.replace(needle,insertion+needle)
    return t
def faults(t):
    old='                Task.Run(() => host.IsDriving(workflowId)).GetAwaiter().GetResult();'
    assert t.count(old)==1
    t=t.replace(old,'                Task.Run(() => host.IsDriving(workflowId)).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();\n                callbackAcquiredGate = true;')
    needle='        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);'
    assert t.count(needle)==1
    t=t.replace(needle,needle+'\n        var callbackAcquiredGate = false;')
    needle='            Assert.True(shutdown.IsCompletedSuccessfully, "callback exception escaped shared shutdown");'
    return t.replace(needle,needle+'\n            Assert.True(throws || callbackAcquiredGate, "Cancellation callback could not acquire Host gate from another thread");',1)
edit('MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs',model)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs',store)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs',guard)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',host)
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs',admission)
edit('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TaskCenterHostTests.cs',faults)
edit('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/LocalWaitFinalizationContractTests.cs',lambda t:t.replace('var residuePath = leasePath + ".fixture.tmp";', 'var residuePath = Path.Combine(_root, "arbitration", ".lease-fixture.tmp");'))
