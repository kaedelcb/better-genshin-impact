using System.Globalization;
using System.Text;

namespace BgiCoordinatorServer.Services;

/// <summary>按北京时间和房间码追加完整日志，独立于有数量限制的页面缓存。</summary>
public sealed class RoomLogArchive(string directory)
{
    private static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);
    private readonly string _directory = Path.GetFullPath(directory);
    private readonly object _gate = new();
    private readonly HashSet<string> _failedFiles = new(StringComparer.Ordinal);

    internal static DateOnly GetDate(DateTime utc) =>
        DateOnly.FromDateTime(utc.ToUniversalTime().Add(BeijingOffset));

    internal void Append(IEnumerable<string> codes, RoomServerLogEntry entry)
    {
        var date = GetDate(entry.TimestampUtc);
        var localTime = new DateTimeOffset(entry.TimestampUtc, TimeSpan.Zero).ToOffset(BeijingOffset);
        lock (_gate)
        {
            foreach (var code in codes)
            {
                var path = GetPath(code, date);
                try
                {
                    Directory.CreateDirectory(_directory);
                    using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                    using var writer = new StreamWriter(stream, new UTF8Encoding(true));
                    if (stream.Length == 0)
                    {
                        writer.WriteLine($"房间 {code} · {date:yyyy-MM-dd} 服务器日志（北京时间 UTC+08:00）");
                        writer.WriteLine($"本文件首条记录：{localTime:yyyy-MM-dd HH:mm:ss.fff zzz}");
                        writer.WriteLine("仅包含启用保存后可关联到此房间的日志；旧版未保存的日志无法补回。按时间从早到晚排列。");
                        writer.WriteLine();
                    }
                    writer.WriteLine($"[{localTime:yyyy-MM-dd HH:mm:ss.fff zzz}] [{entry.Level}] {entry.Category}");
                    writer.WriteLine(entry.Message);
                    writer.WriteLine();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // 不影响联机业务，也不能把发生过保存失败的文件标成完整下载。
                    _failedFiles.Add(path);
                    try { File.WriteAllText(path + ".incomplete", "日志保存曾失败，本日记录可能不完整。"); }
                    catch (Exception markerError) when (markerError is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    public RoomLogDownload? OpenDay(string roomCode, DateOnly date)
    {
        var code = RoomLogStore.NormalizeCode(roomCode) ?? throw new ArgumentException("房间码无效", nameof(roomCode));
        var path = GetPath(code, date);
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            if (_failedFiles.Contains(path) || File.Exists(path + ".incomplete"))
                throw new IOException("本日房间日志保存曾失败，无法提供完整下载。");
            FileStream stream;
            try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch (FileNotFoundException) { return null; }
            return new RoomLogDownload(stream, stream.Length, $"room-{code}-{date:yyyy-MM-dd}.txt");
        }
    }

    public IReadOnlyList<DateOnly> GetAvailableDates(string roomCode, DateOnly today)
    {
        var code = RoomLogStore.NormalizeCode(roomCode) ?? throw new ArgumentException("房间码无效", nameof(roomCode));
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            var dates = new HashSet<DateOnly> { today };
            foreach (var path in Directory.EnumerateFiles(_directory, $"*-{code}.log"))
            {
                var name = Path.GetFileName(path);
                if (name.Length == 21 && name.EndsWith($"-{code}.log", StringComparison.Ordinal) &&
                    DateOnly.TryParseExact(name.AsSpan(0, 10), "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var date))
                    dates.Add(date);
            }
            return dates.OrderDescending().ToArray();
        }
    }

    public async Task<RoomLogRangeDownload?> OpenRangeAsync(string roomCode, RoomLogRange range,
        CancellationToken cancellationToken = default)
    {
        var code = RoomLogStore.NormalizeCode(roomCode) ?? throw new ArgumentException("房间码无效", nameof(roomCode));
        var files = new List<RoomLogFileSnapshot>();
        // 与本进程的追加共用锁，所有文件长度在扫描前固定，避免把下载开始后的记录混入结果。
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(_directory);
                for (var day = range.FirstDate.DayNumber; day <= range.LastDate.DayNumber; day++)
                {
                    var date = DateOnly.FromDayNumber(day);
                    var path = GetPath(code, date);
                    if (_failedFiles.Contains(path) || File.Exists(path + ".incomplete"))
                        throw new IOException("所选日期的房间日志保存曾失败，无法提供完整下载。");
                    try
                    {
                        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        files.Add(new RoomLogFileSnapshot(date, stream, stream.Length));
                    }
                    catch (FileNotFoundException) { }
                }
            }
            catch
            {
                foreach (var file in files) file.Stream.Dispose();
                throw;
            }
        }

        var download = new RoomLogRangeDownload(code, range, files);
        try
        {
            // 先流式扫描以便在响应开始前返回“无记录”或读取失败，随后从同一长度快照输出。
            if (await download.ContainsEntriesAsync(cancellationToken)) return download;
            await download.DisposeAsync();
            return null;
        }
        catch
        {
            await download.DisposeAsync();
            throw;
        }
    }

    private string GetPath(string code, DateOnly date) =>
        Path.Combine(_directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + code + ".log");
}

/// <summary>输入为北京时间的完整分钟，结束分钟也包含在内。</summary>
public sealed class RoomLogRange
{
    private RoomLogRange(DateTime startInclusive, DateTime endExclusive)
    {
        StartInclusive = startInclusive;
        EndExclusive = endExclusive;
    }

    public DateTime StartInclusive { get; }
    public DateTime EndExclusive { get; }
    public DateTime EndMinute => EndExclusive.AddMinutes(-1);
    public DateOnly FirstDate => DateOnly.FromDateTime(StartInclusive);
    public DateOnly LastDate => DateOnly.FromDateTime(EndMinute);

    public static bool TryParse(string? start, string? end, out RoomLogRange? range, out string error)
    {
        range = null;
        const string format = "yyyy-MM-dd'T'HH:mm";
        if (!DateTime.TryParseExact(start, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var first) ||
            !DateTime.TryParseExact(end, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var last))
        {
            error = "请选择有效的开始和结束时间（北京时间，精确到分钟）。";
            return false;
        }
        if (last < first)
        {
            error = "结束时间不能早于开始时间。";
            return false;
        }
        if (DateOnly.FromDateTime(last).DayNumber - DateOnly.FromDateTime(first).DayNumber >= 31)
        {
            error = "一次最多下载 31 个自然日期，请缩短时间段。";
            return false;
        }
        if (last > DateTime.MaxValue.AddMinutes(-1))
        {
            error = "结束时间超出支持范围，请重新选择。";
            return false;
        }
        range = new RoomLogRange(first, last.AddMinutes(1));
        error = "";
        return true;
    }
}

internal sealed record RoomLogFileSnapshot(DateOnly Date, FileStream Stream, long Length);

public sealed class RoomLogRangeDownload : IAsyncDisposable
{
    private readonly string _code;
    private readonly RoomLogRange _range;
    private readonly IReadOnlyList<RoomLogFileSnapshot> _files;

    internal RoomLogRangeDownload(string code, RoomLogRange range, IReadOnlyList<RoomLogFileSnapshot> files)
    {
        _code = code;
        _range = range;
        _files = files;
        FileName = $"room-{code}-{range.StartInclusive:yyyy-MM-dd-HHmm}-{range.EndMinute:yyyy-MM-dd-HHmm}.txt";
    }

    public string FileName { get; }

    internal Task<bool> ContainsEntriesAsync(CancellationToken cancellationToken) =>
        ScanAsync(null, cancellationToken);

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(true), 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync($"房间 {_code} · 服务器日志（北京时间 UTC+08:00）".AsMemory(), cancellationToken);
        await writer.WriteLineAsync($"下载时间段：{_range.StartInclusive:yyyy-MM-dd HH:mm} 至 {_range.EndMinute:yyyy-MM-dd HH:mm}（包含结束分钟）".AsMemory(), cancellationToken);
        await writer.WriteLineAsync("仅包含启用保存后可关联到此房间的日志；旧版未保存的日志无法补回。按时间从早到晚排列。".AsMemory(), cancellationToken);
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
        await ScanAsync(writer, cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }

    private async Task<bool> ScanAsync(StreamWriter? output, CancellationToken cancellationToken)
    {
        var found = false;
        foreach (var file in _files)
        {
            file.Stream.Position = 0;
            using var limited = new SnapshotReadStream(file.Stream, file.Length);
            using var reader = new StreamReader(limited, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            // 原有按天文件的前四行为说明；每条记录的最后另有一个空行。
            for (var index = 0; index < 4; index++)
                if (await reader.ReadLineAsync(cancellationToken) is null)
                    throw new IOException("房间日志文件头不完整，无法确认所选记录。");

            var selected = false;
            var hasRecord = false;
            var previousLineWasEmpty = true;
            while (await reader.ReadLineAsync(cancellationToken) is { } line)
            {
                if (previousLineWasEmpty && TryReadTimestamp(line, file.Date, out var timestamp))
                {
                    hasRecord = true;
                    selected = timestamp >= _range.StartInclusive && timestamp < _range.EndExclusive;
                    found |= selected;
                }
                else if (!hasRecord && line.Length != 0)
                    throw new IOException("房间日志记录格式无法识别，无法确认所选记录。");

                if (selected && output is not null)
                    await output.WriteLineAsync(line.AsMemory(), cancellationToken);
                previousLineWasEmpty = line.Length == 0;
            }
        }
        return found;
    }

    private static bool TryReadTimestamp(string line, DateOnly fileDate, out DateTime timestamp)
    {
        timestamp = default;
        const int timestampLength = 30;
        if (line.Length < 38 || line[0] != '[' || line[31] != ']' || line[32] != ' ' || line[33] != '[')
            return false;
        var levelEnd = line.IndexOf(']', 34);
        if (levelEnd < 0 || levelEnd + 1 >= line.Length || line[levelEnd + 1] != ' ')
            return false;
        var level = line[34..levelEnd];
        if (level is not ("Trace" or "Debug" or "Information" or "Warning" or "Error" or "Critical" or "None"))
            return false;
        if (!DateTimeOffset.TryParseExact(line.AsSpan(1, timestampLength), "yyyy-MM-dd HH:mm:ss.fff zzz",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ||
            parsed.Offset != TimeSpan.FromHours(8) || DateOnly.FromDateTime(parsed.DateTime) != fileDate)
            return false;
        timestamp = parsed.DateTime;
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var file in _files) await file.Stream.DisposeAsync();
    }

    /// <summary>限制 StreamReader 的预读取，不让后续追加越过请求时的长度。</summary>
    private sealed class SnapshotReadStream(FileStream source, long length) : Stream
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
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining == 0 || buffer.Length == 0) return 0;
            var read = await source.ReadAsync(buffer[..(int)Math.Min(buffer.Length, _remaining)], cancellationToken);
            if (read == 0) throw new EndOfStreamException("日志文件在下载期间被截断。");
            _remaining -= read;
            return read;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

public sealed class RoomLogDownload(FileStream stream, long length, string fileName) : IAsyncDisposable
{
    public string FileName { get; } = fileName;

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[64 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException("日志文件在下载期间被截断。");
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    public ValueTask DisposeAsync() => stream.DisposeAsync();
}
