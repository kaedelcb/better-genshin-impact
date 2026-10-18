using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>隔离数据根验证重置的确认、重复点击防护及生产窗口入口；不接真实 BGI。</summary>
public sealed class ResetExecutionStateUiTests
{
    [Fact]
    public async Task ResetCommand_CancelAndBusy_DoNotDuplicateOrAlterPlan()
        => await OnStaAsync(() =>
        {
            var root = NewRoot();
            var host = CreateHost(root);
            var document = Plan();
            host.SaveFlow(document, null);
            var vm = new TaskCenterPanelViewModel(host, autoRefresh: false);
            var planBytes = File.ReadAllBytes(Path.Combine(root, "flows", document.WorkflowId + ".flow.json"));
            var calls = 0;
            vm.ConfirmResetExecutionStateOverride = _ => false;
            vm.ResetExecutionStateOverride = _ =>
            {
                calls++;
                return Task.FromResult(HostActionResult.Effective("不应到达"));
            };
            vm.ResetExecutionStateCommand.Execute(null);
            Assert.Equal(0, calls);
            Assert.Contains("已取消重置", vm.StatusMessage);
            Assert.True(vm.CanResetExecutionState);

            var pending = new TaskCompletionSource<HostActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            vm.ConfirmResetExecutionStateOverride = _ => true;
            vm.ResetExecutionStateOverride = _ => { calls++; return pending.Task; };
            vm.ResetExecutionStateCommand.Execute(null);
            Assert.True(vm.IsResettingExecutionState);
            Assert.False(vm.CanResetExecutionState);
            Assert.False(vm.ResetExecutionStateCommand.CanExecute(null));
            vm.ResetExecutionStateCommand.Execute(null);
            Assert.Equal(1, calls);
            pending.SetResult(HostActionResult.Unavailable("停止终态未确认，原责任保留"));
            PumpUntil(() => !vm.IsResettingExecutionState);
            Assert.True(vm.StatusIsError);
            Assert.Contains("停止终态未确认", vm.StatusMessage);
            Assert.Equal(planBytes, File.ReadAllBytes(Path.Combine(root, "flows", document.WorkflowId + ".flow.json")));
            Assert.Empty(host.Runs.List());

            var empty = new TaskCenterPanelViewModel(CreateHost(Path.Combine(root, "empty")), autoRefresh: false);
            Assert.False(empty.CanResetExecutionState);
            Assert.False(empty.ResetExecutionStateCommand.CanExecute(null));
            vm.StopAutoRefresh();
        });

    [Fact]
    public async Task MainWindow_ResetButtonPreservesFailureHistory_ThenAllowsNewStartAndDelete()
        => await OnStaAsync(() =>
        {
            var root = NewRoot();
            var host = CreateHost(root);
            var document = Plan();
            host.SaveFlow(document, null);
            var originalPlan = File.ReadAllBytes(Path.Combine(root, "flows", document.WorkflowId + ".flow.json"));
            var revision = host.LoadFlowSnapshot(document.WorkflowId!).Revision;
            var interrupted = host.Runs.CreateRun(document.WorkflowId!, revision);
            interrupted.State = WorkflowRunState.Interrupted;
            interrupted.Note = "蓝屏后驱动异常（InvalidOperationException），原始失败证据";
            interrupted.NodeOutcomes = [new() { NodeId = "old-node", Result = "failed", Reason = "原始失败原因" }];
            host.Runs.Update(interrupted);
            var failed = host.Runs.CreateRun(document.WorkflowId!, revision);
            failed.State = WorkflowRunState.Failed;
            failed.Note = "已确认的旧失败，不得改绿";
            host.Runs.Update(failed);
            var failedBytes = File.ReadAllBytes(Path.Combine(root, "runs", failed.RunId + ".run.json"));

            _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var main = new MainViewModel();
            typeof(MainViewModel).GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(main, host);
            MainWindow? window = null;
            try
            {
                window = new MainWindow(main) { ShowActivated = false, Left = -20000, Top = -20000 };
                main.CurrentPage = AppPage.Mistletoe;
                window.Show();
                var shell = window.Mistletoe;
                shell.SelectedTabIndex = 1;
                shell.TaskCenterModeIndex = 0;
                var vm = shell.TaskCenter;
                vm.RefreshCommand.Execute(null);
                window.UpdateLayout();
                var page = Find<MistletoePage>(window);
                Assert.NotNull(page);
                var basic = Find<BasicTaskCenterView>(page!);
                Assert.NotNull(basic);
                var button = Assert.IsType<Button>(basic!.FindName("ResetExecutionStateButton"));
                button.BringIntoView();
                window.UpdateLayout();
                Assert.True(button.IsVisible);
                Assert.True(button.IsEnabled);
                Assert.Same(vm.ResetExecutionStateCommand, button.Command);
                var confirmations = 0;
                vm.ConfirmResetExecutionStateOverride = flow =>
                {
                    confirmations++;
                    Assert.Equal(document.WorkflowId, flow.WorkflowId);
                    var text = TaskCenterPanelViewModel.ResetExecutionStateConfirmation(flow.Name);
                    Assert.Contains("原失败历史会保留", text);
                    Assert.Contains("不会自动启动", text);
                    return true;
                };
                ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
                PumpUntil(() => confirmations == 1 && !vm.IsResettingExecutionState
                    && host.ListActiveRuns().Count == 0);
                Assert.False(vm.StatusIsError, vm.StatusMessage);
                Assert.Contains("重置", vm.StatusMessage);
                Assert.Equal(2, host.Runs.List().Count);
                Assert.Equal(originalPlan, File.ReadAllBytes(Path.Combine(root, "flows", document.WorkflowId + ".flow.json")));
                Assert.Equal(failedBytes, File.ReadAllBytes(Path.Combine(root, "runs", failed.RunId + ".run.json")));
                var originalRun = host.Runs.Load(interrupted.RunId)!;
                Assert.Equal(WorkflowRunState.Interrupted, originalRun.State);
                Assert.Equal(interrupted.Note, originalRun.Note);
                Assert.Equal("原始失败原因", Assert.Single(originalRun.NodeOutcomes).Reason);
                Assert.True(TaskCenterHost.IsExecutionResetReleased(originalRun));
                var history = vm.HistoryRuns.Single(item => item.RunId == interrupted.RunId);
                Assert.Contains("原状态：Interrupted", history.StateBadge);
                Assert.Equal(interrupted.Note, history.NoteText);
                var oldCard = ActiveRunVm.Build(originalRun, host);
                Assert.False(oldCard.CanResume);
                Assert.False(oldCard.CanStop);
                Assert.False(oldCard.BlocksNewStart);
                var historyExpander = Find<Expander>(basic,
                    item => Equals(item.Header, "当前计划的运行历史"));
                Assert.NotNull(historyExpander);
                historyExpander!.IsExpanded = true;
                window.UpdateLayout();
                Assert.NotNull(Find<TextBlock>(historyExpander,
                    item => item.Text.Contains("原始失败证据", StringComparison.Ordinal)));
                RenderResetSnapshotIfRequested(window);

                var start = Find<Button>(basic, item => Equals(item.Content, "⏵⏵ 连续一条龙"));
                Assert.NotNull(start);
                ((IInvokeProvider)new ButtonAutomationPeer(start!)).Invoke();
                PumpUntil(() => host.Runs.List().Count == 3 && !host.IsDriving(document.WorkflowId!));
                Assert.Contains(host.Runs.List(), run => run.RunId != interrupted.RunId
                    && run.RunId != failed.RunId && run.State == WorkflowRunState.Succeeded);
                vm.RefreshCommand.Execute(null);
                vm.DeleteFlowCommand.Execute(vm.Flows.Single());
                PumpUntil(() => vm.Flows.Count == 0);
                Assert.False(vm.StatusIsError, vm.StatusMessage);
                Assert.Equal(3, host.Runs.List().Count);
                Assert.Single(host.ListDeletedFlows());
                Assert.False(button.IsEnabled);
            }
            finally
            {
                if (window is not null)
                {
                    window.Mistletoe.TaskCenter.StopAutoRefresh();
                    if (typeof(MistletoeViewModel).GetField("_bgiStatusRefresh", BindingFlags.Instance | BindingFlags.NonPublic)
                            ?.GetValue(window.Mistletoe) is DispatcherTimer timer) timer.Stop();
                    window.Dodoco.Dispose();
                    window.Hide();
                }
                main.Shutdown();
            }
        });

    [Fact]
    public async Task RemoteReset_UsesSelectedExecutorAndLeavesLocalSameIdentityUntouched()
        => await OnStaAsync(() =>
        {
            var root = NewRoot();
            var local = CreateHost(root);
            var document = Plan();
            local.SaveFlow(document, null);
            var snapshot = local.LoadFlowSnapshot(document.WorkflowId!);
            var localRun = local.Runs.CreateRun(document.WorkflowId!, snapshot.Revision);
            localRun.State = WorkflowRunState.Interrupted;
            local.Runs.Update(localRun);
            var runBytes = File.ReadAllBytes(Path.Combine(root, "runs", localRun.RunId + ".run.json"));
            var main = new MainViewModel { RoomCode = "reset-ui-room-" + Guid.NewGuid().ToString("N") };
            typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(main, new AssistConfig { PlayerUid = "reset-ui-target", PlayerName = "隔离监控端",
                    ObserverMode = true, StandaloneMode = true, ControlRoomPassword = "reset-ui-secret" });
            var target = new RemoteCenterTarget { PlayerUid = "reset-ui-target", PlayerName = "隔离执行端",
                ClientInstanceId = "reset-ui-instance-" + Guid.NewGuid().ToString("N"), ForceLocal = true, Online = true };
            var calls = 0;
            var receiptReads = 0;
            string? resetOperationId = null;
            using var channel = new LocalConfigEditChannel(() => "reset-ui-secret", command =>
            {
                var request = JsonSerializer.Deserialize<RemoteCenterRequest>(RemoteCenterProtocol.PayloadOf(command)!)!;
                RemoteCenterReply reply;
                if (request.Operation == "probe")
                    reply = RemoteCenterReply.Ok(new { instanceId = target.ClientInstanceId,
                        executorName = target.PlayerName, machineId = "reset-ui-machine" });
                else if (request.Operation == "task.list")
                    reply = RemoteCenterReply.Ok(new RemoteTaskListSnapshot { Flows = local.ListFlows().ToList(),
                        Catalog = new ResourceCatalogSnapshot() });
                else if (request.Operation == "task.flow.get")
                    reply = RemoteCenterReply.Ok(document, snapshot.Revision);
                else if (request.Operation == "task.flow.resetExecutionState")
                {
                    Assert.Equal(target.ClientInstanceId, request.TargetInstanceId);
                    Assert.Equal(document.WorkflowId, JsonSerializer.Deserialize<string>(request.PayloadJson!));
                    Interlocked.Increment(ref calls);
                    resetOperationId = command.CommandId;
                    reply = new RemoteCenterReply { Status = "accepted", OperationId = resetOperationId,
                        Message = "执行端正在重置，原历史保留" };
                }
                else if (request.Operation == "operation.get")
                {
                    Assert.NotNull(resetOperationId);
                    Assert.Equal(resetOperationId, JsonSerializer.Deserialize<string>(request.PayloadJson!));
                    Interlocked.Increment(ref receiptReads);
                    reply = RemoteCenterReply.Ok(message: "执行端已重置，原历史保留");
                    reply.OperationId = resetOperationId;
                }
                else reply = RemoteCenterReply.Rejected("测试通道未支持该操作");
                reply.TargetInstanceId = target.ClientInstanceId;
                return Task.FromResult<RemoteCommand?>(RemoteCenterProtocol.Reply(command,
                    target.PlayerUid, target.PlayerName, reply));
            }, _ => { }, channel: "center");
            // Discovery deliberately excludes this process. Use the same authenticated-pipe
            // fixture attachment as ProductionTaskCenterEntryTests, then exercise real routing.
            var endpoints = (ConcurrentDictionary<string, LocalConfigEditChannel.Endpoint>)
                typeof(MainViewModel).GetField("_localCenterEndpoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(main)!;
            endpoints[target.PlayerUid + ":" + target.ClientInstanceId] = channel.Address;
            var vm = new TaskCenterPanelViewModel(main, target);
            try
            {
                PumpUntil(() => vm.Editing is not null && vm.CanResetExecutionState);
                vm.ConfirmResetExecutionStateOverride = _ => true;
                vm.ResetExecutionStateCommand.Execute(null);
                PumpUntil(() => calls == 1 && !vm.IsResettingExecutionState);
                Assert.False(vm.StatusIsError, vm.StatusMessage);
                Assert.Contains("执行端已重置", vm.StatusMessage);
                Assert.True(receiptReads > 0);
                Assert.Null(main.PendingCenterOperation(target));
                Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(root, "runs", localRun.RunId + ".run.json")));
                Assert.False(TaskCenterHost.IsExecutionResetReleased(local.Runs.Load(localRun.RunId)!));
            }
            finally { vm.StopAutoRefresh(); main.Shutdown(); }
        });

    private static string NewRoot()
    {
        Assert.True(AssistantDataDirectory.IsExplicit,
            "必须给测试进程设置独立 NEXUSBGI_DATA_ROOT；禁止使用真实助手数据。");
        var root = Path.Combine(AssistantDataDirectory.Root, "reset-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static WorkflowDocument Plan() => new()
    {
        WorkflowId = "wf-reset-ui", Name = "重置入口隔离计划", Activation = new() { Status = "active" },
        Nodes = [new() { NodeId = "end", Kind = "control.end" }],
    };

    private static TaskCenterHost CreateHost(string root) => new(
        Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
        () => null, null, (_, workflows, runs) =>
        {
            var boundary = new NoSendBoundary();
            return new WorkflowRunner(workflows, runs, boundary, boundary, boundary);
        },
        () => (true, null));

    private sealed class NoSendBoundary : IWorkflowExecutionBoundary, IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public bool SingleNativeSupported => true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            => throw new InvalidOperationException("重置 UI 测试不得发送资源任务");
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("重置 UI 测试不得观察真实任务");
        public Task RequestCancelAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("重置 UI 测试不得取消真实任务");
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("reset-ui-no-effect"));
    }

    private static async Task OnStaAsync(Action test)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            try { test(); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(10) };
        var deadline = DateTime.UtcNow.AddSeconds(10);
        timer.Tick += (_, _) => { if (condition() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; } };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "重置状态未在限定时间内完成");
    }

    private static void RenderResetSnapshotIfRequested(FrameworkElement window)
    {
        var path = Environment.GetEnvironmentVariable("RESET_UI_SNAPSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(window.ActualWidth)),
            Math.Max(1, (int)Math.Ceiling(window.ActualHeight)), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static T? Find<T>(DependencyObject root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is T value && (predicate is null || predicate(value))) return value;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (Find(VisualTreeHelper.GetChild(root, i), predicate) is { } match) return match;
        return null;
    }
}
