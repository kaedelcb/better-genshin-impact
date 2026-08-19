using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class AssistConfigManager
{
    private readonly string _configPath;

    public AssistConfigManager()
    {
        var appDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".";
        _configPath = Path.Combine(appDir, "assistant-config.json");
    }

    public AssistConfig Load()
    {
        if (File.Exists(_configPath))
        {
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AssistConfig>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AssistConfig();
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