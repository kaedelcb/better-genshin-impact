using System.Globalization;
using System.Text;

namespace BgiCoordinatorServer.Services;

/// <summary>按北京时间和房间码追加完整日志，独立于有数量限制的页面缓存。</summary>
public sealed partial class RoomLogArchive(string directory)
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
        var eventId = Guid.NewGuid().ToString("N");
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
                    var session = entry.GameSessionId ?? GetCurrentSession(code, entry.TimestampUtc);
                    var sessionToken = session is null ? "-" : Convert.ToBase64String(Encoding.UTF8.GetBytes(session));
                    writer.WriteLine($"[{localTime:yyyy-MM-dd HH:mm:ss.fff zzz}] [{entry.Level}] {entry.Category} [room-log:v2;event={eventId};session={sessionToken}]");
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
        RoomLogHistorySnapshot snapshot;
        lock (_gate) snapshot = CaptureHistory();
        snapshot = RoomLogLegacyHistory.AddLinks(snapshot, CancellationToken.None);
        var sources = snapshot.Links.Where(link => link.ControlCode == code).Select(link => link.GameCode)
            .Append(code).ToHashSet(StringComparer.Ordinal);
        return snapshot.Files.Where(file => sources.Contains(file.Code)).Select(file => file.Date)
            .Append(today).Distinct().OrderDescending().ToArray();
    }

    public async Task<RoomLogRangeDownload?> OpenRangeAsync(string roomCode, RoomLogRange range,
        CancellationToken cancellationToken = default)
    {
        var code = RoomLogStore.NormalizeCode(roomCode) ?? throw new ArgumentException("房间码无效", nameof(roomCode));
        RoomLogHistorySnapshot snapshot;
        lock (_gate)
        {
            Directory.CreateDirectory(_directory);
            snapshot = CaptureHistory();
        }
        return await RoomLogHistoryExport.CreateAsync(_directory, code, range, snapshot, cancellationToken);
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

public sealed class RoomLogRangeDownload : IAsyncDisposable
{
    private readonly string _code;
    private readonly RoomLogRange _range;
    private readonly FileStream _body;

    internal RoomLogRangeDownload(string code, RoomLogRange range, FileStream body, RoomLogDownloadMetadata metadata)
    {
        _code = code;
        _range = range;
        _body = body;
        Metadata = metadata;
        FileName = $"room-{code}-{range.StartInclusive:yyyy-MM-dd-HHmm}-{range.EndMinute:yyyy-MM-dd-HHmm}.txt";
    }

    public string FileName { get; }
    public RoomLogDownloadMetadata Metadata { get; }

    public async Task CopyToAsync(Stream destination, CancellationToken cancellationToken = default)
    {
        await using var writer = new StreamWriter(destination, new UTF8Encoding(true), 64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync($"房间 {_code} · 服务器日志（北京时间 UTC+08:00）".AsMemory(), cancellationToken);
        await writer.WriteLineAsync($"下载时间段：{_range.StartInclusive:yyyy-MM-dd HH:mm} 至 {_range.EndMinute:yyyy-MM-dd HH:mm}（包含结束分钟）".AsMemory(), cancellationToken);
        await writer.WriteLineAsync($"记录数：{Metadata.Count}；来源房间：{string.Join("、", Metadata.Sources)}；按时间从早到晚排列。".AsMemory(), cancellationToken);
        if (Metadata.FirstTimestampUtc is { } first && Metadata.LastTimestampUtc is { } last)
            await writer.WriteLineAsync($"实际首尾：{first.AddHours(8):yyyy-MM-dd HH:mm:ss.fff} 至 {last.AddHours(8):yyyy-MM-dd HH:mm:ss.fff}（北京时间）".AsMemory(), cancellationToken);
        foreach (var warning in Metadata.Warnings)
            await writer.WriteLineAsync($"保存情况：{warning}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken);
        await writer.FlushAsync(cancellationToken);
        _body.Position = 0;
        await _body.CopyToAsync(destination, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        var scratch = Path.GetDirectoryName(_body.Name)!;
        await _body.DisposeAsync();
        try { Directory.Delete(scratch); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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
