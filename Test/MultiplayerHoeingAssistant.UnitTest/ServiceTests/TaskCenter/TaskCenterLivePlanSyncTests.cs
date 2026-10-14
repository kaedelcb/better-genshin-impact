using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskCenterLivePlanSyncTests
{
    [Fact]
    public async Task TwoOpenEditors_RefreshPeerChangesAndAllowEditingAfterPeerSave()
    {
        await OnSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "plan-live-sync-" + Guid.NewGuid().ToString("N"));
            var host = Host(root);
            var document = new WorkflowDocument { WorkflowId = "wf-sync", Name = "原计划",
                Nodes = [new() { NodeId = "n-one", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常", Revision = "r1" } }] };
            host.SaveFlow(document, null);
            var executor = new TaskCenterPanelViewModel(host, autoRefresh: false);
            var second = new TaskCenterPanelViewModel(Host(root), autoRefresh: false);
            var a = new BasicTaskCenterView { DataContext = executor };
            var b = new BasicTaskCenterView { DataContext = second };
            a.Measure(new Size(1200, 800)); a.Arrange(new Rect(0, 0, 1200, 800)); a.UpdateLayout();
            b.Measure(new Size(1200, 800)); b.Arrange(new Rect(0, 0, 1200, 800)); b.UpdateLayout();
            executor.Editing!.SelectedNode = executor.Editing.Nodes[0];
            second.Editing!.NameText = "监控端保存后的计划";
            second.SaveFlowCommand.Execute(null);
            executor.Refresh(); a.UpdateLayout();
            Assert.Equal("监控端保存后的计划", executor.Editing!.NameText);
            Assert.Equal(host.LoadFlowSnapshot("wf-sync").Revision, executor.Editing.BaseRevision);
            Assert.Equal("n-one", executor.Editing.SelectedNode?.NodeId);
            Assert.False(executor.HasPlanSyncConflict);
            Assert.Same(executor, a.DataContext);
            executor.Editing.NameText = "执行端继续修改";
            executor.SaveFlowCommand.Execute(null);
            Assert.False(executor.StatusIsError, executor.StatusMessage);
            second.Refresh(); b.UpdateLayout();
            Assert.Equal("执行端继续修改", second.Editing!.NameText);
            Assert.Equal(host.LoadFlowSnapshot("wf-sync").Revision, second.Editing.BaseRevision);
            Assert.False(second.HasPlanSyncConflict);
            executor.StopAutoRefresh(); second.StopAutoRefresh();
        });
    }

    [Fact]
    public async Task DirtyPeerEdit_IsPreservedAndBothViewsExposeConflictRecovery()
    {
        await OnSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "plan-live-conflict-" + Guid.NewGuid().ToString("N"));
            var host = Host(root);
            host.SaveFlow(new WorkflowDocument { WorkflowId = "wf-conflict", Name = "原计划", Nodes = [] }, null);
            var executor = new TaskCenterPanelViewModel(host, autoRefresh: false);
            var second = new TaskCenterPanelViewModel(Host(root), autoRefresh: false);
            var view = new BasicTaskCenterView { DataContext = executor };
            view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout();
            var draft = executor.Editing!;
            draft.NameText = "执行端未保存的修改";
            second.Editing!.NameText = "另一端先保存";
            second.SaveFlowCommand.Execute(null);
            executor.Refresh(); view.UpdateLayout();
            Assert.Same(draft, executor.Editing);
            Assert.Equal("执行端未保存的修改", executor.Editing!.NameText);
            Assert.True(executor.HasPlanSyncConflict);
            Assert.Contains("保留草稿", executor.PlanSyncMessage);
            executor.SaveFlowCommand.Execute(null);
            Assert.Equal("另一端先保存", host.LoadFlowSnapshot("wf-conflict").Document.Name);
            var recover = Find<Button>(view, button => Equals(button.Content, "读取最新计划"));
            Assert.NotNull(recover);
            Assert.Equal(Visibility.Visible, recover!.Visibility);
            executor.ConfirmReloadLatestPlanForTest = () => false;
            recover.Command.Execute(null);
            Assert.Same(draft, executor.Editing);
            executor.ConfirmReloadLatestPlanForTest = () => true;
            recover.Command.Execute(null);
            Assert.Equal("另一端先保存", executor.Editing!.NameText);
            Assert.False(executor.HasPlanSyncConflict);
            executor.Editing.NameText = "读取后可继续编辑";
            executor.SaveFlowCommand.Execute(null);
            Assert.Equal("读取后可继续编辑", host.LoadFlowSnapshot("wf-conflict").Document.Name);
            executor.StopAutoRefresh(); second.StopAutoRefresh();
        });
    }

    private static TaskCenterHost Host(string root) => new(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
        Path.Combine(root, "catalog.json"), () => null, () => true, () => null);

    private static T? Find<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        if (root is T matched && predicate(matched)) return matched;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), predicate) is { } found) return found;
        return null;
    }

    private static async Task OnSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { action(); completion.SetResult(); }
            catch (Exception error) { completion.SetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
