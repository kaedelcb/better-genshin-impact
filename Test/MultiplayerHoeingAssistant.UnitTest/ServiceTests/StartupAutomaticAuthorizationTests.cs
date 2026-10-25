using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class StartupAutomaticAuthorizationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "startup-authorization-" + Guid.NewGuid().ToString("N"));
    private StartupFlowStore Store => new(Path.Combine(_root, "startup-flow.json"));

    [Fact]
    public async Task EnableBeforeBgiReady_PersistsOriginalIntentAndRestartsWithoutMissingSource()
    {
        var explicitSource = StartupSourceIntent.Explicit();
        Store.SaveConfirmed(new StartupFlowConfig { Enabled = true,
            AutomaticAuthorization = StartupAutomaticAuthorization.FromExplicit(explicitSource),
            Steps = [new() { Kind = StartupStepKinds.EndFlow }] });
        var reopened = Store.LoadChecked().Config;
        Assert.Null(reopened.AutomaticStopAuthority);
        var automatic = MistletoeViewModel.ResolveAutomaticStartupSource(null, null, true, reopened.AutomaticAuthorization);
        Assert.NotNull(automatic);
        Assert.NotEqual(explicitSource.IntentId, automatic.IntentId);
        Assert.True(automatic.IntentTimestamp >= explicitSource.IntentTimestamp);
        Assert.Null(await automatic.GetOrCheckAsync((_, _, _) => Task.FromResult<WorkflowStopAuthorityRecord?>(null), false, default));
        var original = automatic.IntentTimestamp;
        await Assert.ThrowsAsync<StartupSourceStoppedException>(() => automatic.GetOrCheckAsync((intent, _, _) =>
            Task.FromResult<WorkflowStopAuthorityRecord?>(WorkflowStopAuthority.FromExplicitIntent(
                new("42:99", 1, original + 1, Stopwatch.Frequency), intent.IntentId, intent.IntentTimestamp)), true, default));
        Assert.True(automatic.Revoked);
    }

    [Fact]
    public async Task PendingPermit_AfterTimeoutKeepsOriginalTimeAndRejectsInterveningManualStop()
    {
        var original = StartupSourceIntent.Explicit();
        Store.SaveConfirmed(new() { Enabled = true,
            AutomaticAuthorization = StartupAutomaticAuthorization.FromExplicit(original) });
        var failed = MistletoeViewModel.ResolveAutomaticStartupSource(null, null, true,
            Store.LoadChecked().Config.AutomaticAuthorization)!;
        await Assert.ThrowsAsync<TimeoutException>(() => failed.GetOrCheckAsync(
            (_, _, _) => throw new TimeoutException("通道未就绪"), true, default));
        Assert.True(failed.Revoked);
        var retried = StartupSourceIntent.Automatic(Store.LoadChecked().Config.AutomaticAuthorization!, null);
        Assert.Equal(original.IntentId, retried.IntentId);
        Assert.Equal(original.IntentTimestamp, retried.IntentTimestamp);
        await Assert.ThrowsAsync<StartupSourceStoppedException>(() => retried.GetOrCheckAsync((intent, _, _) =>
            Task.FromResult<WorkflowStopAuthorityRecord?>(WorkflowStopAuthority.FromExplicitIntent(
                new("42:99", 1, original.IntentTimestamp + 1, Stopwatch.Frequency), intent.IntentId, intent.IntentTimestamp)), true, default));
        Assert.True(retried.Revoked);
        var oldCallbackRan = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => failed.GetOrCheckAsync((_, _, _) =>
        {
            oldCallbackRan = true;
            return Task.FromResult<WorkflowStopAuthorityRecord?>(null);
        }, false, default));
        Assert.False(oldCallbackRan);
    }

    [Theory]
    [InlineData("42:99")]
    [InlineData("43:100")]
    public async Task NewStartup_ConfirmsPreviousBeforeBindingCurrentBgi(string epoch)
    {
        var previous = new WorkflowStopAuthorityRecord("42:99", 7, "original", 123, Stopwatch.Frequency);
        var authorization = new StartupAutomaticAuthorization(previous.IntentId, previous.IntentTimestamp);
        Store.SaveConfirmed(new() { Enabled = true, AutomaticAuthorization = authorization, AutomaticStopAuthority = previous });
        var reopened = Store.LoadChecked().Config;
        var source = StartupSourceIntent.Automatic(reopened.AutomaticAuthorization!, reopened.AutomaticStopAuthority);
        var validated = false;
        var authority = await source.GetOrCheckAsync(async (intent, _, ct) =>
        {
            await intent.ValidateAutomaticPredecessorAsync(epoch, _ => false, (_, _) =>
            { validated = true; return Task.CompletedTask; }, ct);
            return new WorkflowStopAuthorityRecord(epoch, 7, intent.IntentId, intent.IntentTimestamp, Stopwatch.Frequency);
        }, false, default);
        Assert.Equal(epoch == previous.Epoch, validated);
        Assert.Equal(epoch, authority!.Epoch);
        Assert.NotEqual(previous.IntentId, authority.IntentId);
        Assert.Equal(previous, source.AutomaticPredecessor);
        // 原挂载的源对象保持原基线；新启动不会把它修改成当前 BGI 的授权。
        Assert.Equal(previous, StartupSourceIntent.Inherit(previous).Authority);
    }

    [Theory]
    [InlineData("42:99", false)]
    [InlineData("43:100", true)]
    [InlineData(null, false)]
    public async Task StoppedOrUnknownPredecessor_CannotAcquireFreshAuthority(string? epoch, bool durableStop)
    {
        var previous = new WorkflowStopAuthorityRecord("42:99", 7, "original", 123, Stopwatch.Frequency);
        var source = StartupSourceIntent.Automatic(new("permit", 123), previous);
        var acquired = false;
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => source.GetOrCheckAsync(async (intent, _, ct) =>
        {
            await intent.ValidateAutomaticPredecessorAsync(epoch, _ => durableStop,
                (_, _) => throw new InvalidOperationException("手动停止版本已变化"), ct);
            acquired = true;
            return new WorkflowStopAuthorityRecord(epoch!, 8, intent.IntentId, intent.IntentTimestamp, Stopwatch.Frequency);
        }, false, default));
        Assert.False(acquired);
        Assert.True(source.Revoked);
    }

    [Fact]
    public void LegacyRevokedPermit_DoesNotDisableANewAutomaticStartup()
    {
        Store.SaveConfirmed(new() { Enabled = true, AutomaticAuthorization = new("permit", 123, Revoked: true) });
        var reopened = Store.LoadChecked().Config;
        var source = MistletoeViewModel.ResolveAutomaticStartupSource(null, null, true, reopened.AutomaticAuthorization)!;
        Assert.False(source.Revoked);
        Assert.NotEqual("permit", source.IntentId);
        Assert.Null(source.Authority);
    }

    [Fact]
    public void FailedSave_IsReportedAndPreservesOriginalFile()
    {
        Store.SaveConfirmed(new() { Enabled = false });
        var original = File.ReadAllBytes(Path.Combine(_root, "startup-flow.json"));
        using (File.Open(Path.Combine(_root, "startup-flow.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var failure = Record.Exception(() => Store.SaveConfirmed(new() { Enabled = true,
                AutomaticAuthorization = new("permit", 123) }));
            Assert.True(failure is IOException or UnauthorizedAccessException);
        }
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(_root, "startup-flow.json")));
        Assert.Empty(Directory.GetFiles(_root, ".startup-flow.*.tmp"));
    }

    [Fact]
    public void LegacyEnabledFlag_StartsANewOccurrenceWithoutReauthorizingOldCallbacks()
    {
        var config = JsonSerializer.Deserialize<StartupFlowConfig>("{\"enabled\":true}")!;
        Assert.NotNull(MistletoeViewModel.ResolveAutomaticStartupSource(null, config.AutomaticStopAuthority, true,
            config.AutomaticAuthorization));
    }

    [Fact]
    public async Task DurableStoppedRun_CannotRebindItsAutomaticSourceAfterBgiRestart()
    {
        var runs = new RunStore(Path.Combine(_root, "runs"));
        var previous = new WorkflowStopAuthorityRecord("42:99", 7, "original", 123, Stopwatch.Frequency);
        var stopped = runs.CreateRun("wf", "rev", stopAuthority: previous);
        stopped.StopRequested = true;
        stopped.State = WorkflowRunState.Cancelled;
        runs.Update(stopped);
        var source = StartupSourceIntent.Automatic(new("permit", 123), previous);
        await Assert.ThrowsAsync<StartupSourceStoppedException>(() => source.GetOrCheckAsync(async (intent, _, ct) =>
        {
            await intent.ValidateAutomaticPredecessorAsync("43:100", runs.IsStartupSourceRevoked,
                (_, _) => Task.CompletedTask, ct);
            throw new Exception("不应取得新授权");
        }, false, default));
        Assert.True(source.Revoked);
        Assert.Single(runs.List());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
