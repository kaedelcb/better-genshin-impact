using System.Globalization;

namespace BgiCoordinatorServer.Services;

/// <summary>下载过滤使用服务端已保存的关联，不用时间相邻或共同成员猜测批次。</summary>
public sealed record RoomLogSelection(string? BatchId = null, string? SessionId = null, bool Direct = false);
public sealed record RoomLogWorld(string Code, string SessionId, string? BatchId, string? HostUid,
    string? HostName, DateTime CreatedAtUtc, DateTime? EndedAtUtc, bool HasSavedLogs);
public sealed record RoomLogCard(string Code, RoomHoeingDay Hoeing, RoomLogWorld[] Worlds, string[] Warnings);
public sealed record RoomLogCards(string Date, string[] Dates, RoomLogCard[] Rooms, string[] UnlinkedRooms, string[] Warnings);

public sealed partial class RoomLogArchive
{
    /// <summary>只读取索引及文件元数据，页面刷新不扫描全服日志正文。</summary>
    public RoomLogCards GetCards(DateOnly date, DateTime utcNow, RoomHoeingHistory history)
    {
        RoomLogHistorySnapshot snapshot;
        lock (_gate) snapshot = CaptureHistory();
        var files = snapshot.Files.Where(file => file.Date == date).ToArray();
        var links = snapshot.Links.Where(link => OnDay(link, date)).ToArray();
        var historyCodes = history.GetRoomCodes(date, utcNow);
        var codes = historyCodes.Concat(links.Select(link => link.ControlCode))
            .Concat(snapshot.Links.Where(link => files.Any(file => file.Code == link.ControlCode))
                .Select(link => link.ControlCode)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var cards = codes.Select(code => new RoomLogCard(code, history.ReadDay(code, date, utcNow),
            links.Where(link => link.ControlCode == code).OrderBy(link => link.StartedUtc)
                .ThenBy(link => link.SessionId, StringComparer.Ordinal)
                .Select(link => new RoomLogWorld(link.GameCode, link.SessionId, link.BatchId, link.HostUid,
                    link.HostName, link.StartedUtc, link.EndedUtc, files.Any(file => file.Code == link.GameCode)))
                .ToArray(),
            snapshot.Warnings.Where(warning => warning.Code == code || warning.Code == "*")
                .Select(warning => warning.Message).Distinct().ToArray())).ToArray();
        var linkedCodes = cards.Select(card => card.Code).Concat(links.Select(link => link.GameCode))
            .ToHashSet(StringComparer.Ordinal);
        var unlinked = files.Select(file => file.Code).Where(code => !linkedCodes.Contains(code))
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        var dates = snapshot.Files.Select(file => file.Date)
            .Concat(snapshot.Links.Select(link => GetDate(link.StartedUtc)))
            .Concat(snapshot.Links.Where(link => link.EndedUtc.HasValue).Select(link => GetDate(link.EndedUtc!.Value)))
            .Concat(history.GetRecordedDates()).Append(date).Distinct().OrderDescending()
            .Select(day => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();
        return new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), dates, cards, unlinked, snapshot.Warnings.Where(warning => warning.Code == "*")
            .Select(warning => warning.Message).Distinct().ToArray());
    }

    private static bool OnDay(RoomLogLink link, DateOnly date)
    {
        var start = GetDate(link.StartedUtc);
        if (start > date) return false;
        if (link.EndedUtc is { } end) return date <= GetDate(end);
        // 未保存结束边界不能据此声称此房间往后每天都存在。
        return date <= GetDate(link.LastObservedUtc ?? link.LinkedUtc);
    }
}
