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
                        "task.flow.save" => SaveRemote(request),
                        _ => RemoteCenterReply.Rejected("测试通道不支持：" + request.Operation)
                    };
                    reply.TargetInstanceId = "isolated-instance";
                    return Task.FromResult<RemoteCommand?>(RemoteCenterProtocol.Reply(command,
                        "isolated-target", "隔离执行端", reply));

                    RemoteCenterReply SaveRemote(RemoteCenterRequest request)
                    {
                        if (request.ExpectedRevision != remoteRevision)
                            return RemoteCenterReply.Rejected("执行端修订已变化");
                        remoteDocument = JsonSerializer.Deserialize<WorkflowDocument>(request.PayloadJson!)!;
                        remoteRevision = "revision-b";
                        savedRemote.TrySetResult(remoteDocument.Name);
                        return RemoteCenterReply.Ok(new { remoteDocument.WorkflowId }, remoteRevision);
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
                        Config = new StartupFlowConfig(), StatusText = "隔离远程状态"
                    });
                window.SetValue(MainWindow.ActiveMistletoeProperty, remote);
                remote.SelectedTabIndex = 1;
                window.UpdateLayout();
                Assert.Same(basic, FindVisualChild<BasicTaskCenterView>(mistletoe));
                Assert.Equal(Visibility.Visible, basic.Visibility);
                Assert.Same(remote.TaskCenter, basic.DataContext);
                Assert.NotSame(shell.TaskCenter, basic.DataContext);
                Assert.Same(target, ((ComboBox)mistletoe!.FindName("RemoteCenterTargetChoice")).SelectedItem);
                PumpUntil(window, () => remote.TaskCenter.Editing?.Draft.WorkflowId == remoteDocument.WorkflowId);
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
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
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

    private static void RenderSnapshotIfRequested(Window window,
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
