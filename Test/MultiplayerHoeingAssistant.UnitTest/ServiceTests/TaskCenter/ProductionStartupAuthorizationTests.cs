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
    public async Task Checkbox_ModeSwitch_Restart_AndAllStopSources_UseTheProductionStartupEntry()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "此检查必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Window? window = null;
            MistletoeViewModel? shell = null;
            MainViewModel? main = null;
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

                (Window, MistletoeViewModel) Open(bool observer = false)
                {
                    var owner = new MainViewModel();
                    main = owner;
                    typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(owner, new AssistConfig { ObserverMode = observer });
                    var host = new TaskCenterHost(Path.Combine(directory, "flows"), Path.Combine(directory, "runs"),
                        Path.Combine(directory, "catalog.json"), () => client, _ => { },
                        (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary, new NoopPrerequisite(), new NoopTerminal()),
                        () => (true, null), () => owner.IsExecutorMode);
                    typeof(MainViewModel).GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(owner, host);
                    var model = new MistletoeViewModel(owner);
                    var page = new MistletoePage { DataContext = model };
                    var native = new Window { Content = page, Width = 1200, Height = 820, Left = -16000, Top = -16000,
                        ShowActivated = false, Title = "隔离自动启动恢复验证" };
                    native.Show();
                    native.UpdateLayout();
                    return (native, model);
                }

                void Close()
                {
                    shell!.PrepareForApplicationExit();
                    shell.TaskCenter.StopAutoRefresh();
                    ((DispatcherTimer)typeof(MistletoeViewModel).GetField("_bgiStatusRefresh",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!).Stop();
                    window!.Close();
                }

                // 执行端、监控端：旧版错误禁用和旧停止基线均不能覆盖用户的 Enabled。
                foreach (var observer in new[] { false, true })
                {
                    store.SaveConfirmed(new() { Enabled = true, DelaySeconds = 0,
                        AutomaticAuthorization = new("legacy-error", 123, true),
                        AutomaticStopAuthority = new("42:99", 99, "legacy-error", 123, Stopwatch.Frequency),
                        Steps = [new() { Kind = StartupStepKinds.EndFlow }] });
                    SetClient(client, "42:99");
                    (window, shell) = Open(observer);
                    shell.OnAppInitialized();
                    PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                    Assert.True(shell.FlowEnabled);
                    Assert.NotNull(FindCheckbox(window, shell));
                    Assert.True(store.LoadChecked().Config.AutomaticAuthorization?.Revoked != true);
                    Assert.Equal(observer, ConfiguredSource(shell).LocalOnly);
                    Close();
                }

                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                var source = ConfiguredSource(shell);
                var timestamp = source.IntentTimestamp;
                foreach (var failure in new Exception[] { new IOException("暂时断线"), new TimeoutException("查询超时") })
                {
                    boundary.InspectionFailure = failure;
                    var recovery = source.GetOrCheckAsync((intent, required, token) =>
                        RefreshSourceAsync(shell, intent, required, token), true, default);
                    PumpUntil(() => shell.StatusText.StartsWith("等待自动恢复"));
                    Assert.False(recovery.IsCompleted);
                    Assert.False(source.Revoked);
                    boundary.InspectionFailure = null;
                    PumpUntil(() => recovery.IsCompleted);
                    Assert.True(recovery.IsCompletedSuccessfully);
                    Assert.Same(source, ConfiguredSource(shell)); // 同一窗口、同一步骤恢复，无手动运行。
                    Assert.Equal(timestamp, source.IntentTimestamp);
                }

                // 原进程确定退出后，当前启动步骤可接续新进程；意图时间不变。
                SetClient(client, "43:100");
                boundary.Epoch = "43:100";
                var changedProcess = source.GetOrCheckAsync((intent, required, token) =>
                    RefreshSourceAsync(shell, intent, required, token), true, default);
                PumpUntil(() => changedProcess.IsCompleted);
                Assert.True(changedProcess.IsCompletedSuccessfully);
                Assert.Equal("43:100", source.Authority!.Epoch);
                Assert.Equal(timestamp, source.IntentTimestamp);

                // 故障等待期间发生 F11：连接恢复后停止当前意图，不能重签后立即启动。
                boundary.InspectionUnknown = true;
                var pending = source.GetOrCheckAsync((intent, required, token) =>
                    RefreshSourceAsync(shell, intent, required, token), true, default);
                PumpUntil(() => shell.StatusText.StartsWith("等待自动恢复"));
                boundary.Version++;
                boundary.InspectionUnknown = false;
                PumpUntil(() => pending.IsCompleted);
                Assert.False(pending.IsCompletedSuccessfully);
                Assert.True(source.StoppedByUser);
                AssertOldSourceCannotRefresh(source);
                Assert.True(store.LoadChecked().Config.Enabled);
                Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Assert.Equal(0, boundary.Submissions);
                Close();
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Assert.NotEqual(source.IntentId, ConfiguredSource(shell).IntentId);
                Close();

                // F11 撤下当前次数，但每天重复的下一次到点计划仍保留。
                store.SaveConfirmed(new() { Enabled = true, DelaySeconds = 0,
                    Steps = [new() { Kind = StartupStepKinds.TimerTrigger, TriggerTime = "23:59", RepeatDaily = true,
                        FireSteps = [new() { Kind = StartupStepKinds.EndFlow }] }] });
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => shell.ArmedTimers.Count == 1 && !shell.IsRunning);
                var oldTimer = shell.ArmedTimers.Single();
                boundary.Version++;
                PumpUntil(() => shell.ArmedTimers.Count == 1 && !ReferenceEquals(oldTimer, shell.ArmedTimers.Single()));
                Assert.True(oldTimer.SourceIntent!.StoppedByUser);
                Assert.True(shell.ArmedTimers.Single().NextFireAt > DateTime.Now);
                Assert.False(shell.ArmedTimers.Single().SourceIntent!.Revoked);
                Invoke(shell, "StopAllAutomation");
                Assert.True(store.LoadChecked().Config.Enabled);
                Assert.Single(shell.ArmedTimers);
                shell.FlowEnabled = false;
                PumpUntil(() => shell.ArmedTimers.Count == 0);
                Close();

                // 明确关闭仍保持关闭，重启不能替用户开启。
                (window, shell) = Open();
                shell.OnAppInitialized();
                Assert.False(shell.IsRunning);
                Assert.False(shell.FlowEnabled);
                Assert.Empty(shell.ArmedTimers);
                Assert.Equal("existing user material stays unchanged", File.ReadAllText(sentinel));
                Assert.Equal(0, boundary.Submissions);
                Close();

                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                shell?.PrepareForApplicationExit();
                shell?.TaskCenter.StopAutoRefresh();
                if (shell is not null)
                    ((DispatcherTimer)typeof(MistletoeViewModel).GetField("_bgiStatusRefresh",
                        BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell)!).Stop();
                window?.Close();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
    }

    private static StartupSourceIntent ConfiguredSource(MistletoeViewModel model)
        => (StartupSourceIntent)typeof(MistletoeViewModel).GetField("_configuredAutoSource",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;

    private static object? Invoke(MistletoeViewModel model, string method)
        => typeof(MistletoeViewModel).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(model, null);

    private static Task<WorkflowStopAuthorityRecord?> RefreshSourceAsync(MistletoeViewModel model,
        StartupSourceIntent source, bool required, CancellationToken ct)
        => (Task<WorkflowStopAuthorityRecord?>)typeof(MistletoeViewModel).GetMethod("RefreshStartupSourceAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(model, [source, required, ct])!;

    private static void SetObserverMode(MainViewModel model, bool observer, bool notify = true)
    {
        var config = (AssistConfig)typeof(MainViewModel).GetField("_config",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(model)!;
        config.ObserverMode = observer;
        if (!notify) return;
        var changed = typeof(MainViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        changed.Invoke(model, [nameof(MainViewModel.IsObserverMode)]);
        changed.Invoke(model, [nameof(MainViewModel.IsExecutorMode)]);
    }

    private static void AssertOldSourceCannotRefresh(StartupSourceIntent source)
    {
        var refreshed = false;
        Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetOrCheckAsync((_, _, _) =>
        {
            refreshed = true;
            return Task.FromResult<WorkflowStopAuthorityRecord?>(null);
        }, false, default)).GetAwaiter().GetResult();
        Assert.False(refreshed);
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
        public Exception? InspectionFailure { get; set; }
        public bool InspectionUnknown { get; set; }
        public TaskCompletionSource<bool?>? InspectionCompletion { get; set; }
        public int InspectionCalls { get; private set; }
        public bool SingleNativeSupported => false;
        public bool RequiresStopAuthority => true;
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string id, long timestamp, CancellationToken ct)
            => Task.FromResult<WorkflowStopAuthorityRecord?>(new(Epoch, Version, id, timestamp, Stopwatch.Frequency));
        public Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
        {
            InspectionCalls++;
            return InspectionCompletion?.Task
                ?? (InspectionFailure is { } failure ? Task.FromException<bool?>(failure)
                    : Task.FromResult<bool?>(InspectionUnknown ? null : authority.Epoch == Epoch && authority.Version == Version));
        }
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
