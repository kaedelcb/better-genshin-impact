using System.Diagnostics;
using System.IO.Pipes;
using System.Reflection;
using BetterGenshinImpact.Core.Monitor;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.ExternalInterface;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

[Collection("TaskTakeoverIncident")]
public sealed class RestartAfterManualStopTests : IDisposable
{
    private readonly DateTime? _previousCancel = CancellationContext.Instance.LastManualCancelAtUtc;

    private static InstanceIpcEnvelope FreshRequest() => InstanceIpcEnvelope.Request("ext.task.start", new
    {
        executionContractVersion = 1,
        expectedStopVersion = ExecutionScope.StopVersionNow,
        bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc },
        workflowRunId = Guid.NewGuid().ToString("N"), nodeId = "new-start", iteration = 0,
        idempotencyKey = Guid.NewGuid().ToString("N"), expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
        configName = "isolated-test", expectedConfigRevision = "isolated-revision", retainUntilAcknowledged = true,
    });

    [Theory]
    [InlineData("fresh", true)]
    [InlineData("old-version", false)]
    [InlineData("old-process", false)]
    [InlineData("expired", false)]
    [InlineData("missing-identity", false)]
    [InlineData("missing-config-revision", false)]
    [InlineData("missing-fence", false)]
    [InlineData("missing-contract", false)]
    [InlineData("string-contract", false)]
    [InlineData("future-contract", false)]
    [InlineData("v2", false)]
    [InlineData("suspend", false)]
    public void StopThenNewStart_OnlyCompleteCurrentContractIsExempt(string change, bool allowed)
    {
        CancellationContext.Instance.ManualCancel();
        var request = FreshRequest();
        var data = request.Data!;
        switch (change)
        {
            case "old-version": data["expectedStopVersion"] = ExecutionScope.StopVersionNow - 1; break;
            case "old-process": data["bgiEpoch"]!["processId"] = -1; break;
            case "expired": data["expiresAtUtc"] = DateTimeOffset.UtcNow.AddSeconds(-1); break;
            case "missing-identity": data.Remove("workflowRunId"); break;
            case "missing-config-revision": data.Remove("expectedConfigRevision"); break;
            case "missing-fence": data.Remove("expectedStopVersion"); break;
            case "missing-contract": data.Remove("executionContractVersion"); break;
            case "string-contract": data["executionContractVersion"] = "1"; break;
            case "future-contract": data["executionContractVersion"] = 2; break;
            case "v2": request = InstanceIpcEnvelope.Request("task.start", data); break;
            case "suspend": request = InstanceIpcEnvelope.Request("task.suspend", data); break;
        }
        var rejection = InstanceRequestHandler.CheckManualStopCooldown(request, "ext");
        if (allowed) Assert.Null(rejection);
        else Assert.Equal("manual_stop_cooldown", rejection?.ErrorCode);
        Assert.True(CancellationContext.Instance.IsInManualStopCooldown(TimeSpan.FromSeconds(30), out _));
    }

    [Fact]
    public async Task FreshStart_WaitsForOldExecutionExit_AndDuplicateDoesNotExecuteTwice()
    {
        using var old = ExecutionScope.Start(new(JobKind.Group, "old", JobSource.Ui));
        CancellationContext.Instance.ManualCancel();
        var request = FreshRequest();
        Assert.Null(InstanceRequestHandler.CheckManualStopCooldown(request, "ext"));
        var fence = request.Data!["expectedStopVersion"]!.Value<long>();
        Assert.Throws<ExecutionNotStartedException>(() => ExecutionScope.Start(new(JobKind.Group,
            "not-exited", JobSource.Ext, JobId: Guid.NewGuid(), ExpectedStopVersion: fence)));
        var slotFree = false;
        var executed = 0;
        using var coordinator = new BgiTaskCoordinator(() => Volatile.Read(ref slotFree), (_, _) => { },
            NullLogger.Instance, slotPollInterval: TimeSpan.FromMilliseconds(10), slotWaitTimeout: TimeSpan.FromSeconds(5));
        var submission = new BgiTaskCoordinator.TaskSubmission(0, "fresh", null, 0, (handle, _) =>
        {
            using var current = ExecutionScope.Start(new(JobKind.Group, "fresh", JobSource.Ext,
                JobId: handle, ExpectedStopVersion: fence));
            Interlocked.Increment(ref executed);
            return Task.FromResult(false);
        }) { IdempotencyKey = Guid.NewGuid().ToString("N"), PayloadFingerprint = "same-payload", ExpectedStopVersion = fence,
            RetainUntilAcknowledged = true };
        var first = coordinator.Submit(submission);
        var duplicate = coordinator.Submit(submission);
        Assert.Equal(first.TaskHandle, duplicate.TaskHandle);
        await Task.Delay(50);
        Assert.Equal(0, Volatile.Read(ref executed));
        old.Dispose();
        Volatile.Write(ref slotFree, true);
        await WaitUntil(() => Volatile.Read(ref executed) == 1);
        Assert.Equal(first.TaskHandle, coordinator.Submit(submission).TaskHandle);
        await Task.Delay(50);
        Assert.Equal(1, Volatile.Read(ref executed));
    }

    [Fact]
    public void StopAfterAdmissionCheck_StillRejectsOldRequestAtExecutionRoot()
    {
        CancellationContext.Instance.ManualCancel();
        var request = FreshRequest();
        Assert.Null(InstanceRequestHandler.CheckManualStopCooldown(request, "ext"));
        var fence = request.Data!["expectedStopVersion"]!.Value<long>();
        CancellationContext.Instance.ManualCancel();
        Assert.Equal("manual_stop_fence", ExecutionRequestContract.Validate(request)?.ErrorCode);
        Assert.Throws<ExecutionNotStartedException>(() => ExecutionScope.Start(new(JobKind.Group,
            "stale", JobSource.Ext, JobId: Guid.NewGuid(), ExpectedStopVersion: fence)));
        Assert.Null(InstanceRequestHandler.CheckManualStopCooldown(FreshRequest(), "ext"));
    }

    [Fact]
    public async Task RealPipe_LegacyCooldownReturnsBoundPreAcceptanceProof()
    {
        CancellationContext.Instance.ManualCancel();
        var context = new InstanceContext(BetterGiInstanceType.ChildSession,
            "restart-stop-proof-" + Guid.NewGuid().ToString("N"), -1);
        using var bootstrap = (InstanceBootstrap)Activator.CreateInstance(typeof(InstanceBootstrap),
            BindingFlags.Instance | BindingFlags.NonPublic, null, [context, null, null], null)!;
        await using var service = new InstanceService(bootstrap, new EmptyServices(),
            new RawInputMonitor(NullLogger<RawInputMonitor>.Instance), null!, NullLogger<InstanceService>.Instance);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await service.StartAsync(deadline.Token);
        using var client = new NamedPipeClientStream(".", context.SessionControlPipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(2000, deadline.Token);
        var request = InstanceIpcEnvelope.Request("ext.task.start", new { configName = "not-executed",
            idempotencyKey = Guid.NewGuid().ToString("N") });
        await InstanceIpcProtocol.WriteJsonAsync(client, request, deadline.Token);
        var response = InstanceIpcProtocol.ReadJson((await InstanceIpcProtocol.ReadFrameAsync(client, deadline.Token))!.Value);
        Assert.False(response.Success);
        Assert.Equal("manual_stop_cooldown", response.ErrorCode);
        Assert.Equal("server_rejected_before_acceptance", response.Data!["executionDisposition"]!.Value<string>());
        Assert.False(response.Data["accepted"]!.Value<bool>());
        Assert.True(JToken.DeepEquals(request.Data, response.Data["request"]));
        Assert.Equal(JobRegistry.CurrentEpoch.ProcessId, response.Data["bgiEpoch"]!["processId"]!.Value<int>());
        await service.StopAsync(deadline.Token);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }

    private sealed class EmptyServices : IServiceProvider { public object? GetService(Type type) => null; }

    public void Dispose()
    {
        CancellationContext.Instance.Set();
        typeof(CancellationContext).GetProperty(nameof(CancellationContext.LastManualCancelAtUtc))!
            .SetValue(CancellationContext.Instance, _previousCancel);
    }
}
