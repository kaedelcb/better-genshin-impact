using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 日志文件浏览器（嘟嘟可 P2 / F2）。
/// 枚举 BGI 日志（&lt;BGI&gt;\log\better-genshin-impact*.log）与助手自身日志（log\assistant_runtime.*.log），
/// 提供整文件加载（ReadAllLines，查看区全量视图）、分块随机访问读取（FileStream.Seek）、
/// 按时间定位、关键字/正则搜索、导出。
/// 所有打开均为 FileShare.ReadWrite | Delete 共享读，兼容 BGI 正在写入的当天文件。
/// </summary>
public sealed class LogFileBrowser
{
    /// <summary>BGI 日志文件名形态（共享给 MemberLogShareService 枚举可下载文件用）。</summary>
    public static readonly Regex BgiLogFileNameRegex = new(
        @"^better-genshin-impact(\d{8})?(_\d{3})*\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>单块读取的目标字节数（分块随机访问 API 与行扫描缓冲用）。</summary>
    public const int ChunkBytes = 64 * 1024;
    /// <summary>搜索结果上限（防止超大文件搜索出几万条卡 UI）。</summary>
    public const int MaxSearchResults = 5000;
    /// <summary>整文件加载的内存上限：超过该大小的文件只读尾部一段（BGI 单日志上限 16MB，正常不触发）。</summary>
    public const long MaxFullLoadBytes = 64 * 1024 * 1024;

    /// <summary>行级别解析（[HH:mm:ss(.fff)] [INF] / [yyyy-MM-dd HH:mm:ss.fff] [INF]）。</summary>
    private static readonly Regex LineLevelRegex = new(
        @"^\[(?:\d{4}-\d{2}-\d{2} )?\d{2}:\d{2}:\d{2}(?:\.\d+)?\] \[(?<lvl>[A-Z]{2,5})\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Func<string?> _bgiLogDirProvider;

    /// <param name="bgiLogDirProvider">BGI 日志目录提供者（可为 null=未配置）。</param>
    public LogFileBrowser(Func<string?> bgiLogDirProvider)
    {
        _bgiLogDirProvider = bgiLogDirProvider;
    }

    /// <summary>助手自身日志目录（exe 目录下 log/，与 MainViewModel.AddLog 写法一致）。</summary>
    public static string AssistantLogDir =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");

    /// <summary>枚举两个数据源的日志文件（本机 BGI 组 + 本机助手组 + 已下载成员组），组内按修改时间倒序。</summary>
    public List<LogFileItem> EnumerateFiles()
    {
        var result = new List<LogFileItem>();
        var bgiDir = _bgiLogDirProvider();
        if (!string.IsNullOrEmpty(bgiDir) && Directory.Exists(bgiDir))
        {
            foreach (var f in Directory.EnumerateFiles(bgiDir, "better-genshin-impact*.log"))
            {
                if (!BgiLogFileNameRegex.IsMatch(Path.GetFileName(f))) continue;
                var fi = new FileInfo(f);
                result.Add(new LogFileItem
                {
                    Name = fi.Name, FullPath = fi.FullName, Group = "本机 · BGI 日志",
                    LastWriteTime = fi.LastWriteTime, Length = fi.Length
                });
            }
        }
        var assistDir = AssistantLogDir;
        if (Directory.Exists(assistDir))
        {
            foreach (var f in Directory.EnumerateFiles(assistDir, "assistant_runtime.*.log"))
            {
                var fi = new FileInfo(f);
                result.Add(new LogFileItem
                {
                    Name = fi.Name, FullPath = fi.FullName, Group = "本机 · 助手日志",
                    LastWriteTime = fi.LastWriteTime, Length = fi.Length
                });
            }
        }
        // 第三分组：远程下载的成员日志（remote_downloads\{成员名}\*.log），名字带成员目录前缀区分来源
        var downloadsDir = Path.Combine(assistDir, "remote_downloads");
        if (Directory.Exists(downloadsDir))
        {
            foreach (var f in Directory.EnumerateFiles(downloadsDir, "*.log", SearchOption.AllDirectories))
            {
                var fi = new FileInfo(f);
                var memberDir = Path.GetFileName(Path.GetDirectoryName(f) ?? "");
                result.Add(new LogFileItem
                {
                    Name = string.IsNullOrEmpty(memberDir) ? fi.Name : $"{memberDir}/{fi.Name}",
                    FullPath = fi.FullName, Group = "已下载的成员日志",
                    LastWriteTime = fi.LastWriteTime, Length = fi.Length
                });
            }
        }
        // 分组顺序固定：本机 BGI → 本机助手 → 已下载成员；组内按修改时间倒序（最新在最上）
        return result
            .OrderBy(f => f.Group switch { "本机 · BGI 日志" => 0, "本机 · 助手日志" => 1, _ => 2 })
            .ThenByDescending(f => f.LastWriteTime)
            .ToList();
    }

    /// <summary>后台扫描文件统计不同实例数（[实例:Sx:Px:Tx] 头段去重）。
    /// 大文件只扫头部 4MB + 尾部 4MB（实例标识通常在头尾都出现），避免打开页面时长时间扫整个大文件。</summary>
    public int CountInstances(string path, CancellationToken ct = default)
    {
        const long FullScanLimit = 8 * 1024 * 1024;   // ≤8MB 全扫
        const long WindowBytes = 4 * 1024 * 1024;     // 大文件头/尾各扫 4MB
        var set = new HashSet<string>();
        using var stream = OpenShared(path);
        if (stream.Length <= FullScanLimit)
        {
            ScanInstances(stream, set, ct);
        }
        else
        {
            ScanInstances(stream, set, ct, WindowBytes);
            stream.Seek(stream.Length - WindowBytes, SeekOrigin.Begin);
            ScanInstances(stream, set, ct); // 尾部读到文件末（起点可能在行中间，正则只认完整 [..] 段，无碍）
        }
        return set.Count;
    }

    /// <summary>从当前位置扫描实例标识到 limit 字节（不填=读到尾）。</summary>
    private static void ScanInstances(FileStream stream, HashSet<string> set, CancellationToken ct, long limit = long.MaxValue)
    {
        var instanceRegex = new Regex(@"\[[A-Za-z][A-Za-z0-9]*:S\d+:P\d+:T\d+\]", RegexOptions.Compiled);
        using var limited = new LimitedReadStream(stream, limit);
        using var reader = new StreamReader(limited, Utf8NoBom, detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            var m = instanceRegex.Match(line);
            if (m.Success) set.Add(m.Value);
        }
    }

    /// <summary>只读限量包装流（读满 Limit 字节后模拟 EOF；不解构底层流）。</summary>
    private sealed class LimitedReadStream(Stream inner, long limit) : Stream
    {
        private long _remaining = limit;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0) return 0;
            var n = inner.Read(buffer, offset, (int)Math.Min(count, _remaining));
            _remaining -= n;
            return n;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>整文件一次读入内存（日志浏览「记事本式」全量视图）：返回全部行（行号/偏移/级别齐全）。
    /// 超过 <see cref="MaxFullLoadBytes"/> 的文件只加载尾部一段并标记 Truncated（起点对齐到行首）。
    /// 行级别延续：无行头级别的续行（堆栈等）归属上一条日志的级别。调用方放后台线程。</summary>
    public FullLogLoad ReadAllLines(string path, CancellationToken ct = default)
    {
        using var stream = OpenShared(path);
        var fileLen = stream.Length;
        long baseOffset = 0;
        var truncated = false;
        if (fileLen > MaxFullLoadBytes)
        {
            truncated = true;
            baseOffset = fileLen - MaxFullLoadBytes;
            stream.Seek(baseOffset, SeekOrigin.Begin);
            // 起点对齐：跳过残段到下一行行首
            var head = new byte[ChunkBytes];
            var hn = stream.Read(head, 0, head.Length);
            var nl = hn > 0 ? Array.IndexOf(head, (byte)'\n', 0, hn) : -1;
            if (nl < 0) return new FullLogLoad { Truncated = true, FileLength = fileLen };
            baseOffset += nl + 1;
            stream.Seek(baseOffset, SeekOrigin.Begin);
        }
        var lines = new List<LogLineItem>();
        var lineNo = 0;
        var level = "";
        foreach (var (line, offset) in EnumerateLines(stream, baseOffset, ct))
        {
            lineNo++;
            var m = LineLevelRegex.Match(line);
            if (m.Success) level = m.Groups["lvl"].Value;
            lines.Add(new LogLineItem { LineNumber = lineNo, Offset = offset, Text = line, Level = level });
        }
        return new FullLogLoad { Lines = lines, Truncated = truncated, FileLength = fileLen };
    }

    /// <summary>按时间定位：正向扫描，返回第一行行头时间 ≥ target 的行起始偏移；无匹配返回 -1。
    /// 无行头时间的续行（堆栈等）跳过比较。大文件耗时，调用方放后台线程。</summary>
    public long FindTimeOffset(string path, TimeSpan target, CancellationToken ct = default)
    {
        using var stream = OpenShared(path);
        var buffer = new byte[ChunkBytes];
        long blockStart = 0;      // buffer[0] 对应的文件偏移
        long lineStart = 0;       // 当前行的起始偏移
        var lineHead = new List<byte>(64); // 当前行头部字节（够行头正则匹配即可，避免整行解码）
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n')
                {
                    if (lineHead.Count < 64) lineHead.Add(buffer[i]);
                    continue;
                }
                if (LogLineTime.TryGetTimeOfDay(Utf8NoBom.GetString(lineHead.ToArray()), out var tod)
                    && tod >= target)
                    return lineStart;
                lineStart = blockStart + i + 1;
                lineHead.Clear();
            }
            blockStart += n;
        }
        // 文件末尾无换行符的最后一行
        if (lineHead.Count > 0
            && LogLineTime.TryGetTimeOfDay(Utf8NoBom.GetString(lineHead.ToArray()), out var last)
            && last >= target)
            return lineStart;
        return -1;
    }

    /// <summary>文件内搜索（关键字或正则），返回命中行列表（行号从 1 起）。调用方放后台线程。</summary>
    public List<SearchResultItem> Search(string path, string pattern, bool isRegex, CancellationToken ct = default)
    {
        var results = new List<SearchResultItem>();
        if (string.IsNullOrEmpty(pattern)) return results;
        Regex? regex = null;
        if (isRegex) regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        long lineNo = 0;
        foreach (var (line, offset) in EnumerateLines(path, ct))
        {
            lineNo++;
            var hit = regex != null
                ? regex.IsMatch(line)
                : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (!hit) continue;
            var preview = line.Length > 160 ? line[..160] + "…" : line;
            results.Add(new SearchResultItem { LineNumber = lineNo, Offset = offset, Preview = preview, FullText = line });
            if (results.Count >= MaxSearchResults) return results;
        }
        return results;
    }

    /// <summary>按时间范围搜索：行头时间落在 [start, end] 内的行（无行头时间的续行归属上一条日志的时间；
    /// 文件头尚无时间戳的行不归入）。调用方放后台线程。</summary>
    public List<SearchResultItem> SearchTimeRange(string path, TimeSpan start, TimeSpan end, CancellationToken ct = default)
    {
        var results = new List<SearchResultItem>();
        long lineNo = 0;
        var haveTime = false;
        var current = TimeSpan.Zero;
        foreach (var (line, offset) in EnumerateLines(path, ct))
        {
            lineNo++;
            if (LogLineTime.TryGetTimeOfDay(line, out var t)) { current = t; haveTime = true; }
            if (!haveTime || current < start || current > end) continue;
            var preview = line.Length > 160 ? line[..160] + "…" : line;
            results.Add(new SearchResultItem { LineNumber = lineNo, Offset = offset, Preview = preview, FullText = line });
            if (results.Count >= MaxSearchResults) return results;
        }
        return results;
    }

    /// <summary>逐行扫描文件（共享读），产出 (行文本, 行起始偏移)。处理跨块续行与 \r\n；调用方在后台线程枚举。</summary>
    private static IEnumerable<(string Line, long Offset)> EnumerateLines(string path, CancellationToken ct)
    {
        using var stream = OpenShared(path);
        foreach (var t in EnumerateLines(stream, 0, ct)) yield return t;
    }

    /// <summary>逐行扫描流（从当前位置读到尾），产出 (行文本, 行起始偏移)。baseOffset=流当前位置对应的文件偏移。</summary>
    private static IEnumerable<(string Line, long Offset)> EnumerateLines(Stream stream, long baseOffset, CancellationToken ct)
    {
        var buffer = new byte[ChunkBytes];
        var tail = new List<byte>(); // 跨块未完成的行字节
        long tailOffset = baseOffset; // tail 首字节在文件中的偏移（= 当前未完成行的起始偏移）
        long blockStart = baseOffset; // buffer[0] 对应的文件偏移
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            var segStart = 0;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                // 拼出完整行：tail + buffer[segStart..i]，去掉行尾 \r
                string line;
                var segLen = i - segStart;
                if (segLen > 0 && buffer[i - 1] == (byte)'\r') segLen--;
                if (tail.Count == 0)
                {
                    line = Utf8NoBom.GetString(buffer, segStart, segLen);
                }
                else
                {
                    tail.AddRange(buffer.AsSpan(segStart, segLen).ToArray());
                    if (tail.Count > 0 && tail[^1] == (byte)'\r') tail.RemoveAt(tail.Count - 1);
                    line = Utf8NoBom.GetString(tail.ToArray());
                }
                var offset = tailOffset;
                segStart = i + 1;
                tail.Clear();
                tailOffset = blockStart + segStart;
                yield return (line, offset);
            }
            // 块内未遇到换行的尾部留存到 tail（若 tail 为空则从本块 segStart 起）
            if (tail.Count == 0 && segStart < n) tailOffset = blockStart + segStart;
            for (var i = segStart; i < n; i++) tail.Add(buffer[i]);
            blockStart += n;
        }
        // 文件末尾无换行符的最后一行
        if (tail.Count > 0)
        {
            if (tail[^1] == (byte)'\r') tail.RemoveAt(tail.Count - 1);
            yield return (Utf8NoBom.GetString(tail.ToArray()), tailOffset);
        }
    }

    /// <summary>原样导出：共享读复制整个文件（正在写入的当天文件同样可复制）。</summary>
    public void CopyFile(string sourcePath, string destPath)
    {
        using var src = OpenShared(sourcePath);
        using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
        src.CopyTo(dst);
    }

    /// <summary>筛选结果导出为 .log（每行原文）或 .csv（行号,时间?,内容）。</summary>
    public void ExportLines(IEnumerable<string> lines, string destPath, bool asCsv)
    {
        using var writer = new StreamWriter(destPath, false, Utf8NoBom);
        if (asCsv)
        {
            writer.WriteLine("LineNo,Text");
            var i = 0;
            foreach (var l in lines)
            {
                i++;
                writer.WriteLine($"{i},\"{l.Replace("\"", "\"\"")}\"");
            }
        }
        else
        {
            foreach (var l in lines) writer.WriteLine(l);
        }
    }

    /// <summary>共享读打开日志文件（BGI 占用锁下也能读）。</summary>
    private static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}
