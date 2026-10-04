from pathlib import Path
import hashlib,json
root=Path.cwd(); out=Path(__file__).parent
paths=['MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs']
facts=[]
for p in paths:
 b=(root/p).read_bytes(); (out/(Path(p).name+'.before.cs')).write_bytes(b)
 facts.append(dict(path=p,bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n')))
(out/'product-before.json').write_text(json.dumps(facts,indent=2))
def replace(b,a,c):
 a=a.replace('\n','\r\n').encode(); c=c.replace('\n','\r\n').encode(); assert b.count(a)==1,a[:100]; return b.replace(a,c)
p=root/paths[0]; b=p.read_bytes()
b=replace(b,'        public required Task<WorkflowRunRecord> Task { get; init; }','        public required Task<WorkflowRunRecord> Task { get; init; }\n        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);')
b=replace(b,'        var all = Task.WhenAll(drives.Select(d => d.Task));','        var all = Task.WhenAll(drives.Select(d => d.Completion.Task));')
b=replace(b,'''    private async Task ObserveDriveAsync(DriveEntry entry)
    {
        try
        {
            var run = await entry.Task.ConfigureAwait(false);
            _log?.Invoke($"[任务中心] 运行 {run.RunId} 终态：{run.State}");''','''    private async Task ObserveDriveAsync(DriveEntry entry)
    {
        var terminalRunId = entry.RunId;
        try
        {
            var run = await entry.Task.ConfigureAwait(false);
            terminalRunId = run.RunId;
            _log?.Invoke($"[任务中心] 运行 {run.RunId} 终态：{run.State}");''')
b=replace(b,'''            MarkAdmissionTerminalIfAny(entry.RunId); // R5.2 B2：运行终态→仲裁操作终局回写（未接线/无映射=零副作用，异常留痕不掩原收敛）
            lock (_gate)
            {
                _drives.Remove(entry.WorkflowId);
                _reservedWorkflows.Remove(entry.WorkflowId);
            }
            entry.Cts.Dispose();
            NotifyStateChanged();''','''            try
            {
                await MarkAdmissionTerminalIfAnyAsync(terminalRunId).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate)
                {
                    _drives.Remove(entry.WorkflowId);
                    _reservedWorkflows.Remove(entry.WorkflowId);
                }
                entry.Cts.Dispose();
                entry.Completion.TrySetResult();
                NotifyStateChanged();
            }''')
p.write_bytes(b)
p=root/paths[1]; b=p.read_bytes()
start=b.index(b'    private void MarkAdmissionTerminalIfAny(string? runId)'); end=b.index(b'    private async Task<HostActionResult> ReconcileAdmissionTerminalForExplicitStopAsync',start)
b=b[:start]+'''    private async Task MarkAdmissionTerminalIfAnyAsync(string? runId)
    {
        try
        {
            var outcome = await ReconcileAdmissionTerminalAsync(runId).ConfigureAwait(false);
            if (outcome is AdmissionTerminalReconciliationOutcome.Pending or AdmissionTerminalReconciliationOutcome.Failed)
                TryLog("[任务中心] 仲裁操作终局回写未确认（保守留待显式重试）。");
        }
        catch (Exception ex)
        {
            TryLog("[任务中心] 仲裁操作终局回写异常（保守留待显式重试）:" + ex.Message);
        }
    }

'''.replace('\n','\r\n').encode()+b[end:]
b=replace(b,'            return await ReconcileAdmissionTerminalCoreBodyAsync(runId).ConfigureAwait(false);','            using var cleanup = new CancellationTokenSource(AdmissionTerminalReconciliationTimeoutForTest ?? TimeSpan.FromSeconds(15));\n            return await ReconcileAdmissionTerminalCoreBodyAsync(runId, cleanup.Token).ConfigureAwait(false);')
b=replace(b,'    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync(string? runId)','    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync(string? runId, CancellationToken cleanupToken)')
start=b.index(b'    private async Task<AdmissionTerminalReconciliationOutcome> ReconcileAdmissionTerminalCoreBodyAsync'); part=b[start:]; assert part.count(b'_shutdownCts.Token')==2
b=b[:start]+part.replace(b'_shutdownCts.Token',b'cleanupToken')
p.write_bytes(b)
after=[]
for p in paths:
 b=(root/p).read_bytes(); after.append(dict(path=p,bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n')))
(out/'product-after.json').write_text(json.dumps(after,indent=2))
print('applied narrow lifecycle repair',flush=True)
