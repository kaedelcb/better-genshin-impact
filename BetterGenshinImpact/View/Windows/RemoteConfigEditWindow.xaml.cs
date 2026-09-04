using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.View.Pages.View;
using BetterGenshinImpact.ViewModel.Pages.View;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Controls;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace BetterGenshinImpact.View.Windows;

/// <summary>
/// 远程配置组编辑窗口（remote-config-group-edit 契约 §3）。
/// 原样复用组级设置（ScriptGroupConfigView）与锄地一条龙设置（MultiplayerHoeingSettingsView 远程模式），
/// 编辑数据全程只在内存临时 <see cref="ScriptGroup"/> 对象中，绝不写本机 User\ScriptGroup 目录；
/// 保存结果经 <see cref="RemoteEditSession"/> 由助手轮询（config.remote_editor_result）取回并回传对方。
/// </summary>
public partial class RemoteConfigEditWindow : FluentWindow
{
    private static readonly string[] HoeingTaskNames = ["锄地一条龙（联机）", "锄地一条龙"];

    private readonly ILogger<RemoteConfigEditWindow> _logger = App.GetLogger<RemoteConfigEditWindow>();

    private readonly string _targetName;
    private readonly string _targetUid;
    private readonly string _groupName;

    // 远程数据（全部内存态，不落盘）
    private readonly ScriptGroup _remoteGroup;
    private readonly AutoHoeingConfig _remoteHoeingConfig;
    private readonly IReadOnlyList<string> _autoFightFiles;
    private readonly IReadOnlyList<string> _autoGeniusFiles;
    private readonly ScriptGroupProject? _soloProject;

    private bool _dirtyGroup;
    private bool _dirtySolo;
    private bool _completed;
    // 设置弹窗重入守卫：双击/连点会弹出第二个基于旧快照的同名弹窗，在其上点保存会覆盖第一次的修改
    private bool _settingsDialogOpen;

    public RemoteConfigEditWindow(string targetName, string targetUid, string groupName, string packageJson)
    {
        _targetName = targetName;
        _targetUid = targetUid;
        _groupName = groupName;

        // 解析 package（契约 §1.2 结构）
        string scriptGroupJson;
        string? autoHoeingConfigJson;
        string? remoteBgiVersion;
        bool groupRunning;
        using (var doc = JsonDocument.Parse(packageJson))
        {
            var root = doc.RootElement;
            scriptGroupJson = root.TryGetProperty("scriptGroupJson", out var sg) ? sg.GetString() ?? "" : "";
            autoHoeingConfigJson = root.TryGetProperty("autoHoeingConfigJson", out var ah) ? ah.GetString() : null;
            remoteBgiVersion = root.TryGetProperty("bgiVersion", out var bv) ? bv.GetString() : null;
            groupRunning = root.TryGetProperty("groupRunning", out var gr) && gr.ValueKind is JsonValueKind.True;
            _autoFightFiles = ReadStringArray(root, "autoFightStrategyFiles");
            _autoGeniusFiles = ReadStringArray(root, "autoGeniusFiles");
        }

        if (string.IsNullOrEmpty(scriptGroupJson))
        {
            throw new InvalidOperationException("远程配置包缺少 scriptGroupJson");
        }

        _remoteGroup = ScriptGroup.FromJson(scriptGroupJson);   // 临时对象，不落盘
        _remoteHoeingConfig = string.IsNullOrEmpty(autoHoeingConfigJson)
            ? new AutoHoeingConfig()
            : JsonSerializer.Deserialize<AutoHoeingConfig>(autoHoeingConfigJson, ConfigService.JsonOptions)
              ?? new AutoHoeingConfig();

        _soloProject = _remoteGroup.Projects.FirstOrDefault(p => HoeingTaskNames.Contains(p.Name));
        if (_soloProject != null)
        {
            _soloProject.SoloTaskSettingsObject ??= new Dictionary<string, object?>();
        }

        InitializeComponent();

        Title = $"远程配置编辑 - {targetName}（UID {targetUid}）";
        Owner = Application.Current.MainWindow;
        SourceInitialized += (_, _) => WindowHelper.TryApplySystemBackdrop(this);

        BannerText.Text = $"正在编辑远程成员「{targetName}」配置组「{groupName}」的配置，保存并回传后将覆盖对方配置。";

        var extraLines = new List<string>();
        if (!string.IsNullOrEmpty(remoteBgiVersion) && remoteBgiVersion != Global.Version)
        {
            extraLines.Add($"⚠ 版本不一致：对方 BGI 版本 {remoteBgiVersion}，本机 {Global.Version}，部分配置项可能不兼容。");
        }
        if (groupRunning)
        {
            extraLines.Add("对方正在运行该配置组，保存后需下次启动生效。");
        }
        if (extraLines.Count > 0)
        {
            BannerExtraText.Text = string.Join("\n", extraLines);
            BannerExtraText.Visibility = Visibility.Visible;
        }

        if (_soloProject == null)
        {
            OpenHoeingSettingsButton.IsEnabled = false;
            OpenHoeingSettingsButton.ToolTip = new System.Windows.Controls.ToolTip
            {
                Content = "该配置组中没有「锄地一条龙（联机）/ 锄地一条龙」任务"
            };
        }

        Closing += OnWindowClosing;
    }

    private bool IsDirty => _dirtyGroup || _dirtySolo;

    private static IReadOnlyList<string> ReadStringArray(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString();
                if (!string.IsNullOrWhiteSpace(s))
                {
                    list.Add(s);
                }
            }
        }
        return list;
    }

    /// <summary>组级设置：复用 ScriptGroupConfigView，直接编辑内存态 remoteGroup.Config（契约 §3.1）。</summary>
    private void OpenGroupSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsDialogOpen) return;   // 防连点重入（同步 ShowDialog 也可能积压第二次点击）
        _settingsDialogOpen = true;
        OpenGroupSettingsButton.IsEnabled = false;
        try
        {
            // AutoFightViewModel 注入对方策略清单（genius → strategyOverride，autofight → combatStrategyOverride）
            var vm = new ScriptGroupConfigViewModel(
                TaskContext.Instance().Config,
                _remoteGroup.Config,
                _autoGeniusFiles,
                _autoFightFiles);
            var dialogWindow = new FluentWindow
            {
                Title = "配置组设置（远程）",
                Content = new ScriptGroupConfigView(vm),
                Width = 800,
                Height = 600,
                MinWidth = 800,
                MaxWidth = 800,
                MinHeight = 600,
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ExtendsContentIntoTitleBar = true,
                WindowBackdropType = WindowBackdropType.Auto,
            };
            dialogWindow.SourceInitialized += (_, _) => WindowHelper.TryApplySystemBackdrop(dialogWindow);
            dialogWindow.ShowDialog();

            // 弹窗直接改 _remoteGroup.Config 对象，关闭即视为可能已修改（与本地打开路径行为一致：无差别写回）
            _dirtyGroup = true;
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[远程配置编辑] 打开组级设置失败");
            ThemedMessageBox.Error($"打开组级设置失败：{ex.Message}", "远程配置编辑", MessageBoxButton.OK);
        }
        finally
        {
            _settingsDialogOpen = false;
            OpenGroupSettingsButton.IsEnabled = true;
        }
    }

    /// <summary>锄地一条龙设置：复用 MultiplayerHoeingSettingsView 远程模式（契约 §3.2）。</summary>
    private async void OpenHoeingSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_soloProject == null || _settingsDialogOpen) return;   // 防连点重入：第二弹窗是旧快照，其保存会覆盖第一次修改
        _settingsDialogOpen = true;
        OpenHoeingSettingsButton.IsEnabled = false;

        try
        {
            var settings = _soloProject.SoloTaskSettingsObject!;

            // 脱离本地配置组的临时 ScriptGroupProject：Name 用对方 project 实际名，GroupInfo 指向内存态 remoteGroup
            var tempItem = new ScriptGroupProject
            {
                Name = _soloProject.Name,
                SoloTaskSettingsObject = settings,
                GroupInfo = _remoteGroup
            };

            // groupIndex 下拉项来自 SoloTaskRegistry（同 ShowHoeingSettingsDialog）
            var settingItems = SoloTaskRegistry.GetSettingItems(_soloProject.Name);
            var groupDef = settingItems.FirstOrDefault(s => s.Name == "groupIndex");
            var groupOptions = groupDef?.Options ?? new List<string> { "路径组一" };
            var groupDefault = groupDef?.DefaultValue?.ToString() ?? "路径组一";

            var vm = new MultiplayerHoeingSettingsViewModel(settings, _remoteHoeingConfig, groupOptions, groupDefault);
            var view = new MultiplayerHoeingSettingsView(
                tempItem,
                vm,
                remoteMode: true,
                globalCfgOverride: _remoteHoeingConfig,
                remoteStrategyFiles: _autoFightFiles);

            var scroll = new ScrollViewer
            {
                Content = view,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                MaxHeight = 600,
            };

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "修改独立任务配置 - 锄地一条龙（远程）",
                Width = 520,
                Content = scroll,
                PrimaryButtonText = "保存",
                CloseButtonText = "取消",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };

            var result = await dialog.ShowDialogAsync();
            if (result != MessageBoxResult.Primary)
            {
                return;
            }

            view.Save();   // 写回 settings 字典（内存态，不落盘）
            _dirtySolo = true;
            // 战斗策略下拉写回的是组级 AutoFightConfig.StrategyName，组配置一并标记 dirty
            _dirtyGroup = true;
            SaveButton.IsEnabled = true;
            // 明确两段式语义：弹窗"保存"只是暂存内存，必须点主窗口「保存并回传」才会发给对方
            Wpf.Ui.Violeta.Controls.Toast.Success("已暂存修改——需点击本窗口「保存并回传」才会发送给对方");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[远程配置编辑] 打开锄地一条龙设置失败");
            ThemedMessageBox.Error($"打开锄地一条龙设置失败：{ex.Message}", "远程配置编辑", MessageBoxButton.OK);
        }
        finally
        {
            _settingsDialogOpen = false;
            OpenHoeingSettingsButton.IsEnabled = true;
        }
    }

    /// <summary>保存并回传：序列化内存态修改 → RemoteEditSession，由助手轮询取回（不写本地文件）。</summary>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? scriptGroupConfigJson = _dirtyGroup
                ? JsonSerializer.Serialize(_remoteGroup.Config, ConfigService.JsonOptions)
                : null;
            string? soloTaskName = _dirtySolo ? _soloProject?.Name : null;
            string? soloTaskSettingsJson = _dirtySolo && _soloProject?.SoloTaskSettingsObject != null
                ? JsonSerializer.Serialize(_soloProject.SoloTaskSettingsObject, ConfigService.JsonOptions)
                : null;

            RemoteEditSession.MarkSaved(scriptGroupConfigJson, soloTaskName, soloTaskSettingsJson);
            _completed = true;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[远程配置编辑] 保存失败");
            ThemedMessageBox.Error($"保存失败：{ex.Message}", "远程配置编辑", MessageBoxButton.OK);
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();   // 未保存确认与 cancelled 标记统一在 OnWindowClosing 处理
    }

    /// <summary>助手主动中止会话（config.abort_remote_editor）时的强制关闭：跳过未保存确认与 cancelled 标记（会话已被复位，再标 cancelled 会留尸体占坑）。</summary>
    internal void ForceCloseFromAbort()
    {
        _completed = true;
        Close();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        if (_completed)
        {
            return;
        }

        if (IsDirty)
        {
            var confirm = ThemedMessageBox.Question(
                "有未保存的修改，关闭后将丢失。确定关闭吗？",
                "远程配置编辑",
                MessageBoxButton.YesNo,
                System.Windows.MessageBoxResult.No);
            if (confirm != System.Windows.MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        RemoteEditSession.MarkCancelled();
        _completed = true;
    }
}
