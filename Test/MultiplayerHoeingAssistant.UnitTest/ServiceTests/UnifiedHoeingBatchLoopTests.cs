using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;
using Authority = BgiCoordinatorServer.Services.CoordinatedBatchState;
using AuthorityBinding = BgiCoordinatorServer.Services.CoordinatedBatchBinding;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>真实助手循环与当前服务端状态机对接；只替换外部BGI传输和阶段动作。</summary>
public sealed class UnifiedHoeingBatchLoopTests
{
    private sealed class Scenario
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, BgiJobInfo> _jobs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, BgiTaskQueueStatus> _terminals = new(StringComparer.Ordinal);
        private readonly bool _hasPeer;
        internal readonly Authority Server;
        internal readonly OnlineHoeingBatch Batch;
        internal readonly List<(string Name, bool Dragon, string Key)> Sent = [];
        internal BgiEpoch Epoch = new() { ProcessId = 7, StartTicksUtc = 70 };
        internal int PrepareCount, TakeoverCount, ExchangeCount, CancelCount, ReleaseCount;
        internal bool PeerRegistered = true, PeerPrepared = true;
        internal bool RejectFirst, RejectAlways, LoseAcceptedReply, LoseUnacceptedReply, KeepRunning;
        internal bool ChangeEpochDuringTakeover;
        internal Exception? PreparationFailure;
        internal Action<Scenario>? AfterSubmit;
        internal Action<Scenario>? DuringExchange;
        internal Task? PreparationWait;

        internal Scenario(bool peer = false, params CoordinatedHoeingBinding[] bindings)
        {
            _hasPeer = peer;
            if (bindings.Length == 0) bindings = [new("锄地组", "group")];
            var members = peer ? new[] { "self", "peer" } : ["self"];
            var plans = members.ToDictionary(uid => uid,
                _ => (IReadOnlyList<AuthorityBinding>)bindings.Select(b => new AuthorityBinding(b.Name, b.Type)).ToArray());
            Server = new Authority(17, members, DateTime.UtcNow, "manual", "start-one", plans);
            Batch = new OnlineHoeingBatch(Client(Server.Snapshot()), "self", new TaskConflictPolicySettings());
        }

        private static CoordinatedBatchSnapshot Client(BgiCoordinatorServer.Services.CoordinatedBatchSnapshot state)
            => JsonSerializer.Deserialize<CoordinatedBatchSnapshot>(JsonSerializer.Serialize(state))!;

        private Task<CoordinatedBatchSnapshot> Exchange(string stage, string result, string reason,
            int index, int attempt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                ExchangeCount++;
                Server.Apply("self", Batch.Token, Server.BatchId, string.Join(",", Batch.Bindings.Select(b => b.Type)),
                    index, attempt, result, DateTime.UtcNow, stage, reason);
                if (_hasPeer && !Server.ExecutionEnded)
                {
                    var layout = string.Join(",", Batch.Bindings.Select(b => b.Type));
                    var phase = Server.Phase;
                    if (phase == "registering" && PeerRegistered)
                        Server.Apply("peer", "peer-token", Server.BatchId, layout, Server.Index, Server.Attempt, "", DateTime.UtcNow, "registered");
                    if (Server.Phase == "preparing" && PeerPrepared)
                        Server.Apply("peer", "peer-token", Server.BatchId, layout, Server.Index, Server.Attempt, "", DateTime.UtcNow, "prepared");
                    if (Server.Phase == "takeover")
                        Server.Apply("peer", "peer-token", Server.BatchId, layout, Server.Index, Server.Attempt, "", DateTime.UtcNow, "takeoverConfirmed");
                    if (Server.Phase == "running" && result == "succeeded")
                        Server.Apply("peer", "peer-token", Server.BatchId, layout, Server.Index, Server.Attempt, "succeeded", DateTime.UtcNow);
                    if (Server.Phase == "stopping")
                        Server.Apply("peer", "peer-token", Server.BatchId, layout, Server.Index, Server.Attempt, "stopped", DateTime.UtcNow);
                }
                DuringExchange?.Invoke(this);
                return Task.FromResult(Client(Server.Snapshot()));
            }
        }

        internal Task<bool> Run() => new CoordinatedBatchLoop(Batch, new CoordinatedBatchRuntime
        {
            Exchange = Exchange,
            Prepare = async ct =>
            {
                Interlocked.Increment(ref PrepareCount);
                if (PreparationWait != null) await PreparationWait.WaitAsync(ct);
                if (PreparationFailure != null) throw PreparationFailure;
                ct.ThrowIfCancellationRequested();
            },
            Takeover = ct =>
            {
                ct.ThrowIfCancellationRequested();
                Assert.Equal("takeover", Server.Phase);
                Interlocked.Increment(ref TakeoverCount);
                Batch.TakeoverTicket = "own-ticket";
                if (ChangeEpochDuringTakeover) Epoch = new BgiEpoch { ProcessId = 8, StartTicksUtc = 80 };
                return Task.CompletedTask;
            },
            ReadJobs = ct =>
            {
                ct.ThrowIfCancellationRequested();
                lock (_gate) return Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = Epoch, Jobs = _jobs.Values.ToList() });
            },
            ReadTerminal = (id, ct) => Task.FromResult(_terminals.GetValueOrDefault(id)),
            Submit = (item, epoch, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Assert.True(CoordinatedBatchLoop.SameEpoch(Epoch, epoch));
                Assert.Equal("running", Server.Phase);
                Sent.Add((item.Name, item.IsOneDragon, item.RequestKey));
                if (RejectAlways || RejectFirst && Sent.Count == 1)
                    return Task.FromResult(new BgiTaskSubmitResult { Success = false, ErrorCode = "task_busy" });
                if (LoseUnacceptedReply) throw new IOException("未确认是否受理");
                var id = Guid.NewGuid().ToString("N");
                _jobs[id] = new BgiJobInfo { JobId = id, IdempotencyKey = item.RequestKey };
                _terminals[id] = new BgiTaskQueueStatus { Status = KeepRunning ? "running" : "completed" };
                AfterSubmit?.Invoke(this);
                if (LoseAcceptedReply) throw new IOException("受理回执丢失");
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = id });
            },
            CancelOwned = (id, ct) =>
            {
                Assert.True(_jobs.ContainsKey(id));
                CancelCount++;
                _terminals[id] = new BgiTaskQueueStatus { Status = "queueCancelled", Cancelled = true };
                return Task.CompletedTask;
            },
            ReleaseTicket = (ticket, epoch, ct) =>
            {
                Assert.Equal("own-ticket", ticket);
                if (!CoordinatedBatchLoop.SameEpoch(Epoch, epoch)) return Task.FromResult(false);
                ReleaseCount++;
                return Task.FromResult(true);
            },
            PollInterval = TimeSpan.FromMilliseconds(2),
            StageTimeout = TimeSpan.FromSeconds(1),
            CleanupTimeout = TimeSpan.FromMilliseconds(200),
            SubmissionReconcileTimeout = TimeSpan.FromMilliseconds(25),
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task BothResourceTypesUseTheSameAuthorityAndOnlyAdvanceAfterTerminal()
    {
        var s = new Scenario(true, new("配置组", "group"), new("一条龙", "onedragon"));
        Assert.True(await s.Run());
        Assert.Equal(1, s.PrepareCount);
        Assert.Equal(1, s.TakeoverCount);
        Assert.Equal(new[] { false, true }, s.Sent.Select(x => x.Dragon));
        Assert.Equal(2, s.Sent.Select(x => x.Key).Distinct().Count());
        Assert.Equal("completed", s.Server.Phase);
        Assert.False(s.Server.IsTerminal); // 外层策略及cleaned回执完成前仍不得接新批。
        Assert.Equal(0, s.ReleaseCount);
    }

    [Fact]
    public async Task MissingPeerPreparationCannotInterruptOldWorkOrSubmit()
    {
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = new Scenario(true) { PeerPrepared = false };
        s.DuringExchange = current => { if (current.ExchangeCount >= 8) reached.TrySetResult(); };
        var running = s.Run();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(0, s.TakeoverCount);
        Assert.Empty(s.Sent);
        s.PeerPrepared = true;
        Assert.True(await running);
    }

    [Fact]
    public async Task SlowPreparationKeepsReportingInsteadOfExpiringRegistrationLease()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var s = new Scenario { PreparationWait = ready.Task };
        s.DuringExchange = current => { if (current.ExchangeCount >= 12) reached.TrySetResult(); };
        var running = s.Run();
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("preparing", s.Server.Phase);
        Assert.Equal(0, s.TakeoverCount);
        ready.SetResult();
        Assert.True(await running);
    }

    [Fact]
    public async Task PreparationFailureReportsMemberAndNeverTakesOver()
    {
        var s = new Scenario(true) { PreparationFailure = new InvalidOperationException("绑定资源不存在") };
        Assert.False(await s.Run());
        Assert.Equal(0, s.TakeoverCount);
        Assert.Empty(s.Sent);
        Assert.Contains("participant_preparation_failed:self", s.Server.Reason);
        Assert.Contains("绑定资源不存在", s.Server.Reason);
        Assert.True(s.Batch.CleanupConfirmed);
    }

    [Fact]
    public async Task LostAcceptedReplyIsAdoptedByOriginalKeyWithoutSecondSend()
    {
        var s = new Scenario { LoseAcceptedReply = true };
        Assert.True(await s.Run());
        Assert.Single(s.Sent);
        Assert.Equal(CoordinatedBatchLoop.RequestKey(s.Batch.BatchId, "self", 0, 0), s.Sent[0].Key);
    }

    [Fact]
    public async Task UnknownAcceptanceNeverResendsOrFalselyReleasesResponsibility()
    {
        var s = new Scenario { LoseUnacceptedReply = true };
        Assert.False(await s.Run());
        Assert.Single(s.Sent);
        Assert.Equal(0, s.ReleaseCount);
        Assert.False(s.Batch.CleanupConfirmed);
        Assert.Equal("unknown", s.Batch.Outcome);
    }

    [Fact]
    public async Task KnownRejectionUsesOneSharedRecoveryWithANewAttemptKey()
    {
        var s = new Scenario(true) { RejectFirst = true };
        Assert.True(await s.Run());
        Assert.Equal(2, s.Sent.Count);
        Assert.NotEqual(s.Sent[0].Key, s.Sent[1].Key);
        Assert.Equal(CoordinatedBatchLoop.RequestKey(s.Batch.BatchId, "self", 0, 1), s.Sent[1].Key);
    }

    [Fact]
    public async Task RecoveryExhaustionCannotStartTheNextBinding()
    {
        var s = new Scenario(true, new("第一组", "group"), new("后续组", "group")) { RejectAlways = true };
        Assert.False(await s.Run());
        Assert.Equal(2, s.Sent.Count);
        Assert.All(s.Sent, item => Assert.Equal("第一组", item.Name));
        Assert.Equal("recovery_budget_exhausted", s.Server.Reason);
        Assert.True(s.Batch.CleanupConfirmed);
    }

    [Fact]
    public async Task UserStopCancelsOnlyOwnedJobAndDoesNotStartNextBinding()
    {
        var s = new Scenario(false, new("第一组", "group"), new("后续组", "group")) { KeepRunning = true };
        s.AfterSubmit = current => current.Batch.Cancel();
        Assert.False(await s.Run());
        Assert.Single(s.Sent);
        Assert.Equal(1, s.CancelCount);
        Assert.Equal(1, s.ReleaseCount);
        Assert.True(s.Batch.CleanupConfirmed);
        Assert.Equal("cancelled", s.Batch.Outcome);
    }

    [Fact]
    public async Task EpochChangeDuringTakeoverNeverSubmitsToSuccessorOrReleasesItsTicket()
    {
        var s = new Scenario { ChangeEpochDuringTakeover = true };
        Assert.False(await s.Run());
        Assert.Empty(s.Sent);
        Assert.Equal(0, s.ReleaseCount);
        Assert.False(s.Batch.CleanupConfirmed);
    }

    [Theory]
    [InlineData("configuration_changed")]
    [InlineData("configuration_not_found")]
    public void ChangedFrozenConfigurationIsAPreparationFailureNotUnknown(string error)
        => Assert.Equal("preparation_failed", CoordinatedBatchOutcome.Classify(new() { Status = "failed", ErrorCode = error }, false));

    [Fact]
    public void ConfigurationRevisionCannotBeReplacedAfterAcceptance()
    {
        var s = new Scenario();
        s.Batch.FreezeConfigurationRevision("锄地组", false, "a");
        Assert.Throws<InvalidOperationException>(() => s.Batch.FreezeConfigurationRevision("锄地组", false, "b"));
    }
}
