using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class AssistConfigManager
{
    private readonly string _configPath;
    private readonly object _configWriteGate = new();
    private bool _configTransferActive;
    internal void SetConfigTransferActive(bool active)
    {
        lock (_configWriteGate) _configTransferActive = active;
    }
    /// <summary>旧版配置路径（基于 exe 目录）。用于首次运行时迁移到新的用户隔离目录，防止同电脑不同 Windows 用户共享配置、以及旧配置丢失。</summary>
    private readonly string _legacyConfigPath;

    public AssistConfigManager()
    {
        // 配置路径改为"按 Windows 用户隔离"：不同用户有独立 %APPDATA%，同电脑不同 Windows 用户在各自目录各存一份配置，互不覆盖。
        // 这也是同 UID 双端（执行端 + 遥控端，可能在同一台电脑的不同 Windows 用户）的前提——每端配置独立。
        var dir = AssistantDataDirectory.Root;
        ConfigTransferService.RecoverInterruptedImport(dir);
        System.IO.Directory.CreateDirectory(dir);
        _configPath = System.IO.Path.Combine(dir, "assistant-config.json");

        // 旧版基于 exe 目录的配置路径（历史版本）。若存在且新路径无配置，则迁移。
        var appDir = System.IO.Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        _legacyConfigPath = System.IO.Path.Combine(appDir, "assistant-config.json");
    }

    /// <summary>首次使用时把旧版 exe 目录配置迁移到新用户隔离路径（迁移后保留旧文件备份，防止迁移出错数据丢失）。</summary>
    private void MigrateIfNeeded()
    {
        try
        {
            if (System.IO.File.Exists(_legacyConfigPath) && !System.IO.File.Exists(_configPath))
            {
                // 迁移到新路径
                System.IO.File.Copy(_legacyConfigPath, _configPath);
                // 旧文件改名备份（_bak），避免下次启动再重复迁移；原始旧文件保留一段时间
                var backup = _legacyConfigPath + ".bak";
                if (!System.IO.File.Exists(backup))
                    System.IO.File.Copy(_legacyConfigPath, backup);
            }
        }
        catch
        {
            // 迁移失败不影响加载（Load 会走空配置默认值）；配置迁移是尽力而为
        }
    }

    /// <summary>
    /// [A0 容错 2026-09-13] 配置文件损坏/被占/半截写入（程序崩溃瞬间保存）时：
    /// 备份坏文件为 .corrupt-时间戳（不删原始证据）后回退默认配置，绝不上抛——
    /// 旧实现裸读裸反序列化，一次坏盘写 = 助手永久无法启动（App.OnStartup 弹"初始化失败"并退出）。
    /// </summary>
    public AssistConfig Load()
    {
        if (!AssistantDataDirectory.IsExplicit) MigrateIfNeeded();
        if (!System.IO.File.Exists(_configPath))
        {
            return new AssistConfig();
        }
        try
        {
            var json = System.IO.File.ReadAllText(_configPath);
            return System.Text.Json.JsonSerializer.Deserialize<AssistConfig>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AssistConfig();
        }
        catch (Exception ex)
        {
            // 留痕 + 备份坏文件（备份失败不掩盖主流程，仅留痕）
            System.Diagnostics.Debug.WriteLine($"[AssistConfig] 配置加载失败，回退默认配置: {ex.Message}");
            try
            {
                var corrupt = _configPath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                System.IO.File.Copy(_configPath, corrupt);
                System.Diagnostics.Debug.WriteLine($"[AssistConfig] 损坏配置已备份: {corrupt}");
            }
            catch (Exception backupEx)
            {
                System.Diagnostics.Debug.WriteLine($"[AssistConfig] 损坏配置备份失败: {backupEx.Message}");
            }
            return new AssistConfig();
        }
    }

    /// <summary>[A0 容错] 原子写：先写 .tmp 再覆盖移动，杜绝半截写入制造坏配置；失败内部留痕不抛（调用方均无 catch）。</summary>
    public void Save(AssistConfig config)
    {
        lock (_configWriteGate)
        {
            if (_configTransferActive) return;
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            if (!LocalPeerSyncService.WriteFileAtomic(_configPath, json))
            {
                System.Diagnostics.Debug.WriteLine($"[AssistConfig] 配置保存失败（已内部容错）: {_configPath}");
            }
        }
    }

    /// <summary>读取 assistant-config.json 的原始 JSON 文本（用于设置弹窗编辑展示）。</summary>
    public string ReadRawJson()
    {
        return File.Exists(_configPath) ? File.ReadAllText(_configPath) : "{}";
    }

    public (string Path, string Revision) ReadBgiPathChecked()
    {
        lock (_configWriteGate)
        {
            var raw = ReadRawJson();
            var config = JsonSerializer.Deserialize<AssistConfig>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("执行端助手配置无法读取");
            return (config.BgiPath, ConfigRevision(raw));
        }
    }

    public (bool Saved, string? Revision, string Message) SaveBgiPathChecked(
        string path, string? expectedRevision)
    {
        lock (_configWriteGate)
        {
            if (_configTransferActive) return (false, null, "设置迁移尚未完成，配置写入已暂停");
            if (string.IsNullOrWhiteSpace(expectedRevision))
                return (false, null, "缺少执行端设置修订，请重新读取");
            var raw = ReadRawJson();
            if (ConfigRevision(raw) != expectedRevision)
                return (false, null, "执行端助手设置已变化，请重新读取后再保存");
            var root = JsonNode.Parse(raw) as JsonObject
                ?? throw new InvalidDataException("执行端助手配置不是 JSON 对象");
            root["bgiPath"] = path;
            var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (File.Exists(_configPath))
            {
                var backup = _configPath + "." + expectedRevision[..8] + ".backup.json";
                if (!File.Exists(backup)) File.Copy(_configPath, backup);
            }
            if (!LocalPeerSyncService.WriteFileAtomic(_configPath, updated))
                return (false, null, "执行端助手设置写入失败，原配置保持");
            var readBack = ReadRawJson();
            if (!string.Equals(readBack, updated, StringComparison.Ordinal))
                return (false, null, "执行端设置写入结果未确认，请重新读取核对");
            return (true, ConfigRevision(readBack), "执行端 BGI 路径已保存；重启执行端助手后生效");
        }
    }

    private static string ConfigRevision(string raw)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>校验并保存用户编辑的 JSON。成功返回 true，JSON 无效返回 false。</summary>
    public bool WriteRawJson(string json)
    {
        lock (_configWriteGate)
        {
            if (_configTransferActive) return false;
            try
            {
                var obj = JsonSerializer.Deserialize<AssistConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (obj == null) return false;
                // [A0 容错] 同 Save：原子写，杜绝半截写入
                return LocalPeerSyncService.WriteFileAtomic(_configPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
                return false;
            }
        }
    }

    public static string GenerateControlRoomCode(List<string> playerUids)
    {
        var sorted = playerUids.OrderBy(u => u).ToList();
        var input = string.Join(",", sorted);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash)[..6];
    }
}
