using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class RoomLogLinkageTests
{
    private static readonly string[] Team = ["100195290", "105615641", "178691574", "298569302"];

    [Fact]
    public async Task FullAuthenticatedRosterLinksEarlierGameLogsAfterFinalMemberJoins()
    {
        using var logs = new ContextLogHarness();
        logs.Context.RegisterControlRoster("DC54A4", Team[0], Team);
        var code = logs.Manager.CreateRoom("host", "房主", playerUid: Team[0], expectedPlayerCount: 4);
        var logger = logs.Factory.CreateLogger("Game");
        using (logs.Context.BeginConnectionScope(logger, "host")) logger.LogInformation("满员前已保存的建房记录");
        Assert.Null(await ReadControlDownload(logs));
        for (var index = 1; index < Team.Length; index++)
            Assert.True(logs.Manager.JoinRoom(code, "member" + index, "player" + index, "成员" + index, Team[index]).Success);
        logs.Context.ObserveGameRoom(logs.Manager.GetRoom(code)!);

        var text = await ReadControlDownload(logs);
        Assert.Contains("满员前已保存的建房记录", text);
        Assert.Contains(code, text);
        Assert.Contains("完整成员名单", text);
    }

    [Fact]
    public async Task SharedMemberAndPartialRosterDoNotMergeUnrelatedGameRooms()
    {
        using var logs = new ContextLogHarness();
        logs.Context.RegisterControlRoster("DC54A4", Team[0], Team);
        var code = logs.Manager.CreateRoom("host", "房主", playerUid: Team[0], expectedPlayerCount: 4);
        for (var index = 1; index < Team.Length; index++)
            logs.Manager.JoinRoom(code, "outsider" + index, "outsider" + index, "其他成员" + index, "999000" + index);
        logs.Context.ObserveGameRoom(logs.Manager.GetRoom(code)!);
        var logger = logs.Factory.CreateLogger("Game");
        using (logs.Context.BeginConnectionScope(logger, "host")) logger.LogInformation("不应出现在控制房间的另一队日志");
        Assert.Null(await ReadControlDownload(logs));
    }

    [Fact]
    public async Task ExplicitServerBatchLinksSelectedSubteamAndPersistsAcrossArchiveReload()
    {
        using var logs = new ContextLogHarness();
        logs.Context.RegisterBatch("DC54A4", "accepted-server-batch", Team[..2]);
        var code = logs.Manager.CreateRoom("host", "房主", playerUid: Team[0], expectedPlayerCount: 2,
            hoeingBatchId: "accepted-server-batch", expectedParticipantUids: Team[..2]);
        var sessionId = logs.Manager.GetRoom(code)!.SessionId;
        var logger = logs.Factory.CreateLogger("Game");
        using (logs.Context.BeginConnectionScope(logger, "host")) logger.LogInformation("批次子队执行记录");
        logs.Manager.DeleteRoom(code);
        var reloaded = new RoomLogArchive(logs.DirectoryPath);
        var text = await ReadControlDownload(logs, reloaded);
        Assert.Contains("批次子队执行记录", text);
        Assert.Contains(sessionId, text);
    }

    [Fact]
    public void RoomLifecycleObserversRunOutsideRoomLockAndCannotBreakBusiness()
    {
        using var logs = new ContextLogHarness();
        var code = logs.Manager.CreateRoom("host", "房主", playerUid: "123");
        var room = logs.Manager.GetRoom(code)!;
        var ended = 0;
        logs.Manager.RoomLogSessionEnded += (_, _) =>
        {
            Assert.False(Monitor.IsEntered(room));
            ended++;
            throw new IOException("观察失败");
        };
        logs.Manager.DeleteRoom(code);
        Assert.Null(logs.Manager.GetRoom(code));
        Assert.Equal(1, ended);
    }

    private static async Task<string?> ReadControlDownload(ContextLogHarness logs, RoomLogArchive? archive = null)
    {
        var date = DateTime.UtcNow.AddHours(8).ToString("yyyy-MM-dd");
        Assert.True(RoomLogRange.TryParse(date + "T00:00", date + "T23:59", out var range, out _));
        await using var download = await (archive ?? logs.Archive).OpenRangeAsync("DC54A4", range!);
        if (download is null) return null;
        using var output = new MemoryStream();
        await download.CopyToAsync(output, CancellationToken.None);
        return System.Text.Encoding.UTF8.GetString(output.ToArray());
    }
}
