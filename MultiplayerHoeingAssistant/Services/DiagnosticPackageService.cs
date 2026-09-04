using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 诊断包一键导出服务（嘟嘟可 P4 / §5-C）。
/// 选一个时间点（默认当前），打包 zip：
/// - bgi_log_slice_{文件名}.log：BGI 日志中目标时间前后 10 分钟的切片（按行头时间戳过滤，
///   行头只有 HH:mm:ss，日期按日志文件名/修改时间拼；多行事件整块保留）；
/// - assistant_runtime.{当天}.s*.log 全天文件（小，直接全放）；
/// - dodoco_exceptions.{当天}.jsonl / dodoco_stats.{当天}.jsonl；
/// - members_snapshot.json：当前成员状态快照；
/// - README.txt：包内容与生成时间说明。
/// 所有源文件均共享读（FileShare.ReadWrite|Delete），正在写入的当天文件也能打包。
/// </summary>
public sealed class DiagnosticPackageService
{
    /// <summary>日志切片的时间窗口（目标时间前后）。</summary>
    private static readonly TimeSpan SliceWindow = TimeSpan.FromMinutes(10);

    // 行头正则（与 BgiLogTailService 同源，同步自 LogParse.cs:22-24；切片只需时间组）
    private static readonly Regex HeaderRegex = new(
        @"^\[(?<time>\d{2}:\d{2}:\d{2}\.\d+)\] \[[^\]]+\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex FileDateRegex = new(
        @"better-genshin-impact(?<d>\d{8})", RegexOptions.Compiled);

    private readonly Func<string?> _bgiLogDirProvider;
    /// <summary>成员状态快照提供者（调用方负责在 UI 线程枚举 Members 并转成可序列化字典列表）。</summary>
    private readonly Func<IReadOnlyList<Dictionary<string, object?>>> _membersSnapshotProvider;

    public DiagnosticPackageService(
        Func<string?> bgiLogDirProvider,
        Func<IReadOnlyList<Dictionary<string, object?>>> membersSnapshotProvider)
    {
        _bgiLogDirProvider = bgiLogDirProvider;
        _membersSnapshotProvider = membersSnapshotProvider;
    }

    /// <summary>生成诊断包 zip。调用方放后台线程；返回打包内容摘要（README 同款，供界面提示）。</summary>
    public string Export(DateTime targetTime, string destZipPath)
    {
        var windowStart = targetTime - SliceWindow;
        var windowEnd = targetTime + SliceWindow;
        var summary = new List<string>();
        var date = targetTime.Date;

        using (var zipStream = new FileStream(destZipPath, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(zipStream, ZipArchiveMode.Create))
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
                    var lines = SliceFile(file, fileDate, windowStart, windowEnd);
                    if (lines.Count == 0) continue;
                    var entryName = $"bgi_log_slice_{Path.GetFileName(file)}";
                    WriteTextEntry(zip, entryName, string.Join('\n', lines) + '\n');
                    summary.Add($"BGI 日志切片 {Path.GetFileName(file)}：{lines.Count} 行");
                }
            }
            else
            {
                summary.Add("BGI 日志目录不可用（未配置 BGI 路径或目录不存在），未含 BGI 切片");
            }

            // 2) 助手日志（当天全天，直接全放）
            var assistDir = LogFileBrowser.AssistantLogDir;
            if (Directory.Exists(assistDir))
            {
                foreach (var file in Directory.EnumerateFiles(assistDir, $"assistant_runtime.{date:yyyy-MM-dd}.s*.log"))
                {
                    AddFileEntry(zip, file, $"assistant_log_{Path.GetFileName(file)}");
                    summary.Add($"助手日志 {Path.GetFileName(file)}");
                }
                // 3) 异常库与统计（当天）
                foreach (var extra in new[] { $"dodoco_exceptions.{date:yyyy-MM-dd}.jsonl", $"dodoco_stats.{date:yyyy-MM-dd}.jsonl" })
                {
                    var path = Path.Combine(assistDir, extra);
                    if (File.Exists(path))
                    {
                        AddFileEntry(zip, path, extra);
                        summary.Add(extra);
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
                summary.Add($"成员状态快照失败: {ex.Message}");
            }

            // 5) README
            var readme = new StringBuilder();
            readme.AppendLine("嘟嘟可诊断包");
            readme.AppendLine($"生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            readme.AppendLine($"目标时间点: {targetTime:yyyy-MM-dd HH:mm:ss}（前后各 {(int)SliceWindow.TotalMinutes} 分钟切片）");
            readme.AppendLine();
            readme.AppendLine("包内容:");
            foreach (var s in summary) readme.AppendLine($"  - {s}");
            readme.AppendLine();
            readme.AppendLine("说明: BGI 日志切片按行头时间戳过滤（行头只有时分秒，日期按日志文件名拼）。");
            readme.AppendLine("排查联机问题时把本 zip 发给队友/开发者即可，替代手工翻两个日志目录。");
            WriteTextEntry(zip, "README.txt", readme.ToString());
        }

        return string.Join('\n', summary);
    }

    /// <summary>从日志文件中切出时间窗口内的行（多行事件整块保留：命中窗口的头行连同其后续非头行）。</summary>
    private List<string> SliceFile(string path, DateTime fileDate, DateTime windowStart, DateTime windowEnd)
    {
        var result = new List<string>();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var inWindow = false; // 上一条头行是否在窗口内（非头行跟随头行去留）
        string? line;
        while ((line = reader.ReadLine()) != null)
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
            if (inWindow) result.Add(line);
        }
        return result;
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
    private static void AddFileEntry(ZipArchive zip, string path, string entryName)
    {
        using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var dst = entry.Open();
        src.CopyTo(dst);
    }

    private static void WriteTextEntry(ZipArchive zip, string entryName, string content)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
