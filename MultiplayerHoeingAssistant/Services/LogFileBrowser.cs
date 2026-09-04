using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 日志文件浏览器（嘟嘟可 P2 / F2）。
/// 枚举 BGI 日志（&lt;BGI&gt;\log\better-genshin-impact*.log）与助手自身日志（log\assistant_runtime.*.log），
/// 提供分块随机访问读取（FileStream.Seek）、按行跳转、关键字/正则搜索、导出。
/// 所有打开均为 FileShare.ReadWrite | Delete 共享读，兼容 BGI 正在写入的当天文件。
/// </summary>
public sealed class LogFileBrowser
{
    private static readonly Regex BgiLogFileNameRegex = new(
        @"^better-genshin-impact(\d{8})?(_\d{3})*\.log$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);

    /// <summary>单块读取的目标字节数（查看区按需加载相邻块）。</summary>
    public const int ChunkBytes = 256 * 1024;
    /// <summary>搜索结果上限（防止超大文件搜索出几万条卡 UI）。</summary>
    public const int MaxSearchResults = 5000;

    private readonly Func<string?> _bgiLogDirProvider;

    /// <param name="bgiLogDirProvider">BGI 日志目录提供者（可为 null=未配置）。</param>
    public LogFileBrowser(Func<string?> bgiLogDirProvider)
    {
        _bgiLogDirProvider = bgiLogDirProvider;
    }

    /// <summary>助手自身日志目录（exe 目录下 log/，与 MainViewModel.AddLog 写法一致）。</summary>
    public static string AssistantLogDir =>
        Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");

    /// <summary>枚举两个数据源的日志文件（BGI 组 + 助手组），按修改时间倒序。</summary>
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
                    Name = fi.Name, FullPath = fi.FullName, Group = "BGI 日志",
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
                    Name = fi.Name, FullPath = fi.FullName, Group = "助手日志",
                    LastWriteTime = fi.LastWriteTime, Length = fi.Length
                });
            }
        }
        return result.OrderByDescending(f => f.LastWriteTime).ToList();
    }

    /// <summary>后台扫描文件统计不同实例数（[实例:Sx:Px:Tx] 头段去重）。</summary>
    public int CountInstances(string path, CancellationToken ct = default)
    {
        var set = new HashSet<string>();
        var instanceRegex = new Regex(@"\[[A-Za-z][A-Za-z0-9]*:S\d+:P\d+:T\d+\]", RegexOptions.Compiled);
        using var stream = OpenShared(path);
        using var reader = new StreamReader(stream, Utf8NoBom);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            ct.ThrowIfCancellationRequested();
            var m = instanceRegex.Match(line);
            if (m.Success) set.Add(m.Value);
        }
        return set.Count;
    }

    /// <summary>
    /// 从 startOffset 起正向读一块（约 <see cref="ChunkBytes"/>）。
    /// 起始处若非行首会跳过残段；结尾会把最后一行读完。
    /// 返回行列表与块区间 [startOffset, endOffset)。
    /// </summary>
    public LogChunk ReadChunkForward(string path, long startOffset)
    {
        using var stream = OpenShared(path);
        var fileLen = stream.Length;
        if (startOffset >= fileLen)
            return new LogChunk { Lines = [], StartOffset = fileLen, EndOffset = fileLen, ReachedEnd = true };

        stream.Seek(startOffset, SeekOrigin.Begin);
        var raw = ReadBytes(stream, ChunkBytes + 4096);
        // 起点对齐：跳过不完整的首行
        var bodyStart = 0;
        if (startOffset > 0)
        {
            var nl = Array.IndexOf(raw, (byte)'\n');
            if (nl < 0) return new LogChunk { Lines = [], StartOffset = fileLen, EndOffset = fileLen, ReachedEnd = true };
            bodyStart = nl + 1;
        }
        // 终点对齐：截到最后一个完整行
        var lastNl = LastIndexOf(raw, (byte)'\n', raw.Length - 1);
        var completeEnd = lastNl >= bodyStart ? lastNl + 1 : bodyStart;

        var lines = DecodeLines(raw, bodyStart, completeEnd, startOffset);
        var endOffset = startOffset + completeEnd;
        return new LogChunk
        {
            Lines = lines,
            StartOffset = startOffset + bodyStart,
            EndOffset = endOffset,
            ReachedEnd = endOffset >= fileLen
        };
    }

    /// <summary>从 endOffset 向前倒读一块（用于滚动到顶部时加载上一块）。</summary>
    public LogChunk ReadChunkBackward(string path, long endOffset)
    {
        using var stream = OpenShared(path);
        var fileLen = stream.Length;
        endOffset = Math.Min(endOffset, fileLen);
        var startOffset = Math.Max(0, endOffset - ChunkBytes);
        if (endOffset <= startOffset)
            return new LogChunk { Lines = [], StartOffset = 0, EndOffset = 0, ReachedStart = true };

        stream.Seek(startOffset, SeekOrigin.Begin);
        var raw = ReadBytes(stream, (int)(endOffset - startOffset));
        var bodyStart = 0;
        if (startOffset > 0)
        {
            var nl = Array.IndexOf(raw, (byte)'\n');
            if (nl < 0) return new LogChunk { Lines = [], StartOffset = startOffset, EndOffset = endOffset };
            bodyStart = nl + 1;
        }
        // 尾对齐：丢掉不完整的末行（除非正好读到文件尾且以换行结束）
        var readEnd = raw.Length;
        if (endOffset < fileLen)
        {
            var lastNl = LastIndexOf(raw, (byte)'\n', raw.Length - 1);
            readEnd = lastNl >= bodyStart ? lastNl + 1 : bodyStart;
        }

        var lines = DecodeLines(raw, bodyStart, readEnd, startOffset);
        return new LogChunk
        {
            Lines = lines,
            StartOffset = startOffset + bodyStart,
            EndOffset = startOffset + readEnd,
            ReachedStart = startOffset + bodyStart <= 0
        };
    }

    /// <summary>按行号跳转：从头扫描数行（大文件耗时，调用方放后台线程）。返回该行起始偏移；行号越界返回 -1。</summary>
    public long FindLineOffset(string path, long lineNumber, CancellationToken ct = default)
    {
        if (lineNumber < 1) return -1;
        using var stream = OpenShared(path);
        var buffer = new byte[ChunkBytes];
        long blockStart = 0;      // buffer[0] 对应的文件偏移
        long lineStart = 0;       // 当前行（currentLine）的起始偏移
        long currentLine = 1;
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            if (currentLine == lineNumber) return lineStart;
            for (var i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                currentLine++;
                lineStart = blockStart + i + 1;
                if (currentLine == lineNumber) return lineStart;
            }
            blockStart += n;
        }
        // 文件最后一行无换行符的情况：currentLine 行存在但未到换行
        return currentLine == lineNumber ? lineStart : -1;
    }

    /// <summary>文件内搜索（关键字或正则），返回命中行列表（行号从 1 起）。调用方放后台线程。</summary>
    public List<SearchResultItem> Search(string path, string pattern, bool isRegex, CancellationToken ct = default)
    {
        var results = new List<SearchResultItem>();
        if (string.IsNullOrEmpty(pattern)) return results;
        Regex? regex = null;
        if (isRegex) regex = new Regex(pattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);

        using var stream = OpenShared(path);
        var buffer = new byte[ChunkBytes];
        var tail = new List<byte>(); // 跨块未完成的行字节
        long tailOffset = 0;         // tail 首字节在文件中的偏移（= 当前未完成行的起始偏移）
        long blockStart = 0;         // buffer[0] 对应的文件偏移
        long lineNo = 0;
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
                var hit = regex != null
                    ? regex.IsMatch(line)
                    : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
                if (hit)
                {
                    var preview = line.Length > 160 ? line[..160] + "…" : line;
                    results.Add(new SearchResultItem { LineNumber = lineNo + 1, Offset = tailOffset, Preview = preview, FullText = line });
                    if (results.Count >= MaxSearchResults) return results;
                }
                lineNo++;
                segStart = i + 1;
                tail.Clear();
                tailOffset = blockStart + segStart;
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
            var line = Utf8NoBom.GetString(tail.ToArray());
            var hit = regex != null
                ? regex.IsMatch(line)
                : line.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            if (hit && results.Count < MaxSearchResults)
            {
                var preview = line.Length > 160 ? line[..160] + "…" : line;
                results.Add(new SearchResultItem { LineNumber = lineNo + 1, Offset = tailOffset, Preview = preview, FullText = line });
            }
        }
        return results;
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

    private static byte[] ReadBytes(Stream stream, int count)
    {
        var buffer = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n <= 0) break;
            read += n;
        }
        if (read == count) return buffer;
        Array.Resize(ref buffer, read);
        return buffer;
    }

    private static int LastIndexOf(byte[] data, byte value, int fromIndex)
    {
        for (var i = Math.Min(fromIndex, data.Length - 1); i >= 0; i--)
            if (data[i] == value) return i;
        return -1;
    }

    /// <summary>把字节段按行解码为 LogLineItem（带每行起始偏移）。</summary>
    private static List<LogLineItem> DecodeLines(byte[] raw, int bodyStart, int bodyEnd, long chunkFileStart)
    {
        var result = new List<LogLineItem>();
        if (bodyEnd <= bodyStart) return result;
        var text = Utf8NoBom.GetString(raw, bodyStart, bodyEnd - bodyStart);
        var lineFileOffset = chunkFileStart + bodyStart;
        var pos = 0;
        while (pos < text.Length)
        {
            var idx = text.IndexOf('\n', pos);
            string lineText;
            int byteLen;
            if (idx < 0)
            {
                lineText = text[pos..].TrimEnd('\r');
                byteLen = Utf8NoBom.GetByteCount(text[pos..]);
                pos = text.Length;
            }
            else
            {
                lineText = text[pos..idx].TrimEnd('\r');
                byteLen = Utf8NoBom.GetByteCount(text[pos..idx]) + 1;
                pos = idx + 1;
            }
            result.Add(new LogLineItem { Offset = lineFileOffset, Text = lineText });
            lineFileOffset += byteLen;
        }
        return result;
    }
}

/// <summary>一块日志内容（带文件偏移区间与到边标记）。</summary>
public sealed class LogChunk
{
    public List<LogLineItem> Lines { get; set; } = [];
    public long StartOffset { get; set; }
    public long EndOffset { get; set; }
    /// <summary>正向读时是否已到文件尾。</summary>
    public bool ReachedEnd { get; set; }
    /// <summary>倒读时是否已到文件头。</summary>
    public bool ReachedStart { get; set; }
}
