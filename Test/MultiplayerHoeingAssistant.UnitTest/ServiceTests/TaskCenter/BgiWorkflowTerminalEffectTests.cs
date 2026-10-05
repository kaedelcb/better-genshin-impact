using System.Diagnostics;
using System.Text.Json;
using Mistletoe.Shared;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Newtonsoft.Json.Linq;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class BgiWorkflowTerminalEffectTests
{
    private sealed class Pin : IDisposable { public void Dispose() { } }
    private sealed class Effects : ITerminalEffectObserver
    {
        internal TerminalEffectProof? Proof;
        internal bool CanPin = true;
        internal Action? BeforeObservation;
        public IDisposable? PinOriginalProcess(BgiEpoch epoch) => CanPin ? new Pin() : null;
        public TerminalEffectProof? Observe(PendingCompletionRecord record, IDisposable? pinned) { BeforeObservation?.Invoke(); return Proof; }
    }
    private sealed class Port : IBgiExecutionPort
    {
        public bool IsReady => true;
        public BgiEpoch? ServerEpoch { get; set; } = new() { ProcessId = 123, StartTicksUtc = 456 };
        public bool HasCapability(string name) => true;
        internal int Sends;
        internal Action<JObject>? Sent;
        internal bool ThrowAfterSend;
        public Task<BgiExternalResponse> SendCommandAsync(string op, object? payload, CancellationToken ct)
        {
            Sends++; Sent?.Invoke(JObject.Parse(JsonSerializer.Serialize(payload)));
            if (ThrowAfterSend) throw new IOException("lost response");
            return Task.FromResult(new BgiExternalResponse { Success = true, Data = "{\"status\":\"accepted\",\"taskHandle\":\"job\"}" });
        }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct) => Task.FromResult<BgiJobListSnapshot?>(null);
        public Task<(string?, BgiJobInfo?)> QueryJobStatusAsync(string id, CancellationToken ct) => Task.FromResult<(string?, BgiJobInfo?)>((null, null));
        public Task CancelOwnedTaskAsync(string id, CancellationToken ct) => throw new Xunit.Sdk.XunitException("must not cancel another epoch");
    }
    private static string Root() => Path.Combine(Path.GetTempPath(), "c17-terminal-effects-" + Guid.NewGuid().ToString("N"));
    private static WorkflowRunRecord Seed(RunStore runs, string action)
    {
        var run = runs.CreateRun("wf", "rev", stopAuthority: new("123:456", 0, "intent", 1, Stopwatch.Frequency));
        run.State = WorkflowRunState.Completing;
        run.PendingCompletion = new() { ActionId = "$flow#0", Kind = "terminal.completionAction", Action = action, BodyCompletedBeforeTerminal = true };
        runs.Update(run); return run;
    }
    private static TerminalEffectProof Proof(JObject p, string action, bool mismatch = false)
    {
        var progress = new TerminalEffectProgress((string)p["terminalEffectToken"]!, "123:456", "job",
            (string)p["idempotencyKey"]!, (string)p["workflowRunId"]!, action,
            TerminalEffectJournal.Fingerprint(BgiExternalClient.ExternalOperations.TerminalCompletionAction, p),
            Environment.MachineName, DateTimeOffset.UtcNow.AddSeconds(-1), action == "shutdown" ? "shutdown_requested" : "software_exit_requested", true);
        if (mismatch) progress = progress with { Epoch = "123:999" };
        return new(progress, action == "shutdown" ? "windows_system_shutdown_and_boot" : "original_process_handle_exit",
            DateTimeOffset.UtcNow, action == "shutdown" ? null : 0, action == "shutdown" ? [10, 11, 12] : []);
    }

    [Theory]
    [InlineData("closeSoftware", false)]
    [InlineData("closeSoftware", true)]
    [InlineData("closeGameAndSoftware", false)]
    public async Task ActualExecutor_PublishesOriginalEffectDurably_AfterDisconnectAndLostReply(string action, bool lostReply)
    {
        var root = Root(); var runs = new RunStore(root); var run = Seed(runs, action); var effects = new Effects();
        var port = new Port { ThrowAfterSend = lostReply };
        port.Sent = p => { effects.Proof = Proof(p, action); port.ServerEpoch = null; };
        var terminal = new BgiWorkflowTerminalExecutor(port, runs, effects);
        var result = await terminal.ExecuteAsync(new() { Kind = "terminal.completionAction", Params = new() { ["action"] = JsonSerializer.SerializeToElement(action) } }, run, default);
        Assert.Equal("executed", result.State); Assert.Equal(1, port.Sends);
        var saved = new RunStore(root).Load(run.RunId)!.PendingCompletion!;
        Assert.Equal("job", saved.JobId); Assert.True(saved.ExecutionExitConfirmed);
        Assert.Equal("succeeded", saved.EffectState); Assert.True(TerminalReleaseEvidence.CompletionSettled(saved));
        saved.TerminalEffectProofJson = null;
        Assert.False(TerminalReleaseEvidence.CompletionSettled(saved), "receipt-less remote word must not discharge destructive effect");
        saved.TerminalEffectToken = null;
        Assert.False(TerminalReleaseEvidence.CompletionSettled(saved), "missing token must not turn an unproved software exit into success");
    }

    [Fact]
    public async Task StopOrUnavailableOriginalProcess_SendsNothing()
    {
        var runs = new RunStore(Root()); var run = Seed(runs, "closeSoftware"); var port = new Port();
        var executor = new BgiWorkflowTerminalExecutor(port, runs, new Effects { CanPin = false });
        Assert.Equal("rejected", (await executor.ExecuteAsync(new() { Params = new() { ["action"] = JsonSerializer.SerializeToElement("closeSoftware") } }, run, default)).State);
        Assert.Equal(0, port.Sends); Assert.False(run.PendingCompletion!.SendAttempted);
        run.StopRequested = true;
        Assert.Equal("rejected", (await executor.ExecuteAsync(new() { Params = new() { ["action"] = JsonSerializer.SerializeToElement("closeSoftware") } }, run, default)).State);
        Assert.Equal(0, port.Sends);
    }

    [Fact]
    public async Task FrozenOriginalBindingCannotChangeDuringEffectObservation()
    {
        var root = Root(); var runs = new RunStore(root); var run = Seed(runs, "closeSoftware");
        var effects = new Effects(); var port = new Port();
        port.Sent = p => effects.Proof = Proof(p, "closeSoftware");
        effects.BeforeObservation = () =>
        {
            effects.BeforeObservation = null;
            Assert.True(runs.UpdateMergingIf(run.RunId, latest =>
            { latest.PendingCompletion!.TerminalRequestFingerprint = new string('F', 64); return true; }, out _));
        };
        await Assert.ThrowsAsync<RunRecordConflictException>(() => new BgiWorkflowTerminalExecutor(port, runs, effects).ExecuteAsync(
            new() { Params = new() { ["action"] = JsonSerializer.SerializeToElement("closeSoftware") } }, run, default));
        var saved = new RunStore(root).Load(run.RunId)!.PendingCompletion!;
        Assert.Null(saved.TerminalEffectProofJson); Assert.False(saved.ExecutionExitConfirmed); Assert.Equal(1, port.Sends);
    }

    [Fact]
    public async Task ShutdownRestart_DurableOriginalEffectFinalizesCompletedBody_NoResend()
    {
        var root = Root(); var runs = new RunStore(root); var run = Seed(runs, "shutdown"); var effects = new Effects(); var port = new Port();
        JObject? payload = null; port.Sent = p => payload = p;
        var executor = new BgiWorkflowTerminalExecutor(port, runs, effects);
        var result = await executor.ExecuteAsync(new() { Params = new() { ["action"] = JsonSerializer.SerializeToElement("shutdown") } }, run, default);
        Assert.Equal("unknown", result.State); Assert.Equal(1, port.Sends);
        run.State = WorkflowRunState.Failed; runs.Update(run);
        var reopened = new RunStore(root); var restored = reopened.Load(run.RunId)!;
        effects.Proof = Proof(payload!, "shutdown", true);
        Assert.Throws<RunRecordConflictException>(() => BgiWorkflowTerminalExecutor.ReconcileDurableEffect(reopened, restored, effects));
        Assert.NotNull(reopened.Load(run.RunId)!.PendingCompletion);
        effects.Proof = Proof(payload!, "shutdown");
        reopened.TerminalEffectObserverForTest = effects;
        Assert.Empty(reopened.RecoverOnStart()); // Actual startup consumer, no BGI connection or resend.
        var saved = new RunStore(root).Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Succeeded, saved.State); Assert.Null(saved.PendingCompletion);
        Assert.Single(saved.CompletionHistory); Assert.True(TerminalReleaseEvidence.RunSettled(saved)); Assert.Equal(1, port.Sends);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(9, false)]
    public async Task WindowsObserver_UsesPinnedRealChildProcess_NotPidAbsence(int exitCode, bool succeeds)
    {
        var root = Root(); var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 2; exit " + exitCode }) info.ArgumentList.Add(arg);
        using var child = Process.Start(info)!;
        var epoch = new BgiEpoch { ProcessId = child.Id, StartTicksUtc = child.StartTime.ToUniversalTime().Ticks };
        var observer = new WindowsTerminalEffectObserver(root); using var pinned = observer.PinOriginalProcess(epoch);
        Assert.NotNull(pinned);
        var record = new PendingCompletionRecord { TerminalEffectToken = new string('a', 64), Epoch = WorkflowStopAuthority.Epoch(epoch),
            JobId = "job", IdempotencyKey = "key", WireRunId = "run", Action = "closeSoftware", TerminalRequestFingerprint = new string('B', 64) };
        var progress = new TerminalEffectProgress(record.TerminalEffectToken, record.Epoch!, "job", "key", "run", record.Action,
            record.TerminalRequestFingerprint, Environment.MachineName, DateTimeOffset.UtcNow, "software_exit_requested", false);
        TerminalEffectJournal.Write(root, record.TerminalEffectToken, progress);
        Assert.Null(observer.Observe(record, pinned));
        await child.WaitForExitAsync();
        Assert.Null(observer.Observe(record, null)); // Missing PID alone must never succeed.
        Assert.Equal(succeeds, observer.Observe(record, pinned) is not null);
        Assert.Equal(succeeds, new WindowsTerminalEffectObserver(root).Observe(record, null) is not null);
    }
}
