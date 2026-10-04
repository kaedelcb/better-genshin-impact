using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 成员配置缓存持久化管理器。
/// 存储位置：%APPDATA%\NexusBGI\member-config-cache.json
/// 与 AssistConfigManager 共享同一 NexusBGI 目录。
/// </summary>
public class MemberConfigCacheManager
{
    private readonly string _cachePath;

    public MemberConfigCacheManager()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "NexusBGI");
        Directory.CreateDirectory(dir);
        _cachePath = Path.Combine(dir, "member-config-cache.json");
    }

    /// <summary>从磁盘加载缓存，文件不存在或解析失败时返回空缓存。</summary>
    public MemberConfigCache Load()
    {
        try
        {
            if (File.Exists(_cachePath))
            {
                var json = File.ReadAllText(_cachePath);
                return JsonSerializer.Deserialize<MemberConfigCache>(
                    json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                    ?? new MemberConfigCache();
            }
        }
        catch
        {
            // 反序列化失败（如旧版本格式变更）时返回空缓存，不影响正常功能
        }
        return new MemberConfigCache();
    }

    /// <summary>将缓存写入磁盘。</summary>
    public void Save(MemberConfigCache cache)
    {
        try
        {
            var json = JsonSerializer.Serialize(cache, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });
            File.WriteAllText(_cachePath, json);
        }
        catch (Exception ex)
        {
            // 写缓存失败不应影响主流程，记日志即可
            System.Diagnostics.Debug.WriteLine($"保存配置缓存失败: {ex.Message}");
        }
    }
}