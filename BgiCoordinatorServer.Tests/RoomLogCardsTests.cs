using System.Text;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomLogCardsTests : IDisposable
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("ROOM_LOG_TEST_ROOT")
        ?? Path.GetTempPath(), "room-cards-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 10, 26, 15, 55, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RestartKeepsDailyBatchesWorldsAndExactDownloadIsolation()
    {
        var archive = new RoomLogArchive(_root);
        var history = new RoomHoeingHistory(Path.Combine(_root, "history"));
        var batch = new CoordinatedBatchSnapshot("batch-one", 1, 0, 0, 1, "registering", "")
            { ParticipantUids = ["10001"] };
        history.ObserveBatch("CTRL_TEAM01", batch, [new("10001", "甲")], Start);
        archive.RecordRoomLink("TEAM01", "WORLD1", "session-one", Start, Start, "batch-one", "10001", "甲");
        Append(archive, "WORLD1", "session-one", "首世界", Start);
        archive.EndRoomLink("WORLD1", "session-one", Start.AddMinutes(10));
        archive.RecordRoomLink("TEAM01", "WORLD2", "session-two", Start.AddMinutes(11), Start.AddMinutes(11), "batch-one", "10002", "乙");
        Append(archive, "WORLD2", "session-two", "第二世界", Start.AddMinutes(12));
        archive.EndRoomLink("WORLD2", "session-two", Start.AddMinutes(13));
        // 同码、同一天的另一批不能混入。
        archive.RecordRoomLink("TEAM01", "WORLD2", "session-other", Start.AddMinutes(14), Start.AddMinutes(14), "batch-other");
        Append(archive, "WORLD2", "session-other", "另一批次", Start.AddMinutes(15));
        history.ObserveBatch("CTRL_TEAM01", batch with { Revision = 2, Phase = "completed" }, [new("10001", "甲")], Start.AddMinutes(13));

        var restarted = new RoomLogArchive(_root);
        var cards = restarted.GetCards(new(2026, 10, 27), Start.AddDays(1), new(Path.Combine(_root, "history")));
        var room = Assert.Single(cards.Rooms);
        Assert.Equal("TEAM01", room.Code);
        Assert.Equal(3, room.Worlds.Length);
        Assert.Equal("甲", room.Worlds[0].HostName);
        Assert.Equal("completed", Assert.Single(room.Hoeing.Runs).Status);
        Assert.Contains("2026-10-26", cards.Dates);
        Assert.Empty(cards.UnlinkedRooms);
        Assert.True(RoomLogRange.TryParse("2026-10-26T00:00", "2026-10-27T23:59", out var range, out _));
        var batchText = await Read(restarted, "TEAM01", range!, new(BatchId: "batch-one"));
        Assert.Contains("首世界", batchText);
        Assert.Contains("第二世界", batchText);
        Assert.DoesNotContain("另一批次", batchText);
        var worldText = await Read(restarted, "WORLD2", range!, new(SessionId: "session-two"));
        Assert.Contains("第二世界", worldText);
        Assert.DoesNotContain("首世界", worldText);
        Assert.DoesNotContain("另一批次", worldText);
        Assert.Null(await restarted.OpenRangeAsync("OTHER1", range!, selection: new(BatchId: "batch-one")));
        Assert.Null(await restarted.OpenRangeAsync("WORLD2", range!, selection: new(SessionId: "missing")));
    }

    [Fact]
    public void LegacyLinkWithoutBatchIsVisibleButNeverAssignedToNearbyBatch()
    {
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("TEAM01", "WORLD1", "old-session", Start, Start);
        Append(archive, "WORLD1", "old-session", "旧记录", Start);
        Append(archive, "ORPHAN", null, "只有房间号的旧日志", Start);
        var cards = new RoomLogArchive(_root).GetCards(new(2026, 10, 26), Start, new(Path.Combine(_root, "history")));
        Assert.Null(Assert.Single(Assert.Single(cards.Rooms).Worlds).BatchId);
        Assert.Equal(new[] { "ORPHAN" }, cards.UnlinkedRooms);
        Assert.Empty(new RoomLogArchive(_root).GetCards(new(2026, 10, 28), Start.AddDays(2),
            new(Path.Combine(_root, "history"))).Rooms);
    }

    [Fact]
    public void AddingVerifiedBatchPreservesEndedBoundaryAcrossReload()
    {
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("TEAM01", "WORLD1", "session", Start, Start);
        archive.EndRoomLink("WORLD1", "session", Start.AddMinutes(1));
        archive.RecordRoomLink("TEAM01", "WORLD1", "session", Start, Start.AddMinutes(2), "verified", "10001", "甲");
        var cards = new RoomLogArchive(_root).GetCards(new(2026, 10, 26), Start, new(Path.Combine(_root, "history")));
        var world = Assert.Single(Assert.Single(cards.Rooms).Worlds);
        Assert.Equal("verified", world.BatchId);
        Assert.Equal(Start.AddMinutes(1), world.EndedAtUtc);
    }

    [Fact]
    public async Task MetadataAndDirectDownloadDoNotReadUnrelatedLogBodies()
    {
        var archive = new RoomLogArchive(_root);
        Append(archive, "WORLD1", null, "可直接下载", Start);
        var lockedPath = Path.Combine(_root, "2026-10-26-LOCKED.log");
        File.WriteAllText(lockedPath, "旧格式或损坏文件");
        using var locked = new FileStream(lockedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Contains(new DateOnly(2026, 10, 26), archive.GetAvailableDates("WORLD1", new(2026, 10, 27), includeLegacy: false));
        Assert.DoesNotContain(new DateOnly(2026, 10, 27), archive.GetAvailableDates("WORLD1", new(2026, 10, 27), includeLegacy: false));
        Assert.Contains(archive.GetAvailableRooms(new(2026, 10, 27), includeLegacy: false), room => room.Code == "WORLD1");
        Assert.Contains("WORLD1", archive.GetCards(new(2026, 10, 26), Start, new(Path.Combine(_root, "history"))).UnlinkedRooms);
        Assert.True(RoomLogRange.TryParse("2026-10-26T00:00", "2026-10-26T23:59", out var range, out _));
        Assert.Contains("可直接下载", await Read(archive, "WORLD1", range!, new(Direct: true)));
    }

    [Fact]
    public void ActualCrossMidnightObservationIsSavedWithoutReusingOldCodeOwnership()
    {
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("TEAM01", "WORLD1", "first", Start, Start, "batch-one");
        Append(archive, "WORLD1", "first", "跨日仍在本次会话", Start.AddMinutes(10));
        var restarted = new RoomLogArchive(_root);
        Assert.Single(restarted.GetCards(new(2026, 10, 27), Start.AddDays(1), new(Path.Combine(_root, "history"))).Rooms);
        restarted.RecordRoomLink("TEAM02", "WORLD1", "second", Start.AddDays(2), Start.AddDays(2), "batch-two");
        Append(restarted, "WORLD1", "second", "复用房间号", Start.AddDays(2));
        var later = new RoomLogArchive(_root).GetCards(new(2026, 10, 28), Start.AddDays(2), new(Path.Combine(_root, "history")));
        Assert.Equal("TEAM02", Assert.Single(later.Rooms).Code);
    }

    private static void Append(RoomLogArchive archive, string code, string? session, string message, DateTime time) =>
        archive.Append([code], new(time, "Information", "CardsTest", message, session));

    private static async Task<string> Read(RoomLogArchive archive, string code, RoomLogRange range, RoomLogSelection selection)
    {
        await using var download = await archive.OpenRangeAsync(code, range, selection: selection);
        Assert.NotNull(download);
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
