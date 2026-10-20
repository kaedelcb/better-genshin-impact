using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using BetterGenshinImpact.Core.Mask;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Runtime;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.ViewModel.Pages;
using Mistletoe.Shared;
using Moq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

[Collection("GlobalExecutionStopWatermark")]
public sealed class HomeTriggerStopTests
{
    // Run alone in a fresh testhost: the actual generated command uses a real WPF dispatcher,
    // but neither constructor, runtime binding, User settings nor input devices are activated.
    [Fact]
    public async Task StopTriggerCommand_RevokesOriginalRecoveryBeforeRuntimeStop_AndLateFailureStaysCancelled()
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    try
                    {
                        await VerifyStopEntryAsync();
                        finished.TrySetResult();
                    }
                    catch (Exception error) { finished.TrySetException(error); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception error) { finished.TrySetException(error); }
        }) { IsBackground = true, Name = "IsolatedHomeTriggerStop" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "隔离停止入口 Dispatcher 未退出。");
    }

    private static async Task VerifyStopEntryAsync()
    {
        Assert.False(ExecutionScope.HasActive);
        var context = CancellationContext.Instance;
        var previousStopTime = context.LastManualCancelAtUtc;
        var runtime = (GameRuntimeService)RuntimeHelpers.GetUninitializedObject(typeof(GameRuntimeService));
        using var startLock = new SemaphoreSlim(1, 1);
        typeof(GameRuntimeService).GetField("_startLock", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, startLock);
        typeof(GameRuntimeService).GetField("_stopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, Task.CompletedTask);
        Assert.Null(runtime.Current);
        var home = (HomePageViewModel)RuntimeHelpers.GetUninitializedObject(typeof(HomePageViewModel));
        typeof(HomePageViewModel).GetField("_gameRuntimeService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(home, runtime);
        typeof(HomePageViewModel).GetField("_stopButtonEnabled", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(home, true);
        typeof(HomePageViewModel).GetField("_bannerImageService", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(home, Mock.Of<IBannerImageService>());
        typeof(HomePageViewModel).GetField("_maskWindowHost", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(home, Mock.Of<IMaskWindowHost>());
        PreemptionGate.Disarm();
        context.Set();
        using var signal = CoordinatedStopSignal.Create();
        var originalVersion = ExecutionScope.StopVersionNow;
        try
        {
            using (var root = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "隔离恢复任务", JobSource.Resume,
                ExpectedStopVersion: originalVersion)))
            {
                Assert.True(ExecutionScope.RegisterCoordinatedStopSignal(signal.Name, originalVersion, out var error), error);
                await runtime.StopAsync(); // Ordinary capture/runtime teardown does not revoke manual recovery authority.
                Assert.Equal(originalVersion, ExecutionScope.StopVersionNow);
                Assert.False(signal.IsStopped);
                Assert.False(root.Token.IsCancellationRequested);

                // Closing/restarting the application retains its prior non-manual teardown semantics.
                typeof(HomePageViewModel).GetMethod("OnClosed", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(home, null);
                await (Task)typeof(GameRuntimeService).GetField("_stopTask", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(runtime)!;
                Assert.Equal(originalVersion, ExecutionScope.StopVersionNow);
                Assert.False(signal.IsStopped);
                Assert.False(root.Token.IsCancellationRequested);

                Assert.True(home.StopTriggerCommand.CanExecute(null));
                await startLock.WaitAsync(); // Hold the real runtime stop boundary, without binding a window.
                Task stop;
                try
                {
                    stop = home.StopTriggerCommand.ExecuteAsync(null);
                    Assert.False(stop.IsCompleted);
                    Assert.Equal(originalVersion + 1, ExecutionScope.StopVersionNow);
                    Assert.NotNull(ExecutionScope.GetManualStopFence().LastManualStopTimestamp);
                    Assert.True(signal.IsStopped);
                    Assert.True(root.Token.IsCancellationRequested);
                    Assert.True(context.IsManualStop);
                    Assert.Equal(TaskRunResult.Cancelled, root.Result);
                    root.Observe(TaskRunResult.Failed); // A late capture failure cannot turn a user's stop into a retryable error.
                    Assert.Equal(TaskRunResult.Cancelled, root.Result);
                    Assert.Equal(1, root.FailureCount);
                }
                finally { startLock.Release(); }
                await stop;
            }
            Assert.Throws<ExecutionNotStartedException>(() =>
            {
                using var unexpected = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "原恢复请求", JobSource.Resume,
                    JobId: Guid.NewGuid(), ExpectedStopVersion: originalVersion));
            });
            Assert.False(ExecutionScope.HasActive);
        }
        finally
        {
            PreemptionGate.Disarm();
            context.Set();
            typeof(CancellationContext).GetProperty(nameof(CancellationContext.LastManualCancelAtUtc))!
                .SetValue(context, previousStopTime);
        }
    }
}
