using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 诊断包一键导出服务（嘟嘟可 P4 / §5-C）。
/// 选一个时间范围（开始~结束，默认最近 10 分钟），打包 zip：
/// - bgi_log_slice_{文件名}.log：BGI 日志中该范围内的切片（按行头时间戳过滤，
///   行头只有 HH:mm:ss，日期按日志文件名/修改时间拼；多行事件整块保留）；
/// - assistant_runtime.{范围内每天}.s*.log 全天文件（小，直接全放）；
/// - dodoco_exceptions.{范围内每天}.jsonl / dodoco_stats.{范围内每天}.jsonl；
/// - members_snapshot.json：当前成员状态快照；
/// - README.txt：包内容与生成时间说明。
/// 所有源文件均共享读（FileShare.ReadWrite|Delete），正在写入的当天文件也能打包。
/// </summary>
public sealed class DiagnosticPackageService
{
    // 行头正则（与 BgiLogTailService 同源，同步自 LogParse.cs:22-24；切片只需时间组）
    private static readonly Regex HeaderRegex = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2}\.\d+)\] \[[^\]]+\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FileDateRegex = new(
        @"better-genshin-impact(?<d>\d{8})", RegexOptions.Compiled);

    private readonly Func<string?> _bgiLogDirProvider;
    private readonly Func<string> _assistantLogDirProvider;
    /// <summary>成员状态快照提供者（调用方负责在 UI 线程枚举 Members 并转成可序列化字典列表）。</summary>
    private readonly Func<IReadOnlyList<Dictionary<string, object?>>> _membersSnapshotProvider;

    public DiagnosticPackageService(
        Func<string?> bgiLogDirProvider,
        Func<IReadOnlyList<Dictionary<string, object?>>> membersSnapshotProvider,
        Func<string>? assistantLogDirProvider = null)
    {
        _bgiLogDirProvider = bgiLogDirProvider;
        _membersSnapshotProvider = membersSnapshotProvider;
        _assistantLogDirProvider = assistantLogDirProvider ?? (() => LogFileBrowser.AssistantLogDir);
    }

    /// <summary>生成诊断包 zip。调用方放后台线程；windowStart/windowEnd 为日志切片时间范围（调用方已处理跨零点）；
    /// 返回打包内容摘要（README 同款，供界面提示）。</summary>
    public string Export(DateTime windowStart, DateTime windowEnd, string destZipPath)
    {
        using var zipStream = new FileStream(destZipPath, FileMode.Create, FileAccess.Write, FileShare.None);
        return Export(windowStart, windowEnd, zipStream);
    }

    /// <summary>网页传输使用可限额流；strict 时任何源读取失败都不返回不完整的包。</summary>
    public string Export(DateTime windowStart, DateTime windowEnd, Stream destination, bool strict = false,
        CancellationToken cancellationToken = default)
    {
        var summary = new List<string>();
        long inputBytes = 0;
        void Charge(string file)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!strict) return;
            inputBytes = checked(inputBytes + new FileInfo(file).Length);
            if (inputBytes > 32 * 1024 * 1024) throw new IOException("诊断数据超过 32 MiB 输入上限，请缩短时间范围");
        }

        using (var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true))
        {
            // 1) BGI 日志切片：目标日期的当天/相邻日期文件都扫（跨天时窗口可能跨文件）
            var bgiDir = _bgiLogDirProvider();
            if (!string.IsNullOrEmpty(bgiDir) && Directory.Exists(bgiDir))
            {
                var candidates = Directory.EnumerateFiles(bgiDir, "better-genshin-impact*.log")
                    .Where(f => Path.GetFileName(f).StartsWith("better-genshin-impact", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var file in candidates)
                {
                    var fileDate = ExtractFileDate(file) ?? File.GetLastWriteTime(file).Date;
                    // 窗口与文件日期相交才值得切（文件内容大体是该日期的日志）
                    if (fileDate < windowStart.Date.AddDays(-1) || fileDate > windowEnd.Date.AddDays(1))
                        continue;
                    var lines = SliceFile(file, fileDate, windowStart, windowEnd, cancellationToken, strict ? chars =>
                    {
                        inputBytes = checked(inputBytes + chars * 2L);
                        if (inputBytes > 32 * 1024 * 1024) throw new IOException("诊断内容超过 32 MiB，请缩短时间范围");
                    } : null);
                    if (lines.Count == 0) continue;
                    var entryName = $"bgi_log_slice_{Path.GetFileName(file)}";
                    WriteTextEntry(zip, entryName, string.Join('\n', lines) + '\n');
                    summary.Add($"BGI 日志切片 {Path.GetFileName(file)}：{lines.Count} 行");
                }
            }
            else
            {
                if (strict) throw new DirectoryNotFoundException("BGI 日志目录不可用");
                summary.Add("BGI 日志目录不可用（未配置 BGI 路径或目录不存在），未含 BGI 切片");
            }

            // 2) 助手日志 + 3) 异常库与统计：范围内每一天的当天文件（小，直接全放）
            var assistDir = _assistantLogDirProvider();
            if (DirectoryAvailable(assistDir, strict))
            {
                for (var date = windowStart.Date; date <= windowEnd.Date; date = date.AddDays(1))
                {
                    foreach (var file in Directory.EnumerateFiles(assistDir, $"assistant_runtime.{date:yyyy-MM-dd}.s*.log"))
                    {
                        Charge(file);
                        AddFileEntry(zip, file, $"assistant_log_{Path.GetFileName(file)}", cancellationToken);
                        summary.Add($"助手日志 {Path.GetFileName(file)}");
                    }
                    foreach (var extra in new[] { $"dodoco_exceptions.{date:yyyy-MM-dd}.jsonl", $"dodoco_stats.{date:yyyy-MM-dd}.jsonl" })
                    {
                        var path = Path.Combine(assistDir, extra);
                        if (FileAvailable(path, strict))
                        {
                            Charge(path);
                            AddFileEntry(zip, path, extra, cancellationToken);
                            summary.Add(extra);
                        }
                    }
                }
            }

            // 4) 成员状态快照
            try
            {
                var members = _membersSnapshotProvider();
                var json = JsonSerializer.Serialize(members, new JsonSerializerOptions { WriteIndented = true });
                WriteTextEntry(zip, "members_snapshot.json", json);
                summary.Add($"成员状态快照（{members.Count} 名成员）");
            }
            catch (Exception ex)
            {
                if (strict) throw;
                summary.Add($"成员状态快照失败: {ex.Message}");
            }

            // 5) 按异常点组织：范围内每条异常记录一个子目录
            //    exceptions/NNN_HHmmss_规则名/record.json（含前后各 5 行日志上下文）+ 匹配到的事发快照帧
            try
            {
                var records = LoadExceptionRecordsInRange(windowStart, windowEnd, strict, cancellationToken);
                var idx = 0;
                foreach (var r in records.OrderBy(r => r.Time).Take(MaxExceptionDirs))
                {
                    idx++;
                    var folder = $"exceptions/{idx:000}_{r.Time:HHmmss}_{SanitizeEntryName(r.RuleName)}";
                    WriteTextEntry(zip, $"{folder}/record.json",
                        JsonSerializer.Serialize(r, new JsonSerializerOptions
                        {
                            WriteIndented = true,
                            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                        }));
                    var dir = IncidentSnapshotService.FindIncidentDir(r.Time, r.RuleName,
                        Path.Combine(_assistantLogDirProvider(), "incidents"), strict);
                    var frames = 0;
                    if (dir != null)
                    {
                        foreach (var f in Directory.EnumerateFiles(dir, "frame_*.jpg"))
                        {
                            Charge(f);
                            AddFileEntry(zip, f, $"{folder}/{Path.GetFileName(f)}", cancellationToken);
                            frames++;
                        }
                    }
                    summary.Add($"异常点 {r.Time:MM-dd HH:mm:ss}「{r.RuleName}」"
                        + (frames > 0 ? $"（含事发截图 {frames} 帧）" : "（无快照）"));
                }
                if (records.Count > MaxExceptionDirs)
                {
                    if (strict) throw new IOException("异常点超过 100 条，请缩短时间范围");
                    summary.Add($"异常点过多，仅打包前 {MaxExceptionDirs} 条（共 {records.Count} 条）");
                }
            }
            catch (Exception ex)
            {
                if (strict) throw;
                summary.Add($"异常点明细打包失败: {ex.Message}");
            }

            // 6) README
            var readme = new StringBuilder();
            readme.AppendLine("嘟嘟可诊断包");
            readme.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            readme.AppendLine($"时间范围: {windowStart:yyyy-MM-dd HH:mm:ss} ~ {windowEnd:yyyy-MM-dd HH:mm:ss}");
            readme.AppendLine();
            readme.AppendLine("包内容:");
            foreach (var s in summary) readme.AppendLine($"  - {s}");
            readme.AppendLine();
            readme.AppendLine("说明: BGI 日志切片按行头时间戳过滤（行头只有时分秒，日期按日志文件名拼）。");
            readme.AppendLine("exceptions/ 下每个子目录是一个异常点：record.json 含触发日志原文与前后各 5 行上下文，");
            readme.AppendLine("若该规则开了“存快照”则同目录还有事发前后的游戏截图（frame_-XX.X ~ frame_+XX.X，秒数见事发录像设置）。");
            readme.AppendLine("排查联机问题时把本 zip 发给队友/开发者即可，替代手工翻两个日志目录。");
            WriteTextEntry(zip, "README.txt", readme.ToString());
        }

        return string.Join('\n', summary);
    }

    /// <summary>异常点明细的打包上限（防规则风暴时 zip 爆炸）。</summary>
    private const int MaxExceptionDirs = 100;

    /// <summary>读范围内每一天的异常库 JSONL，返回时间落在 [windowStart, windowEnd] 的记录。</summary>
    private List<ExceptionRecord> LoadExceptionRecordsInRange(DateTime windowStart, DateTime windowEnd,
        bool strict, CancellationToken cancellationToken)
    {
        var result = new List<ExceptionRecord>();
        var assistDir = _assistantLogDirProvider();
        if (!DirectoryAvailable(assistDir, strict)) return result;
        for (var date = windowStart.Date; date <= windowEnd.Date; date = date.AddDays(1))
        {
            var path = Path.Combine(assistDir, $"dodoco_exceptions.{date:yyyy-MM-dd}.jsonl");
            if (!FileAvailable(path, strict)) continue;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (line.Length == 0) continue;
                ExceptionRecord? r;
                try { r = JsonSerializer.Deserialize<ExceptionRecord>(line); }
                catch when (!strict) { continue; } // PC 继续保留历史容错
                if (r != null && r.Time >= windowStart && r.Time <= windowEnd)
                    result.Add(r);
            }
        }
        return result;
    }

    /// <summary>zip 条目名清洗（路径非法字符转下划线）。</summary>
    private static bool DirectoryAvailable(string path, bool strict)
    {
        if (!strict) return Directory.Exists(path);
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool FileAvailable(string path, bool strict)
    {
        if (!strict) return File.Exists(path);
        try { File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string SanitizeEntryName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name.Replace('/', '_').Replace('\\', '_');
    }

    /// <summary>从日志文件中切出时间窗口内的行（多行事件整块保留：命中窗口的头行连同其后续非头行）。</summary>
    private List<string> SliceFile(string path, DateTime fileDate, DateTime windowStart, DateTime windowEnd,
        CancellationToken cancellationToken, Action<int>? chargeSelected)
    {
        var result = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var inWindow = false; // 上一条头行是否在窗口内（非头行跟随头行去留）
        foreach (var line in chargeSelected == null ? ReadUnboundedLines(reader) : ReadBoundedLines(reader, cancellationToken, true))
        {
            var m = HeaderRegex.Match(line);
            if (m.Success)
            {
                inWindow = false;
                if (TimeSpan.TryParseExact(m.Groups["time"].Value, @"hh\:mm\:ss\.fff",
                        System.Globalization.CultureInfo.InvariantCulture, out var tod))
                {
                    var t = fileDate.Add(tod);
                    inWindow = t >= windowStart && t <= windowEnd;
                }
            }
            // 非头行（消息/异常正文）跟随上一条头行的判定（与 tail 服务"绝不丢行"同一容错语义）
            if (inWindow) { chargeSelected?.Invoke(line.Length + 1); result.Add(line); }
        }
        return result;
    }

    private static IEnumerable<string> ReadUnboundedLines(StreamReader reader)
    {
        string? line;
        while ((line = reader.ReadLine()) != null) yield return line;
    }

    private static IEnumerable<string> ReadBoundedLines(StreamReader reader, CancellationToken cancellationToken, bool strict)
    {
        var buffer = new char[8192];
        var line = new StringBuilder();
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (var i = 0; i < count; i++)
            {
                if (buffer[i] == '\n')
                {
                    if (line.Length > 0 && line[^1] == '\r') line.Length--;
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    line.Append(buffer[i]);
                    if (strict && line.Length > 4 * 1024 * 1024) throw new IOException("日志单行超过 4 MiB，无法生成诊断包");
                }
            }
        }
        if (line.Length != 0) yield return line.ToString();
    }

    private static DateTime? ExtractFileDate(string path)
    {
        var m = FileDateRegex.Match(Path.GetFileName(path));
        if (m.Success && DateTime.TryParseExact(m.Groups["d"].Value, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            return d;
        return null;
    }

    /// <summary>共享读把文件加入 zip。</summary>
    private static void AddFileEntry(ZipArchive zip, string path, string entryName, CancellationToken cancellationToken)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var dst = entry.Open();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = src.Read(buffer, 0, buffer.Length)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dst.Write(buffer, 0, read);
        }
    }

    private static void WriteTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
