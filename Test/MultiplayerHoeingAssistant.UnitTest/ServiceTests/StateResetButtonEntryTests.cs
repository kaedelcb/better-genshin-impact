using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 生产窗口的实际成员卡按钮、确认框和复位编排。仅替换 BGI 物理重建与就绪查询，
/// SDK 经过私有管道；不启动真实 BGI、不访问真实 User。须在独立 testhost 中运行此类。
/// </summary>
[Collection("TaskCenterPipeTransport")]
public sealed class StateResetButtonEntryTests
{
    [Fact]
    public async Task ProductionButton_ResetsBeforeWaitingForOldBatch_AndCanStartAgainWithoutRestartingAssistant()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须在启动 testhost 前指定隔离 NEXUSBGI_DATA_ROOT。");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                Assert.Null(Application.Current); // WPF Application 不能在同一 testhost 中重复创建。
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try { await RunEntryAsync(); finished.TrySetResult(); }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "StateResetButtonEntry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(55));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离重置窗口 Dispatcher 未退出。");
    }

    private static async Task RunEntryAsync()
    {
        const string uid = "100000001";
        var previousIpc = IpcClient.PipeNameOverrideForTest;
        var previousExt = BgiExternalClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        pipe.Start();
        IpcClient.PipeNameOverrideForTest = pipe.PipeName;
        BgiExternalClient.PipeNameOverrideForTest = pipe.PipeName;
        using var external = new BgiExternalClient();
        MainWindow? window = null;
        MainViewModel? vm = null;
        DispatcherTimer? confirmTimer = null;
        var physicalEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldBatchExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialogConfirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var physicalResets = 0;
        var confirmations = 0;
        try
        {
            Assert.Equal(BgiExternalLinkState.Ready, await external.StartAsync());
            vm = new MainViewModel();
            var isolatedBgi = Path.Combine(AssistantDataDirectory.Root, "nonexistent-bgi", "BetterGI.exe");
            Set(vm, "_config", new AssistConfig
            {
                PlayerUid = uid, PlayerName = "隔离重置入口", BgiPath = isolatedBgi,
                ObserverMode = false, StandaloneMode = true, DisclaimerAccepted = true,
            });
            var oldBatch = Batch("old-reset-batch", 21, uid);
            oldBatch.RunTask = oldBatchExited.Task;
            Set(vm, "_activeBatch", oldBatch);
            var executor = new CommandExecutor(null!, isolatedBgi, () => external, logger: vm.AddLog)
            {
                ResetProcessOverrideForTest = () =>
                {
                    Assert.False(oldBatchExited.Task.IsCompleted,
                        "旧批次只能由物理复位释放，不能靠预先完成任务绕过等待。");
                    Interlocked.Increment(ref physicalResets);
                    var blockedStart = (Task<CommandResult>)typeof(MainViewModel)
                        .GetMethod("ExecuteCommandWithHoeingStopAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .Invoke(vm, [new RemoteCommand { Cmd = "start_group", Params = new() { ["groupName"] = "重置中不得启动" } }])!;
                    Assert.True(blockedStart.IsCompletedSuccessfully);
                    Assert.Equal("reset_in_progress", blockedStart.Result.ErrorCode);
                    physicalEntered.TrySetResult();
                    oldBatchExited.TrySetResult();
                    return Task.FromResult(true);
                },
                ResetStatusQueryOverrideForTest = _ => Task.FromResult<IpcResponse?>(Ready()),
                ResetReadyTimeoutForTest = TimeSpan.FromSeconds(2),
            };
            executor.SeedTakeoverStateForTest("old-reset-ticket", new BgiEpoch { ProcessId = 10, StartTicksUtc = 100 });
            Set(vm, "_commandExecutor", executor);
            Set(vm, "_externalClient", external);
            var self = new MemberViewModel
            {
                PlayerUid = uid, PlayerName = "隔离重置入口", IsSelf = true, Online = true, OnlineReady = true,
            };
            vm.Members.Add(self);
            vm.CurrentPage = AppPage.Home;
            window = new MainWindow(vm)
            {
                ShowActivated = false, ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -20000, Top = -20000, Width = 1180, Height = 860,
            };
            Application.Current.MainWindow = window;
            window.Pet.Shutdown();
            window.Show();
            await window.Dispatcher.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var resetButton = Assert.Single(Descendants(window).OfType<Button>()
                .Where(button => Equals(button.Content, "重置状态") && ReferenceEquals(button.DataContext, self)));
            Assert.True(resetButton.IsVisible && resetButton.ActualWidth > 0 && resetButton.ActualHeight > 0);
            Assert.NotNull(resetButton.Command);
            Assert.Same(self, resetButton.CommandParameter);

            confirmTimer = new DispatcherTimer(DispatcherPriority.Background)
            { Interval = TimeSpan.FromMilliseconds(30) };
            confirmTimer.Tick += (_, _) =>
            {
                try
                {
                    var dialogs = Application.Current.Windows.Cast<Window>()
                        .Where(candidate => candidate.Title == "重置状态" && candidate.IsVisible).ToArray();
                    if (dialogs.Length == 0) return;
                    var dialog = Assert.Single(dialogs);
                    var confirm = Assert.Single(Descendants(dialog).OfType<Button>()
                        .Where(button => Equals(button.Content, "确定") || Equals(button.Content, "确认")));
                    Assert.True(confirm.IsVisible && confirm.IsEnabled);
                    confirmations++;
                    confirmTimer.Stop();
                    ((IInvokeProvider)new ButtonAutomationPeer(confirm)).Invoke();
                    dialogConfirmed.TrySetResult();
                }
                catch (Exception error)
                {
                    confirmTimer.Stop();
                    dialogConfirmed.TrySetException(error);
                    foreach (var dialog in Application.Current.Windows.Cast<Window>()
                        .Where(candidate => candidate.Title == "重置状态").ToArray()) dialog.Close();
                }
            };
            confirmTimer.Start();
            ((IInvokeProvider)new ButtonAutomationPeer(resetButton)).Invoke();

            await dialogConfirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // 原故障链先等旧批次 10 秒；本夹具的旧任务只有物理复位进入后才可能退出。
            await physicalEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => vm.CommandLogsText.Contains("本机执行状态已收口", StringComparison.Ordinal)
                && Field<SemaphoreSlim>(vm, "_resetRequestGate").CurrentCount == 1,
                TimeSpan.FromSeconds(20), () => vm.CommandLogsText);
            Assert.Equal(1, confirmations);
            Assert.Equal(1, physicalResets);
            Assert.True(oldBatch.CleanupConfirmed);
            Assert.False(oldBatch.IsAlive);
            Assert.True(oldBatch.RunTask.IsCompletedSuccessfully);
            Assert.Null(Field<OnlineHoeingBatch?>(vm, "_activeBatch"));
            Assert.Null(executor.TakeoverTicketSnapshot);
            Assert.False(self.OnlineReady);
            Assert.Contains("可以重新启动联机", vm.CommandLogsText, StringComparison.Ordinal);
            Assert.DoesNotContain("未完全收口", vm.CommandLogsText, StringComparison.Ordinal);
            var host = vm.TaskCenterHost;
            Assert.True(host.CanOpenOnlineSendAfterBatch);
            var sendGate = Field<TaskCenterOnlineSendGate>(host, "_onlineSendGate");
            Assert.False(sendGate.IsClosed);
            await using (await sendGate.EnterAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(2)))
            {
                var started = await external.SubmitTaskStartAsync(null, "重置后的普通一条龙", 0, 22);
                Assert.True(started.Success);
            }
            var sent = Assert.Single(pipe.Received.Where(request => request.Operation == "ext.task.start")).Data!.Value;
            Assert.False(sent.TryGetProperty("coordinatedHoeing", out _));
            Assert.False(sent.TryGetProperty("hoeingBatchId", out _));
            Assert.False(sent.TryGetProperty("hoeingParticipantUids", out _));

            // 同一 resetId 的重传必须复用结果，不能在新任务开始后再次重建或取消新批次。
            var handled = Field<Dictionary<string, Task<CommandResult>>>(vm, "_handledStateResets");
            var first = Assert.Single(handled);
            var firstResult = await first.Value;
            Assert.Equal("success", firstResult.Status);
            var newBatch = Batch("new-reset-batch", 22, uid);
            Set(vm, "_activeBatch", newBatch);
            var duplicate = (Task<CommandResult>)typeof(MainViewModel)
                .GetMethod("ResetLocalExecutionStateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, [first.Key, oldBatch.BatchId])!;
            Assert.Same(first.Value, duplicate);
            Assert.Same(firstResult, await duplicate);
            Assert.Equal(1, physicalResets);
            Assert.Same(newBatch, Field<OnlineHoeingBatch?>(vm, "_activeBatch"));
            Assert.False(newBatch.IsCancellationRequested);
            newBatch.MarkSettled();
            Set(vm, "_activeBatch", null);
        }
        finally
        {
            confirmTimer?.Stop();
            oldBatchExited.TrySetResult();
            if (window != null)
            {
                window.Pet.Shutdown();
                window.Mistletoe.TaskCenter.StopAutoRefresh();
                StopTimer(window.Mistletoe, "_bgiStatusRefresh");
                StopTimer(window.Mistletoe, "_saveDebounce");
            }
            if (vm != null)
            {
                Set(vm, "_disposing", true);
                await vm.TaskCenterHost.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(12));
            }
            window?.Close();
            IpcClient.PipeNameOverrideForTest = previousIpc;
            BgiExternalClient.PipeNameOverrideForTest = previousExt;
        }
    }

    private static OnlineHoeingBatch Batch(string id, int generation, string uid) => new(
        new CoordinatedBatchSnapshot(id, generation, 0, 0, 1, "running", "")
        {
            Protocol = "hoeing.start.v2", ParticipantUids = [uid],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            { [uid] = [new("隔离联机线路", "group")] },
        }, uid, new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Stop });

    private static IpcResponse Ready()
    {
        using var process = Process.GetCurrentProcess();
        return new() { Success = true, Data = JsonSerializer.Serialize(new
        {
            executionIdle = true, running = false, slotOccupied = false,
            takeoverActive = false, hasSuspendedTaskContext = false, queueDepth = 0,
            currentTaskHandle = (string?)null,
            bgiEpoch = new { processId = process.Id, startTicksUtc = process.StartTime.ToUniversalTime().Ticks },
        }) };
    }

    private static async Task UntilAsync(Func<bool> condition, TimeSpan timeout, Func<string> detail)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < timeout, "等待生产重置命令完成超时：" + detail());
            await Task.Delay(30);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }

    private static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
    private static void Set(object instance, string name, object? value)
        => instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
    private static void StopTimer(object instance, string name)
        => (instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(instance) as DispatcherTimer)?.Stop();
}
