using System.Diagnostics;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>本次开关经现有绑定快照到生产收尾，核对实际 IPC 是否恢复原任务。</summary>
[Collection("TaskCenterPipeTransport")]
public sealed class QuickHoeingCompletionExecutionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckedChoiceUsesBoundResumeAndUncheckedChoiceOnlyReleasesContext(bool useActions)
    {
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        var (vm, _) = QuickHoeingCompletionChoiceTests.Configured("resume");
        var start = QuickHoeingCompletionChoiceTests.Start(useActions);
        var ticket = Guid.NewGuid().ToString("N");
        var batch = new OnlineHoeingBatch(start, "self", vm.SnapshotBatchCompletionPolicy(start))
        { CoordinatedSucceeded = true, TakeoverTicket = ticket };
        using var process = Process.GetCurrentProcess();
        var epoch = new BgiEpoch { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks };
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket, epoch);
        typeof(MainViewModel).GetField("_commandExecutor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, executor);
        typeof(MainViewModel).GetField("_activeBatch", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, batch);
        var root = Guid.NewGuid();
        var attempt = Guid.NewGuid();
        var resumed = false;
        pipe.BodyForTest = (operation, data) =>
        {
            if (operation == "task.resume")
            {
                resumed = !data!.Value.GetProperty("cancel").GetBoolean();
                return (true, null, null, new { status = resumed ? "resumed" : "cleared_not_resumed", attemptId = attempt.ToString("D") });
            }
            if (operation == "task.status")
                return (true, null, null, new
                {
                    running = resumed, hasSuspendedTaskContext = !resumed,
                    suspendedTaskType = "configGroup", suspendedTaskName = "原任务",
                    bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                    executionInstanceId = root.ToString("N"), executionJobId = attempt.ToString("N"), executionStateRevision = 1L
                });
            return null;
        };
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            var completed = (Task)typeof(MainViewModel).GetMethod("ApplyPolicyTeardownOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(vm, new object[] { batch, "联机锄地", false, ticket })!;
            await completed.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(useActions, resumed);
            var release = Assert.Single(pipe.Received.Where(r => r.Operation == "task.resume")).Data!.Value;
            Assert.Equal(!useActions, release.GetProperty("cancel").GetBoolean());
            Assert.Equal(ticket, release.GetProperty("takeoverTicket").GetString());
            Assert.Null(executor.TakeoverTicketSnapshot);
        }
        finally { batch.MarkSettled(); IpcClient.PipeNameOverrideForTest = previous; }
    }
}
