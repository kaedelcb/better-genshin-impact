using System.Text;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RoomLogRangeTests
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("ROOM_LOG_TEST_ROOT") ?? Path.GetTempPath(),
        "room-log-range-tests-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task IncludesCompleteStartAndEndMinutesWithMillisecondBoundaries()
    {
        var archive = new RoomLogArchive(_root);
        Append(archive, new DateTime(2026, 10, 15, 10, 14, 59, 999), "开始前");
        Append(archive, new DateTime(2026, 10, 15, 10, 15, 0), "开始整分");
        Append(archive, new DateTime(2026, 10, 15, 10, 16, 59, 999), "结束分钟最后毫秒");
        Append(archive, new DateTime(2026, 10, 15, 10, 17, 0), "结束后");

        var text = await Read(archive, "ABC123", "2026-10-15T10:15", "2026-10-15T10:16");

        Assert.Contains("开始整分", text);
        Assert.Contains("结束分钟最后毫秒", text);
        Assert.DoesNotContain("开始前", text);
        Assert.DoesNotContain("结束后", text);
        Assert.Contains("包含结束分钟", text);
    }

    [Fact]
    public async Task CrossesBeijingMidnightInOrderAndIsolatesOtherRooms()
    {
        var archive = new RoomLogArchive(_root);
        Append(archive, new DateTime(2026, 10, 14, 23, 58, 59), "昨天范围外");
        Append(archive, new DateTime(2026, 10, 14, 23, 59, 0), "昨天范围内");
        Append(archive, new DateTime(2026, 10, 15, 0, 0, 59, 999), "今天范围内");
        Append(archive, new DateTime(2026, 10, 15, 0, 1, 0), "今天范围外");
        Append(archive, new DateTime(2026, 10, 15, 0, 0, 0), "其他房间", "DEF456");

        var text = await Read(archive, "CTRL_ABC123", "2026-10-14T23:59", "2026-10-15T00:00");

        Assert.True(text.IndexOf("昨天范围内", StringComparison.Ordinal) < text.IndexOf("今天范围内", StringComparison.Ordinal));
        Assert.DoesNotContain("昨天范围外", text);
        Assert.DoesNotContain("今天范围外", text);
        Assert.DoesNotContain("其他房间", text);
    }

    [Fact]
    public async Task ReadsExistingTextArchiveAndKeepsLongMessagesAndExceptionLines()
    {
        Directory.CreateDirectory(_root);
        var longMessage = new string('长', 100_000) + "完整末尾";
        var exception = "System.InvalidOperationException: 异常原因\n   at Example.Work()\n\n内部补充\n[2026-10-15 09:11:00.000 +08:00] [NotALevel] 消息正文";
        var legacy = "房间 ABC123 · 2026-10-15 服务器日志（北京时间 UTC+08:00）\n" +
            "本文件首条记录：2026-10-15 09:10:00.000 +08:00\n" +
            "旧版保存说明\n\n" +
            "[2026-10-15 09:10:00.000 +08:00] [Error] \n" +
            longMessage + "\n" + exception + "\n\n" +
            "[2026-10-15 09:11:00.000 +08:00] [Information] BgiCoordinatorServer.Test\n范围外记录\n\n";
        await File.WriteAllTextAsync(Path.Combine(_root, "2026-10-15-ABC123.log"), legacy, new UTF8Encoding(true));

        var text = await Read(new RoomLogArchive(_root), "ABC123", "2026-10-15T09:10", "2026-10-15T09:10");

        Assert.Contains(longMessage, text);
        Assert.Contains(exception.Replace("\n", Environment.NewLine), text);
        Assert.DoesNotContain("范围外记录", text);
        Assert.DoesNotContain("本文件首条记录", text);
        Assert.Contains("旧版未保存的日志无法补回", text);
    }

    [Fact]
    public async Task RangeReadsAllSavedRecordsWithoutPreviewLimits()
    {
        var archive = new RoomLogArchive(_root);
        for (var index = 0; index < 5206; index++)
            Append(archive, new DateTime(2026, 10, 15, 9, 0, 0), $"归档记录 {index:D4}");

        var text = await Read(new RoomLogArchive(_root), "ABC123", "2026-10-15T09:00", "2026-10-15T09:00");

        Assert.Equal(5206, text.Split("归档记录 ").Length - 1);
        Assert.Contains("归档记录 0000", text);
        Assert.Contains("归档记录 5205", text);
    }

    [Fact]
    public async Task ReturnsNullWhenSelectedMinutesHaveNoRecords()
    {
        var archive = new RoomLogArchive(_root);
        Append(archive, new DateTime(2026, 10, 15, 10, 0, 0), "其他时间");
        var range = Parse("2026-10-15T11:00", "2026-10-15T11:30");

        Assert.Null(await archive.OpenRangeAsync("ABC123", range));
        Assert.Null(await archive.OpenRangeAsync("NOLOG1", range));
    }

    [Theory]
    [InlineData(null, "2026-10-15T10:00")]
    [InlineData("2026-10-15 09:00", "2026-10-15T10:00")]
    [InlineData("2026-10-15T09:00:00", "2026-10-15T10:00")]
    [InlineData("2026-10-15T09:00+08:00", "2026-10-15T10:00")]
    [InlineData("2026-02-30T09:00", "2026-10-15T10:00")]
    [InlineData("2026-10-15T10:01", "2026-10-15T10:00")]
    [InlineData("2026-10-01T23:59", "2026-11-01T00:00")]
    [InlineData("9999-12-31T23:59", "9999-12-31T23:59")]
    public void RejectsMissingMalformedReversedOrOverlongRanges(string? start, string? end)
    {
        Assert.False(RoomLogRange.TryParse(start, end, out var range, out var error));
        Assert.Null(range);
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void AllowsOneMinuteOrAtMost31CalendarDates()
    {
        var minute = Parse("2026-10-15T10:00", "2026-10-15T10:00");
        Assert.Equal(TimeSpan.FromMinutes(1), minute.EndExclusive - minute.StartInclusive);

        var month = Parse("2026-10-01T00:00", "2026-10-31T23:59");
        Assert.Equal(new DateOnly(2026, 10, 1), month.FirstDate);
        Assert.Equal(new DateOnly(2026, 10, 31), month.LastDate);
    }

    [Fact]
    public async Task LengthSnapshotsExcludeAppendsAfterOpeningAllSelectedDates()
    {
        var archive = new RoomLogArchive(_root);
        Append(archive, new DateTime(2026, 10, 14, 23, 59, 0), "昨天点击前");
        Append(archive, new DateTime(2026, 10, 15, 0, 0, 0), "今天点击前");
        await using var download = await archive.OpenRangeAsync("ABC123", Parse("2026-10-14T23:59", "2026-10-15T00:00"));
        Assert.NotNull(download);
        Assert.Equal("room-ABC123-2026-10-14-2359-2026-10-15-0000.txt", download.FileName);
        Append(archive, new DateTime(2026, 10, 14, 23, 59, 10), "昨天点击后");
        Append(archive, new DateTime(2026, 10, 15, 0, 0, 10), "今天点击后");

        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("昨天点击前", text);
        Assert.Contains("今天点击前", text);
        Assert.DoesNotContain("昨天点击后", text);
        Assert.DoesNotContain("今天点击后", text);
    }

    [Fact]
    public async Task SaveFailuresRemainUnavailableAfterRestart()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "2026-10-15-ABC123.log");
        var archive = new RoomLogArchive(_root);
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            Append(archive, new DateTime(2026, 10, 15, 10, 0, 0), "保存失败");
        var range = Parse("2026-10-15T10:00", "2026-10-15T10:00");

        await Assert.ThrowsAsync<IOException>(() => archive.OpenRangeAsync("ABC123", range));
        await Assert.ThrowsAsync<IOException>(() => new RoomLogArchive(_root).OpenRangeAsync("ABC123", range));
    }

    [Fact]
    public async Task UnreadableArchiveOrInvalidRoomCannotProduceSuccessfulDownload()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "unreadable-root");
        await File.WriteAllTextAsync(path, "目录位置被文件占用");
        var unavailable = new RoomLogArchive(path);
        var range = Parse("2026-10-15T10:00", "2026-10-15T10:00");

        await Assert.ThrowsAsync<IOException>(() => unavailable.OpenRangeAsync("ABC123", range));
        Assert.Throws<IOException>(() => unavailable.GetAvailableDates("ABC123", new DateOnly(2026, 10, 15)));
        await Assert.ThrowsAsync<ArgumentException>(() => new RoomLogArchive(_root).OpenRangeAsync("../ABC123", range));
    }

    [Fact]
    public void DatesAreRoomSpecificUniqueDescendingAndAlwaysIncludeToday()
    {
        var archive = new RoomLogArchive(_root);
        Append(archive, new DateTime(2026, 10, 13, 23, 59, 59), "十三日日志");
        Append(archive, new DateTime(2026, 10, 14, 0, 0, 0), "十四日日志");
        Append(archive, new DateTime(2026, 10, 12, 12, 0, 0), "其他房间", "DEF456");
        File.WriteAllText(Path.Combine(_root, "2026-99-99-ABC123.log"), "无效文件名");

        Assert.Equal(new[] { new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 13) },
            archive.GetAvailableDates("CTRL_ABC123", new DateOnly(2026, 10, 15)));
        Assert.Equal(new[] { new DateOnly(2026, 10, 15) }, archive.GetAvailableDates("NOLOG1", new DateOnly(2026, 10, 15)));
    }

    private static void Append(RoomLogArchive archive, DateTime beijingTime, string message, string code = "ABC123") =>
        archive.Append([code], new RoomServerLogEntry(
            new DateTimeOffset(DateTime.SpecifyKind(beijingTime, DateTimeKind.Unspecified), TimeSpan.FromHours(8)).UtcDateTime,
            "Information", "BgiCoordinatorServer.Test", message));

    private static RoomLogRange Parse(string start, string end)
    {
        Assert.True(RoomLogRange.TryParse(start, end, out var range, out var error), error);
        return range!;
    }

    private static async Task<string> Read(RoomLogArchive archive, string code, string start, string end)
    {
        await using var download = await archive.OpenRangeAsync(code, Parse(start, end));
        Assert.NotNull(download);
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }
}
