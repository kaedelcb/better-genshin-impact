using System.Globalization;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class WorkflowEditVm
{
    private int _failureModeIndex;
    private int _originalFailureModeIndex;
    private string _maxRetriesText = "0";
    private string _originalMaxRetriesText = "0";
    public int FailureModeIndex { get => _failureModeIndex; set { if (value is 0 or 1) SetProperty(ref _failureModeIndex, value); } }
    public string MaxRetriesText { get => _maxRetriesText; set => SetProperty(ref _maxRetriesText, value); }

    internal void InitializeFailurePolicy()
    {
        _originalFailureModeIndex = _failureModeIndex = Draft.Execution?.OnNodeFailure == "continue" ? 1 : 0;
        _originalMaxRetriesText = _maxRetriesText = (Draft.Execution?.MaxNodeRetries ?? 0).ToString(CultureInfo.InvariantCulture);
    }

    internal void ApplyFailurePolicy(WorkflowDocument copy)
    {
        if (FailureModeIndex == _originalFailureModeIndex && MaxRetriesText == _originalMaxRetriesText) return;
        var retries = ParseFailureRetries(MaxRetriesText);
        copy.Execution ??= new WorkflowExecutionOptions();
        copy.Execution.OnNodeFailure = FailureModeIndex == 1 ? "continue" : "stop";
        copy.Execution.MaxNodeRetries = retries;
    }

    internal static int ParseFailureRetries(string text)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            || value is < 0 or > WorkflowFailurePolicy.MaximumRetries)
            throw new InvalidOperationException("失败资源重试次数须为0至3；未知、取消和拒绝不会自动重试。");
        return value;
    }
}

public sealed partial class NodeEditVm
{
    private int _failureModeIndex;
    private int _originalFailureModeIndex;
    private string _maxRetriesText = "0";
    private string _originalMaxRetriesText = "0";
    private bool _usePlanFailurePolicy = true;
    private bool _originalUsePlanFailurePolicy;
    public int FailureModeIndex { get => _failureModeIndex; set { if (value is 0 or 1) SetProperty(ref _failureModeIndex, value); } }
    public string MaxRetriesText { get => _maxRetriesText; set => SetProperty(ref _maxRetriesText, value); }
    public bool UsePlanFailurePolicy { get => _usePlanFailurePolicy; set { if (SetProperty(ref _usePlanFailurePolicy, value)) OnPropertyChanged(nameof(HasFailureOverride)); } }
    public bool HasFailureOverride => !UsePlanFailurePolicy;

    internal void InitializeNodeFailurePolicy()
    {
        var strategy = Model.Strategies.FirstOrDefault(s => s.Kind == WorkflowFailurePolicy.StrategyKind);
        _originalUsePlanFailurePolicy = _usePlanFailurePolicy = strategy is null;
        _originalFailureModeIndex = _failureModeIndex = strategy?.GetString("onFailure") == "continue" ? 1 : 0;
        _originalMaxRetriesText = _maxRetriesText = strategy?.Params?.TryGetValue("maxRetries", out var value) == true
            ? value.ToString() : "0";
    }

    internal void ApplyNodeFailurePolicy(WorkflowNode target)
    {
        if (UsePlanFailurePolicy == _originalUsePlanFailurePolicy && FailureModeIndex == _originalFailureModeIndex
            && MaxRetriesText == _originalMaxRetriesText) return;
        var existing = target.Strategies.Where(s => s.Kind == WorkflowFailurePolicy.StrategyKind).ToList();
        if (UsePlanFailurePolicy)
        {
            foreach (var strategy in existing) target.Strategies.Remove(strategy);
            return;
        }
        if (existing.Count > 1) throw new InvalidOperationException("任务含多份失败处理，请先保留一份。");
        var retries = WorkflowEditVm.ParseFailureRetries(MaxRetriesText);
        var failure = existing.SingleOrDefault();
        if (failure is null) target.Strategies.Add(failure = new WorkflowStrategy { Kind = WorkflowFailurePolicy.StrategyKind });
        failure.Params ??= new();
        failure.Params["onFailure"] = JsonSerializer.SerializeToElement(FailureModeIndex == 1 ? "continue" : "stop");
        failure.Params["maxRetries"] = JsonSerializer.SerializeToElement(retries);
    }
}
