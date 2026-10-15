using System.Diagnostics;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>单项结束不能消耗整批收尾；并发触发者共享实际 IPC 完成及失败。</summary>
[Collection("TaskCenterPipeTransport")]
public sealed class CoordinatedTeardownOnceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ItemEdgeCannotSealWholeBatch_ConcurrentFinalizersAwaitTheSameActualReceipt(bool releaseAccepted)
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须指定隔离 NEXUSBGI_DATA_ROOT，不能写真实用户数据。");
        var previous = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        using var releaseReply = new ManualResetEventSlim(false);
        var requestReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ticket = Guid.NewGuid().ToString("N");
        using var process = Process.GetCurrentProcess();
        var executor = new CommandExecutor(null!, "unused") { BgiRunningProbeOverride = () => true };
        executor.SeedTakeoverStateForTest(ticket,
            new BgiEpoch { ProcessId = process.Id, StartTicksUtc = process.StartTime.ToUniversalTime().Ticks });
        var snapshot = new CoordinatedBatchSnapshot("once-" + Guid.NewGuid().ToString("N"), 17, 0, 0, 1, "running", "")
        {
            Protocol = "hoeing.start.v2", Source = "manual", RequestId = Guid.NewGuid().ToString("N"),
            ParticipantUids = ["self"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            { ["self"] = [new("整批收尾任务", "group")] },
        };
        var batch = new OnlineHoeingBatch(snapshot, "self", new TaskConflictPolicySettings
        { Policy = TaskConflictPolicy.Stop }) { TakeoverTicket = ticket };
        var vm = new MainViewModel();
        Set(vm, "_commandExecutor", executor);
        Set(vm, "_activeBatch", batch);
        var shared = (Dictionary<string, TaskCompletionSource>)typeof(MainViewModel)
            .GetField("_coordinatedTeardownTasks", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Task? first = null;
        Task? second = null;
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.BodyForTest = (operation, _) =>
            {
                if (operation != "task.resume") return null;
                requestReceived.TrySetResult();
                // 只阻塞本夹具管道 worker，让调用方可检查真实 IO 尚未结束时的并发等待。
                if (!releaseReply.Wait(TimeSpan.FromSeconds(10)))
                    return (false, "fixture_timeout", "夹具未释放应答", null);
                return releaseAccepted
                    ? (true, null, null, new { status = "cleared_not_resumed" })
                    : (false, "takeover_conflict", "隔离替身：执行权释放被拒绝", null);
            };
            pipe.Start();

            // 第一项锄地下降沿不代表整个联机批次完成，不能写入整批的 once 守卫。
            Assert.False(batch.CoordinatedSucceeded);
            await FinalizeAsync(vm, batch, ticket);
            Assert.Equal(0, pipe.CountOf("task.resume"));
            Assert.False(shared.ContainsKey(batch.BatchId));
            Assert.Equal(ticket, executor.TakeoverTicketSnapshot);

            batch.CoordinatedSucceeded = true;
            first = FinalizeAsync(vm, batch, ticket);
            await requestReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
            second = FinalizeAsync(vm, batch, ticket);
            Assert.True(shared.ContainsKey(batch.BatchId));
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
            Assert.False(shared[batch.BatchId].Task.IsCompleted);
            Assert.Equal(1, pipe.CountOf("task.resume"));
            Assert.Equal(ticket, executor.TakeoverTicketSnapshot);

            releaseReply.Set();
            var results = await Task.WhenAll(Record.ExceptionAsync(() => first), Record.ExceptionAsync(() => second))
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, pipe.CountOf("task.resume"));
            Assert.True(shared[batch.BatchId].Task.IsCompleted);
            if (releaseAccepted)
            {
                Assert.All(results, Assert.Null);
                Assert.True(first.IsCompletedSuccessfully);
                Assert.True(second.IsCompletedSuccessfully);
                Assert.True(shared[batch.BatchId].Task.IsCompletedSuccessfully);
                Assert.Null(executor.TakeoverTicketSnapshot);
            }
            else
            {
                Assert.All(results, error => Assert.IsType<InvalidOperationException>(error));
                Assert.All(results, error => Assert.Contains("执行权释放被拒绝", error!.Message));
                Assert.Same(results[0], results[1]);
                Assert.True(first.IsFaulted);
                Assert.True(second.IsFaulted);
                Assert.True(shared[batch.BatchId].Task.IsFaulted);
                Assert.Equal(ticket, executor.TakeoverTicketSnapshot);
            }
            Assert.Equal(0, pipe.CountOf("task.start"));
            Assert.Equal(0, pipe.CountOf("ext.task.start"));
        }
        finally
        {
            releaseReply.Set();
            foreach (var task in new[] { first, second }.OfType<Task>())
                try { await task.WaitAsync(TimeSpan.FromSeconds(12)); } catch { }
            IpcClient.PipeNameOverrideForTest = previous;
            batch.Cts.Dispose();
        }
    }

    private static void Set(MainViewModel vm, string name, object value) => typeof(MainViewModel)
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, value);

    private static Task FinalizeAsync(MainViewModel vm, OnlineHoeingBatch batch, string ticket) =>
        (Task)typeof(MainViewModel).GetMethod("ApplyPolicyTeardownOnceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [batch, "联机锄地", false, ticket])!;
}
