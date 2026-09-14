using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>奥黛塔桌宠设置（徽章式常驻桌面宠物：显隐/大小/置顶/任务标签/位置）。</summary>
public class PetSettings
{
    /// <summary>是否显示奥黛塔桌宠。默认关（新功能不突袭）。</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; }

    /// <summary>徽章边长（DIP 逻辑像素）。范围 120~400，默认 180。</summary>
    [JsonPropertyName("sizePx")] public double SizePx { get; set; } = 180;

    /// <summary>是否置顶（盖在普通窗口上；不置顶则被其他窗口遮挡）。默认开。</summary>
    [JsonPropertyName("topmost")] public bool Topmost { get; set; } = true;

    /// <summary>是否在徽章下方显示当前任务标签（耕地机卡片同源的任务文本）。默认开。</summary>
    [JsonPropertyName("showTaskLabel")] public bool ShowTaskLabel { get; set; } = true;

    /// <summary>窗口位置（DIP）。null=首次使用，摆到主屏工作区右下角。</summary>
    [JsonPropertyName("posX")] public double? PosX { get; set; }
    [JsonPropertyName("posY")] public double? PosY { get; set; }

    /// <summary>免打扰穿透模式：整窗点击穿透（连双击都穿过去），解锁只走助手托盘子菜单。</summary>
    [JsonPropertyName("clickThrough")] public bool ClickThrough { get; set; }

    /// <summary>宠物整体透明度（0.3~1）。</summary>
    [JsonPropertyName("petOpacity")] public double PetOpacity { get; set; } = 1;

    /// <summary>任务状态详情面板显示开关（面板内 ✕ 关闭即写回 false）。</summary>
    [JsonPropertyName("panelEnabled")] public bool PanelEnabled { get; set; }

    /// <summary>详情面板透明度（0.3~1，与宠物独立）。</summary>
    [JsonPropertyName("panelOpacity")] public double PanelOpacity { get; set; } = 1;
    /// <summary>任务面板极简模式：只显示任务与锄地进度，高度随内容自适应。</summary>
    [JsonPropertyName("panelMinimal")] public bool PanelMinimal { get; set; }

    /// <summary>详情面板位置/大小（DIP）。</summary>
    [JsonPropertyName("panelX")] public double? PanelX { get; set; }
    [JsonPropertyName("panelY")] public double? PanelY { get; set; }
    [JsonPropertyName("panelW")] public double PanelW { get; set; } = 300;
    [JsonPropertyName("panelH")] public double PanelH { get; set; } = 330;

    /// <summary>音效音量（0~1，MediaPlayer Volume）与静音。</summary>
    [JsonPropertyName("soundVolume")] public double SoundVolume { get; set; } = 0.6;
    [JsonPropertyName("soundMuted")] public bool SoundMuted { get; set; }
}

/// <summary>
/// 奥黛塔桌宠设置持久化服务：%APPDATA%/NexusBGI/pet_settings.json
/// （模式与 DodocoSettingsService 一致：读写均线程安全、Save 失败静默、Update 后触发 SettingsChanged）。
/// </summary>
public sealed class PetSettingsService
{
    private readonly string _path;
    private readonly object _lock = new();
    private PetSettings _settings = new();

    /// <summary>设置变更通知（UI 线程订阅后自行刷新绑定）。</summary>
    public event Action? SettingsChanged;

    public PetSettingsService()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "pet_settings.json");
        Load();
    }

    /// <summary>当前设置快照（副本，改了不影响持久化，需经 Update 写回）。</summary>
    public PetSettings Current
    {
        get
        {
            lock (_lock)
                return new PetSettings
                {
                    Enabled = _settings.Enabled,
                    SizePx = _settings.SizePx,
                    Topmost = _settings.Topmost,
                    ShowTaskLabel = _settings.ShowTaskLabel,
                    PosX = _settings.PosX,
                    PosY = _settings.PosY,
                    ClickThrough = _settings.ClickThrough,
                    PetOpacity = _settings.PetOpacity,
                    PanelEnabled = _settings.PanelEnabled,
                    PanelOpacity = _settings.PanelOpacity,
                    PanelX = _settings.PanelX,
                    PanelY = _settings.PanelY,
                    PanelW = _settings.PanelW,
                    PanelH = _settings.PanelH,
                    SoundVolume = _settings.SoundVolume,
                    SoundMuted = _settings.SoundMuted
                };
        }
    }

    /// <summary>修改并立即持久化。mutate 里改原值，保存后触发 SettingsChanged。</summary>
    public void Update(Action<PetSettings> mutate)
    {
        lock (_lock)
        {
            mutate(_settings);
            NormalizeLocked();
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
                    _settings = JsonSerializer.Deserialize<PetSettings>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new PetSettings();
                    NormalizeLocked();
                    return;
                }
            }
            catch { /* 损坏则重建默认 */ }

            _settings = new PetSettings();
            NormalizeLocked();
            SaveLocked();
        }
    }

    /// <summary>数值域矫正（防止手改 JSON 出非法值）。位置不在此约束，恢复时按虚拟屏钳制。</summary>
    private void NormalizeLocked()
    {
        // 越界一律钳回有效域（50~250），不重置默认——保留用户曾保存的合理值
        if (double.IsNaN(_settings.SizePx)) _settings.SizePx = 180;
        else if (_settings.SizePx is < 50 or > 250) _settings.SizePx = Math.Clamp(_settings.SizePx, 50, 250);
        if (double.IsNaN(_settings.PetOpacity) || _settings.PetOpacity is < 0.3 or > 1) _settings.PetOpacity = 1;
        if (double.IsNaN(_settings.PanelOpacity) || _settings.PanelOpacity is < 0.3 or > 1) _settings.PanelOpacity = 1;
        if (double.IsNaN(_settings.PanelW) || _settings.PanelW is < 220 or > 800) _settings.PanelW = 300;
        if (double.IsNaN(_settings.PanelH) || _settings.PanelH is < 200 or > 900) _settings.PanelH = 330;
        if (double.IsNaN(_settings.SoundVolume) || _settings.SoundVolume is < 0 or > 1) _settings.SoundVolume = 0.6;
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
            System.Diagnostics.Debug.WriteLine($"[PetSettingsService] 保存失败: {ex.Message}");
        }
    }
}
