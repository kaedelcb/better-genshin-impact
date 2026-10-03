using BetterGenshinImpact.Service.Execution;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.ExternalInterface;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Execution;

[Collection("TaskTakeoverIncident")]
public class ManualStopFenceContractTests
{
    private static InstanceIpcEnvelope Request(string operation) => new()
    {
        Operation = operation,
        Data = JObject.FromObject(new
        {
            executionContractVersion = 1,
            bgiEpoch = new { processId = JobRegistry.CurrentEpoch.ProcessId, startTicksUtc = JobRegistry.CurrentEpoch.StartTicksUtc },
            idempotencyKey = Guid.NewGuid().ToString("N"), expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            workflowRunId = Guid.NewGuid().ToString("N"), nodeId = "node", iteration = 0,
            configName = "config", expectedConfigRevision = "revision", uid = "123", action = "closeGame",
        }),
    };

    [Theory]
    [InlineData("ext.task.start")]
    [InlineData("ext.prerequisite.account")]
    [InlineData("ext.prerequisite.redeemCode")]
    [InlineData("ext.terminal.completionAction")]
    public void StrictRequest_MissingStopFence_IsRejected(string operation)
        => Assert.Equal("invalid_request", ExecutionRequestContract.Validate(Request(operation))?.ErrorCode);

    [Theory]
    [InlineData("ext.task.start")]
    [InlineData("ext.prerequisite.account")]
    [InlineData("ext.terminal.completionAction")]
    public void QueuedRequest_ManualStopAfterFreeze_IsRejectedWithoutRefreshingVersion(string operation)
    {
        var request = Request(operation);
        request.Data!["expectedStopVersion"] = ExecutionScope.StopVersionNow;
        Assert.Null(ExecutionRequestContract.Validate(request));
        ExecutionScope.StopActive(true);
        Assert.Equal("manual_stop_fence", ExecutionRequestContract.Validate(request)?.ErrorCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    public void SuppliedFence_MustBeNonnegativeIntegerToken(string value)
    {
        var request = Request("ext.task.start");
        request.Data!["expectedStopVersion"] = value == "0" ? new JValue(value) : JToken.Parse(value);
        Assert.Equal("invalid_request", ExecutionRequestContract.Validate(request)?.ErrorCode);
    }

    [Fact]
    public void LegacyRequest_WithoutFence_RetainsCompatibility()
    {
        var request = new InstanceIpcEnvelope { Operation = "task.start", Data = new JObject { ["groupName"] = "group" } };
        Assert.Null(ExecutionRequestContract.Validate(request));
    }

    [Fact]
    public void QueryAndManualStop_UseOneAtomicVersionTimestampAuthority()
    {
        var before = ExecutionScope.GetManualStopFence();
        ExecutionScope.StopActive(false);
        Assert.Equal(before, ExecutionScope.GetManualStopFence());
        var timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        ExecutionScope.StopActive(true);
        var stopped = ExecutionScope.GetManualStopFence();
        Assert.Equal(before.StopVersion + 1, stopped.StopVersion);
        Assert.True(stopped.LastManualStopTimestamp >= timestamp);
        var request = new InstanceIpcEnvelope { Operation = ExternalInterfaceOperations.ManualStopFence };
        Assert.True(ExternalInterfaceQueryPlane.TryDispatch(null!, null!, request, out var response));
        Assert.Equal(stopped.StopVersion, response.Data!["stopVersion"]!.Value<long>());
        Assert.Equal(stopped.LastManualStopTimestamp, response.Data["lastManualStopTimestamp"]!.Value<long?>());
        Assert.Equal(JobRegistry.CurrentEpoch.ProcessId, response.Data["bgiEpoch"]!["processId"]!.Value<int>());
    }

    [Fact]
    public void FinalStart_RejectsOriginalVersionEvenWhenQueueChecksAreBypassed()
    {
        var frozen = ExecutionScope.StopVersionNow;
        ExecutionScope.StopActive(true);
        Assert.Throws<ExecutionNotStartedException>(() =>
        {
            using var unexpected = ExecutionScope.Start(new JobDescriptor(JobKind.Terminal, "stale-final-start", JobSource.Ext,
                JobId: Guid.NewGuid(), ExpectedStopVersion: frozen));
        });
        Assert.False(ExecutionScope.HasActive);
    }

    [Fact]
    public void BusyRootBeforeAdmission_IsTypedNoExecutionForTheRejectedJob()
    {
        using var active = ExecutionScope.Start(new JobDescriptor(JobKind.Group, "active", JobSource.Ext));
        var rejectedJob = Guid.NewGuid();
        var rejected = Assert.Throws<ExecutionNotStartedException>(() => ExecutionScope.Start(
            new JobDescriptor(JobKind.Prerequisite, "rejected", JobSource.Ext, JobId: rejectedJob,
                ExpectedStopVersion: ExecutionScope.StopVersionNow)));
        Assert.Equal(rejectedJob, rejected.JobId);
        Assert.Equal(active, ExecutionScope.Current);
    }
}
