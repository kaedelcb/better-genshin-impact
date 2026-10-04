using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 远程成员完整日志下载·被下载端（与 MemberLogRelayService 的"实时流"互补：本服务是按需文件传输）。
/// 1) 收到 MemberLogFilesRequested：ShareLogFiles 关闭 → 回空列表；否则枚举本机
///    BGI 日志（复用 <see cref="LogFileBrowser.BgiLogFileNameRegex"/>）+ 助手日志
///    assistant_runtime.*.log，回文件名/大小/修改时间，并刷新"允许下载"白名单映射。
/// 2) 收到 MemberLogDownloadRequested：文件名白名单 + 必须在刚枚举的集合内（防伪造路径）；
///    共享读打开 → 后台读入内存 → gzip（日志文本压缩比高）→ base64 按 192KB 原始字节分块，
///    每 ~160ms 发一块（远低于服务端 30 块/秒限流）。
/// 3) 同一时间只允许一个下载任务：正忙 / 文件超 200MB / 校验失败 → 回"忙标记块"
///    （done=true, totalChunks=0，协议约定，观众端提示"对方正忙或拒绝"）。
/// </summary>
public sealed class MemberLogShareService : IDisposable
{
    /// <summary>日志文件名白名单（含中文）：与服务端 CoordinatorHub.LogFileNameRegex 同款，防目录穿越。</summary>
    public static readonly Regex FileNameRegex = new(
        @"^[\w\-.一-鿿]{1,120}\.log$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>单文件原始大小上限（超出回忙标记）。</summary>
    private const long MaxRawBytes = 200L * 1024 * 1024;
    /// <summary>每块原始字节数（gzip 后切片；base64 后 ≤256KB，与服务端上限一致）。</summary>
    private const int ChunkRawBytes = 192 * 1024;
    /// <summary>块间节流（服务端限流 30 块/秒，这里 ~6 块/秒留足余量）。</summary>
    private static readonly TimeSpan ChunkInterval = TimeSpan.FromMilliseconds(160);

    private readonly DodocoSettingsService _settingsService;
    private readonly Func<SignalRClient?> _clientProvider;
    private readonly Func<string?> _bgiLogDirProvider;

    private SignalRClient? _hooked;
    /// <summary>允许下载的文件全集（文件名 → 完整路径），每次应答文件列表时刷新。仅在有锁保护下读写。</summary>
    private readonly Dictionary<string, string> _allowedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _allowedLock = new();
    /// <summary>下载任务并发守卫（0=空闲 1=传输中）。</summary>
    private int _downloadRunning;
    private readonly CancellationTokenSource _cts = new();

    public MemberLogShareService(
        DodocoSettingsService settingsService,
        Func<SignalRClient?> clientProvider,
        Func<string?> bgiLogDirProvider)
    {
        _settingsService = settingsService;
        _clientProvider = clientProvider;
        _bgiLogDirProvider = bgiLogDirProvider;
    }

    /// <summary>客户端实例懒解析换绑（SignalRClient 可能晚于本服务创建；由 DodocoViewModel 的 200ms 节拍驱动）。</summary>
    public void EnsureHooked()
    {
        var client = _clientProvider();
        if (ReferenceEquals(client, _hooked)) return;
        if (_hooked != null)
        {
            _hooked.OnMemberLogFilesRequested -= HandleFilesRequested;
            _hooked.OnMemberLogDownloadRequested -= HandleDownloadRequested;
        }
        _hooked = client;
        if (_hooked != null)
        {
            _hooked.OnMemberLogFilesRequested += HandleFilesRequested;
            _hooked.OnMemberLogDownloadRequested += HandleDownloadRequested;
        }
    }

    /// <summary>枚举本机可分享的日志文件（BGI 日志 + 助手日志），同时刷新允许下载映射（先清后填，删掉消失的文件）。</summary>
    private List<MemberLogFileDescriptor> EnumerateShareableFiles()
    {
        var result = new List<MemberLogFileDescriptor>();
        var allowed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        void Collect(string dir, string pattern, bool filterByBgiRegex)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, pattern))
            {
                var name = Path.GetFileName(f);
                if (filterByBgiRegex && !LogFileBrowser.BgiLogFileNameRegex.IsMatch(name)) continue;
                // 双保险：传输协议白名单（含中文/长度/后缀约束）不通过的也不分享
                if (!FileNameRegex.IsMatch(name)) continue;
                try
                {
                    var fi = new FileInfo(f);
                    result.Add(new MemberLogFileDescriptor
                    {
                        Name = name, Size = fi.Length, LastWrite = fi.LastWriteTime
                    });
                    allowed[name] = fi.FullName; // 同名（理论上 BGI/助手两套命名不重叠）后者覆盖前者
                }
                catch { /* 单文件读取元数据失败不影响其它 */ }
            }
        }

        Collect(_bgiLogDirProvider() ?? "", "better-genshin-impact*.log", filterByBgiRegex: true);
        Collect(LogFileBrowser.AssistantLogDir, "assistant_runtime.*.log", filterByBgiRegex: false);

        lock (_allowedLock)
        {
            _allowedFiles.Clear();
            foreach (var kv in allowed) _allowedFiles[kv.Key] = kv.Value;
        }
        return result.OrderByDescending(f => f.LastWrite).ToList();
    }

    /// <summary>文件列表请求（SignalR 回调线程）：ShareLogFiles 关闭回空列表；枚举放后台线程避免阻塞回调。</summary>
    private void HandleFilesRequested(string requesterUid, string requestId)
    {
        Task.Run(() =>
        {
            try
            {
                var client = _clientProvider();
                if (client?.IsConnected != true) return;
                // 共享关闭 → 回空列表（观众端显示"对方未共享日志文件"语义由空列表+超时区分）
                List<MemberLogFileDescriptor> files = _settingsService.Current.ShareLogFiles
                    ? EnumerateShareableFiles()
                    : new List<MemberLogFileDescriptor>();
                _ = client.ReportMemberLogFilesAsync(requestId, files);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MemberLogShare] 文件列表应答失败: {ex.Message}");
            }
        });
    }

    /// <summary>下载请求（SignalR 回调线程）：校验后立即分派后台传输任务。</summary>
    private void HandleDownloadRequested(string requesterUid, string requestId, string fileName)
    {
        var client = _clientProvider();
        if (client?.IsConnected != true) return;

        // 校验：ShareLogFiles 开关 + 文件名白名单 + 必须在最近枚举的集合内（防伪造）
        string? path = null;
        if (_settingsService.Current.ShareLogFiles && FileNameRegex.IsMatch(fileName))
        {
            lock (_allowedLock)
                _allowedFiles.TryGetValue(fileName, out path);
        }

        // 正忙（同时间只允许一个下载任务）或校验失败 → 回忙标记块（协议：done=true, totalChunks=0）
        if (path == null || Interlocked.CompareExchange(ref _downloadRunning, 1, 0) != 0)
        {
            _ = client.ReportMemberLogChunkAsync(requestId, 0, 0, "", fileName, done: true);
            return;
        }

        Task.Run(async () =>
        {
            try
            {
                await SendFileAsync(client, requestId, fileName, path, _cts.Token);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MemberLogShare] 文件传输失败: {ex.Message}");
                // 传输中途失败：尽力补一个忙标记块让观众端别干等超时
                try { await client.ReportMemberLogChunkAsync(requestId, 0, 0, "", fileName, done: true); }
                catch { /* 尽力而为 */ }
            }
            finally
            {
                Interlocked.Exchange(ref _downloadRunning, 0);
            }
        });
    }

    /// <summary>后台传输：共享读 → GZipStream 边压边切块（ChunkCollector 累积满 192KB 成一块）→
    /// 压缩完成后逐块 base64 节流上行。
    /// 峰值内存 ≈ 压缩后体积（日志文本压缩比高，通常远小于 200MB 原始），不再整读+整压双缓冲。
    /// 注：协议要求首块带 totalChunks，因此必须压缩完再发；边压边发需要对端先收未知总数，不改协议的前提下这是最优形态。</summary>
    private static async Task SendFileAsync(SignalRClient client, string requestId, string fileName,
        string path, CancellationToken ct)
    {
        var chunks = new List<byte[]>();
        using (var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (src.Length > MaxRawBytes)
            {
                // 超上限：回忙标记块（观众端提示"对方正忙或拒绝"）
                await client.ReportMemberLogChunkAsync(requestId, 0, 0, "", fileName, done: true);
                return;
            }
            using var collector = new ChunkCollector(chunks, ChunkRawBytes);
            using (var gz = new GZipStream(collector, CompressionLevel.Fastest, leaveOpen: true))
            {
                await src.CopyToAsync(gz, ct);
            }
            collector.FlushChunk();
        }

        // 空文件也至少发一块（totalChunks=0 是忙标记的协议含义，不能用于空文件）
        var totalChunks = Math.Max(1, chunks.Count);
        for (var seq = 0; seq < totalChunks; seq++)
        {
            ct.ThrowIfCancellationRequested();
            if (client.IsConnected != true) return; // 断线：观众端 30s 无新块超时兜底
            byte[] payload = chunks.Count > 0 ? chunks[seq] : Array.Empty<byte>();
            var chunk = Convert.ToBase64String(payload);
            var done = seq == totalChunks - 1;
            // 串行 await 保证块顺序；发完一块即释放引用，压缩数据随发送逐步让 GC 可回收
            if (chunks.Count > 0) chunks[seq] = null!;
            await client.ReportMemberLogChunkAsync(requestId, seq, totalChunks, chunk, fileName, done);
            if (!done) await Task.Delay(ChunkInterval, ct);
        }
    }

    /// <summary>压缩输出累积切块流：GZipStream 写入累积满 chunkSize 即切出一块进列表（只支持写）。</summary>
    private sealed class ChunkCollector : Stream
    {
        private readonly List<byte[]> _chunks;
        private readonly int _chunkSize;
        private byte[] _buf;
        private int _len;

        public ChunkCollector(List<byte[]> chunks, int chunkSize)
        {
            _chunks = chunks;
            _chunkSize = chunkSize;
            _buf = new byte[chunkSize];
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            while (count > 0)
            {
                var n = Math.Min(count, _chunkSize - _len);
                Buffer.BlockCopy(buffer, offset, _buf, _len, n);
                _len += n; offset += n; count -= n;
                if (_len == _chunkSize) FlushChunk();
            }
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            while (buffer.Length > 0)
            {
                var n = Math.Min(buffer.Length, _chunkSize - _len);
                buffer[..n].CopyTo(_buf.AsSpan(_len));
                _len += n; buffer = buffer[n..];
                if (_len == _chunkSize) FlushChunk();
            }
        }

        /// <summary>把当前未满的一块（若有）切出；压缩结束后调用。</summary>
        public void FlushChunk()
        {
            if (_len == 0) return;
            var chunk = _len == _chunkSize ? _buf : _buf[.._len];
            _chunks.Add(chunk);
            _buf = new byte[_chunkSize];
            _len = 0;
        }

        public override bool CanWrite => true;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        _cts.Cancel();
        // 给在途传输任务一个短暂退出窗口（取消传播 + Task.Delay 即刻返回），
        // 避免 cts 过早释放导致在途任务访问 Token 抛 ObjectDisposedException
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(500);
        while (Volatile.Read(ref _downloadRunning) != 0 && DateTime.UtcNow < deadline)
            Thread.Sleep(20);
        _cts.Dispose();
        if (_hooked != null)
        {
            _hooked.OnMemberLogFilesRequested -= HandleFilesRequested;
            _hooked.OnMemberLogDownloadRequested -= HandleDownloadRequested;
            _hooked = null;
        }
    }
}
