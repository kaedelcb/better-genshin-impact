using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;
using ServerState = BgiCoordinatorServer.Services.CoordinatedBatchState;
using ServerBinding = BgiCoordinatorServer.Services.CoordinatedBatchBinding;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>Runs the production loop for four endpoints against the actual server batch state.</summary>
public sealed class CoordinatedRunningReconnectTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _ticks);
        internal void Advance(TimeSpan duration) => Interlocked.Add(ref _ticks, duration.Ticks);
    }

    private sealed class Team
    {
        private readonly object _gate = new();
        private readonly ServerState _server;
        internal readonly Endpoint[] Endpoints;
        internal Team()
        {
            var uids = Enumerable.Range(0, 4).Select(i => "u" + i).ToArray();
            _server = new(21, uids, DateTime.UtcNow, bindings: uids.ToDictionary(uid => uid,
                _ => (IReadOnlyList<ServerBinding>)[new("group", "group")]));
            Endpoints = uids.Select(uid => new Endpoint(this, uid, Snapshot())).ToArray();
        }
        private CoordinatedBatchSnapshot Snapshot() => JsonSerializer.Deserialize<CoordinatedBatchSnapshot>(
            JsonSerializer.Serialize(_server.Snapshot()))!;
        internal CoordinatedBatchSnapshot Exchange(Endpoint endpoint, string stage, string result, string reason,
            int index, int attempt, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (endpoint.Unavailable)
            {
                endpoint.LossObserved.TrySetResult();
                throw new ControlChannelUnavailableException("simulated transport/rejoin window");
            }
            lock (_gate)
            {
                _server.Apply(endpoint.Uid, endpoint.Batch.Token, endpoint.Batch.BatchId, "group",
                    index, attempt, result, DateTime.UtcNow, stage, reason);
                return Snapshot();
            }
        }
        internal Task<bool>[] Start() => Endpoints.Select(e => e.Run()).ToArray();
        internal async Task SubmittedAll() => await WaitUntil(() => Endpoints.All(e => e.Sends == 1));
        internal void Finish() { foreach (var e in Endpoints) e.AllowFinish = true; }
        internal void Stop() { foreach (var e in Endpoints) e.Batch.Cancel(); }
    }

    private sealed class Endpoint(Team team, string uid, CoordinatedBatchSnapshot start)
    {
        internal readonly string Uid = uid;
        internal readonly OnlineHoeingBatch Batch = new(start, uid, new TaskConflictPolicySettings());
        internal readonly Clock Time = new();
        internal readonly TaskCompletionSource LossObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Unavailable;
        internal volatile bool AllowFinish;
        internal bool LoseAfterPrepare;
        internal bool UnknownSubmitResponse;
        internal bool DeferFirstSubmit;
        internal int Sends, SubmitAttempts, Prepares, Takeovers, Cancels, Finalizes;
        internal readonly List<string> Keys = [];
        private string? _job;
        private bool _cancelled;

        internal Task<bool> Run() => new CoordinatedBatchLoop(Batch, new()
        {
            Clock = Time,
            PollInterval = TimeSpan.FromMilliseconds(2),
            StageTimeout = TimeSpan.FromSeconds(1),
            AuthorityContactTimeout = TimeSpan.FromMilliseconds(30),
            RunningAuthorityContactTimeout = TimeSpan.FromMilliseconds(150),
            CleanupTimeout = TimeSpan.FromMilliseconds(200),
            Prepare = _ =>
            {
                Prepares++;
                if (LoseAfterPrepare)
                {
                    Unavailable = true;
                    Time.Advance(TimeSpan.FromMilliseconds(31));
                }
                return Task.CompletedTask;
            },
            Takeover = _ => { Takeovers++; Batch.TakeoverTicket = "ticket"; return Task.CompletedTask; },
            Exchange = (stage, result, reason, index, attempt, ct) => Task.FromResult(
                team.Exchange(this, stage, result, reason, index, attempt, ct)),
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new()
            {
                Epoch = new() { ProcessId = 9, StartTicksUtc = 90 },
                Jobs = _job is null ? [] : [new() { JobId = _job, IdempotencyKey = Keys[^1] }]
            }),
            ReadTerminal = (_, _) => Task.FromResult<BgiTaskQueueStatus?>(new()
            {
                Status = _cancelled ? "queueCancelled" : AllowFinish ? "completed" : "running",
                Cancelled = _cancelled
            }),
            Submit = (item, _, _) =>
            {
                SubmitAttempts++;
                if (DeferFirstSubmit && SubmitAttempts == 1)
                    throw new ControlSubmissionDeferredException("original grant revoked before send");
                Sends++;
                Keys.Add(item.RequestKey);
                _job = Guid.NewGuid().ToString();
                if (UnknownSubmitResponse) throw new IOException("accepted; response lost");
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = _job });
            },
            CancelOwned = (_, _) => { Cancels++; _cancelled = true; return Task.CompletedTask; },
            ReleaseTicket = (_, _, _) => Task.FromResult(true),
            FinalizeLocal = (_, _) => { Finalizes++; return Task.CompletedTask; },
            OriginalProcessAlive = _ => true
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task RunningFlapSurvivesPreparationBudgetAndFinishesTheSameFourOriginalJobs()
    {
        var team = new Team();
        var runs = team.Start();
        await team.SubmittedAll();
        var self = team.Endpoints[0];
        self.Unavailable = true;
        await self.LossObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        self.Time.Advance(TimeSpan.FromMilliseconds(40)); // Longer than the unchanged preparation budget.
        await Task.Delay(15);
        Assert.False(runs[0].IsCompleted);
        Assert.Equal(0, self.Cancels);
        self.Unavailable = false;
        team.Finish();
        Assert.All(await Task.WhenAll(runs), Assert.True);
        Assert.All(team.Endpoints, endpoint =>
        {
            Assert.Equal(1, endpoint.Sends);
            Assert.Equal(1, endpoint.Prepares);
            Assert.Equal(1, endpoint.Takeovers);
            Assert.Equal(1, endpoint.Finalizes);
        });
    }

    [Fact]
    public async Task RunningHardDeadlineStopsTheOriginalJobAndLateRecoveryCannotResubmit()
    {
        var team = new Team();
        var runs = team.Start();
        await team.SubmittedAll();
        var self = team.Endpoints[0];
        self.Unavailable = true;
        await self.LossObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
        self.Time.Advance(TimeSpan.FromMilliseconds(151));
        Assert.False(await runs[0]);
        Assert.Equal(1, self.Cancels);
        self.Unavailable = false;
        await Task.Delay(10);
        Assert.Equal(1, self.Sends);
        team.Stop();
        await Task.WhenAll(runs);
    }

    [Fact]
    public async Task PreparationStillUsesOriginalBudgetAndNeverStartsBusinessAfterItsFailure()
    {
        var team = new Team();
        var self = team.Endpoints[0];
        self.LoseAfterPrepare = true;
        var runs = team.Start();
        Assert.False(await runs[0]);
        Assert.Equal(1, self.Prepares);
        Assert.Equal(0, self.Sends);
        team.Stop();
        await Task.WhenAll(runs);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task KnownNotSentCanRetrySameKeyButUnknownAcceptedSendIsOnlyReconciled(bool knownNotSent)
    {
        var team = new Team();
        var self = team.Endpoints[0];
        self.DeferFirstSubmit = knownNotSent;
        self.UnknownSubmitResponse = !knownNotSent;
        var runs = team.Start();
        await team.SubmittedAll();
        team.Finish();
        Assert.All(await Task.WhenAll(runs), Assert.True);
        Assert.Equal(1, self.Sends);
        Assert.Equal(knownNotSent ? 2 : 1, self.SubmitAttempts);
        Assert.Single(self.Keys);
        Assert.Equal(CoordinatedBatchLoop.RequestKey(self.Batch.BatchId, self.Uid, 0, 0), self.Keys[0]);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(2, budget.Token);
    }
}
