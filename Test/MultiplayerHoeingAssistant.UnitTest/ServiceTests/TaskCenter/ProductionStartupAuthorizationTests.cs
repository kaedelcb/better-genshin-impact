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

                // 与真实故障相同：尚未绑定 BGI 的自动启动进入倒计时，再切监控端。
                var delayed = store.LoadChecked().Config;
                delayed.DelaySeconds = 5;
                store.SaveConfirmed(delayed);
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => shell.IsRunning && shell.StatusText.Contains("5 秒后"));
                var switchedSource = ConfiguredSource(shell);
                Assert.Null(switchedSource.Authority);
                SetObserverMode(main!, false); // 普通设置刷新会重复发同名通知，不应取消倒计时。
                Assert.True(shell.IsRunning);
                Assert.False(switchedSource.Revoked);
                SetObserverMode(main!, true);
                Invoke(shell, "RetryPendingAutomaticSource"); // 同 2 秒刷新回调使用的生产入口。
                PumpUntil(() => !shell.IsRunning);
                Assert.True(switchedSource.Revoked);
                Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Assert.True(shell.FlowEnabled);
                SetObserverMode(main!, false);
                Assert.False(shell.IsRunning); // 切回执行端不能重新启动刚取消的旧链。
                AssertOldSourceCannotRefresh(switchedSource);
                Close();
                delayed = store.LoadChecked().Config;
                delayed.DelaySeconds = 0;
                store.SaveConfirmed(delayed);

                // 助手进程状态全新，BGI 后就绪；无需再勾选或依赖某个动作节点。
                SetClient(client, "42:99");
                boundary.Epoch = "42:99";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Assert.Equal("42:99", store.LoadChecked().Config.AutomaticStopAuthority!.Epoch);
                Close();

                // 模式已改变但通知尚未派发的竞态仍会进入宿主守卫；不能因此写成永久停止。
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                var racingSource = ConfiguredSource(shell);
                Assert.NotNull(racingSource.Authority);
                SetObserverMode(main!, true, notify: false);
                var modeError = Assert.ThrowsAnyAsync<InvalidOperationException>(() => racingSource.GetOrCheckAsync(
                    (source, required, ct) => RefreshSourceAsync(shell, source, required, ct), false, default))
                    .GetAwaiter().GetResult();
                Assert.Equal("StartupModeUnavailableException", modeError.GetType().Name);
                Assert.True(racingSource.Revoked);
                Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                SetObserverMode(main!, false, notify: false);
                AssertOldSourceCannotRefresh(racingSource);
                Close();
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Close();

                // 已开始的查询在切角色或正常退出后才返回：成功与失败都不能污染许可或原基线。
                foreach (var applicationExit in new[] { false, true })
                foreach (var failInspection in new[] { false, true })
                {
                    (window, shell) = Open();
                    shell.OnAppInitialized();
                    PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                    var originalAuthority = store.LoadChecked().Config.AutomaticStopAuthority;
                    var pendingSource = ConfiguredSource(shell);
                    var inspection = new TaskCompletionSource<bool?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    boundary.InspectionCompletion = inspection;
                    var calls = boundary.InspectionCalls;
                    var pendingCheck = pendingSource.GetOrCheckAsync(
                        (source, required, ct) => RefreshSourceAsync(shell, source, required, ct), false, default);
                    PumpUntil(() => boundary.InspectionCalls > calls);
                    Assert.False(pendingCheck.IsCompleted);
                    if (applicationExit) Invoke(shell, "PrepareForApplicationExit");
                    else SetObserverMode(main!, true);
                    Assert.True(pendingSource.Revoked);
                    boundary.InspectionCompletion = null;
                    if (failInspection) inspection.SetException(new IOException("隔离在途查询失败"));
                    else inspection.SetResult(true);
                    PumpUntil(() => pendingCheck.IsCompleted);
                    var queryError = Record.Exception(() => pendingCheck.GetAwaiter().GetResult());
                    if (failInspection) Assert.IsType<IOException>(queryError);
                    else Assert.IsAssignableFrom<OperationCanceledException>(queryError);
                    Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                    Assert.Equal(originalAuthority, store.LoadChecked().Config.AutomaticStopAuthority);
                    if (!applicationExit) SetObserverMode(main!, false);
                    AssertOldSourceCannotRefresh(pendingSource);
                    Close();
                    (window, shell) = Open();
                    shell.OnAppInitialized();
                    PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                    Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                    Close();
                }

                // BGI 正常换进程，新启动许可只绑定新运行，不改写旧触发器授权。
                SetClient(client, "43:100");
                boundary.Epoch = "43:100";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                Assert.Equal("43:100", store.LoadChecked().Config.AutomaticStopAuthority!.Epoch);
                Close();

                // 查询异常、超时和未知都只废弃本次来源；持久许可和旧基线保留，旧回调仍不能复活。
                foreach (var failureKind in new[] { "io", "timeout", "unknown", "cancelled" })
                foreach (var recovery in new[] { "auto", "manual" })
                {
                    var savedAuthority = store.LoadChecked().Config.AutomaticStopAuthority;
                    boundary.InspectionFailure = failureKind switch
                    {
                        "io" => new IOException("隔离停止权威查询暂时失败"),
                        "timeout" => new TimeoutException("隔离停止权威查询超时"),
                        "cancelled" => new OperationCanceledException("隔离查询内部超时取消"),
                        _ => null,
                    };
                    boundary.InspectionUnknown = failureKind == "unknown";
                    (window, shell) = Open();
                    shell.OnAppInitialized();
                    PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("执行中止"));
                    Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                    Assert.Equal(savedAuthority, store.LoadChecked().Config.AutomaticStopAuthority);
                    var failedSource = ConfiguredSource(shell);
                    Assert.True(failedSource.Revoked);
                    AssertOldSourceCannotRefresh(failedSource);
                    boundary.InspectionFailure = null;
                    boundary.InspectionUnknown = false;
                    if (recovery == "auto")
                    {
                        Close();
                        (window, shell) = Open();
                        shell.OnAppInitialized(); // 故障解除后，新助手自动复核旧基线，无需先手动清许可。
                    }
                    else
                    {
                        shell.RunNowCommand.Execute(null); // 同一助手的显式手动操作使用新来源。
                    }
                    PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                    Assert.NotEqual(failedSource.IntentId, ConfiguredSource(shell).IntentId);
                    AssertOldSourceCannotRefresh(failedSource);
                    Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                    Assert.Equal(0, boundary.Submissions);
                    Close();
                }

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
                Close();
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Close();
                SetClient(client, "44:101");
                boundary.Epoch = "44:101";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);

                // 耐久停止记录也必须拦住新实例；其原始记录保留。
                shell.RunNowCommand.Execute(null);
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                var runs = new RunStore(Path.Combine(directory, "runs"));
                var authority = store.LoadChecked().Config.AutomaticStopAuthority!;
                var stopped = runs.CreateRun("stopped-startup-source", "rev", stopAuthority: authority);
                stopped.StopRequested = true;
                stopped.State = WorkflowRunState.Cancelled;
                runs.Update(stopped);
                Close();
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("执行中止"));
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Close();
                SetClient(client, "45:102");
                boundary.Epoch = "45:102";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Assert.True(runs.Load(stopped.RunId)!.StopRequested);

                // 全停显式撤销已冻结来源的持久许可，换 BGI 后也不能自行恢复。
                shell.RunNowCommand.Execute(null);
                PumpUntil(() => !shell.IsRunning && shell.StatusText.StartsWith("上次执行完成"));
                var stoppedByUser = ConfiguredSource(shell);
                Assert.NotNull(stoppedByUser.Authority);
                var beforeFailedStopSave = File.ReadAllBytes(configFile);
                using (File.Open(configFile, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var saveError = Assert.Throws<TargetInvocationException>(() => Invoke(shell, "StopAllAutomation"));
                    Assert.True(saveError.InnerException is IOException or UnauthorizedAccessException);
                    Assert.True(stoppedByUser.Revoked); // 落盘失败也先封住本进程旧回调。
                }
                Assert.Equal(beforeFailedStopSave, File.ReadAllBytes(configFile));
                Invoke(shell, "StopAllAutomation");
                Assert.True(stoppedByUser.Revoked);
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                AssertOldSourceCannotRefresh(stoppedByUser);
                Close();
                SetClient(client, "46:103");
                boundary.Epoch = "46:103";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));

                // BGI 未就绪、还没有冻结基线时，全停也必须撤销保存的开启意图。
                typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.State))!
                    .SetValue(client, BgiExternalLinkState.Down);
                var enable = FindCheckbox(window, shell)!;
                ((IToggleProvider)new ToggleButtonAutomationPeer(enable)).Toggle();
                ((IToggleProvider)new ToggleButtonAutomationPeer(enable)).Toggle();
                var unfrozen = ConfiguredSource(shell);
                Assert.Null(unfrozen.Authority);
                Assert.False(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Invoke(shell, "StopAllAutomation");
                Assert.True(unfrozen.Revoked);
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Close();
                SetClient(client, "47:104");
                boundary.Epoch = "47:104";
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Close();
                (window, shell) = Open(observer: true);
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));

                // 监控端的本地来源没有执行授权记录，全停仍要写入明确的持久拒绝标记。
                var localEnable = FindCheckbox(window, shell)!;
                ((IToggleProvider)new ToggleButtonAutomationPeer(localEnable)).Toggle();
                ((IToggleProvider)new ToggleButtonAutomationPeer(localEnable)).Toggle();
                Assert.True(ConfiguredSource(shell).LocalOnly);
                Assert.Null(store.LoadChecked().Config.AutomaticAuthorization);
                Invoke(shell, "StopAllAutomation");
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Close();
                (window, shell) = Open(observer: true);
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Close();

                // 兼容旧配置仅有已保存基线、没有许可对象；全停不能因对象缺失漏记停止。
                var legacy = store.LoadChecked().Config;
                legacy.AutomaticAuthorization = null;
                legacy.AutomaticStopAuthority = authority;
                store.SaveConfirmed(legacy);
                (window, shell) = Open(observer: true);
                Invoke(shell, "StopAllAutomation");
                Assert.True(store.LoadChecked().Config.AutomaticAuthorization!.Revoked);
                Close();
                (window, shell) = Open(observer: true);
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Close();
                (window, shell) = Open();
                shell.OnAppInitialized();
                PumpUntil(() => !shell.IsRunning && shell.StatusText.Contains("自动启动许可已停止"));
                Assert.Equal(0, boundary.Submissions);
                Assert.Equal("existing user material stays unchanged", File.ReadAllText(sentinel));
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
