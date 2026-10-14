using System.Reflection;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>隔离数据根下从生产 MainWindow 到 MistletoePage/任务中心的入口冒烟。</summary>
public sealed class ProductionTaskCenterEntryTests
{
    [Fact]
    public async Task MainWindow_MistletoeEntry_SwitchesAdvancedAndReturnsThroughButton()
    {
        Assert.True(MultiplayerHoeingAssistant.Services.AssistantDataDirectory.IsExplicit,
            "此测试必须由外部为本进程指定 NEXUSBGI_DATA_ROOT，不能触碰真实用户目录。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? app = null;
            MainWindow? window = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var workflows = new WorkflowStore(WorkflowStore.DefaultFlowsDir());
                workflows.Save(new WorkflowDocument
                {
                    Name = "窗口验收计划",
                    Nodes =
                    [
                        new() { NodeId = "n-1", Kind = "resource.oneDragonConfig", Ref = new() { Config = "窗口整龙", Revision = "r1" } },
                        new() { NodeId = "n-end", Kind = "control.end" },
                    ],
                }, null);
                var mainVm = new MainViewModel();
                window = new MainWindow(mainVm)
                {
                    ShowActivated = false, Left = -20000, Top = -20000,
                };
                mainVm.CurrentPage = AppPage.Mistletoe;
                window.Show();
                window.Mistletoe.TaskCenter.RefreshCommand.Execute(null);
                window.UpdateLayout();

                var mistletoe = FindVisualChild<MistletoePage>(window);
                Assert.NotNull(mistletoe);
                var shell = window.Mistletoe;
                shell.SelectedTabIndex = 1;
                shell.TaskCenterModeIndex = 0;
                window.UpdateLayout();
                PumpUntil(window, () => shell.TaskCenter.Editing is not null);
                var basic = FindVisualChild<BasicTaskCenterView>(mistletoe!);
                Assert.NotNull(basic);
                Assert.Equal(Visibility.Visible, basic!.Visibility);
                Assert.NotNull(shell.TaskCenter.Editing);
                Assert.Contains("窗口整龙", shell.TaskCenter.Editing!.Nodes.Select(node => node.DisplayName));
                var nameText = FindVisualChild<TextBlock>(basic, text => text.Text == "窗口整龙");
                Assert.NotNull(nameText);
                Assert.True(nameText!.IsVisible);
                Assert.True(nameText.ActualWidth > 0);
                var internalSettings = FindVisualChild<Button>(basic, button => Equals(button.Content, "⚙"));
                Assert.NotNull(internalSettings);
                Assert.True(internalSettings!.IsVisible);

                shell.TaskCenterModeIndex = 1;
                window.UpdateLayout();
                Assert.Equal(Visibility.Collapsed, basic.Visibility);
                var returnButton = FindVisualChild<Button>(mistletoe, button => Equals(button.Content, "←  切回基础模式"));
                Assert.NotNull(returnButton);
                Assert.True(returnButton!.IsVisible);
                Assert.NotNull(returnButton.Command);
                ((IInvokeProvider)new ButtonAutomationPeer(returnButton)).Invoke();
                PumpUntil(window, () => shell.TaskCenterModeIndex == 0);
                window.UpdateLayout();
                Assert.Equal(0, shell.TaskCenterModeIndex);
                Assert.Equal(Visibility.Visible, basic.Visibility);
                Assert.True(nameText.IsVisible);
                Assert.NotNull(shell.TaskCenter.Editing);
                RenderSnapshotIfRequested(window);
                // Feed the original view through an authenticated local center pipe and prove
                // its save command reaches the selected executor without writing local flows.
                var originalLocalState = workflows.List()
                    .Select(flow => (flow.WorkflowId, flow.Revision)).ToArray();
                var savedRemote = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                var remoteDocument = new WorkflowDocument
                {
                    WorkflowId = "wf-remote-isolated", Name = "隔离远程计划",
                    Nodes = [new WorkflowNode { NodeId = "n-remote", Kind = "resource.oneDragonConfig",
                        Ref = new WorkflowResourceRef { Config = "执行端整龙", Revision = "r1" } }]
                };
                var remoteRevision = "revision-a";
                var remoteFlowSaveCount = 0;
                var resourceRevision = "r1";
                var resourceTasks = new List<WorkflowResourceEditor.ResourceTask>
                {
                    new("remote-a", "回神像切换指定队伍", true),
                    new("remote-b", "锄地一条龙", true),
                    new("remote-c", "回神像切换指定队伍", false)
                };
                string? resourceStart = null;
                var resourceSaveCount = 0;
                var loseResourceReply = false;
                RemoteCenterReply? resourceOperationReceipt = null;
                var layoutRequested = false;
                RemoteTriggerInfo[] remoteTriggers = [new("remote-timer-1", "timer",
                    "执行端定时触发器 — 明日执行", new StartupStep
                    {
                        Id = "remote-start-node", Kind = StartupStepKinds.TimerTrigger
                    })];
                RemotePromptInfo[] remotePrompts = [];
                var answeredYes = false;
                var runStartCalls = 0;
                var startupRunCalls = 0;
                var createFlowCalls = 0;
                string? createdFlowId = null;
                var runOperationId = "";
                var runOperationState = "unknown";
                using var centerPipe = new LocalConfigEditChannel(() => "isolated-secret", command =>
                {
                    var request = JsonSerializer.Deserialize<RemoteCenterRequest>(
                        RemoteCenterProtocol.PayloadOf(command)!)!;
                    RemoteCenterReply reply = request.Operation switch
                    {
                        "task.list" => RemoteCenterReply.Ok(new
                        {
                            flows = new[] { new WorkflowCatalogEntry(remoteDocument.WorkflowId!,
                                remoteDocument.Name, remoteRevision, WorkflowFileStatus.Ready,
                                null, Array.Empty<string>(), "active", "remote.flow.json") },
                            active = Array.Empty<WorkflowRunRecord>(), history = Array.Empty<WorkflowRunRecord>(),
                            catalog = new ResourceCatalogSnapshot(), deleted = Array.Empty<WorkflowDeletionReceipt>(),
                            storedResourceEntries = new[] { new TaskCenterResourceEntry
                            {
                                Kind = TaskCenterResourceKind.OneDragonConfig,
                                StableId = "onedragon:执行端整龙", DisplayName = "执行端整龙",
                                ConfigRevision = "r1"
                            } },
                            activeFlowSnapshots = new Dictionary<string, WorkflowSnapshot>(),
                            drivingWorkflowIds = Array.Empty<string>()
                        }),
                        "task.flow.get" => RemoteCenterReply.Ok(remoteDocument, remoteRevision),
                        "task.flow.save" => SaveRemote(request, command.CommandId),
                        "task.resource.layout.get" => ReadResourceLayout(request),
                        "task.resource.layout.save" => SaveResourceLayout(request, command.CommandId),
                        "task.run.start" => BeginRemoteRun(command),
                        "startup.run" => BeginRemoteStartup(command),
                        "operation.get" => ReadRemoteOperation(request),
                        "startup.status" => RemoteCenterReply.Ok(new
                        {
                            isRunning = false, statusText = "执行端空闲", triggers = remoteTriggers,
                            prompts = remotePrompts,
                            nodeStates = new[] { new RemoteNodeRunState("remote-start-node",
                                NodeRunState.CondTrue, "执行端已走是分支") }
                        }),
                        "startup.trigger.cancel" => CancelRemoteTrigger(request),
                        "startup.prompt.answer" => AnswerRemotePrompt(request),
                        _ => RemoteCenterReply.Rejected("测试通道不支持：" + request.Operation)
                    };
                    reply.TargetInstanceId = "isolated-instance";
                    return Task.FromResult<RemoteCommand?>(RemoteCenterProtocol.Reply(command,
                        "isolated-target", "隔离执行端", reply));

                    RemoteCenterReply SaveRemote(RemoteCenterRequest request, string requestId)
                    {
                        if (request.ExpectedRevision is null)
                        {
                            Interlocked.Increment(ref createFlowCalls);
                            createdFlowId = JsonSerializer.Deserialize<WorkflowDocument>(request.PayloadJson!)!.WorkflowId;
                            return RemoteCenterReply.Unknown(requestId, "新计划已写入，但回复丢失");
                        }
                        if (request.ExpectedRevision != remoteRevision)
                            return RemoteCenterReply.Rejected("执行端修订已变化");
                        remoteDocument = JsonSerializer.Deserialize<WorkflowDocument>(request.PayloadJson!)!;
                        remoteRevision = ++remoteFlowSaveCount == 1 ? "revision-b" : "revision-b-" + remoteFlowSaveCount;
                        savedRemote.TrySetResult(remoteDocument.Name);
                        return RemoteCenterReply.Ok(new { remoteDocument.WorkflowId }, remoteRevision);
                    }

                    RemoteCenterReply ReadResourceLayout(RemoteCenterRequest request)
                    {
                        using var args = JsonDocument.Parse(request.PayloadJson!);
                        Assert.Equal(remoteDocument.WorkflowId, args.RootElement.GetProperty("workflowId").GetString());
                        Assert.Equal("n-remote", args.RootElement.GetProperty("nodeId").GetString());
                        layoutRequested = true;
                        return RemoteCenterReply.Ok(new WorkflowResourceEditor.ResourceTaskLayout(
                            resourceRevision, resourceStart, resourceTasks.ToArray()), resourceRevision);
                    }

                    RemoteCenterReply SaveResourceLayout(RemoteCenterRequest request, string requestId)
                    {
                        using var args = JsonDocument.Parse(request.PayloadJson!);
                        var value = args.RootElement;
                        Assert.Equal("resource.oneDragonConfig", value.GetProperty("resourceKind").GetString());
                        Assert.Equal("执行端整龙", value.GetProperty("resourceConfig").GetString());
                        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                        var current = value.GetProperty("current").Deserialize<WorkflowResourceEditor.ResourceTaskLayout>(options)!;
                        if (current.Revision != resourceRevision)
                            return RemoteCenterReply.Rejected("配置已变化，未写入");
                        resourceTasks = value.GetProperty("tasks").Deserialize<List<WorkflowResourceEditor.ResourceTask>>(options)!;
                        resourceStart = value.GetProperty("startTaskId").GetString();
                        resourceRevision = "r" + (++resourceSaveCount + 1);
                        resourceOperationReceipt = RemoteCenterReply.Ok(revision: resourceRevision, message: "BGI 配置已保存");
                        resourceOperationReceipt.OperationId = requestId;
                        return loseResourceReply
                            ? RemoteCenterReply.Unknown(requestId, "配置已写入但回复丢失；等待核对") : resourceOperationReceipt;
                    }

                    RemoteCenterReply CancelRemoteTrigger(RemoteCenterRequest request)
                    {
                        var id = JsonSerializer.Deserialize<string>(request.PayloadJson!);
                        remoteTriggers = remoteTriggers.Where(item => item.RuntimeId != id).ToArray();
                        return RemoteCenterReply.Ok(message: "执行端已取消触发器");
                    }

                    RemoteCenterReply AnswerRemotePrompt(RemoteCenterRequest request)
                    {
                        using var json = JsonDocument.Parse(request.PayloadJson!);
                        if (json.RootElement.GetProperty("runtimeId").GetString() != "remote-prompt-1")
                            return RemoteCenterReply.Rejected("人工确认已结束");
                        answeredYes = json.RootElement.GetProperty("yes").GetBoolean();
                        remotePrompts = [];
                        return RemoteCenterReply.Ok(message: "执行端已接收人工确认");
                    }

                    RemoteCenterReply BeginRemoteRun(RemoteCommand request)
                    {
                        Interlocked.Increment(ref runStartCalls);
                        runOperationId = request.CommandId;
                        return new RemoteCenterReply { Status = "accepted",
                            OperationId = runOperationId, Message = "执行端已受理" };
                    }

                    RemoteCenterReply BeginRemoteStartup(RemoteCommand request)
                    {
                        Interlocked.Increment(ref startupRunCalls);
                        runOperationId = request.CommandId;
                        runOperationState = "ok";
                        return new RemoteCenterReply { Status = "accepted",
                            OperationId = runOperationId, Message = "启动流程已受理" };
                    }

                    RemoteCenterReply ReadRemoteOperation(RemoteCenterRequest request)
                    {
                        var id = JsonSerializer.Deserialize<string>(request.PayloadJson!);
                        if (id == resourceOperationReceipt?.OperationId)
                            return loseResourceReply ? RemoteCenterReply.Unknown(id!, "配置保存回执尚未核对") : resourceOperationReceipt;
                        return id == runOperationId
                            ? new RemoteCenterReply { Status = runOperationState,
                                OperationId = runOperationId, Message = "隔离运行状态" }
                            : RemoteCenterReply.Rejected("找不到操作");
                    }
                }, _ => { }, pipeName: "bettergi-original-ui-" + Guid.NewGuid().ToString("N"), channel: "center");
                typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(mainVm, new AssistConfig { ObserverMode = true, StandaloneMode = true,
                        PlayerUid = "isolated-target", PlayerName = "隔离监控端",
                        ControlRoomPassword = "isolated-secret", TeamUids = ["isolated-target"] });
                typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(mainVm, [nameof(MainViewModel.IsObserverMode)]);
                typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(mainVm, [nameof(MainViewModel.IsExecutorMode)]);
                var target = new RemoteCenterTarget
                {
                    PlayerUid = "isolated-target", PlayerName = "隔离执行端",
                    ClientInstanceId = "isolated-instance", MachineId = "isolated-machine",
                    Online = true, ForceLocal = true
                };
                var endpoints = (ConcurrentDictionary<string, LocalConfigEditChannel.Endpoint>)
                    typeof(MainViewModel).GetField("_localCenterEndpoints", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .GetValue(mainVm)!;
                endpoints[target.PlayerUid + ":" + target.ClientInstanceId] = centerPipe.Address;
                mainVm.RemoteCenterTargets.Add(target);
                var remote = new MistletoeViewModel(mainVm, remoteTarget: target,
                    remoteSnapshot: new RemoteStartupSnapshot
                    {
                        Config = new StartupFlowConfig
                        {
                            Steps = [new StartupStep { Id = "remote-start-node",
                                NodeType = "condition", Kind = "condition.weekday" }]
                        },
                        NodeStates = [new RemoteNodeRunState("remote-start-node",
                            NodeRunState.CondTrue, "执行端已走是分支")],
                        Triggers = remoteTriggers.ToList(),
                        StatusText = "隔离远程状态"
                    });
                window.SetValue(MainWindow.ActiveMistletoeProperty, remote);
                remote.SelectedTabIndex = 0;
                window.UpdateLayout();
                RenderSnapshotIfRequested(window, "REMOTE_STARTUP_UI_SNAPSHOT_PATH");
                remote.SelectedTabIndex = 1;
                window.UpdateLayout();
                Assert.Same(basic, FindVisualChild<BasicTaskCenterView>(mistletoe));
                Assert.Equal(Visibility.Visible, basic.Visibility);
                Assert.Same(remote.TaskCenter, basic.DataContext);
                Assert.NotSame(shell.TaskCenter, basic.DataContext);
                Assert.NotNull(FindVisualChild<Button>(mistletoe,
                    button => Equals(button.Content, "执行策略") && button.Visibility == Visibility.Visible));
                Assert.NotNull(FindVisualChild<Button>(mistletoe,
                    button => Equals(button.Content, "三方接入") && button.Visibility == Visibility.Visible));
                Assert.Equal(NodeRunState.CondTrue, remote.RootChain.Steps[0].RunState);
                Assert.True(remote.RootChain.Steps[0].FalseChain.BranchDimmed);
                Assert.True(remote.HasArmedTimers);
                Assert.False(remote.HasRemotePrompts);
                Assert.Equal("执行端定时触发器 — 明日执行", Assert.Single(remote.RemoteTimerRows).StatusLine);
                Assert.NotNull(FindVisualChild<ItemsControl>(mistletoe,
                    control => ReferenceEquals(control.ItemsSource, remote.RemoteTimerRows)));
                remote.ShowFlowChartCommand.Execute(null);
                var remoteChart = Application.Current.Windows.OfType<FlowChartWindow>().LastOrDefault();
                Assert.NotNull(remoteChart);
                Assert.Contains(target.Label, remoteChart!.Title);
                Assert.Same(remote.RemoteTimerRows,
                    ((ItemsControl)remoteChart!.FindName("TimerList")).ItemsSource);
                remoteChart.Close();
                Assert.Same(target, ((ComboBox)mistletoe!.FindName("RemoteCenterTargetChoice")).SelectedItem);
                try { PumpUntil(window, () => remote.TaskCenter.Editing?.Draft.WorkflowId == remoteDocument.WorkflowId); }
                catch (Exception ex) { throw new InvalidOperationException(
                    $"远程计划未显示：{remote.TaskCenter.StatusMessage}; 列表={remote.TaskCenter.Flows.Count}", ex); }
                Assert.Equal("隔离远程计划", remote.TaskCenter.Editing!.NameText);
                Assert.Contains(remote.TaskCenter.Editing.AppendSources,
                    source => source.DisplayName == "执行端整龙");
                remote.TaskCenter.Editing.NameText = "监控端修改后在执行端保存";
                PumpUntil(window, () => savedRemote.Task.IsCompleted);
                Assert.Equal("监控端修改后在执行端保存", savedRemote.Task.Result);
                PumpUntil(window, () => remote.TaskCenter.Editing?.BaseRevision == "revision-b"
                    && remote.TaskCenter.Editing.NameText == savedRemote.Task.Result);
                remote.TaskCenter.RefreshCommand.Execute(null);
                PumpUntil(window, () => remote.TaskCenter.Flows.Any(flow => flow.Name == savedRemote.Task.Result));
                Assert.Equal(originalLocalState, workflows.List()
                    .Select(flow => (flow.WorkflowId, flow.Revision)).ToArray());
                RenderSnapshotIfRequested(window, "REMOTE_CENTER_UI_SNAPSHOT_PATH");
                // User's existing assistant popup, reached through the original row button.
                var resources = Assert.IsType<ListBox>(basic.FindName("ResourceList"));
                window.UpdateLayout();
                var resourceRow = Assert.IsType<ListBoxItem>(resources.ItemContainerGenerator.ContainerFromIndex(0));
                var settingsButton = FindVisualChild<Button>(resourceRow, b => Equals(b.Content, "⚙"));
                Assert.NotNull(settingsButton);
                resources.SelectedIndex = 0;
                window.UpdateLayout();
                var modifyButton = FindVisualChild<Button>(basic, b => Equals(b.Content, "修改"));
                Assert.NotNull(modifyButton);
                Assert.True(modifyButton!.IsEnabled);
                modifyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var popup = Assert.IsType<Popup>(basic.FindName("ResourceTasksPopup"));
                var tasks = Assert.IsType<ListBox>(basic.FindName("ResourceTaskList"));
                var feedback = Assert.IsType<TextBlock>(basic.FindName("ResourceTasksFeedback"));
                var sync = Assert.IsType<Button>(basic.FindName("ResourceTaskSyncButton"));
                PumpUntil(window, () => layoutRequested && popup.IsOpen && tasks.Items.Count == 3 && tasks.IsHitTestVisible);
                Assert.Equal("执行端整龙 · 内部任务", ((TextBlock)basic.FindName("ResourceTasksTitle")).Text);
                Assert.Equal(new[] { "回神像切换指定队伍", "锄地一条龙", "回神像切换指定队伍" },
                    tasks.Items.Cast<object>().Select(row => row.GetType().GetProperty("Name")!.GetValue(row)?.ToString()));
                RenderSnapshotIfRequested((FrameworkElement)popup.Child, "REMOTE_INTERNAL_TASKS_UI_SNAPSHOT_PATH");
                var closeTasks = FindVisualChild<Button>(popup.Child, b => Equals(b.Content, "×"))!;
                closeTasks.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(popup.IsOpen);
                window.UpdateLayout();
                resourceRow = Assert.IsType<ListBoxItem>(resources.ItemContainerGenerator.ContainerFromIndex(0));
                settingsButton = FindVisualChild<Button>(resourceRow, b => Equals(b.Content, "⚙"));
                Assert.NotNull(settingsButton);
                Assert.Contains(Assert.IsType<NodeEditVm>(settingsButton!.Tag), remote.TaskCenter.Editing!.Nodes);
                settingsButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => popup.IsOpen && tasks.Items.Count == 3 && tasks.IsHitTestVisible);
                void ToggleTask(int index, bool enabled)
                {
                    var row = Assert.IsType<ListBoxItem>(tasks.ItemContainerGenerator.ContainerFromIndex(index));
                    var checkbox = FindVisualChild<CheckBox>(row)!;
                    checkbox.IsChecked = enabled;
                    checkbox.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                }
                ToggleTask(0, false);
                PumpUntil(window, () => resourceSaveCount == 1 && tasks.IsHitTestVisible
                    && remote.TaskCenter.Editing!.Nodes[0].Model.Ref!.Revision == resourceRevision);
                Assert.False(resourceTasks[0].Enabled);
                Assert.Equal(resourceRevision, remoteDocument.Nodes[0].Ref!.Revision);
                Assert.Null(mainVm.PendingCenterOperation(target));
                // A native WPF drop uses the same original popup, with the result sent to the executor.
                var sourceRow = tasks.Items[1];
                var targetRow = Assert.IsType<ListBoxItem>(tasks.ItemContainerGenerator.ContainerFromIndex(0));
                var dragConstructor = typeof(DragEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [typeof(IDataObject), typeof(DragDropKeyStates), typeof(DragDropEffects), typeof(DependencyObject), typeof(Point)], null)!;
                var drop = (DragEventArgs)dragConstructor.Invoke([new DataObject(sourceRow.GetType(), sourceRow),
                    DragDropKeyStates.LeftMouseButton, DragDropEffects.Move, targetRow, new Point(8, 16)]);
                drop.RoutedEvent = UIElement.DropEvent;
                targetRow.RaiseEvent(drop);
                PumpUntil(window, () => resourceSaveCount == 2 && tasks.IsHitTestVisible
                    && remoteDocument.Nodes[0].Ref!.Revision == resourceRevision);
                Assert.True(drop.Handled);
                Assert.Equal(new[] { "remote-b", "remote-a", "remote-c" }, resourceTasks.Select(t => t.TaskId));
                var firstRow = Assert.IsType<ListBoxItem>(tasks.ItemContainerGenerator.ContainerFromIndex(0));
                var contextConstructor = typeof(ContextMenuEventArgs).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                    null, [typeof(object), typeof(bool)], null)!;
                var opening = (ContextMenuEventArgs)contextConstructor.Invoke([firstRow, true]);
                opening.RoutedEvent = FrameworkElement.ContextMenuOpeningEvent;
                firstRow.RaiseEvent(opening);
                Assert.IsType<MenuItem>(tasks.ContextMenu!.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(window, () => resourceSaveCount == 3 && tasks.IsHitTestVisible
                    && remoteDocument.Nodes[0].Ref!.Revision == resourceRevision);
                Assert.Equal("remote-b", resourceStart);
                // Authority changes while popup is open: stale edit must not overwrite it.
                resourceRevision = "executor-newer";
                ToggleTask(1, true);
                PumpUntil(window, () => feedback.Text.Contains("配置已变化"));
                Assert.Equal(3, resourceSaveCount);
                var reload = FindVisualChild<Button>(popup.Child, b => Equals(b.Content, "重新读取 BGI"))!;
                reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => tasks.IsHitTestVisible && sync.IsEnabled);
                sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => remoteDocument.Nodes[0].Ref!.Revision == "executor-newer" && tasks.IsHitTestVisible);
                // Lost save receipt stays pending. Reading again cannot silently resubmit the edit.
                loseResourceReply = true;
                ToggleTask(2, true);
                PumpUntil(window, () => resourceSaveCount == 4 && feedback.Text.Contains("保存结果未确认"));
                Assert.True(remote.TaskCenter.HasPendingRemoteOperation);
                reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => tasks.IsHitTestVisible);
                ToggleTask(2, false);
                PumpUntil(window, () => feedback.Text.Contains("结果未确认"));
                Assert.Equal(4, resourceSaveCount);
                Assert.True(resourceTasks[2].Enabled);
                loseResourceReply = false;
                remote.TaskCenter.ReviewPendingRemoteOperationCommand.Execute(null);
                PumpUntil(window, () => mainVm.PendingCenterOperation(target) is null);
                reload.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => tasks.IsHitTestVisible && sync.IsEnabled);
                sync.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => remoteDocument.Nodes[0].Ref!.Revision == resourceRevision && tasks.IsHitTestVisible);
                remote.ShowFlowChartCommand.Execute(null);
                var returningChart = Application.Current.Windows.OfType<FlowChartWindow>().Last();
                var switchButton = Assert.IsType<Button>(mistletoe.FindName("RemoteCenterSwitchButton"));
                Assert.Equal("返回监控端", switchButton.Content);
                switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => ReferenceEquals(window.ActiveMistletoe, shell));
                window.UpdateLayout();
                Assert.False(popup.IsOpen);
                Assert.False(returningChart.IsLoaded);
                Assert.Same(shell.TaskCenter, basic.DataContext);
                Assert.Equal("连接执行端", switchButton.Content);
                switchButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(window, () => ReferenceEquals(window.ActiveMistletoe, remote));
                window.UpdateLayout();
                Assert.Same(remote.TaskCenter, basic.DataContext);
                Assert.Equal("返回监控端", switchButton.Content);
                Assert.Equal(originalLocalState, workflows.List().Select(flow => (flow.WorkflowId, flow.Revision)).ToArray());
                remote.RemoteTimerRows[0].CancelCommand.Execute(null);
                PumpUntil(window, () => !remote.HasArmedTimers);
                remotePrompts = [new RemotePromptInfo("remote-prompt-1",
                    "执行端等待隔离确认", "继续", "停止", 30)];
                PumpUntil(window, () => remote.HasRemotePrompts);
                Assert.Equal("remote-prompt-1", remote.SelectedRemotePrompt?.RuntimeId);
                remote.AnswerRemoteYesCommand.Execute(null);
                PumpUntil(window, () => answeredYes && !remote.HasRemotePrompts);
                var selectedFlow = Assert.Single(remote.TaskCenter.Flows);
                remote.TaskCenter.StartSelectedPlanCommand.Execute(selectedFlow);
                PumpUntil(window, () => Volatile.Read(ref runStartCalls) == 1
                    && mainVm.PendingCenterOperation(target) is not null
                    && remote.TaskCenter.CanStartSelectedPlan);
                Assert.True(remote.TaskCenter.HasPendingRemoteOperation);
                Assert.NotNull(FindVisualChild<Button>(basic,
                    button => Equals(button.Content, "核对未确认操作")
                        && button.Visibility == Visibility.Visible));
                remote.TaskCenter.StartSelectedPlanCommand.Execute(selectedFlow);
                PumpUntil(window, () => remote.TaskCenter.StatusMessage?.Contains("没有重发", StringComparison.Ordinal) == true);
                Assert.Equal(1, Volatile.Read(ref runStartCalls));
                runOperationState = "ok";
                remote.TaskCenter.ReviewPendingRemoteOperationCommand.Execute(null);
                PumpUntil(window, () => mainVm.PendingCenterOperation(target) is null);
                remote.TaskCenter.StartSelectedPlanCommand.Execute(selectedFlow);
                PumpUntil(window, () => Volatile.Read(ref runStartCalls) == 2
                    && mainVm.PendingCenterOperation(target) is null
                    && remote.TaskCenter.CanStartSelectedPlan);
                var startupPreflight = remote.FlushRemoteWritesAsync();
                PumpUntil(window, () => startupPreflight.IsCompleted);
                Assert.Null(startupPreflight.Result);
                Assert.True(remote.IsNotRunning, "远程启动按钮仍被状态未确认门禁挡住：" + remote.StatusText);
                remote.RunNowCommand.Execute(null);
                try { PumpUntil(window, () => Volatile.Read(ref startupRunCalls) == 1
                    && mainVm.PendingCenterOperation(target) is null && remote.IsNotRunning); }
                catch (Exception ex) { throw new InvalidOperationException(
                    $"启动中心远程执行未完成：calls={startupRunCalls}; status={remote.StatusText}; "
                    + $"pending={mainVm.PendingCenterOperation(target)?.Operation}", ex); }
                Assert.False(shell.IsRunning);
                remote.TaskCenter.DiscardEditCommand.Execute(null);
                remote.TaskCenter.NewFlowCommand.Execute(null);
                PumpUntil(window, () => Volatile.Read(ref createFlowCalls) == 1
                    && mainVm.PendingCenterOperation(target)?.Operation == "task.flow.save");
                Assert.StartsWith("wf-", createdFlowId);
                remote.TaskCenter.NewFlowCommand.Execute(null);
                PumpUntil(window, () => remote.TaskCenter.StatusMessage?.Contains("没有重发", StringComparison.Ordinal) == true);
                Assert.Equal(1, Volatile.Read(ref createFlowCalls));
                remote.StopRemoteRefresh();
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
            finally
            {
                if (window is not null)
                {
                    window.Mistletoe.TaskCenter.StopAutoRefresh();
                    if (typeof(MistletoeViewModel).GetField("_bgiStatusRefresh", BindingFlags.NonPublic | BindingFlags.Instance)
                            ?.GetValue(window.Mistletoe) is System.Windows.Threading.DispatcherTimer timer)
                        timer.Stop();
                    window.Hide();
                }
                if (app is not null && app != Application.Current)
                    app.Shutdown();
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(40));
    }

    private static T? FindVisualChild<T>(DependencyObject? root, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (root is null) return null;
        if (root is T match && (predicate is null || predicate(match))) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindVisualChild(VisualTreeHelper.GetChild(root, i), predicate);
            if (found is not null) return found;
        }
        return null;
    }

    private static void RenderSnapshotIfRequested(FrameworkElement window,
        string environmentVariable = "PRODUCTION_UI_SNAPSHOT_PATH")
    {
        var path = Environment.GetEnvironmentVariable(environmentVariable);
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void PumpUntil(Window window, Func<bool> condition)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(10),
        };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        timer.Tick += (_, _) =>
        {
            if (condition() || DateTime.UtcNow >= deadline)
            {
                timer.Stop();
                frame.Continue = false;
            }
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
        Assert.True(condition(), "任务中心刷新未在限定时间内完成。");
    }
}
