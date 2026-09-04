#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.ViewModel.Pages.View;
using Microsoft.Extensions.Logging;
using Wpf.Ui.Violeta.Controls;
using BetterGenshinImpact.View.Windows;
using Button = Wpf.Ui.Controls.Button;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace BetterGenshinImpact.View.Pages.View;

/// <summary>
/// 联机锄地配置弹窗的 UserControl（multiplayer-hoeing-dialog-xaml-refactor）。
/// 静态卡片结构 + 标量控件由 XAML 承载并双向绑定 VM；本 code-behind 负责三块无法纯 MVVM 化的动态 UI：
/// ① 单机区（SoloPanelHost）② 内置线路按钮组（BuiltinRouteHost）③ 变体偏好面板（VariantPanelHost），
/// 逻辑整体迁自原 ScriptControlViewModel.ShowHoeingSettingsDialog，保存出口收敛到 Save()。
/// </summary>
public partial class MultiplayerHoeingSettingsView : UserControl
{
    private readonly ScriptGroupProject _item;
    private readonly Dictionary<string, object?> _settings;
    private readonly AutoHoeingConfig _globalCfg;

    // 远程配置组编辑模式（remote-config-group-edit §4.1）：true 时禁用一切本机磁盘扫描/全局镜像写入
    private readonly bool _remoteMode;
    private readonly IReadOnlyList<string>? _remoteStrategyFiles;

    private readonly List<SoloTaskSettingItem> _settingItems;
    private readonly Dictionary<string, FrameworkElement> _soloControls = new();

    // 开锄前换武器（hoeing-multiplayer-preswitch-weapon）：两行内存态 + 每行只读单元/启用复选框控件引用
    private readonly List<BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponRow> _preSwitchRows = new();
    private readonly List<(TextBlock CharCell, TextBlock WeaponCell, System.Windows.Controls.CheckBox EnableCheck)> _preSwitchRowControls = new();

    // 当前对话框会话内的变体偏好编辑缓冲（基名 → 变体文件夹名）。保存时写入 settings。
    private readonly Dictionary<string, string> _variantPrefBuffer = new(StringComparer.Ordinal);
    private Action? _refreshVariantPanel;

    // 内置线路按钮组动态容器（迁现状 builtinRouteContainer / importRow / buttonPanel）
    private System.Windows.Controls.StackPanel? _builtinRouteContainer;
    private System.Windows.Controls.StackPanel? _importRow;
    private System.Windows.Controls.Panel? _builtinButtonPanel;

    private readonly RouteDirectoryScanner _routeScanner = new();
    private readonly ILogger<MultiplayerHoeingSettingsView> _logger = App.GetLogger<MultiplayerHoeingSettingsView>();

    public MultiplayerHoeingSettingsViewModel ViewModel { get; }

    public MultiplayerHoeingSettingsView(
        ScriptGroupProject item,
        MultiplayerHoeingSettingsViewModel viewModel,
        bool remoteMode = false,
        AutoHoeingConfig? globalCfgOverride = null,
        IReadOnlyList<string>? remoteStrategyFiles = null)
    {
        _item = item;
        _settings = item.SoloTaskSettingsObject!;
        _remoteMode = remoteMode;
        _remoteStrategyFiles = remoteStrategyFiles;
        // 远程模式用注入的对方 AutoHoeingConfig；本地模式维持旧行为取全局配置
        _globalCfg = globalCfgOverride ?? TaskContext.Instance().Config.AutoHoeingConfig;
        DataContext = ViewModel = viewModel;
        InitializeComponent();   // 占位 ContentControl 此后才就绪

        _settingItems = SoloTaskRegistry.GetSettingItems(item.Name);
        BuildSoloPanel();          // 填充 SoloPanelHost（迁现状 soloPanel 构建）
        InitPreSwitchRowsBuffer();   // 从 _settings 读取还原换武器两行（无则默认空白）
        BuildPreSwitchWeaponRows();  // 构建固定2行开锄前换武器 UI
        InitVariantPrefBuffer();   // 迁现状 variantPrefBuffer 预填

        if (_remoteMode)
        {
            // 远程编辑模式：三块磁盘扫描区域替换为占位提示，不扫本机线路目录
            BuiltinRouteHost.Content = CreateRemoteModePlaceholder();
            MemberRouteRolesHost.Content = CreateRemoteModePlaceholder();
            VariantPanelHost.Content = CreateRemoteModePlaceholder();
            OpenFightStrategyButton.IsEnabled = false;   // "打开联机战斗策略文件"指向本机文件，远程模式禁用
        }
        else
        {
            BuildBuiltinRouteHost();   // 迁 RebuildBuiltinButtons + import/openDir 事件
            BuildMemberRouteRolesHost(); // 成员侧「按线路切角色」配置列表（仅成员可见区，code-behind 填充）
            HookVariantPanel();        // VariantExpander.Expanded += BuildVariantPanelContent; _refreshVariantPanel = ...
            HookFightStrategyButton(); // "打开联机战斗策略文件" → MultiplayerFightStrategyFileHelper.OpenForEdit()
        }

        HookDocButtons();          // 变体卡 Header 的"使用教程"/"制作规则" → OpenDoc
        HookFightStrategyCombo();  // E 卡片复刻战斗策略下拉（绑配置组 AutoFightConfig.StrategyName）
        HookMedicineEatButton();    // 按周期吃食物设置按钮 → 弹窗
        HookPickupParamsButton();   // 配置拾取参数按钮 → 弹窗（4 个共享参数）

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;   // 衔接 UpdateButtonStates
    }

    // 远程编辑模式占位提示（替换磁盘扫描区域）
    private static TextBlock CreateRemoteModePlaceholder() => new()
    {
        Text = "远程编辑模式：路线相关设置请在对方本机修改",
        Foreground = SystemColors.GrayTextBrush,
        Margin = new Thickness(8),
        TextWrapping = TextWrapping.Wrap
    };

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 等价现状 routeModeCombo.SelectionChanged + fixedRoutePathBox.TextChanged → UpdateButtonStates
        if (e.PropertyName is nameof(ViewModel.BuiltinButtonsEnabled)
            or nameof(ViewModel.RouteModeSelection) or nameof(ViewModel.FixedDebugRoutePath))
        {
            UpdateButtonStates();
        }
    }

    private string GetStr(string key, string fallback) =>
        _settings.TryGetValue(key, out var v) ? v?.ToString() ?? fallback : fallback;

    // ===== 单机区构建（迁现状 soloPanel 构建循环；仅换承载容器，构建逻辑零改动）=====
    private void BuildSoloPanel()
    {
        var soloPanel = new System.Windows.Controls.StackPanel();
        foreach (var setting in _settingItems)
        {
            soloPanel.Children.Add(new TextBlock { Text = setting.Label, Margin = new Thickness(0, 8, 0, 2), FontSize = 13 });
            object? currentValue = _settings.TryGetValue(setting.Name, out var ov) ? ov : setting.DefaultValue;
            if (setting.Type == "select" && setting.Options != null)
            {
                var combo = new System.Windows.Controls.ComboBox
                {
                    ItemsSource = setting.Options,
                    SelectedItem = currentValue?.ToString() ?? "",
                    Margin = new Thickness(0, 0, 0, 4)
                };
                soloPanel.Children.Add(combo);
                _soloControls[setting.Name] = combo;
            }
            else if (setting.Type == "bool")
            {
                var check = new System.Windows.Controls.CheckBox
                {
                    IsChecked = currentValue is true or "True" or "true",
                    Margin = new Thickness(0, 0, 0, 4)
                };
                soloPanel.Children.Add(check);
                _soloControls[setting.Name] = check;
            }
            else
            {
                var tb = new TextBox { Text = currentValue?.ToString() ?? "", Margin = new Thickness(0, 0, 0, 4) };
                soloPanel.Children.Add(tb);
                _soloControls[setting.Name] = tb;
            }
        }
        SoloPanelHost.Content = soloPanel;
    }

    // ===== 开锄前换武器（hoeing-multiplayer-preswitch-weapon）=====

    // 读取还原：从 _settings['preSwitchWeaponRows'] 解析两行（缺失/异常 → 两行默认空白）
    private void InitPreSwitchRowsBuffer()
    {
        _preSwitchRows.Clear();
        object? raw = _settings.TryGetValue("preSwitchWeaponRows", out var v) ? v : null;
        _preSwitchRows.AddRange(
            BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponDecisions.ParseRows(raw));
    }

    // 构建固定2行 UI：每行4列（角色名只读 / 武器名只读 / 配置按钮 / 启用复选框）
    private void BuildPreSwitchWeaponRows()
    {
        _preSwitchRowControls.Clear();
        // host 现为 Grid（XAML 已改）。清空并按行数建 RowDefinition。
        // Grid 会强制把子元素水平拉伸到自身完整宽度（StackPanel 不会），从而让行内 Star 列把「配置/启用」推到最右。
        PreSwitchWeaponRowsHost.Children.Clear();
        PreSwitchWeaponRowsHost.RowDefinitions.Clear();
        for (int i = 0; i < BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponDecisions.RowCount; i++)
        {
            int idx = i;
            var row = _preSwitchRows[idx];
            PreSwitchWeaponRowsHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var grid = new System.Windows.Controls.Grid
            {
                Margin = new Thickness(0, 0, 0, 6)
            };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 角色
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 武器
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 配置按钮
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                       // 启用复选框

            bool configured = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponDecisions.IsRowConfigured(row);
            var charCell = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            var weaponCell = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            ApplyCellDisplay(charCell, configured ? row.Character : null);
            ApplyCellDisplay(weaponCell, configured ? row.Weapon : null);
            // 各自包进浅色底色块；列1 用左边距维持与列0 的间隔
            var charBackplate = WrapInBackplate(charCell, new Thickness(0, 0, 0, 0));
            var weaponBackplate = WrapInBackplate(weaponCell, new Thickness(8, 0, 0, 0));
            var cfgBtn = new Button { Content = "配置", Margin = new Thickness(8, 0, 0, 0) };
            var enableCheck = new System.Windows.Controls.CheckBox { Content = "启用", IsChecked = row.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

            System.Windows.Controls.Grid.SetColumn(charBackplate, 0);
            System.Windows.Controls.Grid.SetColumn(weaponBackplate, 1);
            System.Windows.Controls.Grid.SetColumn(cfgBtn, 2);
            System.Windows.Controls.Grid.SetColumn(enableCheck, 3);
            grid.Children.Add(charBackplate);
            grid.Children.Add(weaponBackplate);
            grid.Children.Add(cfgBtn);
            grid.Children.Add(enableCheck);

            cfgBtn.Click += async (_, _) => await OnConfigurePreSwitchRow(idx);
            System.Windows.Controls.Grid.SetRow(grid, idx);
            PreSwitchWeaponRowsHost.Children.Add(grid);
            // 关键：登记的仍是 TextBlock 本体（charCell/weaponCell），不是 Border
            _preSwitchRowControls.Add((charCell, weaponCell, enableCheck));
        }
    }

    // 统一角色/武器单元的「值 + 占位 + 前景色」呈现，供初次构建与配置后刷新共用
    private static void ApplyCellDisplay(TextBlock cell, string? value)
    {
        bool hasValue = !string.IsNullOrWhiteSpace(value);
        cell.Text = hasValue ? value! : "未配置";
        cell.SetResourceReference(
            TextBlock.ForegroundProperty,
            hasValue ? "TextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush");
    }

    // 将文字单元包进浅色底色块 Border（Cell_Backplate）
    private static Border WrapInBackplate(TextBlock cell, Thickness margin)
    {
        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = margin,
            Child = cell,
        };
        border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
        return border;
    }

    // 配置某行：复用「OCR切换武器」设置项（GetSettingDefinitions）动态构建 6 参数子弹窗
    private async Task OnConfigurePreSwitchRow(int idx)
    {
        try
        {
            var defs = BetterGenshinImpact.GameTask.OcrSwitchWeapon.OcrSwitchWeaponTask.GetSettingDefinitions();
            var row = _preSwitchRows[idx];
            var controls = new Dictionary<string, FrameworkElement>();
            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };

            foreach (var def in defs)
            {
                panel.Children.Add(new TextBlock { Text = def.Label, Margin = new Thickness(0, 8, 0, 2), FontSize = 13 });
                object? init = GetPreSwitchRowParamValue(row, def);
                if (def.Type == "select" && def.Options != null)
                {
                    var combo = new System.Windows.Controls.ComboBox
                    {
                        ItemsSource = def.Options,
                        SelectedItem = init?.ToString() ?? "",
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    panel.Children.Add(combo);
                    controls[def.Name] = combo;
                }
                else if (def.Type == "bool")
                {
                    var check = new System.Windows.Controls.CheckBox
                    {
                        IsChecked = init is true or "True" or "true",
                        Margin = new Thickness(0, 0, 0, 4)
                    };
                    panel.Children.Add(check);
                    controls[def.Name] = check;
                }
                else
                {
                    var tb = new TextBox { Text = init?.ToString() ?? "", Margin = new Thickness(0, 0, 0, 4) };
                    panel.Children.Add(tb);
                    controls[def.Name] = tb;
                }
            }

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = $"配置开锄前换武器 - 第{idx + 1}行",
                Content = panel,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var r = await dialog.ShowDialogAsync();
            if (r != MessageBoxResult.Primary) return;   // 取消不变

            // 读回 6 个值写入该行（保持 Enabled 不变）
            ReadPreSwitchDialogInto(row, controls);

            // 刷新该行只读展示（与初次构建共用 ApplyCellDisplay：值/占位「未配置」+ 前景色一致）
            bool configured = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponDecisions.IsRowConfigured(row);
            ApplyCellDisplay(_preSwitchRowControls[idx].CharCell, configured ? row.Character : null);
            ApplyCellDisplay(_preSwitchRowControls[idx].WeaponCell, configured ? row.Weapon : null);
        }
        catch (Exception ex)
        {
            // 弹窗构建/读写异常不应让配置弹窗崩溃（可恢复异常）
            _logger.LogWarning(ex, "[开锄前换武器] 配置第{Idx}行弹窗异常", idx + 1);
            Toast.Warning("配置换武器行失败，请查看日志");
        }
    }

    // 取该行某参数的初值（已存值，无则用定义默认值）
    private static object? GetPreSwitchRowParamValue(
        BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponRow row, SoloTaskSettingItem def)
    {
        return def.Name switch
        {
            "Character" => string.IsNullOrEmpty(row.Character) ? def.DefaultValue : row.Character,
            "Weapon" => string.IsNullOrEmpty(row.Weapon) ? def.DefaultValue : row.Weapon,
            "Element" => string.IsNullOrEmpty(row.Element) ? def.DefaultValue : row.Element,
            "quickMode" => row.QuickMode,
            "gridPosition" => string.IsNullOrEmpty(row.GridPosition) ? def.DefaultValue : row.GridPosition,
            "pageScrollCount" => string.IsNullOrEmpty(row.PageScrollCount) ? def.DefaultValue : row.PageScrollCount,
            _ => def.DefaultValue
        };
    }

    // 从子弹窗控件读回 6 个值写入行（Enabled 不动）
    private static void ReadPreSwitchDialogInto(
        BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponRow row,
        Dictionary<string, FrameworkElement> controls)
    {
        string ReadStr(string key)
        {
            if (!controls.TryGetValue(key, out var ctrl)) return "";
            return ctrl switch
            {
                System.Windows.Controls.ComboBox combo => combo.SelectedItem?.ToString() ?? "",
                TextBox tb => tb.Text ?? "",
                _ => ""
            };
        }
        bool ReadBool(string key)
            => controls.TryGetValue(key, out var ctrl) && ctrl is System.Windows.Controls.CheckBox c && (c.IsChecked ?? false);

        row.Character = ReadStr("Character");
        row.Weapon = ReadStr("Weapon");
        row.Element = ReadStr("Element");
        row.QuickMode = ReadBool("quickMode");
        row.GridPosition = ReadStr("gridPosition");
        row.PageScrollCount = ReadStr("pageScrollCount");
    }

    // 保存：从复选框回填 Enabled，序列化两行写入 _settings（随配置组持久化）
    private void SavePreSwitchWeaponRows()
    {
        for (int i = 0; i < _preSwitchRows.Count && i < _preSwitchRowControls.Count; i++)
            _preSwitchRows[i].Enabled = _preSwitchRowControls[i].EnableCheck.IsChecked ?? false;

        _settings["preSwitchWeaponRows"] =
            BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PreSwitchWeaponDecisions.SerializeRows(_preSwitchRows);
    }

    // ===== 变体偏好缓冲预填（迁现状）：先全局 gcfg.VariantPreferences，再用 settings 覆盖 =====
    private void InitVariantPrefBuffer()
    {
        // _globalCfg 本地模式即 TaskContext 全局配置；远程模式为注入的对方配置
        var gcfg = _globalCfg;
        if (gcfg.VariantPreferences != null)
        {
            foreach (var (k, v) in gcfg.VariantPreferences)
            {
                if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) _variantPrefBuffer[k] = v;
            }
        }
        if (_settings.TryGetValue("variantPreferences", out var existRaw) && existRaw != null)
        {
            try
            {
                if (existRaw is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var p in je.EnumerateObject())
                    {
                        if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            var v = p.Value.GetString();
                            if (!string.IsNullOrEmpty(p.Name) && !string.IsNullOrEmpty(v)) _variantPrefBuffer[p.Name] = v!;
                        }
                    }
                }
                else if (existRaw is Dictionary<string, string> sd)
                {
                    foreach (var (k, v) in sd)
                    {
                        if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v)) _variantPrefBuffer[k] = v;
                    }
                }
            }
            catch (Exception ex)
            {
                // 已存偏好解析失败不应阻断弹窗，仅用全局值预填（可恢复异常）
                _logger.LogWarning(ex, "[变体偏好] 读取配置组已存偏好失败，仅用全局值预填");
            }
        }
    }

    // ===== 内置线路按钮组（迁 RebuildBuiltinButtons + importRow + import/openDir 事件）=====
    private void BuildBuiltinRouteHost()
    {
        _builtinRouteContainer = new System.Windows.Controls.StackPanel();

        var importFolderBtn = new System.Windows.Controls.Button
        {
            Content = "从本地导入线路",
            Margin = new Thickness(0, 8, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };

        var openAssetsDirBtn = new System.Windows.Controls.Button
        {
            Content = "内置目录",
            Margin = new Thickness(8, 8, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openAssetsDirBtn.Click += (s, e) =>
        {
            try
            {
                var assetsDir = System.IO.Path.Combine(Global.Absolute("GameTask"), "AutoHoeing", "Assets");
                System.IO.Directory.CreateDirectory(assetsDir);   // 目录不存在时创建（不删除任何内容）
                Process.Start(new ProcessStartInfo(assetsDir) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                // 打开目录失败不应让弹窗崩溃；记录并提示
                _logger.LogWarning(ex, "[内置线路] 打开内置线路目录失败");
                Toast.Warning("打开内置线路目录失败，请查看日志");
            }
        };

        _importRow = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal
        };
        _importRow.Children.Add(importFolderBtn);
        _importRow.Children.Add(openAssetsDirBtn);

        importFolderBtn.Click += OnImportFolderClick;

        BuiltinRouteHost.Content = _builtinRouteContainer;
        RebuildBuiltinButtons();   // 首次构建
    }

    // import-local-route-folder C2: 可重入构建函数，每次清空容器并按最新扫描结果重建
    private void RebuildBuiltinButtons()
    {
        if (_builtinRouteContainer == null || _importRow == null) return;
        _builtinRouteContainer.Children.Clear();
        _builtinRouteContainer.Children.Add(_importRow);   // 导入按钮 + 打开目录按钮恒在顶部
        _builtinButtonPanel = null;

        var builtinFolders = _routeScanner.ScanBuiltinRoutes();   // 每次重扫
        if (builtinFolders.Count > 0)
        {
            _builtinRouteContainer.Children.Add(new TextBlock
            {
                Text = "内置线路快速选择",
                FontSize = 12,
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 8, 0, 4)
            });

            // 每行一个文件夹：竖直 StackPanel 承载，每行是「线路按钮 + ⚙角色」的水平 StackPanel
            var buttonPanel = new System.Windows.Controls.StackPanel { Orientation = System.Windows.Controls.Orientation.Vertical };
            _builtinButtonPanel = buttonPanel;
            var selectedRoute = GetStr("selectedBuiltinRoute", _globalCfg.SelectedBuiltinRoute);
            var routeButtons = new List<System.Windows.Controls.Button>();

            foreach (var folder in builtinFolders)
            {
                var row = new System.Windows.Controls.StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 6)
                };

                var btn = new System.Windows.Controls.Button
                {
                    Content = folder.FolderName,
                    Margin = new Thickness(0, 0, 8, 0),
                    Tag = folder.FolderName
                };
                routeButtons.Add(btn);

                // 设置按钮样式 - 使用基本的 WPF 样式而不是 WPF UI 的 Appearance
                if (folder.FolderName == selectedRoute)
                {
                    btn.Background = SystemColors.HighlightBrush;
                    btn.Foreground = SystemColors.HighlightTextBrush;
                }
                else
                {
                    btn.Background = SystemColors.ControlBrush;
                    btn.Foreground = SystemColors.ControlTextBrush;
                }

                btn.Click += (s, e) =>
                {
                    // 更新所有线路主按钮样式（仅线路按钮，不含 ⚙角色 按钮）
                    foreach (var rb in routeButtons)
                    {
                        rb.Background = SystemColors.ControlBrush;
                        rb.Foreground = SystemColors.ControlTextBrush;
                    }
                    btn.Background = SystemColors.HighlightBrush;
                    btn.Foreground = SystemColors.HighlightTextBrush;
                    _settings["selectedBuiltinRoute"] = btn.Tag.ToString();
                };

                row.Children.Add(btn);

                // 按线路切角色（hoeing-multiplayer-per-route-switch-roles）：
                // 在文件夹主按钮旁新增「⚙角色」小按钮，点击弹出该文件夹线路角色配置弹窗。
                // 不改动主按钮「点击=选中线路」的现有语义，仅在同一行内追加（纯加法）。
                var cfgRolesBtn = new System.Windows.Controls.Button
                {
                    Content = "⚙角色",
                    Margin = new Thickness(0, 0, 0, 0),
                    Tag = folder.FullPath,
                };
                // 局部变量捕获 folder 字段，避免闭包捕获循环变量问题
                var folderNameForDialog = folder.FolderName;
                var folderFullPathForDialog = folder.FullPath;
                cfgRolesBtn.Click += async (_, _) => await OnConfigureRouteRoles(folderNameForDialog, folderFullPathForDialog);
                row.Children.Add(cfgRolesBtn);

                buttonPanel.Children.Add(row);
            }

            _builtinRouteContainer.Children.Add(buttonPanel);

            // _builtinRouteContainer.Children.Add(new TextBlock
            // {
            //     Text = "手动输入路径优先级高于按钮选择",
            //     FontSize = 11,
            //     Foreground = SystemColors.GrayTextBrush,
            //     Margin = new Thickness(0, 4, 0, 0)
            // });

            // 初始化状态（可见性由 XAML BuiltinRouteHost 绑定 ShowManualRoute 控制，本方法只管启用/高亮）
            UpdateButtonStates();
        }
    }

    // 等价现状 UpdateButtonStates：内置按钮启用/高亮（读 ViewModel.IsBuiltinOnlineMode + FixedDebugRoutePath）
    private void UpdateButtonStates()
    {
        if (_builtinButtonPanel == null) return;
        var useFixedRoutes = RouteModeDecisions.IsBuiltinOnline(ViewModel.RouteModeSelection);
        var hasManualPath = !string.IsNullOrWhiteSpace(ViewModel.FixedDebugRoutePath);
        var buttonsEnabled = useFixedRoutes && !hasManualPath;

        // 可见性统一由 XAML 绑定 ShowManualRoute 控制，本方法只管按钮启用/高亮
        // 按钮现嵌在每行的 StackPanel 内，需递归收集所有 Button
        var allButtons = EnumerateButtons(_builtinButtonPanel).ToList();
        foreach (var btn in allButtons)
        {
            btn.IsEnabled = buttonsEnabled;
        }

        // 如果不满足条件，清除选择状态
        if (!buttonsEnabled)
        {
            foreach (var btn in allButtons)
            {
                btn.Background = SystemColors.ControlBrush;
                btn.Foreground = SystemColors.ControlTextBrush;
            }
        }
    }

    // 递归收集面板内所有 Button（每行文件夹的按钮嵌在行 StackPanel 内）
    private static IEnumerable<System.Windows.Controls.Button> EnumerateButtons(System.Windows.Controls.Panel panel)
    {
        foreach (var child in panel.Children)
        {
            if (child is System.Windows.Controls.Button b) yield return b;
            else if (child is System.Windows.Controls.Panel p)
                foreach (var inner in EnumerateButtons(p)) yield return inner;
        }
    }

    // import-local-route-folder C3: 导入按钮点击 = 弹文件夹选择 → 校验 → 重名确认 → 拷贝 → 选中 → 重建 → 刷新变体 → Toast
    private void OnImportFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
            {
                Description = "选择要导入的本地线路文件夹",
                UseDescriptionForTitle = true
            };
            if (picker.ShowDialog() != true) return;   // 取消 = no-op（Req 2.2）
            var sourcePath = picker.SelectedPath;
            if (string.IsNullOrWhiteSpace(sourcePath)) return;

            // 校验 Valid_Route_Folder（Req 3.1 / 3.2 / 6.1）
            if (!LocalRouteFolderImporter.IsValidRouteFolder(sourcePath))
            {
                Toast.Warning("所选文件夹不含线路文件（*.json）");
                return;
            }

            var assetsDir = System.IO.Path.Combine(Global.Absolute("GameTask"), "AutoHoeing", "Assets");
            var targetName = LocalRouteFolderImporter.ResolveTargetName(sourcePath);
            if (string.IsNullOrWhiteSpace(targetName))
            {
                Toast.Warning("无法解析所选文件夹名称");
                return;
            }
            var targetPath = System.IO.Path.Combine(assetsDir, targetName);

            // 源已在 Assets 内 → 跳过拷贝直接选中（Req 3.5）
            bool copied = false;
            if (!LocalRouteFolderImporter.IsInsideAssets(sourcePath, assetsDir))
            {
                // 重名确认（OQ-2 / Req 4.1）
                if (LocalRouteFolderImporter.NeedsOverwriteConfirm(System.IO.Directory.Exists(targetPath)))
                {
                    var r = ThemedMessageBox.Question(
                        $"内置线路目录已存在同名文件夹「{targetName}」。\n是否覆盖其中的同名文件？（不会删除目标目录的其他文件）",
                        "导入线路 - 重名确认",
                        MessageBoxButton.YesNo,
                        System.Windows.MessageBoxResult.No);
                    if (r != System.Windows.MessageBoxResult.Yes) return;   // 否/取消 = 终止，不拷贝不改选中（Req 4 / 6.4）
                }

                try
                {
                    LocalRouteFolderImporter.CopyDirectoryRecursive(sourcePath, targetPath, overwrite: true);
                    copied = true;
                }
                catch (UnauthorizedAccessException ex)
                {
                    // 无权限写入：记录并提示，不崩溃、不改选中（Req 6.3 / 6.4）
                    _logger.LogWarning(ex, "[导入线路] 无权限拷贝到内置线路目录: {Target}", targetPath);
                    Toast.Error("导入失败：无权限写入内置线路目录");
                    return;
                }
                catch (IOException ex)
                {
                    // 拷贝 IO 错误：记录并提示，不崩溃、不改选中（Req 6.2 / 6.4）
                    _logger.LogWarning(ex, "[导入线路] 拷贝发生 IO 错误: {Target}", targetPath);
                    Toast.Error("导入失败：拷贝文件时发生 IO 错误");
                    return;
                }
            }

            // 成功：写选中 → 重建按钮组（高亮新文件夹）（Req 5.1/5.2/5.3）
            _settings["selectedBuiltinRoute"] = targetName;
            RebuildBuiltinButtons();

            // 刷新变体偏好面板（仅当已展开，OQ-5）
            _refreshVariantPanel?.Invoke();

            // 成功提示 + 联机一致性前提 A2（Req 7.1 / 7.2）
            Toast.Success(copied ? $"已导入线路「{targetName}」并设为当前内置线路" : $"已选中内置线路「{targetName}」");
            Toast.Information("提示：联机仅同步文件夹名并做 MD5 校验，不传输线路文件。请确保每位成员各自拥有同名、同内容的线路文件夹。", time: 6000);
        }
        catch (Exception ex)
        {
            // 兜底：任何未预期异常都不应让弹窗崩溃（Req 6.2/6.3/6.4）
            _logger.LogWarning(ex, "[导入线路] 导入流程发生未预期异常");
            Toast.Error("导入失败，请查看日志");
        }
    }

    // ===== 按线路切角色配置弹窗（hoeing-multiplayer-per-route-switch-roles）=====

    /// <summary>
    /// 为某内置线路文件夹弹出「配置线路角色」弹窗：列出该文件夹下所有线路文件，每行 1 号位 / 2 号位
    /// 角色名输入框，预填已存映射。确认后合并其它文件夹已存条目 + 本文件夹新值 → 序列化写 _settings。
    /// R1.1/R1.3/R1.4/R1.5, R2.1/R2.2。布局用 Grid 承载行内列（ui-layout-debugging-discipline）。
    /// </summary>
    private async Task OnConfigureRouteRoles(string folderName, string folderFullPath)
    {
        try
        {
            var files = _routeScanner.ListRouteFiles(folderFullPath);
            // 读取已存映射，预填该文件夹的条目
            var existing = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PerRouteSwitchRolesDecisions
                .ParseRoutes(_settings.TryGetValue("perRouteSwitchRoles", out var raw) ? raw : null);

            var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };

            // 表头行：线路 / 1号位 / 2号位（列定义与下方数据行一致：*,120,120）
            var headerGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 4) };
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            var hLine = new TextBlock { Text = "线路", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            var h1 = new TextBlock { Text = "1号位", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            var h2 = new TextBlock { Text = "2号位", FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
            System.Windows.Controls.Grid.SetColumn(hLine, 0);
            System.Windows.Controls.Grid.SetColumn(h1, 1);
            System.Windows.Controls.Grid.SetColumn(h2, 2);
            headerGrid.Children.Add(hLine);
            headerGrid.Children.Add(h1);
            headerGrid.Children.Add(h2);
            panel.Children.Add(headerGrid);
            if (files.Count == 0)
            {
                panel.Children.Add(new TextBlock { Text = "该文件夹无线路文件", Margin = new Thickness(0, 8, 0, 2) });
            }

            // 每行：线路名只读 + 1号位 TextBox + 2号位 TextBox，用 Grid 承载（列 *,120,120）
            var rowControls = new List<(string RouteId, TextBox P1, TextBox P2)>();
            foreach (var f in files)
            {
                var routeId = $"{folderName}/{f.RelativeId}";
                existing.TryGetValue(routeId, out var entry);

                var grid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 0) };
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });

                var nameCell = new TextBlock { Text = f.RelativeId, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                var p1 = new TextBox { Text = entry?.Position1 ?? "", PlaceholderText = "1号位", Margin = new Thickness(2, 0, 2, 0) };
                var p2 = new TextBox { Text = entry?.Position2 ?? "", PlaceholderText = "2号位", Margin = new Thickness(2, 0, 2, 0) };
                System.Windows.Controls.Grid.SetColumn(nameCell, 0);
                System.Windows.Controls.Grid.SetColumn(p1, 1);
                System.Windows.Controls.Grid.SetColumn(p2, 2);
                grid.Children.Add(nameCell);
                grid.Children.Add(p1);
                grid.Children.Add(p2);
                panel.Children.Add(grid);
                rowControls.Add((routeId, p1, p2));
            }

            var scroll = new System.Windows.Controls.ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
                MaxHeight = 500,
            };

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = $"配置线路角色 - {folderName}",
                Content = scroll,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var r = await dialog.ShowDialogAsync();
            if (r != MessageBoxResult.Primary) return;

            // 确认：合并已存其它文件夹条目 + 本文件夹新值 → 序列化写 _settings
            var merged = new Dictionary<string, BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteRoleEntry>(existing);
            // 先移除本文件夹下所有旧条目（routeId 以 "{folderName}/" 开头），再用新值覆盖
            var prefix = folderName + "/";
            foreach (var key in merged.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                merged.Remove(key);
            foreach (var (routeId, p1, p2) in rowControls)
            {
                var e = new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteRoleEntry
                {
                    RouteId = routeId,
                    Position1 = p1.Text?.Trim() ?? "",
                    Position2 = p2.Text?.Trim() ?? ""
                };
                if (BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PerRouteSwitchRolesDecisions.RouteNeedsSwitch(e))
                    merged[routeId] = e;
            }
            _settings["perRouteSwitchRoles"] =
                BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.PerRouteSwitchRolesDecisions.SerializeRoutes(merged);
            Toast.Success("线路角色配置已保存");
        }
        catch (Exception ex)
        {
            // 弹窗构建/读写异常不应让配置弹窗崩溃（可恢复异常）
            _logger.LogWarning(ex, "[按线路切角色] 配置弹窗异常");
            Toast.Warning("配置线路角色失败，请查看日志");
        }
    }

    // 成员侧「按线路切角色」配置列表：每行 = 线路文件夹名（只读）+ ⚙角色 按钮。
    // 线路由房主下发，成员不选线路；此处仅为各内置线路文件夹提供配置自己 1/2 号位角色的入口（点 ⚙角色 复用同一弹窗）。
    private void BuildMemberRouteRolesHost()
    {
        if (MemberRouteRolesHost == null) return;
        var panel = new System.Windows.Controls.StackPanel();
        var folders = _routeScanner.ScanBuiltinRoutes();
        if (folders.Count == 0)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "未发现内置线路文件夹",
                Foreground = SystemColors.GrayTextBrush,
                Margin = new Thickness(0, 4, 0, 0)
            });
            MemberRouteRolesHost.Content = panel;
            return;
        }

        foreach (var folder in folders)
        {
            var row = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 6)
            };

            var nameText = new TextBlock
            {
                Text = folder.FolderName,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 220,
                Margin = new Thickness(0, 0, 8, 0)
            };
            row.Children.Add(nameText);

            var cfgRolesBtn = new System.Windows.Controls.Button { Content = "⚙角色" };
            var folderNameForDialog = folder.FolderName;
            var folderFullPathForDialog = folder.FullPath;
            cfgRolesBtn.Click += async (_, _) => await OnConfigureRouteRoles(folderNameForDialog, folderFullPathForDialog);
            row.Children.Add(cfgRolesBtn);

            panel.Children.Add(row);
        }

        MemberRouteRolesHost.Content = panel;
    }

    // ===== 战斗策略 / 文档按钮挂接 =====
    private void HookFightStrategyButton()
    {
        OpenFightStrategyButton.Click += (_, _) =>
            BetterGenshinImpact.GameTask.AutoFight.MultiplayerFightStrategyFileHelper.OpenForEdit();
    }

    // 配置组战斗策略配置（与配置组战斗策略框同一对象）；弹窗由配置组项打开，理论恒非空，仍加守卫
    private BetterGenshinImpact.GameTask.AutoFight.AutoFightConfig? GroupFightConfig
        => _item.GroupInfo?.Config?.PathingConfig?.AutoFightConfig;

    // E 卡片战斗策略下拉：复刻配置组选项（User\AutoFight 下 *.txt + "根据队伍自动选择"），初值取配置组 StrategyName
    // 远程编辑模式：选项源换注入的对方策略清单，不扫本机磁盘
    private void HookFightStrategyCombo()
    {
        try
        {
            var list = new List<string> { "根据队伍自动选择" };
            if (_remoteStrategyFiles != null)
            {
                list.AddRange(_remoteStrategyFiles);
            }
            else
            {
                var folder = Global.Absolute(@"User\AutoFight");
                Directory.CreateDirectory(folder);
                foreach (var f in Directory.GetFiles(folder, "*.txt", SearchOption.AllDirectories))
                {
                    var name = f.Replace(folder, "").Replace(".txt", "");
                    if (name.StartsWith('\\')) name = name[1..];
                    if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
                }
            }
            FightStrategyCombo.ItemsSource = list;

            var cur = GroupFightConfig?.StrategyName;
            FightStrategyCombo.SelectedItem =
                (!string.IsNullOrEmpty(cur) && list.Contains(cur)) ? cur : "根据队伍自动选择";
        }
        catch (Exception ex)
        {
            // 加载策略列表失败不应阻断弹窗（可恢复异常）
            _logger.LogWarning(ex, "[联机战斗策略] 加载策略下拉失败");
        }
    }

    // ===== 按周期吃食物设置（multiplayer-hoeing-auto-eat-food-by-period）=====
    private void HookMedicineEatButton()
    {
        OpenMedicineEatSettingsButton.Click += async (_, _) => await ShowMedicineEatSettingsDialogAsync();
    }

    private async Task ShowMedicineEatSettingsDialogAsync()
    {
        try
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };

            panel.Children.Add(new TextBlock
            {
                Text = "使用前请先在原神【背包-食物】页面，给要吃的食物点击右上角星标「加星置顶」，"
                     + "使目标食物固定排在食物页最前 4 格。本功能按下方序号点击对应格子的食物。\n"
                     + "序号 1~4 对应食物页前 4 格；周期填 0 表示该格不启用周期吃药。\n"
                     + "线路关键词（逗号分隔）：当前线路名包含任一关键词时，到达该线路的传送点就吃这格——"
                     + "无视吃药周期与 CD（哪怕周期填 0 或刚吃过也会吃）。周期与线路两个条件任一满足即吃。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = System.Windows.Media.Brushes.IndianRed
            });

            // 4 行：第N格（固定序号=行号）+ 周期输入(窄) + 线路关键词输入
            var periodBoxes = new TextBox[4];
            var routeBoxes = new TextBox[4];
            string[] curPeriods = { ViewModel.MedicineFoodPeriod1, ViewModel.MedicineFoodPeriod2, ViewModel.MedicineFoodPeriod3, ViewModel.MedicineFoodPeriod4 };
            string[] curRoutes = { ViewModel.MedicineFoodRouteKeywords1, ViewModel.MedicineFoodRouteKeywords2, ViewModel.MedicineFoodRouteKeywords3, ViewModel.MedicineFoodRouteKeywords4 };
            // 列标题行（与数据行同列定义以对齐）：第一列（第N格）留空，第二列=执行周期，第三列=强制吃药线路
            var headerRow = new System.Windows.Controls.Grid { Margin = new Thickness(0, 2, 0, 2) };
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            headerRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var hPeriod = new TextBlock { Text = "周期(秒)", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0), TextWrapping = TextWrapping.Wrap };
            var hRoute = new TextBlock { Text = "强制吃药线路", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 4, 0) };
            System.Windows.Controls.Grid.SetColumn(hPeriod, 1);
            System.Windows.Controls.Grid.SetColumn(hRoute, 2);
            headerRow.Children.Add(hPeriod);
            headerRow.Children.Add(hRoute);
            panel.Children.Add(headerRow);
            for (int i = 0; i < 4; i++)
            {
                var row = new System.Windows.Controls.Grid { Margin = new Thickness(0, 4, 0, 0) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });   // 第N格 标签
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });   // 周期(窄)
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // 线路关键词(占满剩余)

                var label = new TextBlock { Text = $"第{i + 1}格", VerticalAlignment = VerticalAlignment.Center };
                var periodBox = new TextBox { Text = curPeriods[i], PlaceholderText = "周期秒", Margin = new Thickness(4, 0, 4, 0) };
                var routeBox = new TextBox { Text = curRoutes[i], PlaceholderText = "吃药线路关键词（逗号分隔，可留空）", Margin = new Thickness(4, 0, 4, 0) };

                System.Windows.Controls.Grid.SetColumn(label, 0);
                System.Windows.Controls.Grid.SetColumn(periodBox, 1);
                System.Windows.Controls.Grid.SetColumn(routeBox, 2);
                row.Children.Add(label);
                row.Children.Add(periodBox);
                row.Children.Add(routeBox);
                panel.Children.Add(row);

                periodBoxes[i] = periodBox;
                routeBoxes[i] = routeBox;
            }

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "按周期吃食物设置",
                Content = panel,
                MinWidth = 620,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var r = await dialog.ShowDialogAsync();
            if (r != MessageBoxResult.Primary) return;

            // 写回 ViewModel（Save() 会经 WriteMultiplayerSettings 落盘；周期非法留原值由 VM 保留字符串）
            // 序号固定绑定格号（第N格→序号N），归正历史配置里可能的乱序值
            ViewModel.MedicineFoodSlot1 = 1;
            ViewModel.MedicineFoodSlot2 = 2;
            ViewModel.MedicineFoodSlot3 = 3;
            ViewModel.MedicineFoodSlot4 = 4;
            ViewModel.MedicineFoodPeriod1 = periodBoxes[0].Text?.Trim() ?? "0";
            ViewModel.MedicineFoodPeriod2 = periodBoxes[1].Text?.Trim() ?? "0";
            ViewModel.MedicineFoodPeriod3 = periodBoxes[2].Text?.Trim() ?? "0";
            ViewModel.MedicineFoodPeriod4 = periodBoxes[3].Text?.Trim() ?? "0";
            ViewModel.MedicineFoodRouteKeywords1 = routeBoxes[0].Text?.Trim() ?? "";
            ViewModel.MedicineFoodRouteKeywords2 = routeBoxes[1].Text?.Trim() ?? "";
            ViewModel.MedicineFoodRouteKeywords3 = routeBoxes[2].Text?.Trim() ?? "";
            ViewModel.MedicineFoodRouteKeywords4 = routeBoxes[3].Text?.Trim() ?? "";
            Toast.Success("按周期吃食物设置已保存");
        }
        catch (Exception ex)
        {
            // 弹窗构建/读写异常不应让配置弹窗崩溃（可恢复异常）
            _logger.LogWarning(ex, "[按周期吃食物] 设置弹窗异常");
            Toast.Warning("按周期吃食物设置失败，请查看日志");
        }
    }

    // ===== 配置拾取参数（联机/单机共享 AutoHoeingConfig 的 4 个参数）=====
    private void HookPickupParamsButton()
    {
        OpenPickupParamsButton.Click += async (_, _) => await ShowPickupParamsDialogAsync();
    }

    private async Task ShowPickupParamsDialogAsync()
    {
        try
        {
            var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 0) };

            panel.Children.Add(new TextBlock
            {
                Text = "以下参数与单机锄地共用，联机与单机为同一份配置。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 10),
                Foreground = SystemColors.GrayTextBrush
            });

            // 4 个参数：标签 + 说明 + NumberBox（范围与主界面一致）
            var findFBox = AddPickupParamRow(panel,
                "识别间隔(ms)", "两次检测F图标之间等待时间，建议10-200",
                ViewModel.FindFInterval, 10, 200);
            var pickupDelayBox = AddPickupParamRow(panel,
                "拾取后延时(ms)", "连续拾取相同物品时建议调大，建议32-200",
                ViewModel.PickupDelay, 16, 500);
            var rollingDelayBox = AddPickupParamRow(panel,
                "滚动后延时(ms)", "拾取错误时建议调大，建议16-100",
                ViewModel.RollingDelay, 16, 200);
            var scrollCycleBox = AddPickupParamRow(panel,
                "单次滚动周期(ms)", "上下滚动不全时建议调大，建议800-2000",
                ViewModel.ScrollCycle, 500, 3000);

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "配置拾取参数",
                Content = panel,
                MinWidth = 460,
                PrimaryButtonText = "确定",
                CloseButtonText = "取消",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var r = await dialog.ShowDialogAsync();
            if (r != MessageBoxResult.Primary) return;

            // 写回 ViewModel（Save() 经 WriteMultiplayerSettings 落盘）；NumberBox.Value 为 double?，取整回填，null 保留原值
            ViewModel.FindFInterval = ToIntOr(findFBox.Value, ViewModel.FindFInterval);
            ViewModel.PickupDelay = ToIntOr(pickupDelayBox.Value, ViewModel.PickupDelay);
            ViewModel.RollingDelay = ToIntOr(rollingDelayBox.Value, ViewModel.RollingDelay);
            ViewModel.ScrollCycle = ToIntOr(scrollCycleBox.Value, ViewModel.ScrollCycle);
            Toast.Success("拾取参数已保存");
        }
        catch (Exception ex)
        {
            // 弹窗构建/读写异常不应让配置弹窗崩溃（可恢复异常）
            _logger.LogWarning(ex, "[配置拾取参数] 设置弹窗异常");
            Toast.Warning("配置拾取参数失败，请查看日志");
        }
    }

    // 构建单个拾取参数行（标签 + 说明 + NumberBox），返回 NumberBox 以便读回
    private static Wpf.Ui.Controls.NumberBox AddPickupParamRow(
        StackPanel host, string label, string desc, int value, double min, double max)
    {
        host.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 2) });
        host.Children.Add(new TextBlock
        {
            Text = desc,
            Margin = new Thickness(0, 0, 0, 2),
            Foreground = SystemColors.GrayTextBrush,
            TextWrapping = TextWrapping.Wrap
        });
        var box = new Wpf.Ui.Controls.NumberBox
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            SpinButtonPlacementMode = Wpf.Ui.Controls.NumberBoxSpinButtonPlacementMode.Inline,
            Margin = new Thickness(0, 0, 0, 6)
        };
        host.Children.Add(box);
        return box;
    }

    private static int ToIntOr(double? value, int fallback)
        => value.HasValue ? (int)Math.Round(value.Value) : fallback;

    private void HookDocButtons()
    {
        // 顶部"使用教程"按钮 → 打开使用教程 md（迁自原 VariantTutorialButton）
        TopTutorialButton.Click += (s, e) => { e.Handled = true; OpenDoc("联机锄地使用教程.md"); };
        // 变体卡"线路变体说明" → 弹窗展示简明说明
        VariantHelpButton.Click += (s, e) => { e.Handled = true; ShowVariantHelpDialog(); };
        // "制作规则"不变
        VariantRulesButton.Click += (s, e) => { e.Handled = true; OpenDoc("联机锄地变体线路制作规则.md"); };
    }

    // 线路变体说明弹窗：用既有 Wpf.Ui MessageBox 展示简明说明（不跳转文件），单关闭按钮
    private void ShowVariantHelpDialog()
    {
        try
        {
            var text =
                "线路变体：同一条线路的不同打法版本（最多 A/B/C/D 四个）。\n\n" +
                "• 多人锄地时，不同成员可各跑同一线路的不同变体（如 A 打左侧怪、B 打右侧怪），在战斗点附近自动对齐、一起放行。\n" +
                "• 在「线路变体偏好」里按总文件夹选一次变体，该文件夹下所有线路都按此变体跑。\n" +
                "• 没选变体、或所选变体缺少某条线路时，自动回退到代表变体（A→B→C→D 第一个存在的），不会中断。\n" +
                "• 老线路没有变体子文件夹时行为不变，照常全自动同步。\n\n" +
                "想自己制作变体线路？点击「制作规则」查看详细规范。";

            var box = new Wpf.Ui.Controls.MessageBox
            {
                Title = "线路变体说明",
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(4)
                },
                CloseButtonText = "知道了",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            _ = box.ShowDialogAsync();
        }
        catch (Exception ex)
        {
            // 弹窗构建/显示异常不应让配置弹窗崩溃（可恢复异常）
            _logger.LogWarning(ex, "[变体说明] 弹窗显示失败");
            Toast.Warning("显示线路变体说明失败，请查看日志");
        }
    }

    // 打开输出根目录下的某个 md 说明文档
    private void OpenDoc(string fileName)
    {
        try
        {
            var docPath = System.IO.Path.Combine(AppContext.BaseDirectory, fileName);
            if (System.IO.File.Exists(docPath))
            {
                Process.Start(new ProcessStartInfo(docPath) { UseShellExecute = true });
            }
            else
            {
                Toast.Warning($"未找到《{fileName}》，请重新编译以生成该文件");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[变体偏好] 打开说明文档失败: {File}", fileName);
            Toast.Warning("打开说明文档失败，请查看日志");
        }
    }

    // ===== 变体偏好面板（迁现状 BuildVariantPanelContent / refreshVariantPanel）=====
    private void HookVariantPanel()
    {
        VariantExpander.Expanded += (_, _) => BuildVariantPanelContent();
        // C7: 供导入成功后复用（OQ-5）。IsExpanded 守卫在委托内，避免前向引用。
        _refreshVariantPanel = () => { if (VariantExpander.IsExpanded) BuildVariantPanelContent(); };
    }

    // import-local-route-folder C7: 幂等，每次 forceRefresh 重扫
    private void BuildVariantPanelContent()
    {
        try
        {
            var globalCfg = _globalCfg;
            var dirs = BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingTask
                .ResolveAllHoeingRouteDirs(globalCfg);
            // 按"总文件夹"分组：每个总文件夹 → 其下存在的变体子文件夹集合（A→B→C→D）
            var topFolders = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantScanner
                .ScanTopFolders(dirs, forceRefresh: true);

            if (topFolders.Count == 0)
            {
                VariantPanelHost.Content = new TextBlock
                {
                    Text = "未发现变体线路。请在总线路文件夹下建 A变体/B变体/C变体/D变体 子文件夹并放入对应 _a/_b 后缀的 JSON。",
                    Margin = new Thickness(8),
                    Foreground = SystemColors.GrayTextBrush,
                    TextWrapping = TextWrapping.Wrap
                };
                return;
            }

            var stack = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
            foreach (var kv in topFolders.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                var topName = kv.Key;
                var availableFolders = kv.Value;   // 已按 A→B→C→D 排序
                if (availableFolders.Count == 0) continue;

                var rowGrid = new System.Windows.Controls.Grid { Margin = new Thickness(0, 0, 0, 6) };
                // 左列放线路名（自适应，给个最小宽），右列按钮占满剩余空间（避免说明文字被截断）
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90 });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var label = new TextBlock
                {
                    Text = topName,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 12, 0),
                    TextWrapping = TextWrapping.Wrap
                };
                System.Windows.Controls.Grid.SetColumn(label, 0);
                rowGrid.Children.Add(label);

                // 默认代表（A→B→C→D 第一个存在）
                var repFolder = availableFolders[0];

                // 预读各变体说明（变体说明.txt），用于弹窗 + 按钮展示（R15.11）
                var folderDescs = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var vf in availableFolders)
                {
                    var desc = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantScanner
                        .ReadVariantDescription(dirs, topName, vf);
                    folderDescs[vf] = desc;
                }
                string DescSuffix(string vf)
                    => folderDescs.TryGetValue(vf, out var d) && !string.IsNullOrEmpty(d) ? $"（{d}）" : "";

                string CurrentLabel()
                {
                    if (_variantPrefBuffer.TryGetValue(topName, out var f) && !string.IsNullOrEmpty(f))
                        return $"当前：{f}{DescSuffix(f)}";
                    return $"跟随默认（{repFolder}{DescSuffix(repFolder)}）";
                }

                var pickBtn = new Button
                {
                    Content = CurrentLabel(),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                };
                System.Windows.Controls.Grid.SetColumn(pickBtn, 1);
                rowGrid.Children.Add(pickBtn);

                pickBtn.Click += async (_, _) =>
                {
                    try
                    {
                        var optionPanel = new System.Windows.Controls.StackPanel { Margin = new Thickness(8) };
                        optionPanel.Children.Add(new TextBlock
                        {
                            Text = $"为「{topName}」选择变体（整个文件夹下所有线路都跑此变体）：",
                            Margin = new Thickness(0, 0, 0, 8),
                            TextWrapping = TextWrapping.Wrap
                        });
                        string? chosen = _variantPrefBuffer.TryGetValue(topName, out var cur) ? cur : null;
                        var group = "variant_" + topName;

                        var rbDefault = new System.Windows.Controls.RadioButton
                        {
                            Content = $"跟随默认（{repFolder}{DescSuffix(repFolder)}）",
                            GroupName = group,
                            Margin = new Thickness(0, 2, 0, 2),
                            IsChecked = string.IsNullOrEmpty(chosen)
                        };
                        optionPanel.Children.Add(rbDefault);
                        var folderRadios = new List<System.Windows.Controls.RadioButton>();
                        foreach (var f in availableFolders)
                        {
                            var rb = new System.Windows.Controls.RadioButton
                            {
                                Content = $"{f}{DescSuffix(f)}",
                                GroupName = group,
                                Tag = f,
                                Margin = new Thickness(0, 2, 0, 2),
                                IsChecked = string.Equals(chosen, f, StringComparison.Ordinal)
                            };
                            folderRadios.Add(rb);
                            optionPanel.Children.Add(rb);
                        }

                        var pickDialog = new Wpf.Ui.Controls.MessageBox
                        {
                            Title = $"选择变体 - {topName}",
                            Content = optionPanel,
                            PrimaryButtonText = "确定",
                            CloseButtonText = "取消",
                            Owner = Application.Current.MainWindow,
                            WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        };
                        var r = await pickDialog.ShowDialogAsync();
                        if (r != MessageBoxResult.Primary) return;

                        var pickedFolder = folderRadios.FirstOrDefault(x => x.IsChecked == true)?.Tag as string;
                        if (string.IsNullOrEmpty(pickedFolder))
                            _variantPrefBuffer.Remove(topName);   // 跟随默认 = 清除偏好
                        else
                            _variantPrefBuffer[topName] = pickedFolder!;
                        pickBtn.Content = CurrentLabel();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[变体偏好] 选择变体弹窗异常");
                    }
                };

                stack.Children.Add(rowGrid);
            }
            VariantPanelHost.Content = stack;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[变体偏好] 折叠面板加载失败");
            VariantPanelHost.Content = new TextBlock
            {
                Text = "加载变体列表失败，请查看日志",
                Margin = new Thickness(8),
                Foreground = SystemColors.GrayTextBrush
            };
        }
    }

    // ===== 保存出口（聚合 VM 标量 + 动态 UI + 单机区 + 变体）=====
    public void Save()
    {
        bool isMp = ViewModel.MultiplayerEnabled;
        _settings["multiplayerEnabled"] = isMp;
        if (isMp)
        {
            ViewModel.WriteMultiplayerSettings(_settings);   // 见映射表；selectedBuiltinRoute 不在 VM
            // selectedBuiltinRoute 兜底：按钮点过则已写入 settings，否则取 settings/globalCfg 当前值
            if (!_settings.ContainsKey("selectedBuiltinRoute"))
            {
                _settings["selectedBuiltinRoute"] = GetStr("selectedBuiltinRoute", _globalCfg.SelectedBuiltinRoute);
            }
            SavePreSwitchWeaponRows();   // 开锄前换武器两行配置（随配置组持久化）
        }
        else
        {
            SaveSoloBranch();
        }
        SaveVariantPreferences();
        SaveFightStrategySelection();
    }

    // 战斗策略下拉写回：写入配置组 AutoFightConfig.StrategyName（与配置组战斗策略框同一参数，运行时非固定分支生效）
    private void SaveFightStrategySelection()
    {
        var gfc = GroupFightConfig;
        if (gfc != null && FightStrategyCombo.SelectedItem is string sel && !string.IsNullOrEmpty(sel))
        {
            gfc.StrategyName = sel;
        }
    }

    // 单机分支保存（迁现状，逐字符等价）：遍历 settingItems + controls 写值 + Remove 联机专属字段
    private void SaveSoloBranch()
    {
        foreach (var setting in _settingItems)
        {
            if (!_soloControls.TryGetValue(setting.Name, out var ctrl)) continue;
            object? value = ctrl switch
            {
                System.Windows.Controls.ComboBox combo => combo.SelectedItem?.ToString(),
                System.Windows.Controls.CheckBox check => check.IsChecked ?? false,
                TextBox tb => setting.Type == "number"
                    ? double.TryParse(tb.Text, out var n) ? (object)n : tb.Text
                    : tb.Text,
                _ => null
            };
            _settings[setting.Name] = value;
        }
        // 清除联机专属字段，防止残留值在单机模式下被 ApplySettingsOverride 错误应用
        // 注意：保留 "multiplayerRole" 和 "memberJoinMode"，避免单机保存破坏联机角色配置
        foreach (var key in new[]
        {
            "targetHostName", "coordinatorServerUrl",
            "playerName", "playerUid", "multiplayerPartyName", "multiplayerStartAvatarName",
            "expectedPlayerCount", "roomWhitelist",
            "partyTimeoutSeconds", "partyTimeoutAction",
            "syncPointMinDistance", "startRouteIndex", "enableKazuhaSync", "multiplayerUseFixedFightStrategy",
            "kazuhaSyncWaitSeconds", "kazuhaSyncTimeoutSeconds", "kazuhaWaitSkillCdSeconds",
            "fightTimeoutSeconds",
            // === 落后追赶调试参数（单机模式清除，hoeing-multiplayer-lagging-member-catchup）===
            "enableLaggingCatchUp", "lagSegmentThreshold",
            // === 重开续跑开关（单机模式清除，hoeing-multiworld-host-restart-resume-round）===
            "multiWorldResumeEnabled",
            // === 单人调试模式开关（单机模式清除，hoeing-multiplayer-solo-debug-mode）===
            "soloDebugMode",
            // === 快速同步点抢报（单机模式清除）===
            "fastSyncPointEnabled", "fastSyncPathingDistance", "fastSyncTeleportLoadingDelayMs",
            "sharedFightEndQuorumEnabled", "sharedFightEndQuorumRatio",
            "debugMode", "useFixedDebugRoutes", "fixedDebugRoutePath", "selectedBuiltinRoute",
            "multiWorldEnabled", "multiWorldCount"
        })
        {
            _settings.Remove(key);
        }
        // 单机模式清除开锄前换武器配置（与现有联机专属字段处理一致，避免残留值被执行层误读）
        _settings.Remove("preSwitchWeaponRows");
        // 单机模式清除按线路切角色配置（hoeing-multiplayer-per-route-switch-roles，联机专属字段）
        _settings.Remove("perRouteSwitchRoles");
    }

    // 变体偏好写入（迁现状）：不分联机/单机，Save 末尾统一
    private void SaveVariantPreferences()
    {
        if (_variantPrefBuffer.Count > 0)
        {
            _settings["variantPreferences"] = new Dictionary<string, string>(_variantPrefBuffer, StringComparer.Ordinal);
            // 远程编辑模式跳过全局镜像（不污染本机全局配置）
            if (!_remoteMode)
            {
                var gcfg = TaskContext.Instance().Config.AutoHoeingConfig;
                foreach (var (k, v) in _variantPrefBuffer)
                    gcfg.SetVariantPreference(k, v);   // 镜像到全局兜底
            }
        }
        else
        {
            _settings.Remove("variantPreferences");
        }
    }

    /// <summary>
    /// 手动打开联机助手
    /// </summary>
    private void OnManualLaunchAssistant(object sender, RoutedEventArgs e)
    {
        try
        {
            // 如果已有助手进程在运行，不弹窗，什么都不做
            var existing = System.Diagnostics.Process.GetProcessesByName("MultiplayerHoeingAssistant");
            if (existing.Length > 0)
            {
                return;
            }

            var assistantPath = System.IO.Path.Combine(AppContext.BaseDirectory, @"Tools\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.exe");
            if (System.IO.File.Exists(assistantPath))
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = assistantPath,
                        UseShellExecute = true
                    }
                };
                process.Start();
                Toast.Success("已启动联机助手");
            }
            else
            {
                Toast.Warning("联机助手不存在，请先编译");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "手动启动联机助手失败");
            Toast.Warning("启动联机助手失败");
        }
    }
}
