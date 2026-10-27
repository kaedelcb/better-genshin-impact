using System.Globalization;
using System.Text;
using System.Text.Json;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed record RoomHoeingMember(string Uid, string Name);
public sealed record RoomHoeingRun(string Id, DateTime RequestedAtUtc, DateTime? StartedAtUtc,
    DateTime ObservedAtUtc, DateTime? EndedAtUtc, string Status, string Reason,
    IReadOnlyList<RoomHoeingMember> Members);
public sealed record RoomHoeingDay(string Date, string Status, IReadOnlyList<RoomHoeingRun> Runs,
    IReadOnlyList<string> Warnings);

/// <summary>管理页的行为记录旁路。只保存正式成员的实际锄地观察和服务端批次结果，不驱动联机业务。</summary>
public sealed class RoomHoeingHistory(string root)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromSeconds(150);
    private readonly string _root = Path.GetFullPath(root);
    private readonly object _gate = new();
    private readonly Dictionary<string, RoomState> _rooms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Signal> _signals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _legacyRuns = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnings = [];

    private sealed class RoomState
    {
        public Dictionary<string, SavedRun> Runs { get; } = new(StringComparer.Ordinal);
        public HashSet<string> Warnings { get; } = [];
        public bool Damaged;
        public DateTime LastSavedAtUtc;
    }
    private sealed record SavedRun(RoomHoeingRun Run, long Revision, bool Terminal);
    private sealed record SavedRoom(string Code, IReadOnlyList<SavedRun> Runs);
    private sealed record Signal(string Code, string RunId, bool Running, DateTime SeenUtc);

    public RoomHoeingDay ReadToday(string roomCode, DateTime utcNow) =>
        ReadDay(roomCode, RoomLogArchive.GetDate(Utc(utcNow)), utcNow);

    public RoomHoeingDay ReadDay(string roomCode, DateOnly date, DateTime utcNow)
    {
        var code = RoomLogStore.NormalizeCode(roomCode);
        var now = Utc(utcNow);
        lock (_gate)
        {
            if (code is null) return new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "none", [], []);
            var room = Load(code);
            ExpireSignals(code, room, now);
            var runs = TodayRuns(room, date, date == RoomLogArchive.GetDate(now)).OrderBy(run => run.RequestedAtUtc).ThenBy(run => run.Id, StringComparer.Ordinal).ToArray();
            var status = runs.Any(run => run.Status == "running") ? "running"
                : runs.Any(run => run.Status == "preparing") ? "preparing" : runs.LastOrDefault()?.Status ?? "none";
            return new(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), status, runs,
                room.Warnings.Concat(_warnings).Distinct().ToArray());
        }
    }

    public IReadOnlyList<string> GetTodayRoomCodes(DateTime utcNow) =>
        GetRoomCodes(RoomLogArchive.GetDate(Utc(utcNow)), utcNow);

    public IReadOnlyList<string> GetRoomCodes(DateOnly date, DateTime utcNow)
    {
        lock (_gate)
        {
            try
            {
                if (Directory.Exists(_root))
                    foreach (var path in Directory.EnumerateFiles(_root, "*.json"))
                    {
                        var name = Path.GetFileNameWithoutExtension(path);
                        if (RoomLogStore.NormalizeCode(name) == name) Load(name);
                    }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { _warnings.Add("锄地记录目录读取失败，今日记录可能缺失。"); }
            var now = Utc(utcNow);
            foreach (var (code, room) in _rooms) ExpireSignals(code, room, now);
            return _rooms.Where(pair => TodayRuns(pair.Value, date, date == RoomLogArchive.GetDate(now)).Any() || pair.Value.Warnings.Count > 0)
                .Select(pair => pair.Key).Order(StringComparer.Ordinal).ToArray();
        }
    }

    public IReadOnlyList<DateOnly> GetRecordedDates()
    {
        lock (_gate)
            return _rooms.Values.SelectMany(room => room.Runs.Values).SelectMany(saved => new[]
                { saved.Run.RequestedAtUtc, saved.Run.StartedAtUtc ?? saved.Run.RequestedAtUtc,
                    saved.Run.ObservedAtUtc, saved.Run.EndedAtUtc ?? saved.Run.ObservedAtUtc })
                .Select(RoomLogArchive.GetDate).Distinct().OrderDescending().ToArray();
    }

    // The caller supplies a server-owned snapshot, never the phase or member list from a status report.
    internal void ObserveBatch(string group, CoordinatedBatchSnapshot batch,
        IReadOnlyList<RoomHoeingMember> members, DateTime utcNow)
    {
        var code = RoomLogStore.NormalizeCode(group);
        if (code is null || batch.BatchId.Length is < 1 or > 128 || members.Count == 0
            || members.Any(member => !IsUid(member.Uid))
            || !members.Select(member => member.Uid).Order(StringComparer.Ordinal)
                .SequenceEqual(batch.ParticipantUids.Order(StringComparer.Ordinal))) return;
        var now = Utc(utcNow);
        lock (_gate)
        {
            var room = Load(code);
            // A quick command may run unrelated tasks. Keep it only if real hoeing was observed.
            if (!room.Runs.TryGetValue(batch.BatchId, out var saved))
            {
                if (batch.QuickCommandKey.Length > 0) return;
                saved = new(new(batch.BatchId, now, null, now, null, "preparing", "已受理联机锄地批次，等待实际开锄", members.ToArray()), -1, false);
            }
            if (saved.Terminal || batch.Revision <= saved.Revision) return;
            var run = saved.Run;
            var status = run.Status;
            var reason = run.Reason;
            DateTime? ended = run.EndedAtUtc;
            var terminal = batch.Phase is "completed" or "aborted";
            if (batch.Phase == "completed")
            {
                status = batch.Reason.StartsWith("completed_with_task_errors:", StringComparison.Ordinal)
                    ? "completedWithErrors" : "completed";
                reason = batch.Reason.Length == 0 ? "服务端已确认全队批次完成" : batch.Reason;
                ended = now;
            }
            else if (batch.Phase == "aborted")
            {
                reason = batch.Reason;
                status = reason.Contains("unknown", StringComparison.Ordinal) ? "unknown"
                    : reason is "manual_reset" or "user_stop_all" || reason.Contains("cancelled", StringComparison.Ordinal)
                        ? "stopped" : "failed";
                ended = now;
            }
            var changed = run with { Status = status, Reason = reason, EndedAtUtc = ended,
                ObservedAtUtc = now, Members = members.ToArray() };
            room.Runs[batch.BatchId] = new(changed, batch.Revision, terminal);
            Save(code, room);
        }
    }

    /// <summary>仅由已登记执行端进入；UID、姓名和实例取自服务端成员，不信任上报中的自报身份。</summary>
    internal void ObserveMember(string group, string uid, string name, string instanceId,
        ControlStatus status, string? verifiedBatchId, DateTime utcNow)
    {
        var code = RoomLogStore.NormalizeCode(group);
        if (code is null || !IsUid(uid) || status.TaskStatusAvailable == false) return;
        var now = Utc(utcNow);
        lock (_gate)
        {
            var room = Load(code);
            var key = code + "\0" + uid + "\0" + instanceId;
            if (!status.AutoHoeingRunning)
            {
                if (_signals.TryGetValue(key, out var prior) && prior.Running)
                {
                    _signals[key] = prior with { Running = false, SeenUtc = now };
                    EndObservation(code, room, prior.RunId, now);
                }
                return;
            }
            var id = verifiedBatchId;
            // After a server restart the coordinator may no longer know the batch. A known saved ID
            // can resume its observation, but the self-reported phase still cannot complete it.
            if (id is null && status.HoeingBatchId is { Length: > 0 } reported && room.Runs.ContainsKey(reported)) id = reported;
            if (id is null)
            {
                if (!_legacyRuns.TryGetValue(code, out id))
                    _legacyRuns[code] = id = "observed-" + Guid.NewGuid().ToString("N");
            }
            if (!room.Runs.TryGetValue(id, out var saved))
                saved = new(new(id, now, null, now, null, "preparing", "", []), -1, false);
            if (saved.Terminal) return;
            if (_signals.TryGetValue(key, out var previous) && previous.Running && previous.RunId != id)
            {
                _signals[key] = previous with { Running = false, SeenUtc = now };
                EndObservation(code, room, previous.RunId, now);
            }
            var run = saved.Run;
            var member = new RoomHoeingMember(uid, name);
            var members = run.Members.Where(item => item.Uid != uid).Append(member).OrderBy(item => item.Uid, StringComparer.Ordinal).ToArray();
            _signals[key] = new(code, id, true, now);
            var changed = run with
            {
                StartedAtUtc = run.StartedAtUtc ?? now, ObservedAtUtc = now, Status = "running",
                Reason = "正式成员已上报实际锄地中", Members = members,
            };
            room.Runs[id] = saved with { Run = changed };
            // Keep live heartbeats in memory; persist at least once a minute and on every semantic change.
            if (run.Status != changed.Status || run.StartedAtUtc is null || !run.Members.SequenceEqual(members)
                || now - room.LastSavedAtUtc >= TimeSpan.FromMinutes(1)) Save(code, room);
        }
    }

    internal void ObserveDisconnected(string group, string uid, string instanceId, DateTime utcNow)
    {
        var code = RoomLogStore.NormalizeCode(group);
        if (code is null) return;
        lock (_gate)
        {
            var key = code + "\0" + uid + "\0" + instanceId;
            if (!_signals.TryGetValue(key, out var signal) || !signal.Running) return;
            var now = Utc(utcNow);
            _signals[key] = signal with { Running = false, SeenUtc = now };
            EndObservation(code, Load(code), signal.RunId, now);
        }
    }

    private void ExpireSignals(string code, RoomState room, DateTime now)
    {
        foreach (var (key, signal) in _signals.Where(pair => pair.Value.Code == code && pair.Value.Running && now - pair.Value.SeenUtc > LiveTimeout).ToArray())
        {
            _signals[key] = signal with { Running = false };
            EndObservation(code, room, signal.RunId, signal.SeenUtc);
        }
        foreach (var saved in room.Runs.Values.Where(item => !item.Terminal && item.Run.Status == "preparing"
                     && now - item.Run.ObservedAtUtc > LiveTimeout).ToArray())
            room.Runs[saved.Run.Id] = saved with { Run = saved.Run with { Status = "unknown", Reason = "开锄准备后未收到新的确认，结果未确认" } };
    }

    private void EndObservation(string code, RoomState room, string id, DateTime now)
    {
        if (_signals.Values.Any(signal => signal.Code == code && signal.RunId == id && signal.Running)) return;
        if (_legacyRuns.GetValueOrDefault(code) == id) _legacyRuns.Remove(code);
        if (!room.Runs.TryGetValue(id, out var saved) || saved.Terminal || saved.Run.Status != "running") return;
        room.Runs[id] = saved with { Run = saved.Run with { Status = "unknown", ObservedAtUtc = now,
            Reason = "已观察到锄地，当前未收到运行或完成确认" } };
        Save(code, room);
    }

    private static IEnumerable<RoomHoeingRun> TodayRuns(RoomState room, DateOnly date, bool includeActive) => room.Runs.Values.Select(item => item.Run)
        .Where(run => RoomLogArchive.GetDate(run.RequestedAtUtc) == date
            || run.StartedAtUtc is { } started && RoomLogArchive.GetDate(started) == date
            || run.EndedAtUtc is { } ended && RoomLogArchive.GetDate(ended) == date
            || RoomLogArchive.GetDate(run.ObservedAtUtc) == date
            || RoomLogArchive.GetDate(run.RequestedAtUtc) < date
                && RoomLogArchive.GetDate(run.EndedAtUtc ?? run.ObservedAtUtc) > date
            || includeActive && run.Status is "running" or "preparing");

    private RoomState Load(string code)
    {
        if (_rooms.TryGetValue(code, out var room)) return room;
        _rooms[code] = room = new();
        var path = Path.Combine(_root, code + ".json");
        try
        {
            if (!File.Exists(path)) return room;
            var saved = JsonSerializer.Deserialize<SavedRoom>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                ?? throw new JsonException();
            if (saved.Code != code) throw new JsonException();
            foreach (var item in saved.Runs)
            {
                var run = item.Run;
                if (string.IsNullOrWhiteSpace(run.Id) || run.Members.Any(member => !IsUid(member.Uid))
                    || run.EndedAtUtc < run.RequestedAtUtc || run.StartedAtUtc < run.RequestedAtUtc
                    || run.Status is not ("preparing" or "running" or "completed" or "completedWithErrors" or "stopped" or "failed" or "unknown"))
                    throw new JsonException();
                // Saved liveness is never evidence of current execution after this process starts.
                room.Runs.Add(run.Id, item with { Run = !item.Terminal
                    ? run with { Status = "unknown", Reason = "服务端重启后尚未收到本轮新的运行或完成确认" } : run });
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NullReferenceException)
        {
            room.Damaged = true;
            room.Warnings.Add("锄地记录读取失败或文件损坏，今日记录可能缺失；原文件已保留。");
        }
        return room;
    }

    private void Save(string code, RoomState room)
    {
        if (room.Damaged) return;
        var path = Path.Combine(_root, code + ".json");
        var temporary = path + ".tmp";
        try
        {
            Directory.CreateDirectory(_root);
            using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, new SavedRoom(code, room.Runs.Values.ToArray()), JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
            room.LastSavedAtUtc = room.Runs.Values.Select(item => item.Run.ObservedAtUtc).DefaultIfEmpty().Max();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { room.Warnings.Add("锄地记录保存失败，本次结果可能在服务端重启后丢失。"); }
    }

    private static bool IsUid(string uid) => !string.IsNullOrWhiteSpace(uid) && uid.Length <= 20 && uid.All(char.IsAsciiDigit);
    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc ? value
        : value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
