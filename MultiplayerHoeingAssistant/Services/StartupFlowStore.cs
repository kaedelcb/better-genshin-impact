using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · 启动中心流程配置的读写存储。
/// 路径：%APPDATA%/NexusBGI/startup-flow.json（与 assistant-config.json 同目录，按 Windows 用户隔离）。
/// 纪律：JSON 损坏/反序列化失败 → 落回空配置（默认不启用），绝不让助手启动失败。
/// </summary>
public class StartupFlowStore
{
    private readonly string _configPath;

    public StartupFlowStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "NexusBGI");
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "startup-flow.json");
    }

    public StartupFlowConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath)) return new StartupFlowConfig();
            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<StartupFlowConfig>(json,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? new StartupFlowConfig();
        }
        catch (Exception ex)
        {
            // 配置损坏不炸启动：落回空配置（Enabled=false），调用方记日志提示用户重排
            System.Diagnostics.Debug.WriteLine($"[StartupFlowStore] 配置读取失败，已落回空流程: {ex.Message}");
            return new StartupFlowConfig();
        }
    }

    public void Save(StartupFlowConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupFlowStore] 配置保存失败: {ex.Message}");
        }
    }
}
