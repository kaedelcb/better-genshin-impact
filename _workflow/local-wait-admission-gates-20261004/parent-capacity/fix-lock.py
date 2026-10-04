from pathlib import Path
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs')
b=p.read_bytes(); text=b.decode(); nl='\r\n'
start=text.index('    public AdmissionResult MarkOperationTerminal(')
end=text.index('    /// <summary>统一关闭接口',start)
original=text[start:end]
core_start=original.index('            var read = _store.Read();')
core_end=original.index('        }\r\n        finally',core_start)
core=original[core_start:core_end]
core=nl.join(line[4:] if line.startswith('    ') else line for line in core.split(nl))
prefix='''    public AdmissionResult MarkOperationTerminal(string requestIdentity, string authoritativeTerminalEvidence)
    {
        if (string.IsNullOrWhiteSpace(authoritativeTerminalEvidence))
            return AdmissionResult.Of(AdmissionResultKind.Error, "evidence_required", "权威终态证据必填（不凭超时/未命中终局）。", requestIdentity);
        _gate.Wait();
        try { return MarkOperationTerminalLocked(requestIdentity, authoritativeTerminalEvidence); }
        finally { _gate.Release(); }
    }

    /// <summary>宿主异步终局回写：等待串行门时让出调用线程；取得门前取消不会改变任何操作责任。</summary>
    public async Task<AdmissionResult> MarkOperationTerminalAsync(string requestIdentity,
        string authoritativeTerminalEvidence, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(authoritativeTerminalEvidence))
            return AdmissionResult.Of(AdmissionResultKind.Error, "evidence_required", "权威终态证据必填（不凭超时/未命中终局）。", requestIdentity);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return MarkOperationTerminalLocked(requestIdentity, authoritativeTerminalEvidence); }
        finally { _gate.Release(); }
    }

    private AdmissionResult MarkOperationTerminalLocked(string requestIdentity, string authoritativeTerminalEvidence)
    {
'''.replace('\n',nl)
new=prefix+core+'    }'+nl+nl
text=text[:start]+new+text[end:]; p.write_bytes(text.encode())
p=Path('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs'); text=p.read_bytes().decode()
def replace(old,new):
    global text
    assert text.count(old)==1,old
    text=text.replace(old,new)
replace('private void SweepTerminalNodeOperations(string runId)','private async Task SweepTerminalNodeOperations(string runId)')
replace('var r = _admission.MarkOperationTerminal(op.RequestIdentity, "runstore-seal:" + seal.Id);',
        'var r = await _admission.MarkOperationTerminalAsync(op.RequestIdentity, "runstore-seal:" + seal.Id, _shutdownCts.Token).ConfigureAwait(false);')
replace('        SweepTerminalNodeOperations(run.RunId!);','        await SweepTerminalNodeOperations(run.RunId!).ConfigureAwait(false);')
replace('?? _admission.MarkOperationTerminal(op.RequestIdentity,',
        '?? await _admission.MarkOperationTerminalAsync(op.RequestIdentity,')
replace(': "runstore-seal:" + releaseSeal.Id);', ': "runstore-seal:" + releaseSeal.Id, _shutdownCts.Token).ConfigureAwait(false);')
p.write_bytes(text.encode())
