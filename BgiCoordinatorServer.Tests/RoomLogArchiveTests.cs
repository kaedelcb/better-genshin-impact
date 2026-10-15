using System.Text;
using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RoomLogArchiveTests
{
    private readonly string _root = Path.Combine(
        Environment.GetEnvironmentVariable("ROOM_LOG_TEST_ROOT") ?? Path.GetTempPath(),
        "room-log-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Today = new(2026, 10, 15, 3, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Date = new(2026, 10, 15);

    [Fact]
    public async Task DownloadSurvivesCacheEvictionAndRestartWithoutTruncatingMessages()
    {
        var archive = new RoomLogArchive(_root);
        var store = new RoomLogStore(archive, new FixedClock(Today));
        for (var index = 0; index < 5205; index++)
            store.Append(["ABC123"], LogLevel.Information, "Test", $"记录 {index:D4}");
        var longMessage = new string('长', 3000) + "消息末尾";
        store.Append(["ABC123"], LogLevel.Error, "Test", longMessage);
        Assert.Equal(200, store.Read("ABC123").Count);
        Assert.DoesNotContain("消息末尾", store.Read("ABC123")[0].Message);

        // 重新创建存储，等价于重启后继续读取同一持久目录。
        var restarted = new RoomLogArchive(_root);
        restarted.Append(["ABC123"], new RoomServerLogEntry(Today.AddHours(1), "Warning", "Test", "重启后追加"));
        var text = await Read(restarted, "ABC123", Date);
        Assert.Contains("记录 0000", text);
        Assert.Contains("记录 5204", text);
        Assert.Contains(longMessage, text);
        Assert.Contains("重启后追加", text);
        Assert.Equal(5205, text.Split("记录 ").Length - 1);
        Assert.Equal(1, text.Split("本文件首条记录").Length - 1);
    }

    [Fact]
    public async Task UsesBeijingMidnightAndIsolatesOtherRooms()
    {
        var archive = new RoomLogArchive(_root);
        var beforeMidnight = new DateTime(2026, 10, 14, 15, 59, 59, DateTimeKind.Utc);
        new RoomLogStore(archive, new FixedClock(beforeMidnight))
            .Append(["ABC123"], LogLevel.Information, "Test", "昨天记录");
        new RoomLogStore(archive, new FixedClock(beforeMidnight.AddSeconds(1)))
            .Append(["CTRL_ABC123"], LogLevel.Information, "Test", "今天零点记录");
        new RoomLogStore(archive, new FixedClock(Today))
            .Append(["DEF456"], LogLevel.Information, "Test", "其他房间记录");

        var text = await Read(archive, "ABC123", Date);
        Assert.Contains("今天零点记录", text);
        Assert.Contains("2026-10-15 00:00:00.000 +08:00", text);
        Assert.DoesNotContain("昨天记录", text);
        Assert.DoesNotContain("其他房间记录", text);
        Assert.Equal(new DateOnly(2026, 10, 14), RoomLogArchive.GetDate(beforeMidnight));
        Assert.Equal(Date, RoomLogArchive.GetDate(beforeMidnight.AddSeconds(1)));
        Assert.Null(archive.OpenDay("NOLOG1", Date));
        Assert.Throws<ArgumentException>(() => archive.OpenDay("../ABC123", Date));
    }

    [Fact]
    public async Task DownloadStopsAtRequestBoundaryWhileNewLogsContinue()
    {
        var archive = new RoomLogArchive(_root);
        var store = new RoomLogStore(archive, new FixedClock(Today));
        store.Append(["ABC123"], LogLevel.Information, "Test", "点击前");
        await using var download = archive.OpenDay("ABC123", Date)!;
        Assert.Equal("room-ABC123-2026-10-15.txt", download.FileName);
        store.Append(["ABC123"], LogLevel.Information, "Test", "点击后");
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("点击前", text);
        Assert.DoesNotContain("点击后", text);
        Assert.Contains("点击后", await Read(archive, "ABC123", Date));
    }

    [Fact]
    public void FailedSaveDoesNotBreakPreviewOrAllowACompleteDownload()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "2026-10-15-ABC123.log");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var archive = new RoomLogArchive(_root);
            var store = new RoomLogStore(archive, new FixedClock(Today));
            store.Append(["ABC123"], LogLevel.Information, "Test", "保存失败但业务继续");
            Assert.Single(store.Read("ABC123"));
            Assert.Throws<IOException>(() => archive.OpenDay("ABC123", Date));
        }
        Assert.True(File.Exists(path + ".incomplete"));
        Assert.Throws<IOException>(() => new RoomLogArchive(_root).OpenDay("ABC123", Date));
    }

    [Fact]
    public async Task ProviderSavesFullExceptionAndExcludesUnassociatedGlobalLogs()
    {
        var archive = new RoomLogArchive(_root);
        using var provider = new RoomLogProvider(new RoomLogStore(archive, new FixedClock(Today)));
        var logger = provider.CreateLogger("Test");
        logger.LogError(new InvalidOperationException("异常详细原因"), "房间 {RoomCode} 失败", "ABC123");
        logger.LogError("全服消息不能出现在房间日志");
        var text = await Read(archive, "ABC123", Date);
        Assert.Contains("System.InvalidOperationException: 异常详细原因", text);
        Assert.DoesNotContain("全服消息", text);
    }

    private static async Task<string> Read(RoomLogArchive archive, string code, DateOnly date)
    {
        await using var download = archive.OpenDay(code, date);
        Assert.NotNull(download);
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        return Encoding.UTF8.GetString(output.ToArray());
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc);
    }
}
