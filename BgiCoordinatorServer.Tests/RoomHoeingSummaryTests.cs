using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomHoeingSummaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bgi-summary-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Day = new(2026, 11, 26);
    private static readonly DateTime Midnight = new(2026, 11, 25, 16, 0, 0, DateTimeKind.Utc);
    private static readonly RoomHoeingMember[] Members = [new("10001", "甲"), new("10002", "乙")];
    private static RoomHoeingRun Run(string id, double from, double to, string status = "completed", bool started = true) =>
        new(id, Midnight.AddHours(from), started ? Midnight.AddHours(from) : null, Midnight.AddHours(to),
            status is "running" or "unknown" or "preparing" ? null : Midnight.AddHours(to), status, "", Members);
    private static HoeingSummary Build(DateOnly start, DateOnly end, params HoeingSummarySource[] rooms) =>
        RoomHoeingSummary.Create(start, end, Midnight.AddHours(12), rooms, new Dictionary<string, string> { ["ABC123"] = "测试队" }, null, []);

    [Fact]
    public void CrossMidnightAndOverlappingBatchesAreClippedAndUnionedPerRoom()
    {
        var result = Build(Day.AddDays(-1), Day, new("ABC123", [Run("a", -2, 2), Run("b", 1, 3)], []),
            new("DEF456", [Run("c", 1, 3)], []));
        Assert.Equal(7 * 3600, result.Seconds);
        Assert.Equal(2 * 3600, result.Days[0].Seconds);
        Assert.Equal(5 * 3600, result.Days[1].Seconds);
        Assert.Equal(2, result.RoomCount);
        Assert.Equal(2, result.MemberCount); // same UID in different rooms is not two players
        Assert.Equal(3, result.Counts.Started);
        Assert.Equal(3, result.Counts.Completed);
        Assert.Equal(1d, result.Counts.CompletionRate);
        Assert.Equal("测试队", result.Rooms[0].Note);
        Assert.Equal(5 * 3600, result.Rooms[0].Seconds);
        Assert.Equal(4 * 3600, result.Rooms[0].LongestSeconds);
        var today = Build(Day, Day, new HoeingSummarySource("ABC123", [Run("a", -2, 2), Run("b", 1, 3)], []));
        Assert.Equal(3 * 3600, today.Seconds);
        Assert.Equal(1, today.Counts.Started);
        Assert.Equal(1, today.Counts.Continued);
        Assert.Equal(2, today.Counts.Completed);
    }

    [Fact]
    public void UnknownStopsAtObservationAndOnlyActualStartsCount()
    {
        var result = Build(Day, Day, new HoeingSummarySource("ABC123", [Run("unknown", 0, 1, "unknown"),
            Run("error", 2, 3, "completedWithErrors"), Run("stop", 3, 4, "stopped"), Run("fail", 4, 5, "failed"),
            Run("done", 5, 6), Run("running", 10, 11, "running"), Run("attempt", 7, 8, "completed", false),
            Run("preparing", 11, 11, "preparing", false)], []));
        Assert.Equal(7 * 3600, result.Seconds);
        Assert.Equal(6, result.Counts.Started);
        Assert.Equal(1, result.Counts.Completed);
        Assert.Equal(1, result.Counts.CompletedWithErrors);
        Assert.Equal(1, result.Counts.Stopped);
        Assert.Equal(1, result.Counts.Failed);
        Assert.Equal(1, result.Counts.Unknown);
        Assert.Equal(2, result.Counts.NotStarted);
        Assert.Equal(1, result.RunningRoomCount);
        Assert.Equal(.25, result.Counts.CompletionRate);
        Assert.Equal(3600, result.Rooms[0].Runs.Single(row => row.Run.Id == "unknown").Seconds);
    }

    [Fact]
    public void NoObservedStartDoesNotInventParticipantsOrDuration()
    {
        var result = Build(Day, Day, new("ABC123", [Run("attempt", 0, 1, "completed", false)], []),
            new("DEF456", [], ["损坏"]));
        Assert.Equal(2, result.Rooms.Length);
        Assert.Equal(0, result.RoomCount);
        Assert.Equal(0, result.MemberCount);
        Assert.Equal(0, result.Seconds);
        Assert.Null(result.Counts.CompletionRate);
        Assert.Contains("损坏", result.Warnings);
    }

    [Fact]
    public void PersistedOfflineHistoryAndOnlyConfirmedWorldLinksAreIncluded()
    {
        var history = new RoomHoeingHistory(Path.Combine(_root, "hoeing-history"));
        var batch = new CoordinatedBatchSnapshot("batch", 1, 0, 0, 1, "registering", "") { ParticipantUids = ["10001", "10002"] };
        history.ObserveBatch("ABC123", batch, Members, Midnight);
        history.ObserveMember("ABC123", "10001", "甲", "instance", new ControlStatus { AutoHoeingRunning = true, TaskStatusAvailable = true }, "batch", Midnight);
        history.ObserveBatch("ABC123", batch with { Revision = 2, Phase = "completed" }, Members, Midnight.AddHours(1));
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("ABC123", "WLD001", "one", Midnight, Midnight, "batch", "10001", "甲");
        archive.RecordRoomLink("ABC123", "WLD002", "two", Midnight, Midnight, "different", "10001", "甲");
        archive.RecordRoomLink("DEF456", "WLD003", "three", Midnight, Midnight, "batch", "10001", "甲");
        var now = Midnight.AddHours(2);
        var (rooms, warnings) = new RoomHoeingHistory(Path.Combine(_root, "hoeing-history")).ReadSummarySource(now);
        var result = RoomHoeingSummary.Create(Day, Day, now, rooms, null, archive.ReadSummaryLogHistory(), warnings);
        var row = Assert.Single(Assert.Single(result.Rooms).Runs);
        Assert.Equal(3600, row.Seconds);
        Assert.Equal("WLD001", Assert.Single(row.Worlds).Code);
        Assert.Equal(1, result.Counts.Completed);
    }

    [Fact]
    public void RestartAndExpiredRunningObservationAreNotExtendedToNow()
    {
        var history = new RoomHoeingHistory(_root);
        var status = new ControlStatus { AutoHoeingRunning = true, TaskStatusAvailable = true };
        history.ObserveMember("ABC123", "10001", "甲", "instance", status, null, Midnight);
        history.ObserveMember("ABC123", "10001", "甲", "instance", status, null, Midnight.AddMinutes(1));
        var now = Midnight.AddHours(8);
        foreach (var store in new[] { new RoomHoeingHistory(_root), history })
        {
            var (rooms, warnings) = store.ReadSummarySource(now);
            var result = RoomHoeingSummary.Create(Day, Day, now, rooms, null, null, warnings);
            Assert.Equal(60, result.Seconds);
            Assert.Equal(0, result.RunningRoomCount);
            Assert.Equal(1, result.Counts.Unknown);
        }
    }

    [Fact]
    public void RangeValidationRejectsFutureReverseAndExcessiveRanges()
    {
        var now = Midnight.AddHours(12);
        Assert.True(RoomHoeingSummary.IsValidRange(Day.AddDays(-365), Day, now));
        Assert.False(RoomHoeingSummary.IsValidRange(Day.AddDays(-366), Day, now));
        Assert.False(RoomHoeingSummary.IsValidRange(Day, Day.AddDays(1), now));
        Assert.False(RoomHoeingSummary.IsValidRange(Day, Day.AddDays(-1), now));
        Assert.False(RoomHoeingSummary.IsValidRange(DateOnly.MinValue, DateOnly.MinValue, now));
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
