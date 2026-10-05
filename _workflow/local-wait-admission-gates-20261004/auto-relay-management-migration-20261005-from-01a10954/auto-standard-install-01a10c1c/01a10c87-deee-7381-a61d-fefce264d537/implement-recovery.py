from pathlib import Path
import json,hashlib
root=Path.cwd();base=Path(__file__).resolve().parent
before=json.loads((base/'recovery-fix/source-before.json').read_text(encoding='utf-8'))
def edit(rel, transform):
    p=root/rel;data=p.read_bytes();assert hashlib.sha256(data).hexdigest()==before[rel]['sha256'],'unexpected source writer'
    nl='\r\n' if b'\r\n' in data else '\n';text=data.decode('utf-8').replace('\r\n','\n')
    new=transform(text).replace('\n',nl).encode('utf-8');assert len(new)>=len(data)-300,'unexpected shrink';p.write_bytes(new)
    print(rel,len(data),len(new),hashlib.sha256(new).hexdigest())
method='''    /// <summary>Only recovery identity/resource metadata; never an executable or editable snapshot.</summary>
    internal WorkflowMigrationRecoveryDescriptor LoadMigrationRecovery(string workflowId)
    {
        lock (_gate)
        {
            var file = PathFor(workflowId);
            if (MigrationSwitchTransaction.HasReparsePoint(file))
                throw new WorkflowQuarantinedException("Recovery file contains a link; original retained.");
            var bytes = File.ReadAllBytes(file);
            var entry = InspectBytes(bytes, file);
            if (entry.Status == WorkflowFileStatus.Quarantined)
                throw new WorkflowQuarantinedException("Recovery metadata is quarantined: " + entry.QuarantineReason);
            var current = JsonSerializer.Deserialize<WorkflowDocument>(Encoding.UTF8.GetString(bytes), JsonOptions)!;
            var hasTransaction = current.ExtensionData?.TryGetValue(WorkflowMigrationConsumer.TransactionField, out _) == true;
            if (!Directory.Exists(MigrationRootFor(workflowId)))
            {
                if (hasTransaction) throw new WorkflowQuarantinedException("Original recovery transaction is missing.");
                return DescribeRecovery(workflowId, current);
            }
            using var tx = OpenMigration(workflowId);
            var manifest = tx.LoadValidated() ?? throw new WorkflowQuarantinedException("Original recovery manifest is missing or invalid.");
            var relative = workflowId + ".flow.json";
            if (manifest.ConfigRoot != Path.GetFullPath(_flowsDir) ||
                manifest.ActivationRecord is { } activation && activation.Path != relative ||
                manifest.Changes.Any(c => c.Path != relative || c.Kind != ChangeKind.Modified))
                throw new WorkflowQuarantinedException("Recovery transaction root or target identity mismatch.");
            if (hasTransaction && (current.ExtensionData![WorkflowMigrationConsumer.TransactionField].ValueKind != JsonValueKind.String ||
                current.ExtensionData[WorkflowMigrationConsumer.TransactionField].GetString() != manifest.TransactionId))
                throw new WorkflowQuarantinedException("Recovery transaction identity mismatch.");
            if (manifest.Changes.Count == 0)
            {
                if (hasTransaction || current.Activation?.Status != "candidate-ready" ||
                    manifest.Stage is not (MigrationStage.None or MigrationStage.Snapshotting or MigrationStage.SnapshotReady))
                    throw new WorkflowQuarantinedException("Recovery target has not been declared.");
                return DescribeRecovery(workflowId, current);
            }
            if (!manifest.FileHashes.TryGetValue(relative, out var expected))
                throw new WorkflowQuarantinedException("Original workflow baseline is missing.");
            if (!hasTransaction && !HashBytes(bytes).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowQuarantinedException("Unbound recovery document differs from the original baseline.");
            var baselineFile = Path.Combine(manifest.SnapshotPath, relative);
            if (MigrationSwitchTransaction.HasReparsePoint(baselineFile))
                throw new WorkflowQuarantinedException("Recovery baseline contains a link.");
            var baselineBytes = File.ReadAllBytes(baselineFile);
            if (!HashBytes(baselineBytes).Equals(expected, StringComparison.OrdinalIgnoreCase) ||
                InspectBytes(baselineBytes, file).Status == WorkflowFileStatus.Quarantined)
                throw new WorkflowQuarantinedException("Original workflow baseline cannot be verified.");
            var baseline = JsonSerializer.Deserialize<WorkflowDocument>(Encoding.UTF8.GetString(baselineBytes), JsonOptions)!;
            if (baseline.WorkflowId != workflowId || baseline.Activation?.Status != "candidate-ready" ||
                baseline.ExtensionData?.ContainsKey(WorkflowMigrationConsumer.TransactionField) == true)
                throw new WorkflowQuarantinedException("Original candidate identity does not match recovery.");
            return DescribeRecovery(workflowId, baseline);
        }
    }

    private static WorkflowMigrationRecoveryDescriptor DescribeRecovery(string workflowId, WorkflowDocument document)
        => new(workflowId, document.Activation?.Status, document.Nodes.Select(n =>
            new WorkflowMigrationResourceReference(n.Kind, n.Ref?.Config, n.Ref?.ConfigKey, n.Ref?.Revision)).ToArray());

'''
def store(text):
    needle='    /// <summary>\n    /// 保存流程定义，返回新修订号。'
    assert text.count(needle)==1;text=text.replace(needle,method+needle)
    text+='''\n// This descriptor deliberately has no WorkflowDocument and cannot be passed to a Runner.\ninternal sealed record WorkflowMigrationResourceReference(string Kind, string? Config, string? ConfigKey, string? Revision);\ninternal sealed record WorkflowMigrationRecoveryDescriptor(string WorkflowId, string? ActivationStatus,\n    IReadOnlyList<WorkflowMigrationResourceReference> Resources);\n'''
    return text
edit('MultiplayerHoeingAssistant/Services/TaskCenter/WorkflowStore.cs',store)
def standard(text):
    needle='    internal static Plan? Find(string candidateRoot, WorkflowSnapshot snapshot, bool hasWorkflowHistory)\n    {'
    replacement='''    internal static Plan? Find(string candidateRoot, WorkflowSnapshot snapshot, bool hasWorkflowHistory)
        => FindCore(candidateRoot, snapshot.Document.WorkflowId!, snapshot.Document.Activation?.Status,
            snapshot.Document.Nodes.Select(n => new WorkflowMigrationResourceReference(n.Kind,
                n.Ref?.Config, n.Ref?.ConfigKey, n.Ref?.Revision)).ToArray(), hasWorkflowHistory);

    internal static Plan? FindRecovery(string candidateRoot, WorkflowMigrationRecoveryDescriptor recovery, bool hasWorkflowHistory)
        => FindCore(candidateRoot, recovery.WorkflowId, recovery.ActivationStatus, recovery.Resources, hasWorkflowHistory);

    private static Plan? FindCore(string candidateRoot, string workflowId, string? activationStatus,
        IReadOnlyList<WorkflowMigrationResourceReference> resources, bool hasWorkflowHistory)
    {'''
    assert text.count(needle)==1;text=text.replace(needle,replacement)
    for a,b in [('ids.ContainsKey(snapshot.Document.WorkflowId!)','ids.ContainsKey(workflowId)'),('foreach (var node in snapshot.Document.Nodes)','foreach (var node in resources)'),('node.Ref?.Config','node.Config'),('node.Ref.ConfigKey','node.ConfigKey'),('node.Ref.Revision','node.Revision'),('snapshot.Document.WorkflowId + "\\n"','workflowId + "\\n"'),('snapshot.Document.Activation?.Status == "active"','activationStatus == "active"')]:
        # Keep the wrapper's original snapshot projection intact.
        first=text.index('    private static Plan? FindCore');text=text[:first]+text[first:].replace(a,b)
    return text
edit('MultiplayerHoeingAssistant/Services/TaskCenter/StandardMigrationConsumer.cs',standard)
def host(text):
    start=text.index('    public async Task<HostActionResult> RollbackMigrationAsync');end=text.index('    public string SaveFlow',start)
    part=text[start:end]
    assert part.count('var snapshot = _workflows.LoadSnapshot(workflowId);')==1
    part=part.replace('var snapshot = _workflows.LoadSnapshot(workflowId);','var recovery = _workflows.LoadMigrationRecovery(workflowId);')
    part=part.replace('StandardMigrationConsumer.Find(LegacyCandidateRoot(), snapshot,','StandardMigrationConsumer.FindRecovery(LegacyCandidateRoot(), recovery,')
    part=part.replace('snapshot.Document.Activation?.Status != "candidate-ready"','recovery.ActivationStatus != "candidate-ready"')
    return text[:start]+part+text[end:]
edit('MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.cs',host)
