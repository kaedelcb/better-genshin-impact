from pathlib import Path
import json,hashlib
r=Path.cwd();d=r/'_workflow/local-wait-admission-gates-20261004/typed-parent'
paths=['MultiplayerHoeingAssistant/Models/TaskCenter/WorkflowRunModels.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStore.cs','MultiplayerHoeingAssistant/Services/TaskCenter/RunStoreEvidenceGuard.cs','MultiplayerHoeingAssistant/Services/TaskCenter/TaskCenterHost.Admission.cs','MultiplayerHoeingAssistant/Services/TaskCenter/Arbitration/ArbitrationAdmissionService.cs','MultiplayerHoeingAssistant/Models/TaskCenter/ArbitrationModels.cs']
facts=[]
for p in paths:
 b=(r/p).read_bytes();facts.append(dict(path=p,bytes=len(b),lines=len(b.splitlines()),sha256=hashlib.sha256(b).hexdigest(),bom=b.startswith(b'\xef\xbb\xbf'),crlf=b.count(b'\r\n'),lf=b.count(b'\n')))
(d/'source-before.json').write_text(json.dumps(facts,indent=2))
def edit(p,fn):
 f=r/p;b=f.read_bytes();bom=b.startswith(b'\xef\xbb\xbf');s=b.decode('utf-8-sig');nl='\r\n' if '\r\n' in s else '\n';s=s.replace('\r\n','\n');new=fn(s);assert len(new)>len(s)*.95;pfx=b'\xef\xbb\xbf' if bom else b'';f.write_bytes(pfx+new.replace('\n',nl).encode())
def rep(s,a,b):
 assert s.count(a)==1,(a[:100],s.count(a));return s.replace(a,b)
edit(paths[0],lambda s:rep(s,'    public string? AdmissionSourceScope { get; set; }','    public string? AdmissionSourceScope { get; set; }\n\n    [JsonPropertyName("admissionParentSource")]\n    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]\n    public AdmissionParentSource? AdmissionParentSource { get; set; }'))
edit(paths[1],lambda s:rep(s,'        Persist(rec, expectedRecordRevision: 0);','        if (handoff is { Mode: StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger }\n            && rec.AdmissionSourceScope is not null)\n            rec.AdmissionParentSource = AdmissionParentSource.Handoff(rec, handoff);\n        Persist(rec, expectedRecordRevision: 0);'))
edit(paths[2],lambda s:rep(s,'        var oldPermit = current.CurrentSubmission?.SendPermit;','        Require(current.AdmissionSourceScope == next.AdmissionSourceScope);\n        Require(current.AdmissionParentSource == next.AdmissionParentSource);\n        var originalBindings = next.Handoffs.Select(h => JsonSerializer.Serialize(h)).ToList();\n        foreach (var binding in current.Handoffs)\n            Require(originalBindings.Remove(JsonSerializer.Serialize(binding)));\n        if (current.AdmissionParentSource is { } parent)\n            Require(parent.MatchesHandoff(next));\n        var oldPermit = current.CurrentSubmission?.SendPermit;'))
def host(s):
 s=s.replace('private (string RequestIdentity, string Scope)? TryGetAdmissionParent','private AdmissionParentSource? TryGetAdmissionParent').replace('internal (string RequestIdentity, string Scope)? AdmissionParentForTest','internal AdmissionParentSource? AdmissionParentForTest').replace('internal static (string RequestIdentity, string Scope)? ResolveAdmissionParent','internal static AdmissionParentSource? ResolveAdmissionParent')
 s=rep(s,'        var handoffIdentity = "run-source:" + request.RunId;\n        var sourceKind = string.Equals(parent.Value.RequestIdentity, handoffIdentity, StringComparison.Ordinal)','        var sourceKind = parent.Value.Kind == AdmissionParentKind.StartupHandoff')
 s=rep(s,'                ? (op.RequestIdentity, scope0!)','                ? run?.AdmissionParentSource is not null ? null\n                    : new AdmissionParentSource(1, AdmissionParentKind.PanelFlowRegistration, runId, workflowId, scope0!, op.RequestIdentity)')
 start=s.index('        if (run is null || !string.Equals(run.WorkflowId, workflowId, StringComparison.Ordinal)) return null;',s.index('internal static AdmissionParentSource? ResolveAdmissionParent'))
 end=s.index('\n    }',start)
 s=s[:start]+'''        if (run is null || run.RunId != runId || run.WorkflowId != workflowId
            || run.AdmissionParentSource is not { } original
            || !IsCanonicalAdmissionScope(original.Scope) || !original.MatchesHandoff(run)) return null;
        return original;'''+s[end:]
 s=rep(s,'                BgiEpochProvider = CurrentBgiEpoch,','                BgiEpochProvider = CurrentBgiEpoch,\n                HandoffParentProvider = (runId, workflowId) =>\n                    ResolveAdmissionParent([], _runs.Load(runId), runId, workflowId),')
 s=rep(s,'                Namespace = "successor",','                Namespace = "successor",\n                ParentSource = parentRegistration,')
 return s
edit(paths[3],host)
edit(paths[5],lambda s:rep(s,'    [JsonPropertyName("parentRequestIdentity")] public string? ParentRequestIdentity { get; set; }','    [JsonPropertyName("parentRequestIdentity")] public string? ParentRequestIdentity { get; set; }\n    [JsonPropertyName("parentSource")]\n    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]\n    public AdmissionParentSource? ParentSource { get; set; }'))
