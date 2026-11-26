namespace BgiCoordinatorServer.Services;

internal sealed record HoeingSummarySource(string Code, RoomHoeingRun[] Runs, string[] Warnings);
public sealed record HoeingSummaryRun(RoomHoeingRun Run, DateTime? FromUtc, DateTime? ToUtc,
    double Seconds, bool Approximate, RoomLogWorld[] Worlds);
public sealed record HoeingSummaryCounts(int Started, int Continued, int Completed, int CompletedWithErrors,
    int Stopped, int Failed, int Unknown, int Preparing, int Running, int NotStarted, double? CompletionRate);
public sealed record HoeingSummaryRoom(string Code, string? Note, int MemberCount, double Seconds, double LongestSeconds,
    HoeingSummaryCounts Counts, string Status, HoeingSummaryRun[] Runs, string[] Warnings);
public sealed record HoeingSummaryDay(DateOnly Date, double Seconds, int Rooms, int Started, int Completed);
public sealed record HoeingSummary(DateOnly Start, DateOnly End, DateTime CapturedAtUtc, int RoomCount,
    int MemberCount, int RunningRoomCount, double Seconds, double AverageSeconds, HoeingSummaryCounts Counts,
    HoeingSummaryRoom[] Rooms, HoeingSummaryDay[] Days, string[] Warnings);

/// <summary>Read-only reporting over existing batch history. Times are observed batch elapsed time, not active game input time.</summary>
internal static class RoomHoeingSummary
{
    private static DateTime Midnight(DateOnly date) => date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-8);
    private static double Duration(DateTime start, DateTime end) => Math.Max(0, (end - start).TotalSeconds);
    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;
    private static DateTime Min(DateTime a, DateTime b) => a < b ? a : b;

    internal static bool IsValidRange(DateOnly start, DateOnly end, DateTime now) =>
        start > DateOnly.MinValue && end >= start && end <= RoomLogArchive.GetDate(now)
        && end.DayNumber - start.DayNumber < 366;

    internal static HoeingSummary Create(DateOnly start, DateOnly end, DateTime now,
        HoeingSummarySource[] sources, IReadOnlyDictionary<string, string>? notes,
        RoomLogHistorySnapshot? logHistory, IEnumerable<string> warnings)
    {
        var from = Midnight(start);
        var until = Midnight(end.AddDays(1));
        var cutoff = Min(now, until);
        bool InRange(DateTime instant) => instant >= from && instant < until && instant <= now;
        var links = (logHistory?.Links ?? []).Where(link => link.BatchId is not null)
            .ToLookup(link => (link.ControlCode, link.BatchId));
        var files = (logHistory?.Files ?? []).Where(file => file.Date >= start && file.Date <= end)
            .Select(file => file.Code).ToHashSet(StringComparer.Ordinal);
        var rooms = new List<HoeingSummaryRoom>();
        var roomIntervals = new List<(DateTime From, DateTime To)[]>();
        foreach (var source in sources)
        {
            var rows = new List<HoeingSummaryRun>();
            foreach (var run in source.Runs)
            {
                // Unknown results use the last recorded observation, never the current clock.
                var bound = run.Status == "running" ? now : run.Status == "unknown"
                    ? run.ObservedAtUtc : run.EndedAtUtc ?? run.ObservedAtUtc;
                DateTime? clippedStart = null, clippedEnd = null;
                if (run.StartedAtUtc is { } actualStart && actualStart <= now)
                {
                    var a = Max(from, actualStart);
                    var b = Min(cutoff, bound);
                    if (a < b) { clippedStart = a; clippedEnd = b; }
                }
                if (clippedStart is null && !InRange(run.RequestedAtUtc)
                    && !(run.StartedAtUtc is { } opened && InRange(opened))
                    && !(run.EndedAtUtc is { } ended && InRange(ended))
                    && !InRange(run.ObservedAtUtc)) continue;
                var worlds = links[(source.Code, run.Id)]
                    .Where(link => RoomLogArchive.GetDate(link.StartedUtc) <= end &&
                        RoomLogArchive.GetDate(link.EndedUtc ?? link.LastObservedUtc ?? link.LinkedUtc) >= start)
                    .OrderBy(link => link.StartedUtc).Select(link => new RoomLogWorld(link.GameCode, link.SessionId,
                        link.BatchId, link.HostUid, link.HostName, link.StartedUtc, link.EndedUtc, files.Contains(link.GameCode))).ToArray();
                rows.Add(new(run, clippedStart, clippedEnd, clippedStart.HasValue ? Duration(clippedStart.Value, clippedEnd!.Value) : 0,
                    run.Status is "unknown" or "running", worlds));
            }
            if (rows.Count == 0 && source.Warnings.Length == 0) continue;
            var ordered = rows.OrderByDescending(row => row.Run.StartedAtUtc ?? row.Run.RequestedAtUtc)
                .ThenBy(row => row.Run.Id, StringComparer.Ordinal).ToArray();
            var intervals = Merge(ordered);
            roomIntervals.Add(intervals);
            var counts = Count(ordered, from, until, now);
            var status = ordered.Any(row => row.Run.Status == "running") ? "running"
                : ordered.Any(row => row.Run.Status == "preparing") ? "preparing" : ordered.FirstOrDefault()?.Run.Status ?? "none";
            rooms.Add(new(source.Code, notes?.GetValueOrDefault(source.Code), Members(ordered.Where(row => Participated(row, from, until, now))).Distinct().Count(),
                intervals.Sum(i => Duration(i.From, i.To)), ordered.Select(row => row.Seconds).DefaultIfEmpty().Max(), counts,
                status, ordered, source.Warnings));
        }
        var allRuns = rooms.SelectMany(room => room.Runs).ToArray();
        var roomCount = rooms.Count(room => room.Runs.Any(row => Participated(row, from, until, now)));
        var seconds = rooms.Sum(room => room.Seconds);
        var days = new List<HoeingSummaryDay>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var dayFrom = Midnight(date);
            var dayTo = Midnight(date.AddDays(1));
            var dailySeconds = roomIntervals.Sum(intervals => intervals.Sum(i => Duration(Max(i.From, dayFrom), Min(i.To, dayTo))));
            days.Add(new(date, dailySeconds, rooms.Count(room => room.Runs.Any(row => Participated(row, dayFrom, dayTo, now))),
                allRuns.Count(row => row.Run.StartedAtUtc is { } began && began >= dayFrom && began < dayTo && began <= now),
                allRuns.Count(row => row.Run.StartedAtUtc.HasValue && row.Run.Status == "completed" && row.Run.EndedAtUtc is { } finished
                    && finished >= dayFrom && finished < dayTo && finished <= now)));
        }
        return new(start, end, now, roomCount, Members(allRuns.Where(row => Participated(row, from, until, now))).Distinct().Count(),
            end == RoomLogArchive.GetDate(now) ? rooms.Count(room => room.Counts.Running > 0) : 0,
            seconds, roomCount == 0 ? 0 : seconds / roomCount, Count(allRuns, from, until, now),
            rooms.OrderByDescending(room => room.Seconds).ThenBy(room => room.Code, StringComparer.Ordinal).ToArray(),
            days.ToArray(), warnings.Concat(logHistory?.Warnings.Select(warning => warning.Message) ?? [])
                .Concat(rooms.SelectMany(room => room.Warnings)).Distinct().ToArray());
    }

    private static bool Participated(HoeingSummaryRun row, DateTime from, DateTime until, DateTime now) =>
        row.Run.StartedAtUtc is { } began && (began >= from && began < until && began <= now
            || row.FromUtc < until && row.ToUtc > from);

    private static IEnumerable<string> Members(IEnumerable<HoeingSummaryRun> rows) => rows
        .Where(row => row.Run.StartedAtUtc.HasValue).SelectMany(row => row.Run.Members.Select(member => member.Uid));

    private static HoeingSummaryCounts Count(HoeingSummaryRun[] rows, DateTime from, DateTime until, DateTime now)
    {
        bool InRange(DateTime instant) => instant >= from && instant < until && instant <= now;
        var actual = rows.Where(row => Participated(row, from, until, now) || row.Run.StartedAtUtc.HasValue
            && row.Run.EndedAtUtc is { } ended && InRange(ended)).ToArray();
        int Ended(string status) => actual.Count(row => row.Run.Status == status && row.Run.EndedAtUtc is { } end && InRange(end));
        var completed = Ended("completed");
        var errors = Ended("completedWithErrors");
        var stopped = Ended("stopped");
        var failed = Ended("failed");
        var known = completed + errors + stopped + failed;
        return new(actual.Count(row => InRange(row.Run.StartedAtUtc!.Value)),
            actual.Count(row => row.Run.StartedAtUtc < from && row.ToUtc > from), completed, errors, stopped, failed,
            rows.Count(row => row.Run.Status == "unknown"), rows.Count(row => row.Run.Status == "preparing"),
            rows.Count(row => row.Run.Status == "running" && until > now && row.Run.StartedAtUtc.HasValue),
            rows.Count(row => !row.Run.StartedAtUtc.HasValue), known == 0 ? null : (double)completed / known);
    }

    private static (DateTime From, DateTime To)[] Merge(IEnumerable<HoeingSummaryRun> rows)
    {
        var result = new List<(DateTime From, DateTime To)>();
        foreach (var row in rows.Where(row => row.FromUtc.HasValue).OrderBy(row => row.FromUtc))
        {
            var from = row.FromUtc!.Value;
            var to = row.ToUtc!.Value;
            if (result.Count == 0 || from > result[^1].To) result.Add((from, to));
            else result[^1] = (result[^1].From, Max(result[^1].To, to));
        }
        return result.ToArray();
    }
}
