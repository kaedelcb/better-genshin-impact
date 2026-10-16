using System.Diagnostics;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class HoeingModeAdmissionTests
{
    [Theory]
    [InlineData(true, "false", false)]
    [InlineData(false, "false", false)]
    [InlineData(true, "true", true)]
    [InlineData(false, "true", true)]
    [InlineData(true, "missing", true)]
    [InlineData(true, "null", true)]
    [InlineData(true, "\"false\"", true)]
    [InlineData(true, "0", true)]
    [InlineData(false, "missing", false)]
    public async Task RealStatusFramesAllowOnlyConfirmedSoloHoeingToYield(bool progressRunning, string rawMode, bool blocked)
    {
        var oldPipe = IpcClient.PipeNameOverrideForTest;
        await using var pipe = new BgiInstancePipeDouble();
        using var process = Process.GetCurrentProcess();
        var id = Guid.NewGuid();
        var status = new JsonObject
        {
            ["bgiEpoch"] = new JsonObject { ["processId"] = process.Id, ["startTicksUtc"] = process.StartTime.ToUniversalTime().Ticks },
            ["slotOccupied"] = true, ["executionIdentityAvailable"] = true,
            ["executionInstanceId"] = id.ToString("N"), ["executionStateRevision"] = 6,
            ["running"] = true, ["autoHoeingRunning"] = progressRunning
        };
        if (rawMode != "missing") status["autoHoeingMultiplayer"] = JsonNode.Parse(rawMode);
        pipe.BodyForTest = (op, _) => op == "task.status" ? (true, null, null, status) : null;
        try
        {
            IpcClient.PipeNameOverrideForTest = pipe.PipeName;
            pipe.Start();
            var executor = new CommandExecutor(null!, "unused");
            var boundary = await executor.CaptureExecutionBoundarySnapshotAsync();
            Assert.True(boundary.Known);
            Assert.Equal(id, boundary.Active!.Value.InstanceId);
            var failure = Record.Exception(() => MainViewModel.EnsureHoeingAllowsCoordinatedStart(boundary));
            if (blocked) Assert.IsType<InvalidOperationException>(failure);
            else Assert.Null(failure);
            if (rawMode == "false") Assert.False(boundary.AutoHoeingMultiplayer);
            else if (rawMode == "true") Assert.True(boundary.AutoHoeingMultiplayer);
            else Assert.Null(boundary.AutoHoeingMultiplayer);
            Assert.Equal(0, pipe.CountOf("task.suspend"));
        }
        finally { IpcClient.PipeNameOverrideForTest = oldPipe; }
    }
}
