using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class NodeEditVm
{
    private bool _includedInPlan = true;
    private bool _originalIncludedInPlan;
    private bool _originalLegacyFilter;
    public bool IncludedInPlan
    {
        get => _includedInPlan;
        set
        {
            if (!SetProperty(ref _includedInPlan, value)) return;
            OnPropertyChanged(nameof(SettingsSummary));
            OnPropertyChanged(nameof(IsEnabled));
            NotifyTimeWindowState();
        }
    }
    public string SettingsSummary
    {
        get
        {
            var parts = new List<string>
            {
                !HasWeekdays
                    ? "每天"
                    : WeekdaysStrategy?.GetStringArray("days") is { Count: > 0 } days
                        ? "星期 " + string.Join("", days.Select(day => day.Length > 1 ? day[1..] : day))
                        : "未选择日期",
            };
            if (HasAccount) parts.Add("账号 " + AccountUidMasked);
            if (!UsePlanFailurePolicy) parts.Add(FailureModeIndex == 1 ? "失败继续" : "失败停止");
            return string.Join(" · ", parts);
        }
    }
    public string WeekdaysDisplay => !HasWeekdays
        ? "每天"
        : WeekdaysStrategy?.GetStringArray("days") is { Count: > 0 } days
            ? string.Join("/", days.Select(day => day.Length > 1 ? day[1..] : day))
            : "未选择日期";
    public string AccountDisplay => HasAccount ? AccountUidMasked : "未绑定账号";
    public string ParticipationNote => _originalLegacyFilter
        ? "此配置单沿用旧版计划设置；打开右侧启用后会按当前计划执行。" : "";

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
