using System.Text;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomLogHistoryTests
{
    private readonly string _root = Path.Combine(Environment.GetEnvironmentVariable("ROOM_LOG_TEST_ROOT") ?? Path.GetTempPath(),
        "room-history-tests-" + Guid.NewGuid().ToString("N"));
    private static readonly DateTime Start = new(2026, 10, 18, 5, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task PersistsLinksAndCollectsEveryHistoricalSessionWithExactSessionIsolation()
    {
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("CTR123", "GAME01", "session-one", Start, Start);
        Add(archive, "GAME01", "旧锄地首条", Start, "session-one");
        archive.EndRoomLink("GAME01", "session-one", Start.AddMinutes(1));
        // 离开事件可能在删除房间后的同一调用尾部才记入，精确会话仍须收齐。
        Add(archive, "GAME01", "旧锄地尾条", Start.AddMinutes(1).AddMilliseconds(2), "session-one");
        archive.RecordRoomLink("OTHER1", "GAME01", "session-other", Start.AddMinutes(2), Start.AddMinutes(2));
        Add(archive, "GAME01", "其他控制房间同码会话", Start.AddMinutes(2), "session-other");
        archive.RecordRoomLink("CTR123", "GAME02", "session-two", Start.AddMinutes(3), Start.AddMinutes(3));
        Add(archive, "GAME02", "新锄地记录", Start.AddMinutes(3), "session-two");
        Add(archive, "CTR123", "控制房间原始记录", Start.AddMinutes(4));

        var restarted = new RoomLogArchive(_root);
        var (text, metadata) = await Read(restarted, "CTR123");

        Assert.Equal(4, metadata.Count);
        Assert.Contains("旧锄地首条", text);
        Assert.Contains("旧锄地尾条", text);
        Assert.Contains("新锄地记录", text);
        Assert.Contains("控制房间原始记录", text);
        Assert.DoesNotContain("其他控制房间同码会话", text);
        Assert.Equal(new[] { "CTR123", "GAME01", "GAME02" }, metadata.Sources);
        Assert.Contains(restarted.GetAvailableRooms(new DateOnly(2026, 10, 19)), room => room.Code == "CTR123" && room.Kind == "control");
        Assert.Contains(new DateOnly(2026, 10, 18), restarted.GetAvailableDates("CTR123", new DateOnly(2026, 10, 19)));
    }

    [Fact]
    public async Task SortsOutOfOrderRecordsAndDeduplicatesOnlyTheSamePersistedEvent()
    {
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("CTR123", "GAME01", "session-one", Start, Start);
        Add(archive, "GAME01", "最后时间", Start.AddMinutes(2), "session-one");
        archive.Append(["CTR123", "GAME01"], new RoomServerLogEntry(Start, "Information", "Test", "同一事件", "session-one"));
        Add(archive, "GAME01", "相同消息", Start.AddMinutes(1), "session-one");
        Add(archive, "GAME01", "相同消息", Start.AddMinutes(1), "session-one");

        var (text, metadata) = await Read(archive, "CTR123");

        Assert.Equal(4, metadata.Count);
        Assert.Equal(1, text.Split("同一事件").Length - 1);
        Assert.Equal(2, text.Split("相同消息").Length - 1);
        Assert.True(text.IndexOf("同一事件", StringComparison.Ordinal) < text.IndexOf("最后时间", StringComparison.Ordinal));
        Assert.Equal(Start, metadata.FirstTimestampUtc);
        Assert.Equal(Start.AddMinutes(2), metadata.LastTimestampUtc);
    }

    [Fact]
    public async Task RestoresLegacySampleFromFullRoundOrderAndStopsAtNextSameCodeCreation()
    {
        Directory.CreateDirectory(_root);
        WriteLegacy("DC54A4", Entry("13:00:00.000", "玩家 火山老师(298569302) 加入控制房间 DC54A4 (Web=False, Remote=False)"));
        WriteLegacy("SLZDWH", Entry("13:47:41.049", "连接 old(火山老师) 创建房间 SLZDWH") +
            Entry("13:48:04.650", "[RoundOrder] 房间 SLZDWH 生成权威轮换序列（排除 0 已完成）：298569302 -> 100195290 -> 105615641 -> 178691574") +
            Entry("14:00:00.000", "历史锄地正文") +
            Entry("15:00:00.000", "连接 new(另一人) 创建房间 SLZDWH") +
            Entry("15:01:00.000", "后续同码未证归属，不可混入"));

        var (text, metadata) = await Read(new RoomLogArchive(_root), "DC54A4");

        Assert.Equal(4, metadata.Count);
        Assert.Contains("历史锄地正文", text);
        Assert.DoesNotContain("后续同码未证归属", text);
        Assert.DoesNotContain("连接 new", text);
        Assert.Contains(metadata.Warnings, warning => warning.Contains("完整权威UID名单", StringComparison.Ordinal));
        Assert.Contains(new RoomLogArchive(_root).GetAvailableRooms(new DateOnly(2026, 10, 18)), room => room.Code == "DC54A4" && room.Kind == "control");
    }

    [Fact]
    public async Task DoesNotInferFromPartialRosterOrMissingCreationOrUnprovedControlCode()
    {
        Directory.CreateDirectory(_root);
        WriteLegacy("DC54A4", Entry("13:00:00.000", "玩家 火山老师(298569302) 加入控制房间 DC54A4 (Web=False, Remote=False)"));
        WriteLegacy("GAME01", Entry("13:01:00.000", "[RoundOrder] 房间 GAME01 生成权威轮换序列（排除 0 已完成）：298569302 -> 100195290 -> 105615641 -> 178691574") +
            Entry("13:02:00.000", "缺建房边界"));
        WriteLegacy("GAME02", Entry("13:01:00.000", "连接 old(玩家) 创建房间 GAME02") +
            Entry("13:02:00.000", "[RoundOrder] 房间 GAME02 生成权威轮换序列（排除 1 已完成）：298569302 -> 100195290 -> 105615641 -> 178691574") +
            Entry("13:03:00.000", "缺完整名单"));

        var (text, metadata) = await Read(new RoomLogArchive(_root), "DC54A4");

        Assert.Equal(1, metadata.Count);
        Assert.DoesNotContain("缺建房边界", text);
        Assert.DoesNotContain("缺完整名单", text);
    }

    [Fact]
    public async Task CorruptJournalLeavesDirectLogsDownloadableAndReportsMissingAssociations()
    {
        var archive = new RoomLogArchive(_root);
        Add(archive, "CTR123", "控制日志保留", Start);
        File.WriteAllText(Path.Combine(_root, "room-links.jsonl"), "{interrupted");

        var (text, metadata) = await Read(new RoomLogArchive(_root), "CTR123");

        Assert.Contains("控制日志保留", text);
        Assert.Contains(metadata.Warnings, warning => warning.Contains("索引保存失败或损坏", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableDayDisablesLegacySessionInferenceAcrossDatesWithoutLosingExplicitSessions()
    {
        Directory.CreateDirectory(_root);
        WriteLegacy("DC54A4", Entry("13:00:00.000", "玩家 火山老师(298569302) 加入控制房间 DC54A4 (Web=False, Remote=False)"));
        var legacy = "房间 SLZDWH\n首条\n旧版说明\n\n" +
            Entry("13:01:00.000", "连接 old(玩家) 创建房间 SLZDWH") +
            Entry("13:02:00.000", "[RoundOrder] 房间 SLZDWH 生成权威轮换序列（排除 0 已完成）：298569302 -> 100195290 -> 105615641 -> 178691574") +
            Entry("13:03:00.000", "旧推断不再可靠");
        File.WriteAllText(Path.Combine(_root, "2026-10-17-SLZDWH.log"), legacy.Replace("2026-10-18", "2026-10-17"));
        File.WriteAllText(Path.Combine(_root, "2026-10-18-SLZDWH.log"), "房间 SLZDWH\n首条\n旧版说明\n\n损坏的会话边界");
        var archive = new RoomLogArchive(_root);
        archive.RecordRoomLink("DC54A4", "SLZDWH", "explicit-session", Start.AddDays(1), Start.AddDays(1));
        Add(archive, "SLZDWH", "明确会话仍可读取", Start.AddDays(1), "explicit-session");
        Assert.True(RoomLogRange.TryParse("2026-10-17T00:00", "2026-10-17T23:59", out var oldRange, out _));
        Assert.Null(await archive.OpenRangeAsync("DC54A4", oldRange!));
        Assert.True(RoomLogRange.TryParse("2026-10-19T00:00", "2026-10-19T23:59", out var range, out _));

        await using var download = await archive.OpenRangeAsync("DC54A4", range!);
        Assert.NotNull(download);
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        var text = Encoding.UTF8.GetString(output.ToArray());

        Assert.Contains("明确会话仍可读取", text);
        Assert.DoesNotContain("旧推断不再可靠", text);
        Assert.Contains(download.Metadata.Warnings, warning => warning.Contains("已停用该房间的旧版推断关联", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DownloadHasNoDateOrCacheLimitAndDisposesAllTemporaryFiles()
    {
        var archive = new RoomLogArchive(_root);
        for (var index = 0; index < 5301; index++)
            Add(archive, "CTR123", "大量记录" + index, Start.AddDays(index % 65));
        Assert.True(RoomLogRange.TryParse("2026-10-01T00:00", "2026-12-31T23:59", out var range, out _));
        await using (var download = await archive.OpenRangeAsync("CTR123", range!))
        {
            Assert.NotNull(download);
            Assert.Equal(5301, download.Metadata.Count);
        }
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(_root, ".downloads"), "*", SearchOption.AllDirectories));
        Assert.Empty(Directory.EnumerateDirectories(Path.Combine(_root, ".downloads")));
    }

    [Fact]
    public async Task CancelledDownloadReleasesAllTemporaryFiles()
    {
        var archive = new RoomLogArchive(_root);
        Add(archive, "CTR123", "记录", Start);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => archive.OpenRangeAsync("CTR123", Range(), cancellation.Token));
        var scratch = Path.Combine(_root, ".downloads");
        Assert.False(Directory.Exists(scratch) && Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories).Any());
    }

    private void WriteLegacy(string code, string entries) => File.WriteAllText(Path.Combine(_root, $"2026-10-18-{code}.log"),
        $"房间 {code} · 2026-10-18 服务器日志（北京时间 UTC+08:00）\n本文件首条记录\n旧版保存说明\n\n" + entries, new UTF8Encoding(true));
    private static string Entry(string time, string message) =>
        $"[2026-10-18 {time} +08:00] [Information] BgiCoordinatorServer.Services.RoomOperations\n{message}\n\n";
    private static void Add(RoomLogArchive archive, string code, string message, DateTime time, string? sessionId = null) =>
        archive.Append([code], new RoomServerLogEntry(time, "Information", "Test", message, sessionId));
    private static RoomLogRange Range()
    {
        Assert.True(RoomLogRange.TryParse("2026-10-18T00:00", "2026-10-18T23:59", out var range, out _));
        return range!;
    }
    private static async Task<(string Text, RoomLogDownloadMetadata Metadata)> Read(RoomLogArchive archive, string code)
    {
        await using var download = await archive.OpenRangeAsync(code, Range());
        Assert.NotNull(download);
        using var output = new MemoryStream();
        await download.CopyToAsync(output);
        return (Encoding.UTF8.GetString(output.ToArray()), download.Metadata);
    }
}
