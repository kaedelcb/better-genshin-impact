using System.IO;
using System.Security.Cryptography;
using System.Text;
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
    private readonly object _gate = new();

    public StartupFlowStore()
    {
        var dir = AssistantDataDirectory.Root;
        Directory.CreateDirectory(dir);
        _configPath = Path.Combine(dir, "startup-flow.json");
    }

    internal StartupFlowStore(string configPath) => _configPath = configPath;

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
            SaveConfirmed(config);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[StartupFlowStore] 配置保存失败: {ex.Message}");
        }
    }

    /// <summary>本机关键设置必须原子保存并读回确认；失败交给界面回报。</summary>
    public void SaveConfirmed(StartupFlowConfig config)
    {
        lock (_gate)
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true }));
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".startup-flow.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, bytes);
                File.Move(temp, _configPath, true);
                if (!File.ReadAllBytes(_configPath).AsSpan().SequenceEqual(bytes))
                    throw new IOException("启动流程保存后读回不一致，自动启动许可未确认");
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
    }

    /// <summary>远程编辑读取必须响亮报告坏文件；空文件身份用 null 表示。</summary>
    public (StartupFlowConfig Config, string? Revision) LoadChecked()
    {
        lock (_gate)
        {
            if (!File.Exists(_configPath)) return (new StartupFlowConfig(), null);
            var bytes = File.ReadAllBytes(_configPath);
            var config = JsonSerializer.Deserialize<StartupFlowConfig>(bytes)
                ?? throw new InvalidDataException("启动流程配置为空，原文件已保留");
            return (config, Convert.ToHexString(SHA256.HashData(bytes)));
        }
    }

    /// <summary>远程保存的修订保护与原子写入口；失败抛出，供调用方明确回执。</summary>
    public string SaveChecked(StartupFlowConfig config, string? expectedRevision)
    {
        lock (_gate)
        {
            var exists = File.Exists(_configPath);
            var current = exists ? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(_configPath))) : null;
            if (!string.Equals(current, expectedRevision, StringComparison.OrdinalIgnoreCase))
                throw new StartupRevisionConflictException("启动流程修订已变化，请刷新后再保存；原文件未覆盖");
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(config,
                new JsonSerializerOptions { WriteIndented = true }));
            var directory = Path.GetDirectoryName(_configPath)!;
            Directory.CreateDirectory(directory);
            var temp = Path.Combine(directory, $".startup-flow.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temp, bytes);
                if (exists)
                    File.Copy(_configPath, Path.Combine(directory, $"startup-flow.{current![..8]}.backup.json"), true);
                File.Move(temp, _configPath, true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
            return Convert.ToHexString(SHA256.HashData(bytes));
        }
    }
}
