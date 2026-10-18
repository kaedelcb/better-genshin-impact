using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BgiCoordinatorServer.Services;

public sealed record RoomLogDownloadMetadata(long Count, DateTime? FirstTimestampUtc, DateTime? LastTimestampUtc,
    string[] Sources, string[] Warnings);
public sealed record RoomLogAvailableRoom(string Code, string Kind, DateOnly[] Dates, string[] Warnings);

internal sealed record RoomLogLink(string ControlCode, string GameCode, string SessionId,
    DateTime StartedUtc, DateTime LinkedUtc, DateTime? EndedUtc = null, string Source = "persisted");
internal sealed record RoomLogHistoryWarning(string Code, string Message);
internal sealed record RoomLogHistoryFile(string Path, string Code, DateOnly Date, long Length, bool Incomplete);
internal sealed record RoomLogHistorySnapshot(RoomLogHistoryFile[] Files, RoomLogLink[] Links,
    RoomLogHistoryWarning[] Warnings);

public sealed partial class RoomLogArchive
{
    private readonly Dictionary<string, RoomLogLink> _links = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RoomLogLink> _currentLinks = new(StringComparer.Ordinal);
    private readonly List<RoomLogHistoryWarning> _historyWarnings = [];
    private bool _historyLoaded;
    private bool _historyDamaged;

    public void RecordRoomLink(string controlRoomCode, string gameRoomCode, string gameSessionId,
        DateTime sessionStartUtc, DateTime linkedAtUtc)
    {
        var control = RoomLogStore.NormalizeCode(controlRoomCode) ?? throw new ArgumentException("控制房间码无效");
        var game = RoomLogStore.NormalizeCode(gameRoomCode) ?? throw new ArgumentException("锄地房间码无效");
        if (string.IsNullOrWhiteSpace(gameSessionId)) throw new ArgumentException("房间会话标识不能为空");
        if (control == game) throw new ArgumentException("控制房间和锄地房间不能相同");
        var link = new RoomLogLink(control, game, gameSessionId, Utc(sessionStartUtc), Utc(linkedAtUtc));
        lock (_gate)
        {
            LoadHistory();
            if (_links.TryGetValue(gameSessionId, out var old))
            {
                if (old.ControlCode != control || old.GameCode != game || old.StartedUtc != link.StartedUtc)
                {
                    RecordHistoryWarning(control, $"会话 {gameSessionId} 的历史关联冲突，未采用冲突关联。");
                    return;
                }
                _currentLinks[game] = old;
                return;
            }
            if (AppendJournal("room-links.jsonl", link))
            {
                _links[gameSessionId] = link;
                _currentLinks[game] = link;
            }
        }
    }

    public void EndRoomLink(string gameRoomCode, string gameSessionId, DateTime endedAtUtc)
    {
        var game = RoomLogStore.NormalizeCode(gameRoomCode) ?? throw new ArgumentException("锄地房间码无效");
        lock (_gate)
        {
            LoadHistory();
            if (!_links.TryGetValue(gameSessionId, out var old) || old.GameCode != game) return;
            var ended = Utc(endedAtUtc);
            if (ended < old.StartedUtc) return;
            var link = old with { EndedUtc = ended };
            if (AppendJournal("room-links.jsonl", link)) _links[gameSessionId] = link;
            if (_currentLinks.TryGetValue(game, out var current) && current.SessionId == gameSessionId)
                _currentLinks.Remove(game);
        }
    }

    public void RecordHistoryWarning(string roomCode, string message)
    {
        var code = RoomLogStore.NormalizeCode(roomCode) ?? throw new ArgumentException("房间码无效");
        lock (_gate)
        {
            LoadHistory();
            var warning = new RoomLogHistoryWarning(code, message);
            if (_historyWarnings.Contains(warning)) return;
            _historyWarnings.Add(warning);
            AppendJournal("room-history-warnings.jsonl", warning);
        }
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value : value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();

    private string? GetCurrentSession(string code, DateTime timestampUtc) =>
        _currentLinks.TryGetValue(code, out var link) && Utc(timestampUtc) >= link.StartedUtc
            ? link.SessionId : null;

    private bool AppendJournal<T>(string fileName, T value)
    {
        var path = Path.Combine(_directory, fileName);
        try
        {
            Directory.CreateDirectory(_directory);
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read,
                4096, FileOptions.WriteThrough);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value) + "\n");
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _historyDamaged = true;
            try { File.WriteAllText(Path.Combine(_directory, "room-history.incomplete"), "历史关联保存曾失败，关联可能缺失。"); }
            catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException) { }
            return false;
        }
    }

    private void LoadHistory()
    {
        if (_historyLoaded) return;
        _historyLoaded = true;
        try
        {
            Directory.CreateDirectory(_directory);
            var linksPath = Path.Combine(_directory, "room-links.jsonl");
            if (File.Exists(linksPath))
                foreach (var line in File.ReadLines(linksPath, Encoding.UTF8))
                {
                    var link = JsonSerializer.Deserialize<RoomLogLink>(line) ?? throw new JsonException();
                    if (RoomLogStore.NormalizeCode(link.ControlCode) != link.ControlCode ||
                        RoomLogStore.NormalizeCode(link.GameCode) != link.GameCode || string.IsNullOrWhiteSpace(link.SessionId) ||
                        link.EndedUtc < link.StartedUtc) throw new JsonException();
                    if (_links.TryGetValue(link.SessionId, out var old) &&
                        (old.ControlCode != link.ControlCode || old.GameCode != link.GameCode || old.StartedUtc != link.StartedUtc))
                        throw new JsonException();
                    _links[link.SessionId] = link;
                }
            var warningsPath = Path.Combine(_directory, "room-history-warnings.jsonl");
            if (File.Exists(warningsPath))
                foreach (var line in File.ReadLines(warningsPath, Encoding.UTF8))
                    _historyWarnings.Add(JsonSerializer.Deserialize<RoomLogHistoryWarning>(line) ?? throw new JsonException());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // 不用损坏的生命周期猜测会话归属；房间自身已保存的日志仍然可以下载。
            _links.Clear();
            _historyDamaged = true;
        }
        _historyDamaged |= File.Exists(Path.Combine(_directory, "room-history.incomplete"));
    }

    private RoomLogHistorySnapshot CaptureHistory()
    {
        LoadHistory();
        var files = new List<RoomLogHistoryFile>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*.log"))
        {
            var name = Path.GetFileName(path);
            if (name.Length != 21 || name[10] != '-' ||
                !DateOnly.TryParseExact(name.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var date)) continue;
            var code = RoomLogStore.NormalizeCode(name.Substring(11, 6));
            if (code is null) continue;
            files.Add(new RoomLogHistoryFile(path, code, date, new FileInfo(path).Length,
                _failedFiles.Contains(path) || File.Exists(path + ".incomplete")));
        }
        var warnings = _historyWarnings.ToList();
        if (_historyDamaged) warnings.Add(new RoomLogHistoryWarning("*", "历史关联索引保存失败或损坏，部分历史锄地房间可能缺失，不能证明完整。"));
        return new RoomLogHistorySnapshot(files.ToArray(), _links.Values.ToArray(), warnings.ToArray());
    }

    public IReadOnlyList<RoomLogAvailableRoom> GetAvailableRooms(DateOnly today)
    {
        RoomLogHistorySnapshot snapshot;
        lock (_gate) snapshot = CaptureHistory();
        snapshot = RoomLogLegacyHistory.AddLinks(snapshot, CancellationToken.None);
        var codes = snapshot.Files.Select(file => file.Code)
            .Concat(snapshot.Links.SelectMany(link => new[] { link.ControlCode, link.GameCode })).Distinct(StringComparer.Ordinal);
        return codes.Order(StringComparer.Ordinal).Select(code =>
        {
            var links = snapshot.Links.Where(link => link.ControlCode == code).ToArray();
            var sources = links.Select(link => link.GameCode).Append(code).ToHashSet(StringComparer.Ordinal);
            var kind = links.Length != 0 ? "control" : snapshot.Links.Any(link => link.GameCode == code) ? "game" : "unknown";
            return new RoomLogAvailableRoom(code, kind,
                snapshot.Files.Where(file => sources.Contains(file.Code)).Select(file => file.Date).Append(today).Distinct().OrderDescending().ToArray(),
                snapshot.Warnings.Where(warning => warning.Code == code || warning.Code == "*").Select(warning => warning.Message).Distinct().ToArray());
        }).ToArray();
    }
}

internal sealed record RoomLogHistoryRecord(DateTime TimestampUtc, string Header, string Message,
    string SourceCode, string? EventId, string? SessionId, long Ordinal)
{
    public string SortId => EventId ?? $"{SourceCode}:{Ordinal:D20}";
}

internal static class RoomLogArchiveReader
{
    public static IEnumerable<RoomLogHistoryRecord> Read(RoomLogHistoryFile file, CancellationToken cancellationToken)
    {
        using var source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var limited = new RoomLogSnapshotStream(source, file.Length);
        using var reader = new StreamReader(limited, Encoding.UTF8, true);
        for (var index = 0; index < 4; index++)
            if (reader.ReadLine() is null) throw new IOException("房间日志文件头不完整，无法确认所选记录。");
        RoomLogHistoryRecord? current = null;
        var message = new StringBuilder();
        var previousEmpty = true;
        long ordinal = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = previousEmpty ? ParseHeader(line, file, ++ordinal) : null;
            if (candidate is not null && (current?.EventId is null || candidate.EventId is not null))
            {
                if (current is not null) yield return current with { Message = FinishMessage(message) };
                current = candidate;
                message.Clear();
            }
            else if (current is not null) message.AppendLine(line);
            else if (line.Length != 0) throw new IOException("房间日志记录格式无法识别，无法确认所选记录。");
            previousEmpty = line.Length == 0;
        }
        if (current is not null) yield return current with { Message = FinishMessage(message) };
    }

    private static string FinishMessage(StringBuilder message)
    {
        var value = message.ToString();
        // 仅移除每条记录固定的最后一条分隔空行，不裁切正文及异常内部空行。
        return value.EndsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal)
            ? value[..^Environment.NewLine.Length] : value;
    }

    private static RoomLogHistoryRecord? ParseHeader(string line, RoomLogHistoryFile file, long ordinal)
    {
        if (line.Length < 38 || line[0] != '[' || line[31] != ']' || line[32] != ' ' || line[33] != '[') return null;
        var end = line.IndexOf(']', 34);
        if (end < 0 || end + 1 >= line.Length || line[end + 1] != ' ' ||
            line[34..end] is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical" or "None")) return null;
        if (!DateTimeOffset.TryParseExact(line.AsSpan(1, 30), "yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var timestamp) || timestamp.Offset != TimeSpan.FromHours(8) ||
            DateOnly.FromDateTime(timestamp.DateTime) != file.Date) return null;
        string? eventId = null, sessionId = null;
        const string marker = " [room-log:v2;event=";
        var markerAt = line.LastIndexOf(marker, StringComparison.Ordinal);
        if (markerAt >= 0 && line.EndsWith(']'))
        {
            var fields = line[(markerAt + marker.Length)..^1].Split(";session=", StringSplitOptions.None);
            if (fields.Length != 2 || !Guid.TryParseExact(fields[0], "N", out _)) throw new IOException("房间日志事件标识损坏。");
            eventId = fields[0];
            try { sessionId = fields[1] == "-" ? null : Encoding.UTF8.GetString(Convert.FromBase64String(fields[1])); }
            catch (FormatException exception) { throw new IOException("房间日志会话标识损坏。", exception); }
            line = line[..markerAt];
        }
        return new RoomLogHistoryRecord(timestamp.UtcDateTime, line, "", file.Code, eventId, sessionId, ordinal);
    }
}

internal sealed class RoomLogSnapshotStream(Stream source, long length) : Stream
{
    private readonly long _length = length;
    private long _remaining = length;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => _length;
    public override long Position { get => _length - _remaining; set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_remaining == 0 || count == 0) return 0;
        var read = source.Read(buffer, offset, (int)Math.Min(count, _remaining));
        if (read == 0) throw new EndOfStreamException("日志文件在下载期间被截断。");
        _remaining -= read;
        return read;
    }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class RoomLogLegacyHistory
{
    public static RoomLogHistorySnapshot AddLinks(RoomLogHistorySnapshot snapshot, CancellationToken cancellationToken)
    {
        var controls = new HashSet<string>(snapshot.Links.Select(link => link.ControlCode), StringComparer.Ordinal);
        var boundaries = new Dictionary<string, List<(DateTime Timestamp, string? Control)>>(StringComparer.Ordinal);
        var warnings = snapshot.Warnings.ToList();
        var unreadableCodes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in snapshot.Files.GroupBy(file => file.Code))
        {
            foreach (var file in group.OrderBy(file => file.Date))
            {
                if (file.Incomplete)
                {
                    unreadableCodes.Add(file.Code);
                    continue;
                }
                foreach (var record in SafeRead(file, warnings, unreadableCodes, cancellationToken))
                {
                    if (!record.Header.Contains("BgiCoordinatorServer.Services.RoomOperations", StringComparison.Ordinal)) continue;
                    if (record.Message.Contains("控制房间 " + group.Key, StringComparison.Ordinal) ||
                        record.Message.Contains("控制房间 CTRL_" + group.Key, StringComparison.Ordinal)) controls.Add(group.Key);
                    if (record.EventId is not null) continue;
                    if (!boundaries.TryGetValue(group.Key, out var events)) boundaries[group.Key] = events = [];
                    if (Regex.IsMatch(record.Message, @"^连接 .+ 创建房间 " + group.Key + @"\r?\n?$", RegexOptions.CultureInvariant))
                        events.Add((record.TimestampUtc, null));
                    var order = Regex.Match(record.Message, @"^\[RoundOrder\] 房间 " + group.Key +
                        @" 生成权威轮换序列（排除 0 已完成）：(?<uids>\d{9}(?: -> \d{9}){0,3})\r?\n?$", RegexOptions.CultureInvariant);
                    if (!order.Success) continue;
                    var uids = order.Groups["uids"].Value.Split(" -> ");
                    if (uids.Distinct(StringComparer.Ordinal).Count() != uids.Length) continue;
                    // 与助手 GenerateControlRoomCode 相同：完整名单排序、逗号拼接、UTF8 SHA256 前6位。
                    var input = string.Join(",", uids.OrderBy(uid => uid));
                    var code = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)))[..6];
                    events.Add((record.TimestampUtc, code));
                }
            }
        }
        var links = snapshot.Links.ToList();
        foreach (var unreadableCode in unreadableCodes)
        {
            var message = $"历史锄地房间 {unreadableCode} 存在保存失败或无法解析的日期，无法确认跨日会话边界，已停用该房间的旧版推断关联；明确保存的会话关联不受影响。";
            warnings.Add(new RoomLogHistoryWarning(unreadableCode, message));
            if (boundaries.TryGetValue(unreadableCode, out var events))
            {
                foreach (var control in events.Where(item => item.Control is not null && controls.Contains(item.Control))
                             .Select(item => item.Control!).Distinct(StringComparer.Ordinal))
                    warnings.Add(new RoomLogHistoryWarning(control, message));
                boundaries.Remove(unreadableCode);
            }
        }
        foreach (var (game, events) in boundaries)
        {
            DateTime? start = null;
            string? control = null;
            bool conflicting = false;
            void Finish(DateTime? end)
            {
                if (start is null || control is null || conflicting || !controls.Contains(control)) return;
                // 显式新格式会话已经有可靠归属，不以旧推断替代。
                if (snapshot.Links.Any(link => link.GameCode == game && link.StartedUtc == start.Value)) return;
                links.Add(new RoomLogLink(control, game, $"legacy:{game}:{start.Value.Ticks}", start.Value,
                    start.Value, end, "legacyRoundOrder"));
                warnings.Add(new RoomLogHistoryWarning(control,
                    $"历史锄地房间 {game} 根据建房边界、完整权威UID名单及已保存控制房间恢复关联；" +
                    (end is null ? "未保存结束边界，本次仅包含请求开始时已保存的记录，旧版未保存的日志无法补回。" : "边界以前未保存的日志无法补回。")));
            }
            foreach (var item in events.OrderBy(item => item.Timestamp))
            {
                if (item.Control is null)
                {
                    Finish(item.Timestamp);
                    start = item.Timestamp;
                    control = null;
                    conflicting = false;
                }
                else if (start is not null)
                {
                    if (control is not null && control != item.Control) conflicting = true;
                    control = item.Control;
                }
            }
            Finish(null);
        }
        return snapshot with { Links = links.ToArray(), Warnings = warnings.Distinct().ToArray() };
    }

    private static IEnumerable<RoomLogHistoryRecord> SafeRead(RoomLogHistoryFile file,
        List<RoomLogHistoryWarning> warnings, HashSet<string> unreadableCodes, CancellationToken cancellationToken)
    {
        using var records = RoomLogArchiveReader.Read(file, cancellationToken).GetEnumerator();
        while (true)
        {
            RoomLogHistoryRecord record;
            try
            {
                if (!records.MoveNext()) break;
                record = records.Current;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                unreadableCodes.Add(file.Code);
                warnings.Add(new RoomLogHistoryWarning(file.Code, $"{file.Date:yyyy-MM-dd} 的旧日志不能完整解析，历史关联可能缺失。"));
                break;
            }
            yield return record;
        }
    }
}
