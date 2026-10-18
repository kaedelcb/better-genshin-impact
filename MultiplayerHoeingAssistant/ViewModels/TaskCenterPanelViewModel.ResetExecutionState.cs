using System.Windows;
using System.Windows.Input;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
    private bool _resetExecutionStateInFlight;
    private RelayCommand? _resetExecutionStateCommand;

    public bool IsResettingExecutionState => _resetExecutionStateInFlight;
    public IReadOnlyList<HistoryRunVm> CurrentPlanHistory => HistoryRuns
        .Where(run => run.WorkflowId == SelectedWorkflowId).ToArray();
    public bool HasCurrentPlanHistory => CurrentPlanHistory.Count > 0;
    public bool CanResetExecutionState => !_resetExecutionStateInFlight && !_startResumeInFlight
        && !_remoteSaving && !_remoteAbandoned && (_remoteTarget is null || !_remoteStale)
        && Flows.Any(flow => flow.WorkflowId == SelectedWorkflowId && !flow.IsQuarantined);

    internal Func<WorkflowListItemVm, bool>? ConfirmResetExecutionStateOverride { get; set; }
    internal Func<string, Task<HostActionResult>>? ResetExecutionStateOverride { get; set; }

    internal static string ResetExecutionStateConfirmation(string name)
        => $"重置计划「{name}」的执行状态？\n\n"
            + "计划内容和原失败历史会保留。将停止当前执行，并放弃已中断的旧执行；"
            + "无法确认已停止的任务会保留原责任并报告失败。\n\n"
            + "重置不会自动启动计划。完成后可手动从头启动，或删除计划。";

    public RelayCommand ResetExecutionStateCommand => _resetExecutionStateCommand ??= new(
        async _ => await ResetSelectedExecutionStateAsync(), _ => CanResetExecutionState);

    private void NotifyResetExecutionStateAvailability()
    {
        OnPropertyChanged(nameof(CanResetExecutionState));
        OnPropertyChanged(nameof(IsResettingExecutionState));
        OnPropertyChanged(nameof(CurrentPlanHistory));
        OnPropertyChanged(nameof(HasCurrentPlanHistory));
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task ResetSelectedExecutionStateAsync()
    {
        if (!CanResetExecutionState) return;
        var flow = Flows.FirstOrDefault(item => item.WorkflowId == SelectedWorkflowId);
        if (flow is null) return;
        _resetExecutionStateInFlight = true;
        _startResumeInFlight = true;
        NotifyResetExecutionStateAvailability();
        OnPropertyChanged(nameof(CanStartSelectedPlan));
        OnPropertyChanged(nameof(CanStartFromNode));
        try
        {
            var confirmed = ConfirmResetExecutionStateOverride?.Invoke(flow)
                ?? MessageBox.Show(ResetExecutionStateConfirmation(flow.Name), "重置执行状态",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                SetStatus("已取消重置，计划与执行状态未更改。", false);
                return;
            }

            SetStatus($"正在重置计划「{flow.Name}」的执行状态，请稍候…", false);
            if (_remoteTarget is not null)
            {
                await RemoteOperationAsync("task.flow.resetExecutionState", flow.WorkflowId);
                return;
            }

            var result = await (ResetExecutionStateOverride?.Invoke(flow.WorkflowId)
                ?? _host.ResetWorkflowExecutionStateAsync(flow.WorkflowId));
            ApplyActionResult(result);
        }
        catch (Exception ex)
        {
            SetStatus("重置执行状态失败：" + ex.Message + "；请刷新后核对，原历史保留。", true);
        }
        finally
        {
            _resetExecutionStateInFlight = false;
            _startResumeInFlight = false;
            Refresh();
            NotifyResetExecutionStateAvailability();
            OnPropertyChanged(nameof(CanStartSelectedPlan));
            OnPropertyChanged(nameof(CanStartFromNode));
        }
    }
}
