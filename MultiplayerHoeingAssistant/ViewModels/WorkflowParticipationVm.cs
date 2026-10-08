using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class NodeEditVm
{
    private bool _includedInPlan = true;
    private bool _originalIncludedInPlan;
    private bool _originalLegacyFilter;
    public bool IncludedInPlan { get => _includedInPlan; set => SetProperty(ref _includedInPlan, value); }
    public string ParticipationNote => _originalLegacyFilter
        ? "旧连续计划原本跳过此配置。勾选后会明确将它纳入当前计划；原一条龙配置保持。" : "";

    internal void InitializeParticipation()
    {
        _originalLegacyFilter = Flag(Model, "legacyFiltered");
        _originalIncludedInPlan = _includedInPlan = !_originalLegacyFilter && !Flag(Model, "planDisabled");
    }
    private static bool Flag(WorkflowNode node,string name)
        => node.ExtensionData?.TryGetValue(name,out var value)==true && value.ValueKind==JsonValueKind.True;
    internal void ApplyParticipation(WorkflowNode target)
    {
        if (IncludedInPlan == _originalIncludedInPlan) return;
        target.ExtensionData ??= new();
        target.ExtensionData["planDisabled"] = JsonSerializer.SerializeToElement(!IncludedInPlan);
        if (IncludedInPlan && _originalLegacyFilter)
            target.ExtensionData["legacyFiltered"] = JsonSerializer.SerializeToElement(false);
    }
}
