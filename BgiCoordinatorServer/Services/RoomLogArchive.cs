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

    private string GetPath(string code, DateOnly date) =>
        Path.Combine(_directory, date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "-" + code + ".log");
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
