using System.IO;
using System.IO.Compression;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 助手全部设置一键导出/导入（跨机器迁移）。
/// 打包 %APPDATA%/NexusBGI 下的全部设置 JSON 为单个 zip（含 manifest.json 校验清单），
/// 目标机器导入后重启助手即完成迁移。
/// 导入安全策略：
/// - 白名单条目，拒绝 zip 内出现路径分隔符或未知文件（防路径穿越）；
/// - 覆盖前把现有设置整体备份到 NexusBGI/backup-import-时间戳/，可手工回滚；
/// - assistant-config.json 中 ClientInstanceId（本机实例标识）永远保留本机值，
///   BgiPath/BgiPackageDir 在导入值在本机不存在时保留本机值（路径是机器相关的）。
/// </summary>
public class ConfigTransferService
{
    /// <summary>导出格式版本；导入时校验，防止跨大版本误读。</summary>
    private const int FormatVersion = 1;

    /// <summary>组成"全部设置"的文件清单（与各 Service 的持久化路径约定一致，均在 %APPDATA%/NexusBGI）。</summary>
    public static readonly string[] SettingFileNames =
    [
        "assistant-config.json",     // 主配置：房间/密码/UID/队伍/BGI 路径/启动策略/定时上线/一键命令等
        "startup-flow.json",         // 槲寄生启动流程
        "startup-flow-schemes.json", // 启动流程方案库
        "dodoco_settings.json",      // 嘟嘟可设置
        "dodoco_watch_rules.json",   // 嘟嘟可关键词监控规则
        "pet_settings.json",         // 奥黛塔桌宠设置
        "member-config-cache.json",  // 成员配置缓存（成员离线时用的配置组）
    ];

    private static string ConfigDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI");

    /// <summary>导出结果（供 UI 汇报）。</summary>
    public record ExportResult(bool Success, string Message, string ZipPath);

    /// <summary>导入结果（供 UI 汇报：实际导入的文件 / 包内缺失的文件 / 备份目录）。</summary>
    public record ImportResult(bool Success, string Message, List<string> Imported, List<string> Missing, string BackupDir);

    /// <summary>一键导出全部设置到指定 zip 路径。</summary>
    public ExportResult Export(string zipPath)
    {
        try
        {
            var exported = new List<string>();
            using var fs = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
            WriteJsonEntry(zip, "manifest.json", JsonSerializer.Serialize(new
            {
                app = "NexusBGI-Assistant",
                formatVersion = FormatVersion,
                exportedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
                files = SettingFileNames,
            }, new JsonSerializerOptions { WriteIndented = true }));

            foreach (var name in SettingFileNames)
            {
                var path = Path.Combine(ConfigDir, name);
                if (!File.Exists(path)) continue; // 缺失的设置（从未修改过的功能）跳过，导入侧按 manifest 报告
                zip.CreateEntryFromFile(path, name, CompressionLevel.Optimal);
                exported.Add(name);
            }

            if (exported.Count == 0)
                return new ExportResult(false, "没有任何可导出的设置文件（本机尚未生成任何设置）。", zipPath);

            return new ExportResult(true,
                $"已导出 {exported.Count} 个设置文件到：\n{zipPath}", zipPath);
        }
        catch (Exception ex)
        {
            return new ExportResult(false, $"导出失败: {ex.Message}", zipPath);
        }
    }

    /// <summary>从指定 zip 导入全部设置（覆盖前备份现有文件，导入后由调用方提示重启生效）。</summary>
    public ImportResult Import(string zipPath)
    {
        try
        {
            if (!File.Exists(zipPath))
                return new ImportResult(false, "文件不存在。", [], [], "");

            using var fs = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            var entries = zip.Entries;

            // ① 校验 manifest：没有清单不是本助手导出的设置包
            var manifestEntry = entries.FirstOrDefault(e => e.FullName == "manifest.json");
            if (manifestEntry == null)
                return new ImportResult(false, "不是有效的助手设置包（缺少 manifest.json）。", [], [], "");
            using (var r = new StreamReader(manifestEntry.Open()))
            using (var doc = JsonDocument.Parse(r.ReadToEnd()))
            {
                if (!doc.RootElement.TryGetProperty("formatVersion", out var ver) || ver.GetInt32() != FormatVersion)
                    return new ImportResult(false, "设置包格式版本不兼容，请先升级助手再导入。", [], [], "");
            }

            // ② 白名单校验：只接受已知设置文件名，拒绝子目录/未知文件（防路径穿越与恶意包）
            var unknown = entries
                .Where(e => e.FullName != "manifest.json" && !SettingFileNames.Contains(e.FullName))
                .Select(e => e.FullName).ToList();
            if (unknown.Count > 0)
                return new ImportResult(false, $"设置包含未知文件，已拒绝导入：\n{string.Join("\n", unknown)}", [], [], "");

            // ③ 先解到临时目录并验证 JSON 可解析，全部通过才动现有文件
            var tempDir = Path.Combine(Path.GetTempPath(), $"NexusBGI-import-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var extracted = new List<string>();
            try
            {
                foreach (var entry in entries)
                {
                    if (entry.FullName == "manifest.json") continue;
                    var target = Path.Combine(tempDir, entry.FullName);
                    entry.ExtractToFile(target, overwrite: true);
                    var json = File.ReadAllText(target);
                    if (entry.FullName == "assistant-config.json")
                        JsonSerializer.Deserialize<AssistConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    else
                        JsonDocument.Parse(json); // 语法校验
                    extracted.Add(entry.FullName);
                }

                // ④ 覆盖前备份现有设置
                var backupDir = Path.Combine(ConfigDir, $"backup-import-{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(backupDir);
                foreach (var name in SettingFileNames)
                {
                    var existing = Path.Combine(ConfigDir, name);
                    if (File.Exists(existing))
                        File.Copy(existing, Path.Combine(backupDir, name), overwrite: true);
                }

                // ⑤ 逐文件落盘（原子写），assistant-config.json 做本机相关字段保留
                var imported = new List<string>();
                foreach (var name in extracted)
                {
                    var json = File.ReadAllText(Path.Combine(tempDir, name));
                    if (name == "assistant-config.json")
                        json = PreserveMachineSpecificFields(json);
                    if (!LocalPeerSyncService.WriteFileAtomic(Path.Combine(ConfigDir, name), json))
                        throw new IOException($"{name} 写入失败（文件被占用？）");
                    imported.Add(name);
                }

                var missing = SettingFileNames.Where(n => !extracted.Contains(n)).ToList();
                var msg = $"已导入 {imported.Count} 个设置文件：\n{string.Join("\n", imported)}"
                    + (missing.Count > 0 ? $"\n\n包内未包含（保持本机现状）：\n{string.Join("\n", missing)}" : "")
                    + $"\n\n原设置已备份到：\n{backupDir}";
                return new ImportResult(true, msg, imported, missing, backupDir);
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { /* 临时目录清理失败不影响导入 */ }
            }
        }
        catch (JsonException)
        {
            return new ImportResult(false, "设置包内容损坏（JSON 无法解析），已放弃导入，本机设置未改动。", [], [], "");
        }
        catch (Exception ex)
        {
            return new ImportResult(false, $"导入失败: {ex.Message}", [], [], "");
        }
    }

    /// <summary>
    /// assistant-config.json 落盘前的本机字段保留：
    /// - ClientInstanceId（服务端按 (UID, 实例标识) 区分连接，跨机复制会导致身份冲突）永远保留本机值；
    /// - BgiPath/BgiPackageDir 仅当导入值在本机真实存在时采用，否则保留本机值（路径机器相关）。
    /// </summary>
    private static string PreserveMachineSpecificFields(string importedJson)
    {
        var imported = JsonSerializer.Deserialize<AssistConfig>(importedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        if (imported == null) throw new JsonException("assistant-config.json 无法解析为助手配置");

        var localPath = Path.Combine(ConfigDir, "assistant-config.json");
        AssistConfig? local = null;
        if (File.Exists(localPath))
        {
            try
            {
                local = JsonSerializer.Deserialize<AssistConfig>(File.ReadAllText(localPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { /* 本机配置损坏时按无本机值处理 */ }
        }

        if (local != null)
        {
            imported.ClientInstanceId = local.ClientInstanceId;
            if (imported.BgiPath != local.BgiPath && !File.Exists(imported.BgiPath))
                imported.BgiPath = local.BgiPath;
            if (imported.BgiPackageDir != local.BgiPackageDir && !Directory.Exists(imported.BgiPackageDir))
                imported.BgiPackageDir = local.BgiPackageDir;
        }
        else
        {
            // 全新机器：强制清空实例标识，助手启动时会重新生成
            imported.ClientInstanceId = "";
        }

        return JsonSerializer.Serialize(imported, new JsonSerializerOptions { WriteIndented = true });
    }

    private static void WriteJsonEntry(ZipArchive zip, string name, string json)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(entry.Open());
        w.Write(json);
    }
}
