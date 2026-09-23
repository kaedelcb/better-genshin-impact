using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class MainViewModelTaskStatusParsingTests
{
    [Theory]
    [InlineData("{\"running\":true,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900},\"stateRevision\":3}", true, true)]
    [InlineData("{\"running\":false,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900},\"stateRevision\":3}", false, true)]
    [InlineData("{}", false, false)]
    [InlineData("{\"running\":\"false\",\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900}}", false, false)]
    [InlineData("{\"running\":false}", false, false)]
    public void ParseTaskStatusData_SeparatesIdleFromUnavailable(
        string json, bool expectedRunning, bool expectedAvailable)
    {
        using var document = JsonDocument.Parse(json);

        var result = MainViewModel.ParseTaskStatusData(document.RootElement);

        Assert.Equal(expectedRunning, result.BgiRunning);
        Assert.Equal(expectedAvailable, result.TaskStatusAvailable);
        if (expectedAvailable)
        {
            Assert.Equal("9:900", result.TaskStatusBgiEpoch);
            Assert.Equal(3L, result.StateRevision);
        }
    }

    [Fact]
    public void ParseTaskStatusData_RequiresCoherentVersionedExecutionIdentity()
    {
        var instanceId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var validJson = $$"""
            {"running":true,"bgiEpoch":{"processId":9,"startTicksUtc":900},
             "executionIdentityAvailable":true,"executionInstanceId":"{{instanceId:N}}",
             "executionStateRevision":7,"executionRunId":"{{runId:N}}","executionJobId":"{{jobId:N}}",
             "executionKind":"OneDragon","executionSource":"Ui","executionName":"一条龙",
             "executionStopRequested":false}
            """;
        using var validDocument = JsonDocument.Parse(validJson);

        var valid = MainViewModel.ParseTaskStatusData(validDocument.RootElement);

        Assert.True(valid.ExecutionIdentityAvailable);
        Assert.Equal(instanceId, valid.ExecutionInstanceId);
        Assert.Equal(7L, valid.ExecutionStateRevision);
        Assert.Equal(runId, valid.ExecutionRunId);
        Assert.Equal(jobId, valid.ExecutionJobId);
        Assert.Equal("OneDragon", valid.ExecutionKind);
        Assert.Equal("Ui", valid.ExecutionSource);
        Assert.Equal("一条龙", valid.ExecutionName);
        Assert.False(valid.ExecutionStopRequested);

        using var malformedDocument = JsonDocument.Parse(validJson.Replace(
            "\"executionName\":\"一条龙\",", "", StringComparison.Ordinal));
        var malformed = MainViewModel.ParseTaskStatusData(malformedDocument.RootElement);
        Assert.False(malformed.ExecutionIdentityAvailable);
        Assert.Null(malformed.ExecutionInstanceId);
    }

    [Fact]
    public void LocalExecutionIdentity_IsNotSerializedIntoRoomStatus()
    {
        var instanceId = Guid.NewGuid();
        var status = new ControlStatus
        {
            TaskRunning = true,
            CurrentExecution = new TaskExecutionIdentitySnapshot(
                instanceId, 1, Guid.NewGuid(), null, "OneDragon", "Ui", "一条龙", false)
        };

        var gatewayJson = JsonSerializer.Serialize(status, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(gatewayJson);

        Assert.False(document.RootElement.TryGetProperty("currentExecution", out _));
    }

    [Fact]
    public void ControlStatus_HidesExecutionIdentityAfterStatusFreshnessExpires()
    {
        var execution = new TaskExecutionIdentitySnapshot(
            Guid.NewGuid(), 1, Guid.NewGuid(), null, "OneDragon", "Ui", "一条龙", false);
        var fresh = new ControlStatus
        {
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "9:900",
            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow,
            CurrentExecution = execution
        };
        var stale = new ControlStatus
        {
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "9:900",
            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow.Add(ControlStatus.TaskStatusFreshnessWindow.Negate()).AddSeconds(-1),
            CurrentExecution = execution
        };
        var expiresAfterPublication = new ControlStatus
        {
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "9:900",
            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow,
            CurrentExecution = execution
        };

        Assert.Same(execution, fresh.CurrentExecution);
        Assert.Null(fresh.GetFreshCurrentExecution(DateTimeOffset.UtcNow, "8:800"));
        Assert.Null(stale.CurrentExecution);
        expiresAfterPublication.TaskStatusObservedAtUtc = DateTimeOffset.UtcNow
            .Add(ControlStatus.TaskStatusFreshnessWindow.Negate()).AddSeconds(-1);
        Assert.Null(expiresAfterPublication.CurrentExecution);
        expiresAfterPublication.TaskStatusObservedAtUtc = DateTimeOffset.UtcNow;
        Assert.Null(expiresAfterPublication.CurrentExecution);
    }

    [Fact]
    public void ReadOnlyStatusEpochMustMatchVerifiedPipeProcess()
    {
        using var matchingDocument = JsonDocument.Parse(
            "{\"running\":true,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900}} ");
        using var wrongPidDocument = JsonDocument.Parse(
            "{\"running\":true,\"bgiEpoch\":{\"processId\":8,\"startTicksUtc\":900}} ");
        using var wrongStartDocument = JsonDocument.Parse(
            "{\"running\":true,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":901}} ");

        var matching = MainViewModel.ParseTaskStatusData(matchingDocument.RootElement);
        var wrongPid = MainViewModel.ParseTaskStatusData(wrongPidDocument.RootElement);
        var wrongStart = MainViewModel.ParseTaskStatusData(wrongStartDocument.RootElement);

        Assert.True(MainViewModel.IsStatusEpochForProcess(matching, 9, 900));
        Assert.False(MainViewModel.IsStatusEpochForProcess(wrongPid, 9, 900));
        Assert.False(MainViewModel.IsStatusEpochForProcess(wrongStart, 9, 900));
    }

    [Fact]
    public void V2PingIdentity_RequiresSessionPidAndExactProcessStartTicks()
    {
        using var validPing = JsonDocument.Parse(
            "{\"windowsSessionId\":1,\"processId\":9,\"processStartTicks\":900}");
        using var missingStart = JsonDocument.Parse(
            "{\"windowsSessionId\":1,\"processId\":9}");
        using var invalidPid = JsonDocument.Parse(
            "{\"windowsSessionId\":1,\"processId\":0,\"processStartTicks\":900}");

        Assert.True(IpcClient.TryReadRemoteProcessIdentity(validPing.RootElement,
            out var sessionId, out var processId, out var startTicks));
        Assert.Equal(1, sessionId);
        Assert.Equal(9, processId);
        Assert.Equal(900L, startTicks);
        Assert.False(IpcClient.TryReadRemoteProcessIdentity(missingStart.RootElement, out _, out _, out _));
        Assert.False(IpcClient.TryReadRemoteProcessIdentity(invalidPid.RootElement, out _, out _, out _));
    }

    [Fact]
    public void V2TaskStatusEpoch_MustMatchPingProcessIdentity()
    {
        const string validStatus = "{\"running\":true,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900}}";
        const string wrongPid = "{\"running\":true,\"bgiEpoch\":{\"processId\":8,\"startTicksUtc\":900}}";
        const string wrongStart = "{\"running\":true,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":901}}";
        const string missingEpoch = "{\"running\":false}";

        Assert.True(IpcClient.IsTaskStatusFromRemoteProcess(validStatus, 9, 900));
        Assert.False(IpcClient.IsTaskStatusFromRemoteProcess(wrongPid, 9, 900));
        Assert.False(IpcClient.IsTaskStatusFromRemoteProcess(wrongStart, 9, 900));
        Assert.False(IpcClient.IsTaskStatusFromRemoteProcess(missingEpoch, 9, 900));
        Assert.False(IpcClient.IsTaskStatusFromRemoteProcess(validStatus, null, 900));
        Assert.False(IpcClient.IsTaskStatusFromRemoteProcess(validStatus, 9, null));
    }

    [Fact]
    public void StatusPublication_RechecksFreshnessAndCurrentExternalEpoch()
    {
        var now = DateTimeOffset.UtcNow;
        var status = new ControlStatus
        {
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "9:900",
            TaskStatusObservedAtUtc = now
        };

        Assert.True(MainViewModel.IsTaskStatusPublishable(status, now, false, null, null));
        Assert.True(MainViewModel.IsTaskStatusPublishable(status, now, true, "9:900", "9:900"));
        Assert.False(MainViewModel.IsTaskStatusPublishable(status, now, true, "9:900", "8:800"));
        Assert.False(MainViewModel.IsTaskStatusPublishable(status, now, true, "9:900", null));
        Assert.False(MainViewModel.IsTaskStatusPublishable(status, now.AddSeconds(21), false, null, null));
    }

    [Theory]
    [InlineData("9:900", "ticket-a", "ticket-a", true)]
    [InlineData("10:1000", "ticket-a", "ticket-a", false)]
    [InlineData("9:900", "ticket-b", "ticket-a", false)]
    [InlineData("9:900", "ticket-a", "ticket-b", false)]
    public void DelayedHoeingTeardown_RequiresOriginalEpochAndTicket(
        string expectedEpoch, string expectedTicket, string currentTicket, bool allowed)
    {
        using var document = JsonDocument.Parse(
            "{\"running\":false,\"bgiEpoch\":{\"processId\":9,\"startTicksUtc\":900}," +
            "\"autoHoeingRunning\":false,\"hasSuspendedTaskContext\":true," +
            "\"suspendedTakeoverTicket\":\"ticket-a\"}");

        Assert.Equal(allowed, MainViewModel.IsDelayedHoeingTeardownAuthorized(
            document.RootElement, expectedEpoch, expectedTicket, currentTicket));
    }
}
