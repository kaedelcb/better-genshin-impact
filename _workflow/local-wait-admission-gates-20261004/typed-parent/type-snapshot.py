from pathlib import Path
import json,hashlib
p=Path('MultiplayerHoeingAssistant/Models/TaskCenter/AdmissionParentSource.cs');b=p.read_bytes();Path('_workflow/local-wait-admission-gates-20261004/typed-parent/parent-model-before-typed-handoff.json').write_text(json.dumps(dict(bytes=len(b),sha256=hashlib.sha256(b).hexdigest())))
s=b.decode().replace('\r\n','\n')
s=s.replace('public readonly record struct AdmissionParentSource(','''public sealed record AdmissionHandoffIdentity(string IntentKey, string ExecutionId, string StepId,
    string? TriggerKind, string Mode, string? ExtensionDataJson)
{
    public static AdmissionHandoffIdentity Capture(HandoffIdentity original)
        => new(original.IntentKey, original.ExecutionId, original.StepId, original.TriggerKind, original.Mode,
            original.ExtensionData is null ? null : JsonSerializer.Serialize(original.ExtensionData));
}

public readonly record struct AdmissionParentSource(''').replace('string Scope, string RequestIdentity, string? OriginalHandoffJson = null)','string Scope, string RequestIdentity, AdmissionHandoffIdentity? OriginalHandoff = null)').replace('original.IntentKey, JsonSerializer.Serialize(original));','original.IntentKey, AdmissionHandoffIdentity.Capture(original));').replace('var json = OriginalHandoffJson;','var original = OriginalHandoff;').replace('!string.IsNullOrEmpty(json)','original is not null && original.IntentKey == identity').replace('JsonSerializer.Serialize(h) == json','AdmissionHandoffIdentity.Capture(h) == original')
p.write_bytes(s.encode())
p=Path('Test/MultiplayerHoeingAssistant.UnitTest/ServiceTests/TaskCenter/TypedAdmissionParentTests.cs');b=p.read_bytes();s=b.decode().replace('\r\n','\n').replace('source with { OriginalHandoffJson = "{}" }','source with { OriginalHandoff = source.OriginalHandoff! with { ExecutionId = "forged" } }').replace('"forged", "{}");','"forged", AdmissionHandoffIdentity.Capture(Original()));');p.write_bytes(s.replace('\n','\r\n' if b'\r\n' in b else '\n').encode())
