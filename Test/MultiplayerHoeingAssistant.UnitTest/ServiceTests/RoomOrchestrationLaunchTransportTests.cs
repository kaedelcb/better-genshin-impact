using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// Private pipe + real admission ledger: the switch callback can release its lock
// after receipt, while a retained result still owns normal external-start responsibility.
[Collection("TaskCenterPipeTransport")]
public sealed class RoomOrchestrationLaunchTransportTests : IAsyncLifetime
{
    private readonly BgiInstancePipeDouble _pipe = new() { StateContinuationForTest = true };
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "room-successor-" + Guid.NewGuid().ToString("N"));
    private string? _previousPipe;
    private BgiExternalClient _client = null!;
    private BgiEpoch Epoch => _client.ServerEpoch!;
    private object WireEpoch => new { processId = Epoch.ProcessId, startTicksUtc = Epoch.StartTicksUtc };

    public async Task InitializeAsync()
    {
        _previousPipe = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = _pipe.PipeName;
        _pipe.Start();
        _client = new BgiExternalClient();
        Assert.Equal(BgiExternalLinkState.Ready, await _client.StartAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _pipe.DisposeAsync();
        BgiExternalClient.PipeNameOverrideForTest = _previousPipe;
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private RoomOrchestrationLaunch Launch(long stop = 0, string type = "onedragon") => new("intent-" + Guid.NewGuid().ToString("N"), new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskName = "乙房准备", SpecifiedTaskType = type,
        SpecifiedStartMode = "beginning", SpecifiedConfigRevision = "r1", SpecifiedTaskPriority = 15,
    }, Epoch, stop, Stopwatch.GetTimestamp(), "confirmed-B");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartReceiptOrUnknownCanBeReconciledByOriginalKeyWithoutReplaying(bool loseReceipt)
    {
        var launch = Launch();
        var key = CommandExecutor.RoomOrchestrationTaskKey(launch.IntentId);
        var job = Guid.NewGuid().ToString("N");
        var terminal = false;
        _pipe.BodyForTest = (op, _) => op switch
        {
            "ext.config.describe" => (true, null, null, new { bgiEpoch = WireEpoch, configRevision = "r1",
                tasks = new[] { new { taskId = "node-1", enabled = true } } }),
            "ext.task.start" => loseReceipt ? (true, null, null, new { status = "queued" })
                : (true, null, null, new { status = "queued", taskHandle = job }),
            "ext.task.queueStatus" => (true, null, null, new { bgiEpoch = WireEpoch, idempotencyKey = key,
                taskHandle = job, status = Volatile.Read(ref terminal) ? "completed" : "running",
                executionExitConfirmed = Volatile.Read(ref terminal) }),
            _ => null,
        };
        var host = new TaskCenterHost(Path.Combine(_directory, "flows"), Path.Combine(_directory, "runs"),
            Path.Combine(_directory, "catalog.json"), () => _client, null, null, () => (true, null),
            admissionWired: true, admissionSeams: new() { Epoch = WorkflowStopAuthority.Epoch(Epoch), Occupied = false, FactsUnknown = false });
        host.FlowRuntime.IsRoomIdentityCurrent = identity => identity == launch.RoomIdentity;
        try
        {
            var executor = new CommandExecutor(null!, "unused", () => _client, roomOrchestrationAdmission: host.AdmitExternalStartAsync);
            var result = await executor.StartRoomOrchestrationTaskAsync(launch, _ => Task.CompletedTask, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(loseReceipt ? "failed" : "success", result.Status);
            if (loseReceipt) Assert.Equal("result_unknown", result.ErrorCode);
            else Assert.Equal(job, result.JobId);
            Assert.False(result.IsTerminal);
            Assert.Equal(1, _pipe.CountOf("ext.task.start"));
            var duplicate = await executor.StartRoomOrchestrationTaskAsync(launch, _ => Task.CompletedTask, CancellationToken.None);
            Assert.NotEqual("success", duplicate.Status);
            Assert.Equal(1, _pipe.CountOf("ext.task.start"));

            Volatile.Write(ref terminal, true);
            var settled = await _client.QueryTaskQueueStatusByKeyAsync(key);
            Assert.True(settled!.ExecutionExitConfirmed);
            Assert.True(await host.ReconcileRoomOrchestrationTaskAsync(launch.IntentId, Epoch, settled, CancellationToken.None));
            Assert.True(await host.ReconcileRoomOrchestrationTaskAsync(launch.IntentId, Epoch, settled, CancellationToken.None));
            Assert.Equal(1, _pipe.CountOf("ext.task.start"));
        }
        finally { await host.ShutdownAsync(); }
    }
}
