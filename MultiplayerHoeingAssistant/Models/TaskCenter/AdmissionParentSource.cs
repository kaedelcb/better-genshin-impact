using System.Text.Json;

namespace MultiplayerHoeingAssistant.Models;

public enum AdmissionParentKind { PanelFlowRegistration = 1, StartupHandoff = 2 }

/// <summary>受理时固定的原父引用；版本未知或原绑定缺失不能用于准入。</summary>
public sealed record AdmissionHandoffIdentity(string IntentKey, string ExecutionId, string StepId,
    string? TriggerKind, string Mode, string? ExtensionDataJson)
{
    public static AdmissionHandoffIdentity Capture(HandoffIdentity original)
        => new(original.IntentKey, original.ExecutionId, original.StepId, original.TriggerKind, original.Mode,
            original.ExtensionData is null ? null : JsonSerializer.Serialize(original.ExtensionData));
}

public readonly record struct AdmissionParentSource(
    int Version, AdmissionParentKind Kind, string RunId, string WorkflowId,
    string Scope, string RequestIdentity, AdmissionHandoffIdentity? OriginalHandoff = null)
{
    public static AdmissionParentSource Handoff(WorkflowRunRecord run, HandoffIdentity original)
        => new(1, AdmissionParentKind.StartupHandoff, run.RunId, run.WorkflowId,
            run.AdmissionSourceScope!, original.IntentKey, AdmissionHandoffIdentity.Capture(original));

    public bool MatchesHandoff(WorkflowRunRecord run)
    {
        var identity = RequestIdentity;
        var original = OriginalHandoff;
        return Version == 1 && Kind == AdmissionParentKind.StartupHandoff
           && RunId == run.RunId && WorkflowId == run.WorkflowId && Scope == run.AdmissionSourceScope
           && !string.IsNullOrWhiteSpace(identity) && original is not null && original.IntentKey == identity
           && run.Handoffs is { } bindings && bindings.All(h => h is not null)
           && bindings.Count(h => h.IntentKey == identity) == 1
           && bindings.Any(h => h.IntentKey == identity
               && h.Mode is StartupHandoffModes.Start or StartupHandoffModes.ArmTrigger
               && AdmissionHandoffIdentity.Capture(h) == original);
    }
}
