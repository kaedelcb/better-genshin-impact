using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class StartupSourceStopAuthorityTests
{
    [Theory]
    [InlineData("timer")]
    [InlineData("watchdog")]
    [InlineData("log")]
    public async Task RepeatCallback_AfterVersionChangeNeverCreatesFreshPermission(string kind)
    {
        var authority = new WorkflowStopAuthorityRecord("42:99", 7, "original-mount", 123, System.Diagnostics.Stopwatch.Frequency);
        var source = StartupSourceIntent.Inherit(authority);
        var current = 7L;
        var requests = new List<StartupHandoffRequest>();
        var runner = new StartupFlowRunner(
            (_, _) => Task.FromResult(new CommandResult { Status = "success" }),
            (request, _) => { requests.Add(request); return Task.FromResult(StartupHandoffResult.Accepted("run", null)); },
            _ => { }, (_, _) => Task.FromResult((true, "ok")), _ => { }, () => null, _ => { },
            sourceAuthorityProvider: (intent, _, _) => Task.FromResult<WorkflowStopAuthorityRecord?>(
                new("42:99", current, intent.IntentId, intent.IntentTimestamp, System.Diagnostics.Stopwatch.Frequency)));
        var trigger = new StartupTriggerInfo(kind, "mount", "2026-10-03") { SourceIntent = source };
        var steps = new[] { new StartupStep { Kind = StartupStepKinds.EnterTaskCenter, TaskCenterFlowId = "wf" } };
        await runner.RunAsync(steps, default, trigger);
        current = 8;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync(steps, default, trigger));
        Assert.Equal(authority, Assert.Single(requests).StopAuthority);
        Assert.True(source.Revoked);
        Assert.Equal(authority, source.Authority);
    }

    [Fact]
    public async Task DelayedReady_KeepsOriginalIntentTimeAndRejectsInterveningStop()
    {
        var source = new StartupSourceIntent("before-ready", 123);
        Assert.Null(await source.GetOrCheckAsync((_, _, _) => Task.FromResult<WorkflowStopAuthorityRecord?>(null), false, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetOrCheckAsync((intent, _, _) =>
            Task.FromResult<WorkflowStopAuthorityRecord?>(WorkflowStopAuthority.FromExplicitIntent(
                new ManualStopFence("42:99", 7, 200, System.Diagnostics.Stopwatch.Frequency), intent.IntentId, intent.IntentTimestamp)), true, default));
        Assert.True(source.Revoked);
        Assert.Null(source.Authority);
        Assert.Equal(123, source.IntentTimestamp);
    }

    [Fact]
    public async Task UnknownFrozenSource_IsRevokedAndCannotRecaptureAfterConnectionReturns()
    {
        var authority = new WorkflowStopAuthorityRecord("42:99", 7, "mount", 123, System.Diagnostics.Stopwatch.Frequency);
        var source = StartupSourceIntent.Inherit(authority);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.GetOrCheckAsync(
            (_, _, _) => Task.FromResult<WorkflowStopAuthorityRecord?>(null), false, default));
        var called = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.GetOrCheckAsync((_, _, _) =>
        {
            called = true;
            return Task.FromResult<WorkflowStopAuthorityRecord?>(authority);
        }, true, default));
        Assert.False(called);
        Assert.Equal(authority, source.Authority);
    }

    [Fact]
    public void AutomaticProfileSerialization_PreservesTheFrozenAuthority()
    {
        var authority = new WorkflowStopAuthorityRecord("42:99", 7, "explicit-source", 123, System.Diagnostics.Stopwatch.Frequency);
        var config = new StartupFlowConfig { Enabled = true, AutomaticStopAuthority = authority };
        var restored = JsonSerializer.Deserialize<StartupFlowConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal(authority, restored.AutomaticStopAuthority);
    }

    [Fact]
    public void StoppedSource_CannotCreateAnotherRunUnderTheSameOriginalAuthority()
    {
        var dir = Path.Combine(Path.GetTempPath(), "source-stop-" + Guid.NewGuid().ToString("N"));
        try
        {
            var runs = new RunStore(dir);
            var authority = new WorkflowStopAuthorityRecord("42:99", 7, "original-source", 123, System.Diagnostics.Stopwatch.Frequency);
            var old = runs.CreateRun("wf", "rev", stopAuthority: authority);
            old.StopRequested = true;
            old.State = WorkflowRunState.Cancelled;
            runs.Update(old);
            Assert.Throws<InvalidOperationException>(() => runs.CreateRun("wf", "rev", stopAuthority: authority));
            Assert.Single(runs.List());
            var fresh = runs.CreateRun("wf", "rev", stopAuthority: authority with { IntentId = "new-explicit-intent", IntentTimestamp = 124 });
            Assert.Equal(2, runs.List().Count);
            Assert.NotEqual(old.RunId, fresh.RunId);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("timer")]
    [InlineData("watchdog")]
    [InlineData("log")]
    public async Task FireSteps_InheritOriginalMountAuthorityInsteadOfNewExecutionIdentity(string kind)
    {
        var authority = new WorkflowStopAuthorityRecord("42:99", 7, "mount-intent", 123, System.Diagnostics.Stopwatch.Frequency);
        var trigger = JsonSerializer.Deserialize<StartupTriggerInfo>(JsonSerializer.Serialize(new
        {
            Kind = kind, InstanceId = "mount", OccurrenceKey = "2026-10-03", StopAuthority = authority,
        }))!;
        StartupHandoffRequest? observed = null;
        var runner = new StartupFlowRunner(
            (_, _) => Task.FromResult(new CommandResult { Status = "success" }),
            (request, _) => { observed = request; return Task.FromResult(StartupHandoffResult.Accepted("run", null)); },
            _ => { }, (_, _) => Task.FromResult((true, "ok")), _ => { }, () => null, _ => { });
        await runner.RunAsync([new StartupStep { Kind = StartupStepKinds.EnterTaskCenter, TaskCenterFlowId = "wf" }], default, trigger);
        Assert.NotNull(observed);
        Assert.Equal(authority, observed!.StopAuthority);
        Assert.NotEqual(authority.IntentId, observed.ExecutionId);
    }
}
