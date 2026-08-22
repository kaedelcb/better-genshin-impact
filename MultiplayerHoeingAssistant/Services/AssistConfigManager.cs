using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class AssistConfigManager
{
    private readonly string _configPath;
    /// <summary>旧版配置路径（基于 exe 目录）。用于首次运行时迁移到新的用户隔离目录，防止同电脑不同 Windows 用户共享配置、以及旧配置丢失。</summary>
    private readonly string _legacyConfigPath;

    public AssistConfigManager()
    {
        // 配置路径改为"按 Windows 用户隔离"：不同用户有独立 %APPDATA%，同电脑不同 Windows 用户在各自目录各存一份配置，互不覆盖。
        // 这也是同 UID 双端（执行端 + 遥控端，可能在同一台电脑的不同 Windows 用户）的前提——每端配置独立。
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = System.IO.Path.Combine(appData, "NexusBGI");
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

    public AssistConfig Load()
    {
        MigrateIfNeeded();
        if (System.IO.File.Exists(_configPath))
        {
            var json = System.IO.File.ReadAllText(_configPath);
            return System.Text.Json.JsonSerializer.Deserialize<AssistConfig>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AssistConfig();
        }
        return new AssistConfig();
    }

    public void Save(AssistConfig config)
    {
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_configPath, json);
    }

    /// <summary>读取 assistant-config.json 的原始 JSON 文本（用于设置弹窗编辑展示）。</summary>
    public string ReadRawJson()
    {
        return File.Exists(_configPath) ? File.ReadAllText(_configPath) : "{}";
    }

    /// <summary>校验并保存用户编辑的 JSON。成功返回 true，JSON 无效返回 false。</summary>
    public bool WriteRawJson(string json)
    {
        try
        {
            var obj = JsonSerializer.Deserialize<AssistConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (obj == null) return false;
            File.WriteAllText(_configPath, JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch
        {
            return false;
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