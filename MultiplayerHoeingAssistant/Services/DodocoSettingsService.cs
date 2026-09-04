using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>嘟嘟可统一设置（P3 尾巴收口：卡死阈值 + 截图设置 + 全部静音 集中持久化）。</summary>
public class DodocoSettings
{
    /// <summary>卡死判定阈值（分钟）：任务运行中但日志超时无新行 → 疑似卡死。默认 3。</summary>
    [JsonPropertyName("stallThresholdMinutes")] public int StallThresholdMinutes { get; set; } = 3;
    /// <summary>截图自动刷新间隔（秒）：1/3/5/10 之一。默认 3（仅在开启自动刷新时生效）。</summary>
    [JsonPropertyName("screenshotIntervalSeconds")] public int ScreenshotIntervalSeconds { get; set; } = 3;
    /// <summary>截图自动刷新开关（默认手动）。</summary>
    [JsonPropertyName("screenshotAutoRefresh")] public bool ScreenshotAutoRefresh { get; set; }
    /// <summary>截图缩略宽度（像素，高度等比缩放，JPEG 质量 ~70）。默认 1280。</summary>
    [JsonPropertyName("thumbnailWidth")] public int ThumbnailWidth { get; set; } = 1280;
    /// <summary>全部静音：异常监控/卡死心跳只记录不红点/不响铃/不弹托盘。</summary>
    [JsonPropertyName("muteAll")] public bool MuteAll { get; set; }
    /// <summary>共享我的桌面截图：开启后每 10 秒把一帧 480px JPEG 上报到房间，供成员远程查看。默认关。</summary>
    [JsonPropertyName("shareDesktopScreenshot")] public bool ShareDesktopScreenshot { get; set; }
}

/// <summary>
/// 嘟嘟可设置持久化服务：%APPDATA%/NexusBGI/dodoco_settings.json
/// （与 dodoco_watch_rules.json 同目录，跟随 AssistConfigManager 的配置目录约定）。
/// 读写均线程安全；Save 失败静默（设置丢失不影响主流程）。
/// </summary>
public sealed class DodocoSettingsService
{
    private readonly string _path;
    private readonly object _lock = new();
    private DodocoSettings _settings = new();

    /// <summary>设置变更通知（UI 线程订阅后自行刷新绑定）。</summary>
    public event Action? SettingsChanged;

    public DodocoSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "dodoco_settings.json");
        Load();
    }

    /// <summary>当前设置快照（副本，改了不影响持久化，需经 Update 写回）。</summary>
    public DodocoSettings Current
    {
        get
        {
            lock (_lock)
                return new DodocoSettings
                {
                    StallThresholdMinutes = _settings.StallThresholdMinutes,
                    ScreenshotIntervalSeconds = _settings.ScreenshotIntervalSeconds,
                    ScreenshotAutoRefresh = _settings.ScreenshotAutoRefresh,
                    ThumbnailWidth = _settings.ThumbnailWidth,
                    MuteAll = _settings.MuteAll,
                    ShareDesktopScreenshot = _settings.ShareDesktopScreenshot
                };
        }
    }

    /// <summary>修改并立即持久化。mutate 里改副本，保存后触发 SettingsChanged。</summary>
    public void Update(Action<DodocoSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            SaveLocked();
        }
        SettingsChanged?.Invoke();
    }

    private void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(_path))
                {
                    var json = File.ReadAllText(_path);
                    _settings = JsonSerializer.Deserialize<DodocoSettings>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new DodocoSettings();
                    NormalizeLocked();
                    return;
                }
            }
            catch { /* 损坏则重建默认 */ }

            _settings = new DodocoSettings();
            // 迁移：P2 的"全部静音"曾存在 dodoco_watch_rules.json 里，首次建仓时沿用旧值
            try
            {
                var rulesPath = Path.Combine(Path.GetDirectoryName(_path)!, "dodoco_watch_rules.json");
                if (File.Exists(rulesPath))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(rulesPath));
                    if (doc.RootElement.TryGetProperty("muteAll", out var m) && m.ValueKind == JsonValueKind.True)
                        _settings.MuteAll = true;
                }
            }
            catch { /* 迁移失败用默认 false */ }
            NormalizeLocked();
            SaveLocked();
        }
    }

    /// <summary>数值域矫正（防止手改 JSON 出非法值）。</summary>
    private void NormalizeLocked()
    {
        if (_settings.StallThresholdMinutes < 1) _settings.StallThresholdMinutes = 3;
        if (_settings.ScreenshotIntervalSeconds is not (1 or 3 or 5 or 10)) _settings.ScreenshotIntervalSeconds = 3;
        if (_settings.ThumbnailWidth is < 320 or > 3840) _settings.ThumbnailWidth = 1280;
    }

    private void SaveLocked()
    {
        try
        {
            File.WriteAllText(_path,
                JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DodocoSettingsService] 保存失败: {ex.Message}");
        }
    }
}
