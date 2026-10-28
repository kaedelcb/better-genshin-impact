using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>已配对本机网页的嘟嘟可业务适配；不接受调用方提供的文件路径。</summary>
internal sealed class DodocoLocalService : IDisposable
{
    private const int MaxLogEntries = 5000;
    private const int MaxLogChars = 8 * 1024 * 1024;
    private const int MaxLineChars = 256 * 1024;
    private const int ChunkBytes = 192 * 1024;
    private const long MaxFileBytes = 200L * 1024 * 1024;
    private readonly BgiLogTailService _tail;
    private readonly DodocoWebRelayService _relay;
    private readonly ScreenshotService _screenshots;
    private readonly DodocoSettingsService _settings;
    private readonly Func<string?> _bgiLogDir;
    private readonly Func<string> _assistantLogDir;
    private readonly Func<string, object> _identity;
    private readonly Func<ScreenFrame?>? _captureOverride;
    private readonly object _gate = new();
    private readonly Queue<(long Sequence, LogEntry Entry, int Chars)> _recent = new();
    private readonly Dictionary<string, Dictionary<string, FileSelection>> _files = new(StringComparer.Ordinal);
    private long _sequence;
    private int _recentChars;
    private bool _disposed;

    private sealed record FileSelection(string Path, string Root, long Size, DateTime LastWriteUtc, FileIdentity Identity);
    private readonly record struct FileIdentity(uint Volume, uint High, uint Low);

    public DodocoLocalService(BgiLogTailService tail, DodocoWebRelayService relay, ScreenshotService screenshots,
        DodocoSettingsService settings, Func<string?> bgiLogDir, Func<string, object> identity,
        Func<string>? assistantLogDir = null, Func<ScreenFrame?>? captureOverride = null)
    {
        (_tail, _relay, _screenshots, _settings, _bgiLogDir, _identity) =
            (tail, relay, screenshots, settings, bgiLogDir, identity);
        _assistantLogDir = assistantLogDir ?? (() => LogFileBrowser.AssistantLogDir);
        _captureOverride = captureOverride;
        _tail.EntryReceived += OnEntry;
        _tail.HistoryBatchReceived += OnHistory;
    }

    private void OnEntry(LogEntry entry)
    {
        var source = entry.Source.Length > 4096 ? entry.Source[..4096] : entry.Source;
        var message = entry.Message;
        var exception = entry.Exception;
        if (message.Length + (exception?.Length ?? 0) > MaxLineChars)
        {
            message = message[..Math.Min(message.Length, MaxLineChars)] + "…[本机实时日志单行已截断，完整内容可在日志浏览中查看]";
            exception = null;
        }
        var retained = entry with { Source = source, Message = message, Exception = exception };
        var chars = source.Length + message.Length + (exception?.Length ?? 0) + (entry.Instance?.Length ?? 0) + 64;
        lock (_gate)
        {
            if (_disposed) return;
            _recent.Enqueue((++_sequence, retained, chars));
            _recentChars += chars;
            while (_recent.Count > MaxLogEntries || _recentChars > MaxLogChars)
                _recentChars -= _recent.Dequeue().Chars;
        }
    }
    private void OnHistory(IReadOnlyList<LogEntry> entries)
    {
        foreach (var entry in entries) OnEntry(entry);
    }

    internal async Task<LocalBrowserResult> ExecuteAsync(LocalBrowserRequest request, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(DodocoLocalService));
            object result = request.Operation switch
            {
                "local.identity" => _identity(request.InstanceId),
                "log.poll" => Poll(request.Args),
                "log.files" => EnumerateFiles(request.SessionId, cancellationToken),
                "log.chunk" => await ReadChunkAsync(request.SessionId, request.Args, cancellationToken).ConfigureAwait(false),
                "screenshot.capture" => Capture(),
                _ when request.Operation.StartsWith("watch.", StringComparison.Ordinal)
                    || request.Operation.StartsWith("report.", StringComparison.Ordinal)
                    => await _relay.ExecuteLocalAsync(request.Operation, request.Args, request.SessionId, cancellationToken).ConfigureAwait(false),
                _ => throw new ArgumentException("不支持的本机操作")
            };
            return new LocalBrowserResult("ok", Data: result);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new LocalBrowserResult("error", ex.Message); }
    }

    private object Poll(JsonObject args)
    {
        long? cursor = args["cursor"] is null ? null : GetInteger(args["cursor"]!);
        (long Sequence, LogEntry Entry, int Chars)[] page;
        long next, dropped;
        lock (_gate)
        {
            if (cursor is < 0 || cursor > _sequence) throw new ArgumentException("日志游标无效，请重新订阅");
            var available = _recent.ToArray();
            var start = cursor ?? Math.Max(0, _sequence - 200);
            dropped = available.Length == 0 ? 0 : Math.Max(0, available[0].Sequence - start - 1);
            var selected = new List<(long Sequence, LogEntry Entry, int Chars)>();
            var chars = 0;
            foreach (var entry in available)
            {
                if (entry.Sequence <= start) continue;
                if (selected.Count >= 500 || (selected.Count > 0 && chars + entry.Chars > 384 * 1024)) break;
                selected.Add(entry);
                chars += entry.Chars;
            }
            page = selected.ToArray();
            next = page.Length == 0 ? _sequence : page[^1].Sequence;
        }
        return new { cursor = next, lines = page.Select(x => MemberLogLineCodec.Render(x.Entry)).ToArray(), dropped,
            sourceAvailable = !string.IsNullOrEmpty(_tail.CurrentFilePath) };
    }

    private object EnumerateFiles(string sessionId, CancellationToken cancellationToken)
    {
        var rows = new List<(string Id, string Name, string Group, long Size, DateTime LastWrite)>();
        var items = new Dictionary<string, FileSelection>(StringComparer.Ordinal);
        var unavailable = new List<string>();
        var bgi = _bgiLogDir();
        if (string.IsNullOrWhiteSpace(bgi)) unavailable.Add("BGI 日志目录未配置");
        else Collect(bgi, "本机 · BGI 日志", false, name => LogFileBrowser.BgiLogFileNameRegex.IsMatch(name));
        var assistant = _assistantLogDir();
        Collect(assistant, "本机 · 助手日志", false,
            name => name.StartsWith("assistant_runtime.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        Collect(Path.Combine(assistant, "remote_downloads"), "已下载的成员日志", true,
            name => name.EndsWith(".log", StringComparison.OrdinalIgnoreCase));
        Collect(Path.Combine(assistant, "imported"), "外部导入日志", false, _ => true);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DodocoLocalService));
            cancellationToken.ThrowIfCancellationRequested();
            _files[sessionId] = items;
        }
        return new { files = rows.OrderByDescending(x => x.LastWrite).Select(x => new
            { id = x.Id, name = x.Name, group = x.Group, size = x.Size, lastWrite = x.LastWrite }).ToArray(),
            unavailable = unavailable.Distinct(StringComparer.Ordinal).ToArray() };

        void Collect(string root, string group, bool recursive, Func<string, bool> include)
        {
            cancellationToken.ThrowIfCancellationRequested();
            root = Path.GetFullPath(root);
            if (!Directory.Exists(root)) { unavailable.Add(group + "目录不可用"); return; }
            EnsureNoReparse(root, root);
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                foreach (var path in Directory.EnumerateFiles(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!include(Path.GetFileName(path))) continue;
                    if (items.Count >= 1000) throw new InvalidOperationException("日志文件超过本机列表上限 1000 个，请缩小日志范围");
                    try
                    {
                        EnsureNoReparse(root, path);
                        using var stream = OpenVerified(root, path, null);
                        var id = Guid.NewGuid().ToString("N");
                        var metadata = GetMetadata(stream.SafeFileHandle);
                        var selection = new FileSelection(path, root, stream.Length, metadata.LastWriteUtc, metadata.Identity);
                        items.Add(id, selection);
                        var name = recursive ? Path.GetRelativePath(root, path).Replace('\\', '/') : Path.GetFileName(path);
                        rows.Add((id, name, group, selection.Size, metadata.LastWriteUtc.ToLocalTime()));
                    }
                    catch (FileNotFoundException) { /* 枚举期间文件已消失 */ }
                    catch (DirectoryNotFoundException) { /* 同上 */ }
                    catch (UnauthorizedAccessException) { unavailable.Add(group + "部分文件不可读取或为链接"); }
                }
                if (!recursive) continue;
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                    else unavailable.Add(group + "包含未读取的链接目录");
                }
            }
        }
    }

    private async Task<object> ReadChunkAsync(string sessionId, JsonObject args, CancellationToken cancellationToken)
    {
        var id = args["id"]?.GetValue<string>();
        var offset = args["offset"] is null ? -1 : GetInteger(args["offset"]!);
        if (string.IsNullOrEmpty(id) || id.Length > 64 || offset < 0) throw new ArgumentException("文件标识或偏移无效");
        FileSelection item;
        lock (_gate)
            if (!_files.TryGetValue(sessionId, out var files) || !files.TryGetValue(id, out item!))
                throw new InvalidOperationException("日志文件列表已失效，请重新读取");
        if (item.Size > MaxFileBytes) throw new InvalidOperationException("日志超过 200 MiB 上限");
        if (offset > item.Size) throw new ArgumentException("文件偏移超出范围");
        EnsureNoReparse(item.Root, item.Path);
        await using var stream = OpenVerified(item.Root, item.Path, item.Identity);
        if (stream.Length < item.Size) throw new IOException("日志文件已缩短，请重新读取列表");
        var currentWrite = GetMetadata(stream.SafeFileHandle).LastWriteUtc;
        if (stream.Length == item.Size && currentWrite != item.LastWriteUtc)
            throw new IOException("日志文件已修改，请重新读取列表");
        stream.Position = offset;
        var count = (int)Math.Min(ChunkBytes, item.Size - offset);
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken).ConfigureAwait(false);
            if (n == 0) throw new IOException("日志文件读取不完整，请重新读取列表");
            read += n;
        }
        return new { offset, total = item.Size, base64 = Convert.ToBase64String(buffer), done = offset + read == item.Size };
    }

    private object Capture()
    {
        byte[]? jpeg;
        int width, height;
        DateTime capturedAt;
        if (_captureOverride != null)
        {
            var frame = _captureOverride();
            jpeg = frame?.JpegBytes; width = frame?.Width ?? 0; height = frame?.Height ?? 0;
            capturedAt = frame?.Time ?? DateTime.Now;
        }
        else
        {
            jpeg = _screenshots.CaptureJpeg(_settings.Current.ThumbnailWidth, 70, out width, out height);
            capturedAt = DateTime.Now;
        }
        if (jpeg is not { Length: > 0 } || width <= 0 || height <= 0) throw new InvalidOperationException("本机画面采集失败或当前会话不可用");
        if (jpeg.Length > 1024 * 1024) throw new InvalidOperationException("本机画面超过 1 MiB，请调低截图宽度");
        return new { jpegBase64 = Convert.ToBase64String(jpeg), width, height, capturedAt };
    }

    private static long GetInteger(JsonNode node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<long>(out var longValue)) return longValue;
            if (value.TryGetValue<int>(out var intValue)) return intValue;
        }
        throw new ArgumentException("数字参数无效");
    }

    internal void ReleaseSession(string sessionId)
    {
        lock (_gate) _files.Remove(sessionId);
        _relay.ReleaseLocalSession(sessionId);
    }

    private static void EnsureNoReparse(string root, string path)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var full = Path.GetFullPath(path);
        if (!full.Equals(root, StringComparison.OrdinalIgnoreCase)
            && !full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("文件不属于允许的日志目录");
        var relative = Path.GetRelativePath(root, full);
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("日志目录是链接，不能通过本机网页读取");
        if (relative == ".") return;
        foreach (var part in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("日志路径包含链接，不能通过本机网页读取");
        }
    }

    private static FileStream OpenVerified(string root, string path, FileIdentity? expected)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            var actual = FinalPath(stream.SafeFileHandle);
            var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            if (!actual.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("日志文件已移出允许的目录");
            if (expected is { } id && GetIdentity(stream.SafeFileHandle) != id)
                throw new IOException("日志文件身份已变化，请重新读取列表");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static FileIdentity GetIdentity(SafeFileHandle handle)
        => GetMetadata(handle).Identity;

    private static (FileIdentity Identity, DateTime LastWriteUtc) GetMetadata(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var ticks = ((long)(uint)info.LastWriteTime.dwHighDateTime << 32) | (uint)info.LastWriteTime.dwLowDateTime;
        return (new(info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow), DateTime.FromFileTimeUtc(ticks));
    }

    private static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
        if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
        var path = buffer.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _files.Clear(); _recent.Clear(); }
        _tail.EntryReceived -= OnEntry;
        _tail.HistoryBatchReceived -= OnHistory;
    }
}
