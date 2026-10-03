using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using System.Diagnostics;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class StopAuthorityPersistenceTests
{
    [Fact]
    public void FrozenAuthority_MustSurviveSerializationInsteadOfBecomingCurrentVersion()
    {
        const string json = "{\"runId\":\"run-test\",\"stopAuthority\":{\"epoch\":\"42:99\",\"version\":7,\"intentId\":\"explicit-intent\",\"intentTimestamp\":123,\"monotonicFrequency\":10000000}}";
        var run = JsonSerializer.Deserialize<WorkflowRunRecord>(json)!;
        Assert.NotNull(typeof(WorkflowRunRecord).GetProperty("StopAuthority"));
        using var persisted = JsonDocument.Parse(JsonSerializer.Serialize(run));
        Assert.True(persisted.RootElement.TryGetProperty("stopAuthority", out var authority), "durable stop authority must not be dropped");
        Assert.Equal(7, authority.GetProperty("version").GetInt64());
        Assert.Equal("explicit-intent", authority.GetProperty("intentId").GetString());
    }

    [Fact]
    public void IntentBeforeManualStop_IsRevokedEvenWhenCooldownHasElapsed()
    {
        var now = Stopwatch.GetTimestamp();
        var fence = new ManualStopFence("42:99", 7, now - 40 * Stopwatch.Frequency, Stopwatch.Frequency);
        Assert.Throws<InvalidOperationException>(() => WorkflowStopAuthority.FromExplicitIntent(
            fence, "old-intent", now - 60 * Stopwatch.Frequency));
        var fresh = WorkflowStopAuthority.FromExplicitIntent(fence, "new-explicit-intent", now);
        Assert.Equal(7, fresh.Version);
    }

    [Fact]
    public void QueryEpochDriftOrMissingTimestamp_DoesNotBecomeVersionZero()
    {
        var response = new BgiExternalResponse { Success = true, Data =
            "{\"bgiEpoch\":{\"processId\":42,\"startTicksUtc\":99},\"stopVersion\":7,\"lastManualStopTimestamp\":null,\"monotonicFrequency\":" + Stopwatch.Frequency + "}" };
        Assert.Null(WorkflowStopAuthority.Parse(response, "42:99", "42:99"));
        Assert.Null(WorkflowStopAuthority.Parse(response, "42:99", "42:100"));
    }

    [Fact]
    public void RunStore_RejectsBaselineReplacementRemovalAndRetroactiveBinding()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stop-authority-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RunStore(dir);
            var authority = new WorkflowStopAuthorityRecord("42:99", 7, "intent", 123, Stopwatch.Frequency);
            var run = store.CreateRun("wf", "rev", stopAuthority: authority);
            run.StopAuthority = authority with { Version = 8 };
            Assert.Throws<RunRecordConflictException>(() => store.Update(run));
            run = store.Load(run.RunId)!;
            run.StopAuthority = null;
            Assert.Throws<RunRecordConflictException>(() => store.Update(run));
            Assert.Equal(authority, store.Load(run.RunId)!.StopAuthority);
            var legacy = store.CreateRun("old", "rev");
            legacy.StopAuthority = authority;
            Assert.Throws<RunRecordConflictException>(() => store.Update(legacy));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
