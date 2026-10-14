using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class ProductionStartupAuthorizationTests
{
    [Fact]
    public async Task Checkbox_ManualEarlyEnd_Restart_NewBgiAndStop_UseTheProductionStartupEntry()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "此检查必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            MistletoeViewModel? shell = null;
            using var client = new BgiExternalClient();
            var boundary = new StartupBoundary();
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                _ = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var directory = Path.Combine(AssistantDataDirectory.Root, "startup-entry");
                var store = new StartupFlowStore();
                var sentinel = Path.Combine(AssistantDataDirectory.Root, "startup-entry-preserved.txt");
                File.WriteAllText(sentinel, "existing user material stays unchanged");
                store.SaveConfirmed(new() { DelaySeconds = 0, Steps = [new() { Kind = StartupStepKinds.EndFlow }] });

                (Window, MistletoeViewModel) Open()
                {
                    var main = new MainViewModel();
                    var host = new TaskCenterHost(Path.Combine(directory, "flows"), Path.Combine(directory, "runs"),
                        Path.Combine(directory, "catalog.json"), () => client, _ => { },
                        (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary, new NoopPrerequisite(), new NoopTerminal()),
                        () => (true, null), () => true);
                    typeof(MainViewModel).GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, host);
                    var model = new MistletoeViewModel(main);
                    var page = new MistletoePage { DataContext = model };
                    var native = new Window { Content = page, Width = 1200, Height = 820, Left = -16000, Top = -16000,
                        ShowActivated = false, Title = "隔离自动启动恢复验证" };
                    native.Show();
                    native.UpdateLayout();
                    return (native, model);
                }

                void Close()
                {
                    shell!.TaskCenter.StopAutoRefresh();
                    ((DispatcherTimer)typeof(MistletoeViewModel).GetField("_bgiStatusRefresh",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!).Stop();
                    window!.Close();
                }

                (window, shell) = Open();
                var checkbox = FindCheckbox(window, shell);
                Assert.NotNull(checkbox);
                ((IToggleProvider)new ToggleButtonAutomationPeer(checkbox!)).Toggle();
                PumpUntil(() => shell.FlowEnabled);
                Assert.NotNull(store.LoadChecked().Config.AutomaticAuthorization);
                Assert.Null(store.LoadChecked().Config.AutomaticStopAuthority); // BGI 尚未就绪仍已保存开启意图。
                shell.RunNowCommand.Execute(null);
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Close();

                // 助手进程状态全新，BGI 后就绪；无需再勾选或依赖某个动作节点。
                SetClient(client, "42:99");
                boundary.Epoch = "42:99";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Assert.Equal("42:99", store.LoadChecked().Config.AutomaticStopAuthority!.Epoch);
                Close();

                // BGI 正常换进程，新启动许可只绑定新运行，不改写旧触发器授权。
                SetClient(client, "43:100");
                boundary.Epoch = "43:100";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Assert.Equal("43:100", store.LoadChecked().Config.AutomaticStopAuthority!.Epoch);
                Close();

                boundary.Version++;
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("执行中止"));
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Assert.Equal(0, boundary.Submissions);
                Assert.Equal("existing user material stays unchanged", File.ReadAllText(sentinel));
                // 界面不能把保存失败显示为成功关闭；原配置和许可保留。
                var configFile = Path.Combine(AssistantDataDirectory.Root, "startup-flow.json");
                var preserved = File.ReadAllBytes(configFile);
                using (File.Open(configFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    ((IToggleProvider)new ToggleButtonAutomationPeer(FindCheckbox(window, shell)!)).Toggle();
                    Assert.True(shell.FlowEnabled);
                    Assert.Contains("保存失败", shell.StatusText);
                }
                Assert.Equal(preserved, File.ReadAllBytes(configFile));
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                shell?.TaskCenter.StopAutoRefresh();
                if (shell is not null)
                    ((DispatcherTimer)typeof(MistletoeViewModel).GetField("_bgiStatusRefresh",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!).Stop();
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static CheckBox? FindCheckbox(DependencyObject root, MistletoeViewModel model)
    {
        if (root is CheckBox box && ReferenceEquals(box.DataContext, model)
            && BindingOperations.GetBindingExpression(box, System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)
                ?.ParentBinding.Path.Path == nameof(MistletoeViewModel.FlowEnabled)) return box;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindCheckbox(VisualTreeHelper.GetChild(root, i), model) is { } match) return match;
        return null;
    }

    private static void PumpUntil(Func<bool> predicate)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (!predicate() && DateTime.UtcNow < until)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Assert.True(predicate(), "隔离启动入口未到达预期终态");
    }

    private static void SetClient(BgiExternalClient client, string epoch)
    {
        var parts = epoch.Split(':');
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.State))!.SetValue(client, BgiExternalLinkState.Ready);
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.ServerEpoch))!.SetValue(client,
            new BgiEpoch { ProcessId = int.Parse(parts[0]), StartTicksUtc = long.Parse(parts[1]) });
    }

    private sealed class StartupBoundary : IWorkflowExecutionBoundary
    {
        public string Epoch { get; set; } = "42:99";
        public long Version { get; set; }
        public int Submissions { get; private set; }
        public bool SingleNativeSupported => false;
        public bool RequiresStopAuthority => true;
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string id, long timestamp, CancellationToken ct)
            => Task.FromResult<WorkflowStopAuthorityRecord?>(new(Epoch, Version, id, timestamp, Stopwatch.Frequency));
        public Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
            => Task.FromResult<bool?>(authority.Epoch == Epoch && authority.Version == Version);
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        { Submissions++; throw new InvalidOperationException("此入口检查不得提交任务"); }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("此入口检查不执行 BGI 任务");
    }

    private sealed class NoopPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class NoopTerminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("isolated-noop"));
    }
}
