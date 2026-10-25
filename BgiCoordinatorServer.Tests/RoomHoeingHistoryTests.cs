using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomHoeingHistoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-hoeing-history-tests", Guid.NewGuid().ToString("N"));
    private static readonly DateTime Today = new(2026, 10, 25, 4, 0, 0, DateTimeKind.Utc);
    private static readonly RoomHoeingMember[] Members = [new("10001", "成员甲"), new("10002", "成员乙")];
    private static CoordinatedBatchSnapshot Batch(string id = "batch", string phase = "registering", long revision = 0, string reason = "")
        => new(id, 1, 0, 0, revision, phase, reason) { ParticipantUids = ["10001", "10002"] };
    private static ControlStatus Status(bool running, string? batchId = "batch", bool? available = true)
        => new() { TaskStatusAvailable = available, AutoHoeingRunning = running, HoeingBatchId = batchId, BgiStatus = "running" };

    [Fact]
    public void CompletedResultSurvivesIdleRepeatedReportsAndFreshStore()
    {
        var history = new RoomHoeingHistory(_root);
        history.ObserveBatch("ABC123", Batch(), Members, Today);
        history.ObserveMember("ABC123", "10001", "成员甲", "instance", Status(true), "batch", Today.AddMinutes(1));
        history.ObserveMember("ABC123", "10001", "成员甲", "instance", Status(true), "batch", Today.AddMinutes(2));
        history.ObserveBatch("ABC123", Batch(phase: "completed", revision: 5), Members, Today.AddMinutes(3));
        history.ObserveBatch("ABC123", Batch(phase: "completed", revision: 5), Members, Today.AddMinutes(4));
        history.ObserveMember("ABC123", "10001", "成员甲", "instance", Status(false), "batch", Today.AddMinutes(5));
        history.ObserveMember("ABC123", "10001", "成员甲", "instance", Status(true), "batch", Today.AddMinutes(6));

        var day = new RoomHoeingHistory(_root).ReadToday("ABC123", Today.AddHours(1));
        Assert.Equal("completed", day.Status);
        var run = Assert.Single(day.Runs);
        Assert.Equal(Today.AddMinutes(1), run.StartedAtUtc);
        Assert.Equal(Today.AddMinutes(3), run.EndedAtUtc);
        Assert.Equal(2, run.Members.Count);
        Assert.Empty(day.Warnings);
        Assert.Contains("ABC123", new RoomHoeingHistory(_root).GetTodayRoomCodes(Today));
    }

    [Fact]
    public void PreparationAndUntrustedDefaultsDoNotInventActualHoeingOrCompletion()
    {
        var history = new RoomHoeingHistory(_root);
        history.ObserveBatch("ABC123", Batch(), Members, Today);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true, available: false), "batch", Today);
        history.ObserveMember("ABC123", "web_123", "网页", "i", Status(true), "batch", Today);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(false), "batch", Today);
        var run = Assert.Single(history.ReadToday("ABC123", Today).Runs);
        Assert.Equal("preparing", run.Status);
        Assert.Null(run.StartedAtUtc);
        Assert.Null(run.EndedAtUtc);
        Assert.Equal("unknown", Assert.Single(new RoomHoeingHistory(_root).ReadToday("ABC123", Today).Runs).Status);
    }

    [Fact]
    public void LostObservationAndRestartCannotKeepRunningOrMakeSuccess()
    {
        var history = new RoomHoeingHistory(_root);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true, null), null, Today);
        var first = Assert.Single(history.ReadToday("ABC123", Today).Runs);
        Assert.Equal("running", first.Status);
        Assert.Equal("unknown", Assert.Single(new RoomHoeingHistory(_root).ReadToday("ABC123", Today).Runs).Status);
        var expired = Assert.Single(history.ReadToday("ABC123", Today.AddMinutes(3)).Runs);
        Assert.Equal("unknown", expired.Status);
        Assert.Null(expired.EndedAtUtc);
    }

    [Fact]
    public void LegacyMembersShareAnObservationAndFalseDoesNotCreateRuns()
    {
        var history = new RoomHoeingHistory(_root);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(false, null), null, Today);
        Assert.Empty(history.ReadToday("ABC123", Today).Runs);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true, null), null, Today);
        history.ObserveMember("ABC123", "10002", "成员乙", "j", Status(true, null), null, Today);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(false, null), null, Today.AddSeconds(20));
        Assert.Equal("running", history.ReadToday("ABC123", Today.AddSeconds(20)).Status);
        history.ObserveDisconnected("ABC123", "10002", "j", Today.AddSeconds(30));
        for (var i = 0; i < 3; i++)
            history.ObserveMember("ABC123", "10002", "成员乙", "j", Status(false, null), null, Today.AddSeconds(40));
        var prior = Assert.Single(history.ReadToday("ABC123", Today.AddSeconds(40)).Runs);
        Assert.Equal("unknown", prior.Status);
        Assert.Equal(2, prior.Members.Count);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true, null), null, Today.AddHours(1));
        Assert.Equal(2, history.ReadToday("ABC123", Today.AddHours(1)).Runs.Count);
    }

    [Fact]
    public void BeijingDayIncludesCrossMidnightContinuationAndEndButNotYesterdaysCompletedRun()
    {
        var beforeMidnight = new DateTime(2026, 10, 24, 15, 59, 40, DateTimeKind.Utc);
        var afterMidnight = beforeMidnight.AddSeconds(30);
        var history = new RoomHoeingHistory(_root);
        history.ObserveBatch("ABC123", Batch(), Members, beforeMidnight);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true), "batch", beforeMidnight);
        history.ObserveBatch("DEF456", Batch("old", "completed"), Members, beforeMidnight);
        Assert.Single(history.ReadToday("ABC123", afterMidnight).Runs);
        Assert.Empty(history.ReadToday("DEF456", afterMidnight).Runs);
        history.ObserveBatch("ABC123", Batch(phase: "completed", revision: 5), Members, afterMidnight);
        var restarted = new RoomHoeingHistory(_root);
        Assert.Equal("2026-10-25", restarted.ReadToday("ABC123", afterMidnight).Date);
        Assert.Equal("completed", restarted.ReadToday("ABC123", afterMidnight).Status);
        Assert.Equal(new[] { "ABC123" }, restarted.GetTodayRoomCodes(afterMidnight));
        Assert.Empty(restarted.ReadToday("ABC123", afterMidnight.AddDays(1)).Runs);
    }

    [Theory]
    [InlineData("completed", "completed_with_task_errors:10001", "completedWithErrors")]
    [InlineData("aborted", "manual_reset", "stopped")]
    [InlineData("aborted", "user_stop_all", "stopped")]
    [InlineData("aborted", "participant_unknown:10001", "unknown")]
    [InlineData("aborted", "participant_failed_after_peer_completed:10001", "failed")]
    public void ServerTerminalRemainsSeparateFromObservation(string phase, string reason, string expected)
    {
        var history = new RoomHoeingHistory(_root);
        history.ObserveBatch("ABC123", Batch(), Members, Today);
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true), "batch", Today);
        history.ObserveBatch("ABC123", Batch(phase: phase, reason: reason, revision: 3), Members, Today.AddMinutes(1));
        history.ObserveBatch("ABC123", Batch(phase: "running", revision: 2), Members, Today.AddMinutes(2));
        history.ObserveMember("ABC123", "10001", "成员甲", "i", Status(true), "batch", Today.AddMinutes(2));
        var run = Assert.Single(new RoomHoeingHistory(_root).ReadToday("ABC123", Today).Runs);
        Assert.Equal(expected, run.Status);
        Assert.Equal(reason, run.Reason);
        Assert.Equal(Today.AddMinutes(1), run.EndedAtUtc);
    }

    [Fact]
    public void QuickCommandWithoutActualHoeingIsNotIncluded()
    {
        var history = new RoomHoeingHistory(_root);
        var batch = Batch() with { QuickCommandKey = "一键传奇" };
        history.ObserveBatch("ABC123", batch, Members, Today);
        history.ObserveBatch("ABC123", batch with { Phase = "completed", Revision = 2 }, Members, Today);
        Assert.Empty(history.ReadToday("ABC123", Today).Runs);
    }

    [Fact]
    public void MalformedFileIsPreservedAndSaveFailureIsVisible()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "ABC123.json");
        File.WriteAllText(path, "{invalid");
        var history = new RoomHoeingHistory(_root);
        history.ObserveBatch("ABC123", Batch(), Members, Today);
        Assert.NotEmpty(history.ReadToday("ABC123", Today).Warnings);
        Assert.Equal("{invalid", File.ReadAllText(path));
        var blockedRoot = Path.Combine(_root, "file-as-directory");
        File.WriteAllText(blockedRoot, "owned test blocker");
        var blocked = new RoomHoeingHistory(blockedRoot);
        blocked.ObserveBatch("ABC123", Batch(), Members, Today);
        Assert.NotEmpty(blocked.ReadToday("ABC123", Today).Warnings);
    }

    [Fact]
    public async Task RealOperationsRecordOnlyRegisteredExecutorsAndAuthoritativeTerminal()
    {
        var history = new RoomHoeingHistory(_root);
        var h = new GatewayTestHarness();
        var broadcaster = new GatewayBroadcaster(h.LegacyHub.Object, h.GatewayHub.Object, h.Tracker, Mock.Of<ILogger<GatewayBroadcaster>>());
        var ops = new RoomOperations(h.RoomManager, Mock.Of<ILogger<RoomOperations>>(), broadcaster,
            new RoomPhaseObserver(Mock.Of<ILogger<RoomPhaseObserver>>()), h.Capabilities, hoeingHistory: history);
        var client = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        var observer = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        var foreign = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await ops.JoinControlRoomAsync(client, "ABC123", "pw", "10001", "真实成员", clientInstanceId: "real-instance");
        await ops.JoinControlRoomAsync(observer, "ABC123", "pw", "10001", "观察端", isRemote: true);
        await ops.JoinControlRoomAsync(foreign, "DEF456", "pw", "10002", "另一房间");
        var report = withRoom(Status(true), "ABC123");
        await ops.ReportControlStatusAsync(observer, report);
        await ops.ReportControlStatusAsync(foreign, report);
        Assert.Empty(history.ReadToday("ABC123", DateTime.UtcNow).Runs);
        report.AutoHoeingRunning = false;
        report.OnlineHoeingGroupNames = ["联机锄地"];
        await ops.ReportControlStatusAsync(client, report);
        var batch = await ops.BeginManualCoordinatedBatchAsync(client, "request", ["10001"], null);
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
            batch = await ops.UpdateCoordinatedBatchAsync(client, batch.Generation, "token", batch.BatchId, "group", 0, 0, "", stage);
        report.HoeingBatchId = batch.BatchId;
        report.PlayerUid = "spoofed-id";
        report.PlayerName = "伪造姓名";
        report.AutoHoeingRunning = true;
        report.HoeingStartPhase = "completed";
        await ops.ReportControlStatusAsync(client, report);
        var observed = Assert.Single(history.ReadToday("ABC123", DateTime.UtcNow).Runs);
        Assert.Equal("running", observed.Status);
        Assert.Equal(new RoomHoeingMember("10001", "真实成员"), Assert.Single(observed.Members));
        batch = await ops.UpdateCoordinatedBatchAsync(client, batch.Generation, "token", batch.BatchId, "group", 0, 0, "succeeded");
        Assert.Equal("completed", batch.Phase);
        Assert.Equal("completed", history.ReadToday("ABC123", DateTime.UtcNow).Status);
        Assert.Equal("completed", new RoomHoeingHistory(_root).ReadToday("ABC123", DateTime.UtcNow).Status);

        static ControlStatus withRoom(ControlStatus status, string code) { status.RoomCode = code; return status; }
    }

    [Fact]
    public async Task DisconnectImmediatelyMarksObservedRunUnknownBeforeMemberIdentityIsCleared()
    {
        var history = new RoomHoeingHistory(_root);
        var h = new GatewayTestHarness();
        var broadcaster = new GatewayBroadcaster(h.LegacyHub.Object, h.GatewayHub.Object, h.Tracker, Mock.Of<ILogger<GatewayBroadcaster>>());
        var ops = new RoomOperations(h.RoomManager, Mock.Of<ILogger<RoomOperations>>(), broadcaster,
            new RoomPhaseObserver(Mock.Of<ILogger<RoomPhaseObserver>>()), h.Capabilities, hoeingHistory: history);
        var client = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await ops.JoinControlRoomAsync(client, "ABC123", "pw", "10001", "成员甲", clientInstanceId: "instance");
        await ops.ReportControlStatusAsync(client, new ControlStatus
        { RoomCode = "ABC123", AutoHoeingRunning = true, TaskStatusAvailable = true });
        Assert.Equal("running", history.ReadToday("ABC123", DateTime.UtcNow).Status);

        await ops.HandleDisconnectAsync(client, null);

        var run = Assert.Single(history.ReadToday("ABC123", DateTime.UtcNow).Runs);
        Assert.Equal("unknown", run.Status);
        Assert.Null(run.EndedAtUtc);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
