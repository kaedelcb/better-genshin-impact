using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · 启动中心「方案」快照的读写存储（命名保存/恢复整份启动流程配置）。
/// 路径：%APPDATA%/NexusBGI/startup-flow-schemes.json（与 startup-flow.json 同目录，按 Windows 用户隔离）。
/// 纪律与 StartupFlowStore 一致：JSON 损坏/反序列化失败 → 落回空列表，绝不让助手启动失败。
/// </summary>
public class StartupFlowSchemeStore
{
    private readonly string _schemesPath;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public StartupFlowSchemeStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "NexusBGI");
        Directory.CreateDirectory(dir);
        _schemesPath = Path.Combine(dir, "startup-flow-schemes.json");
    }

    public List<StartupFlowScheme> Load()
    {
        try
        {
            if (!File.Exists(_schemesPath)) return [];
            var json = File.ReadAllText(_schemesPath);
            return JsonSerializer.Deserialize<List<StartupFlowScheme>>(json, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            // 方案库损坏不炸启动：落回空列表（不影响主配置 startup-flow.json）
            System.Diagnostics.Debug.WriteLine($"[StartupFlowSchemeStore] 方案读取失败，已落回空列表: {ex.Message}");
            return [];
        }
    }

    public void SaveAll(IReadOnlyList<StartupFlowScheme> schemes)
    {
        try
        {
            var json = JsonSerializer.Serialize(schemes, JsonOptions);
            File.WriteAllText(_schemesPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupFlowSchemeStore] 方案保存失败: {ex.Message}");
        }
    }

    /// <summary>配置的深拷贝（JSON 往返）：保存快照与恢复快照都用它，保证方案与当前配置互不影响。</summary>
    public static StartupFlowConfig Clone(StartupFlowConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<StartupFlowConfig>(json, JsonOptions) ?? new StartupFlowConfig();
    }
}
