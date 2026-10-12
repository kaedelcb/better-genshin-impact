using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class BasicTaskCenterView
{
    private sealed record ResourceTaskRow(string TaskId, string Name, bool Enabled, bool IsStart);

    private TaskCenterPanelViewModel? _resourceTaskHost;
    private WorkflowEditVm? _resourceTaskEditing;
    private NodeEditVm? _resourceTaskNode;
    private WorkflowResourceEditor.ResourceTaskLayout? _resourceTaskLayout;
    private bool _resourceTaskNeedsPlanSync;
    private string? _resourceTaskWorkflowId;
    private string? _resourceTaskNodeId;
    private string? _resourceTaskConfig;
    private string? _resourceTaskKind;
    private ResourceTaskRow? _resourceTaskContextRow;
    private ResourceTaskRow? _resourceTaskDragRow;
    private Point _resourceTaskDragStart;
    private bool _resourceTasksBusy;

    private async void OpenResourceTasksClick(object sender, RoutedEventArgs e)
    {
        if (Host is not { Editing: { } editing } host
            || (sender as FrameworkElement)?.Tag is not NodeEditVm node
            || !editing.Nodes.Contains(node)) return;
        AddTaskPopup.IsOpen = false;
        NodeSettingsPopup.IsOpen = false;
        PlanSettingsPopup.IsOpen = false;
        editing.SelectedNode = node;
        _resourceTaskHost = host;
        _resourceTaskEditing = editing;
        _resourceTaskNode = node;
        _resourceTaskWorkflowId = editing.Draft.WorkflowId;
        _resourceTaskNodeId = node.NodeId;
        _resourceTaskConfig = node.Model.Ref?.Config;
        _resourceTaskKind = node.Model.Kind;
        _resourceTaskLayout = null;
        _resourceTaskNeedsPlanSync = false;
        ResourceTasksTitle.Text = node.Model.Kind == "resource.singleTask"
            ? $"{node.DisplayName} · 所属配置任务" : $"{node.DisplayName} · 内部任务";
        ResourceTasksPopup.IsOpen = true;
        await ReloadResourceTasksAsync();
    }

    private void CloseResourceTasksClick(object sender, RoutedEventArgs e)
    {
        if (_resourceTasksBusy) return;
        ResourceTasksPopup.IsOpen = false;
        _resourceTaskHost = null;
        _resourceTaskEditing = null;
        _resourceTaskNode = null;
        _resourceTaskLayout = null;
        _resourceTaskNeedsPlanSync = false;
        _resourceTaskWorkflowId = null;
        _resourceTaskNodeId = null;
        _resourceTaskConfig = null;
        _resourceTaskKind = null;
        ResourceTaskList.ItemsSource = null;
        ResourceTaskSyncButton.IsEnabled = false;
    }

    private bool TryRebindResourceTaskNode(out TaskCenterPanelViewModel host, out NodeEditVm node)
    {
        host = _resourceTaskHost!;
        node = null!;
        if (host is null || !ReferenceEquals(Host, host)
            || host.Editing is not { } editing || editing.Draft.WorkflowId != _resourceTaskWorkflowId)
            return false;
        var current = editing.Nodes.FirstOrDefault(item => item.NodeId == _resourceTaskNodeId);
        if (current is null || current.Model.Kind != _resourceTaskKind
            || current.Model.Ref?.Config != _resourceTaskConfig) return false;
        _resourceTaskEditing = editing;
        _resourceTaskNode = current;
        node = current;
        return true;
    }

    private async void RefreshResourceTasksClick(object sender, RoutedEventArgs e)
        => await ReloadResourceTasksAsync();

    private async void SyncResourceTaskReferenceClick(object sender, RoutedEventArgs e)
    {
        if (_resourceTasksBusy || !TryRebindResourceTaskNode(out var host, out var node)) return;
        _resourceTasksBusy = true;
        ResourceTaskList.IsEnabled = false;
        ResourceTaskSyncButton.IsEnabled = false;
        ResourceTasksFeedback.Foreground = Brushes.LightGray;
        ResourceTasksFeedback.Text = "正在核对 BGI 并保存计划引用…";
        var synced = false;
        try
        {
            var latest = await host.ReadResourceTaskLayoutAsync(node);
            if (!TryRebindResourceTaskNode(out _, out node))
                throw new InvalidOperationException("计划已切换，请重新打开设置");
            var savedNode = host.SaveResourceRevisionAfterLayout(node, latest.Revision);
            if (savedNode is null) throw new InvalidOperationException("计划引用未保存，请查看计划状态");
            _resourceTaskNode = savedNode;
            _resourceTaskEditing = host.Editing;
            _resourceTaskLayout = latest;
            _resourceTaskNeedsPlanSync = false;
            ResourceTasksFeedback.Foreground = Brushes.LightGreen;
            ResourceTasksFeedback.Text = "已同步当前计划引用。";
            synced = true;
        }
        catch (Exception ex)
        {
            ResourceTasksFeedback.Foreground = Brushes.Orange;
            ResourceTasksFeedback.Text = "同步失败：" + ex.Message;
        }
        finally { _resourceTasksBusy = false; }
        if (synced) await ReloadResourceTasksAsync();
        else
        {
            ResourceTaskList.IsEnabled = _resourceTaskLayout is not null;
            ResourceTaskSyncButton.IsEnabled = _resourceTaskLayout is not null;
        }
    }

    private async Task ReloadResourceTasksAsync()
    {
        if (_resourceTasksBusy || !TryRebindResourceTaskNode(out var host, out var node)) return;
        _resourceTasksBusy = true;
        ResourceTaskList.IsEnabled = false;
        ResourceTaskSyncButton.IsEnabled = false;
        ResourceTasksFeedback.Foreground = Brushes.LightGray;
        ResourceTasksFeedback.Text = "正在读取 BGI 配置…";
        try
        {
            var layout = await host.ReadResourceTaskLayoutAsync(node);
            if (!ResourceTasksPopup.IsOpen || !TryRebindResourceTaskNode(out _, out _)) return;
            _resourceTaskLayout = layout;
            ResourceTaskList.ItemsSource = layout.Tasks.Select(task =>
                new ResourceTaskRow(task.TaskId, task.Name, task.Enabled, task.TaskId == layout.StartTaskId)).ToArray();
            var referenceStale = _resourceTaskNeedsPlanSync || _resourceTaskNode?.Model.Ref?.Revision != layout.Revision;
            ResourceTasksFeedback.Text = referenceStale
                ? "BGI 配置已变化；可同步当前计划引用。"
                : layout.Tasks.Count == 0 ? "此配置没有内部任务。" : $"已读取 {layout.Tasks.Count} 个内部任务";
            ResourceTaskSyncButton.IsEnabled = referenceStale;
            ResourceTaskList.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _resourceTaskLayout = null;
            ResourceTaskList.ItemsSource = null;
            ResourceTaskSyncButton.IsEnabled = false;
            ResourceTasksFeedback.Foreground = Brushes.Orange;
            ResourceTasksFeedback.Text = ex.Message;
        }
        finally { _resourceTasksBusy = false; }
    }

    private async Task ApplyResourceTasksAsync(IReadOnlyList<WorkflowResourceEditor.ResourceTask> tasks,
        string? startTaskId, string action)
    {
        var current = _resourceTaskLayout;
        if (_resourceTasksBusy || current is null) return;
        if (!TryRebindResourceTaskNode(out var host, out var node))
        {
            ResourceTasksFeedback.Foreground = Brushes.Orange;
            ResourceTasksFeedback.Text = "计划已切换，请关闭后重新打开设置。";
            ResourceTaskList.IsEnabled = false;
            return;
        }
        _resourceTasksBusy = true;
        ResourceTaskList.IsEnabled = false;
        ResourceTaskSyncButton.IsEnabled = false;
        ResourceTasksFeedback.Foreground = Brushes.LightGray;
        ResourceTasksFeedback.Text = "正在保存到 BGI 配置…";
        try
        {
            var revision = await host.ApplyResourceTaskLayoutAsync(node, current, tasks, startTaskId);
            if (!TryRebindResourceTaskNode(out _, out node))
            {
                _resourceTaskLayout = null;
                ResourceTasksFeedback.Foreground = Brushes.Orange;
                ResourceTasksFeedback.Text = "BGI 配置已保存，但计划在保存期间切换；请重新打开计划并更新资源引用。";
                return;
            }
            var savedNode = host.SaveResourceRevisionAfterLayout(node, revision);
            if (savedNode is null)
            {
                _resourceTaskLayout = null;
                _resourceTaskNeedsPlanSync = true;
                ResourceTasksFeedback.Foreground = Brushes.Orange;
                ResourceTasksFeedback.Text = "BGI 配置已保存，但计划引用未保存；请处理上方错误后重新读取引用。";
                return;
            }
            _resourceTaskNode = savedNode;
            _resourceTaskEditing = host.Editing;
            _resourceTaskLayout = null;
            _resourceTaskNeedsPlanSync = false;
            ResourceTasksFeedback.Foreground = Brushes.LightGreen;
            ResourceTasksFeedback.Text = $"{action}已保存到 BGI 配置和当前计划。";
        }
        catch (Exception ex)
        {
            ResourceTasksFeedback.Foreground = Brushes.Orange;
            ResourceTasksFeedback.Text = action + "未保存：" + ex.Message;
            ResourceTaskList.ItemsSource = null;
            _resourceTaskLayout = null;
        }
        finally { _resourceTasksBusy = false; }
        // Re-read actual BGI state after a confirmed write, including its new revision and order.
        if (_resourceTaskLayout is null && ResourceTasksFeedback.Foreground == Brushes.LightGreen)
            await ReloadResourceTasksAsync();
    }

    private async void ResourceTaskEnabledClick(object sender, RoutedEventArgs e)
    {
        if ((sender as CheckBox)?.Tag is not ResourceTaskRow row || _resourceTaskLayout is not { } layout) return;
        var enabled = ((CheckBox)sender).IsChecked == true;
        var tasks = layout.Tasks.Select(task => task.TaskId == row.TaskId ? task with { Enabled = enabled } : task).ToArray();
        var start = !enabled && layout.StartTaskId == row.TaskId ? null : layout.StartTaskId;
        await ApplyResourceTasksAsync(tasks, start, "启用状态");
    }

    private void ResourceTaskContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var item = FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is not ResourceTaskRow row || _resourceTasksBusy)
        {
            e.Handled = true;
            return;
        }
        _resourceTaskContextRow = row;
        ResourceTaskList.SelectedItem = row;
        if (ResourceTaskList.ContextMenu is { } menu)
        {
            if (menu.Items[0] is MenuItem set) set.IsEnabled = row.Enabled && !row.IsStart;
            if (menu.Items[1] is MenuItem clear) clear.IsEnabled = _resourceTaskLayout?.StartTaskId != null;
        }
    }

    private async void SetResourceTaskStartClick(object sender, RoutedEventArgs e)
    {
        if (_resourceTaskLayout is not { } layout || _resourceTaskContextRow is not { Enabled: true } row) return;
        await ApplyResourceTasksAsync(layout.Tasks, row.TaskId, "执行起点");
    }

    private async void ClearResourceTaskStartClick(object sender, RoutedEventArgs e)
    {
        if (_resourceTaskLayout is not { } layout) return;
        await ApplyResourceTasksAsync(layout.Tasks, null, "执行起点");
    }

    private void ResourceTaskHandleDown(object sender, MouseButtonEventArgs e)
    {
        _resourceTaskDragRow = (sender as FrameworkElement)?.Tag as ResourceTaskRow;
        _resourceTaskDragStart = e.GetPosition(null);
    }

    private void ResourceTaskHandleMove(object sender, MouseEventArgs e)
    {
        if (_resourceTasksBusy || e.LeftButton != MouseButtonState.Pressed || _resourceTaskDragRow is not { } row) return;
        var current = e.GetPosition(null);
        if (Math.Abs(current.X - _resourceTaskDragStart.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(current.Y - _resourceTaskDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _resourceTaskDragRow = null;
        DragDrop.DoDragDrop(ResourceTaskList, new DataObject(typeof(ResourceTaskRow), row), DragDropEffects.Move);
    }

    private void ResourceTaskHandleUp(object sender, MouseButtonEventArgs e)
        => _resourceTaskDragRow = null;

    private void ResourceTaskDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_resourceTasksBusy && e.Data.GetDataPresent(typeof(ResourceTaskRow))
            ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ResourceTaskDrop(object sender, DragEventArgs e)
    {
        if (_resourceTaskLayout is not { } layout || _resourceTasksBusy
            || e.Data.GetData(typeof(ResourceTaskRow)) is not ResourceTaskRow source
            || FindVisualParent<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is not ResourceTaskRow target
            || source.TaskId == target.TaskId) return;
        var tasks = layout.Tasks.ToList();
        var oldIndex = tasks.FindIndex(task => task.TaskId == source.TaskId);
        var targetIndex = tasks.FindIndex(task => task.TaskId == target.TaskId);
        if (oldIndex < 0 || targetIndex < 0) return;
        var moved = tasks[oldIndex];
        tasks.RemoveAt(oldIndex);
        tasks.Insert(targetIndex, moved);
        e.Handled = true;
        await ApplyResourceTasksAsync(tasks, layout.StartTaskId, "内部任务顺序");
    }
}
