using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class SingleTaskControlTests
{
    [Theory]
    [InlineData(false, "stop")]
    [InlineData(true, "stop")]
    [InlineData(false, "pause")]
    [InlineData(true, "pause")]
    [InlineData(false, "preparing")]
    [InlineData(true, "preparing")]
    public async Task OriginalSingleButton_ShowsControlsAndControlsTheSameRun(bool remote, string action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            TaskCenterHost? host = null;
            TaskCenterPanelViewModel? panel = null;
            LocalConfigEditChannel? pipe = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var root = Path.Combine(Path.GetTempPath(), "single-control-ui-" + Guid.NewGuid().ToString("N"));
                var boundary = new ControlledBoundary { HoldAuthority = action == "preparing" };
                host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"), Path.Combine(root, "catalog.json"),
                    () => null, null, (_, flows, runs) => new WorkflowRunner(flows, runs, boundary, boundary, boundary),
                    () => (true, null), localExecutionCapability: () => true, statusSnapshotProvider: () => null);
                host.SaveFlow(new WorkflowDocument { Name = "单任务控制计划", Nodes =
                [new() { NodeId = "single", Kind = "resource.oneDragonConfig", Ref = new() { Config = "单任务配置", Revision = "r1" } },
                 new() { NodeId = "later", Kind = "resource.oneDragonConfig", Ref = new() { Config = "后续配置", Revision = "r2" } }] }, null);
                var workflowId = host.ListFlows().Single().WorkflowId;
                if (remote)
                {
                    Assert.True(AssistantDataDirectory.IsExplicit, "远程 UI 测试必须指定隔离数据根。");
                    const string uid = "single-control-monitor";
                    const string secret = "single-control-isolated-secret";
                    var target = new RemoteCenterTarget { PlayerUid = uid, PlayerName = "隔离执行端",
                        ClientInstanceId = Guid.NewGuid().ToString("N"), MachineId = "isolated", Online = true, ForceLocal = true };
                    var executor = host;
                    pipe = new LocalConfigEditChannel(() => secret, async command =>
                    {
                        var request = JsonSerializer.Deserialize<RemoteCenterRequest>(RemoteCenterProtocol.PayloadOf(command)!)!;
                        RemoteCenterReply reply;
                        switch (request.Operation)
                        {
                            case "task.list":
                                var overview = executor.ListRunOverview();
                                reply = RemoteCenterReply.Ok(new RemoteTaskListSnapshot
                                {
                                    Flows = executor.ListFlows().ToList(), Active = overview.Active.ToList(), History = overview.History.ToList(),
                                    Catalog = executor.Catalog.Current, StoredResourceEntries = executor.ListStoredResourceEntries().ToList(),
                                    ActiveFlowSnapshots = new() { [workflowId] = executor.LoadFlowSnapshot(workflowId) },
                                    DrivingWorkflowIds = executor.IsDriving(workflowId) ? [workflowId] : []
                                });
                                break;
                            case "task.flow.get":
                                var snapshot = executor.LoadFlowSnapshot(JsonSerializer.Deserialize<string>(request.PayloadJson!)!);
                                reply = RemoteCenterReply.Ok(snapshot.Document, snapshot.Revision);
                                break;
                            case "task.run.start":
                                using (var args = JsonDocument.Parse(request.PayloadJson!))
                                {
                                    var data = args.RootElement;
                                    var started = await executor.StartWorkflowAsync(data.GetProperty("workflowId").GetString()!,
                                        data.GetProperty("entryNodeId").GetString(), data.GetProperty("expectedRevision").GetString(),
                                        data.GetProperty("stopAfterNodeId").GetString());
                                    reply = Result(started);
                                }
                                break;
                            case "task.run.action":
                                using (var args = JsonDocument.Parse(request.PayloadJson!))
                                {
                                    var data = args.RootElement;
                                    reply = Result(await executor.RequestRunActionAsync(data.GetProperty("runId").GetString()!,
                                        Enum.Parse<WorkflowRunAction>(data.GetProperty("action").GetString()!)));
                                }
                                break;
                            case "task.run.resume":
                                reply = Result(await executor.ResumeRunAsync(JsonSerializer.Deserialize<string>(request.PayloadJson!)!));
                                break;
                            default: reply = RemoteCenterReply.Rejected("隔离通道不支持：" + request.Operation); break;
                        }
                        reply.TargetInstanceId = target.ClientInstanceId;
                        return RemoteCenterProtocol.Reply(command, uid, "隔离执行端", reply);
                    }, _ => { }, pipeName: "single-control-ui-" + Guid.NewGuid().ToString("N"), channel: "center");
                    var main = new MainViewModel();
                    typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(main, new AssistConfig { ObserverMode = true, StandaloneMode = true,
                            PlayerUid = uid, ControlRoomPassword = secret, TeamUids = [uid] });
                    var endpoints = (ConcurrentDictionary<string, LocalConfigEditChannel.Endpoint>)typeof(MainViewModel)
                        .GetField("_localCenterEndpoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    endpoints[uid + ":" + target.ClientInstanceId] = pipe.Address;
                    panel = new TaskCenterPanelViewModel(main, target);
                }
                else panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
                var view = new BasicTaskCenterView { DataContext = panel };
                Pump(view, () => panel.Editing is not null);
                panel.Editing!.SelectedNode = panel.Editing.Nodes[0];
                Layout(view);
                ButtonOf(view, "启动单任务").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump(view, () => action == "preparing" ? boundary.AuthorityEntered.Task.IsCompleted : boundary.BodyEntered.Task.IsCompleted);
                Pump(view, () => panel.CurrentPlanRuns.Count == 1);
                var run = host.Runs.List().Single();
                var card = panel.CurrentPlanRuns.Single();
                Assert.Equal(run.RunId, card.RunId);
                Assert.Equal("single", run.StopAfterNodeId);
                if (action == "preparing")
                {
                    Assert.Equal(WorkflowRunState.Planned, run.State);
                    Assert.Equal("正在准备", card.StateText);
                    Assert.False(card.CanPause);
                }
                else
                {
                    Assert.Equal(WorkflowRunState.Running, run.State);
                    Assert.Equal("运行中", card.StateText);
                    Assert.True(card.CanPause);
                    Assert.Equal(Visibility.Visible, ButtonOf(view, "暂停").Visibility);
                }
                Assert.True(card.CanStop);
                Assert.True(card.BlocksNewStart);
                Assert.Equal(Visibility.Visible, ButtonOf(view, "停止").Visibility);
                var controlButton = ButtonOf(view, action == "pause" ? "暂停" : "停止");
                Assert.Same(card, controlButton.CommandParameter);
                controlButton.Command.Execute(controlButton.CommandParameter);
                if (action == "pause")
                {
                    Pump(view, () => panel.StatusMessage?.Contains("暂停") == true);
                    Assert.Empty(boundary.CancelledJobs);
                    boundary.BodyResult.TrySetResult("succeeded");
                    Pump(view, () => host.Runs.Load(run.RunId)?.State == WorkflowRunState.Paused && !host.IsDriving(workflowId));
                    Pump(view, () => panel.CurrentPlanRuns.Single().CanResume);
                    var resume = ButtonOf(view, "恢复");
                    Assert.Equal(Visibility.Visible, resume.Visibility);
                    resume.Command.Execute(resume.CommandParameter);
                    Pump(view, () => host.Runs.Load(run.RunId)?.State == WorkflowRunState.Succeeded && !host.IsDriving(workflowId));
                    Assert.Equal(new[] { "single" }, boundary.SubmittedNodes);
                }
                else
                {
                    Pump(view, () => host.Runs.Load(run.RunId)?.State == WorkflowRunState.Cancelled && !host.IsDriving(workflowId));
                    Assert.True(host.Runs.Load(run.RunId)!.StopRequested);
                    if (action == "preparing") Assert.Empty(boundary.SubmittedNodes);
                    else Assert.Equal(new[] { "single-job" }, boundary.CancelledJobs);
                }
                Assert.DoesNotContain("later", boundary.SubmittedNodes);
                Assert.Equal(0, boundary.TerminalCalls);
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                panel?.StopAutoRefresh();
                boundaryCleanup(host);
                pipe?.Dispose();
            }
            static void boundaryCleanup(TaskCenterHost? host) { if (host is not null) _ = host.ShutdownAsync(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static RemoteCenterReply Result(HostActionResult result) => result.Status == HostActionStatus.Unavailable
        ? RemoteCenterReply.Rejected(result.Message) : RemoteCenterReply.Ok(message: result.Message);
    private static void Layout(FrameworkElement view)
    { view.Measure(new Size(1200, 800)); view.Arrange(new Rect(0, 0, 1200, 800)); view.UpdateLayout(); }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static Button ButtonOf(FrameworkElement view, string text) => Descendants(view).OfType<Button>()
        .Single(button => Equals(button.Content, text));
    private static void Pump(FrameworkElement view, Func<bool> ready)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        var timer = new DispatcherTimer(DispatcherPriority.Background, view.Dispatcher) { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) =>
        {
            if (view.DataContext is TaskCenterPanelViewModel panel) panel.RefreshCommand.Execute(null);
            Layout(view);
            if (ready() || DateTime.UtcNow >= deadline) { timer.Stop(); frame.Continue = false; }
        };
        timer.Start(); Dispatcher.PushFrame(frame);
        Assert.True(ready(), "单任务 UI 或控制未在限定时间内更新。");
    }

    private sealed class ControlledBoundary : IWorkflowExecutionBoundary, IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public bool HoldAuthority;
        public bool SingleNativeSupported => true;
        public bool RequiresStopAuthority => HoldAuthority;
        public TaskCompletionSource AuthorityEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BodyEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> BodyResult = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> SubmittedNodes = [];
        public List<string> CancelledJobs = [];
        public int TerminalCalls;
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string id, long timestamp, CancellationToken ct)
            => Task.FromResult<WorkflowStopAuthorityRecord?>(HoldAuthority
                ? new("42:99", 1, id, timestamp, System.Diagnostics.Stopwatch.Frequency) : null);
        public async Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
        { AuthorityEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return true; }
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            SubmittedNodes.Add(request.Node.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("single-job"));
        }
        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string id, CancellationToken ct)
        { BodyEntered.TrySetResult(); return BoundaryTerminalResult.Observed(await BodyResult.Task.WaitAsync(ct)); }
        public Task RequestCancelAsync(string id, CancellationToken ct)
        { CancelledJobs.Add(id); BodyResult.TrySetResult("cancelled"); return Task.CompletedTask; }
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
        { TerminalCalls++; return Task.FromResult(TerminalExecutionResult.Executed("terminal")); }
    }
}
