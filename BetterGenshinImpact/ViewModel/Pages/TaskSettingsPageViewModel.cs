using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoArtifactSalvage;
using BetterGenshinImpact.GameTask.AutoCook;
using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFriendship;
using BetterGenshinImpact.GameTask.AutoFriendship.Model;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoFishing;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation;
using BetterGenshinImpact.GameTask.AutoMusicGame;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using BetterGenshinImpact.GameTask.AutoWood;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.GetGridIcons;
using BetterGenshinImpact.GameTask.Model.GameUI;
using BetterGenshinImpact.GameTask.UseRedeemCode;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Interface;
using BetterGenshinImpact.View.Pages;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.ViewModel.Pages.View;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using CommunityToolkit.Mvvm.Messaging.Messages;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Windows.System;
using Wpf.Ui;
using Wpf.Ui.Violeta.Controls;
using TextBox = Wpf.Ui.Controls.TextBox;
using BetterGenshinImpact.GameTask.AutoFriendship;



namespace BetterGenshinImpact.ViewModel.Pages;

public partial class TaskSettingsPageViewModel : ViewModel
{
    public AllConfig Config { get; set; }

    private readonly INavigationService _navigationService;
    private readonly TaskTriggerDispatcher _taskDispatcher;

    private CancellationTokenSource? _cts;
    private static readonly object _locker = new();

    // [ObservableProperty]
    // private string[] _strategyList;

    [ObservableProperty]
    private bool _switchAutoGeniusInvokationEnabled;

    [ObservableProperty]
    private string _switchAutoGeniusInvokationButtonText = "启动";

    [ObservableProperty]
    private int _autoWoodRoundNum;

    [ObservableProperty]
    private int _autoWoodDailyMaxCount = 2000;

    [ObservableProperty]
    private bool _switchAutoWoodEnabled;

    [ObservableProperty]
    private string _switchAutoWoodButtonText = "启动";

    //[ObservableProperty]
    //private string[] _combatStrategyList;

    [ObservableProperty]
    private int _autoDomainRoundNum;

    [ObservableProperty]
    private bool _switchAutoDomainEnabled;

    [ObservableProperty]
    private string _switchAutoDomainButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoBossEnabled;

    [ObservableProperty]
    private string _switchAutoBossButtonText = "启动";

    [ObservableProperty]
    private int _autoStygianOnslaughtRoundNum;

    [ObservableProperty]
    private bool _switchAutoStygianOnslaughtEnabled;

    [ObservableProperty]
    private string _switchAutoStygianOnslaughtButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoFightEnabled;

    [ObservableProperty]
    private string _switchAutoFightButtonText = "启动";

    [ObservableProperty]
    private string _switchAutoTrackButtonText = "启动";

    [ObservableProperty]
    private string _switchAutoTrackPathButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoMusicGameEnabled;

    [ObservableProperty]
    private string _switchAutoMusicGameButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoAlbumEnabled;

    [ObservableProperty]
    private string _switchAutoAlbumButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoCookEnabled;

    [ObservableProperty]
    private string _switchAutoCookButtonText = "启动";

    [ObservableProperty]
    private List<string> _domainNameList;

    public static List<string> ArtifactSalvageStarList = ["4", "3", "2", "1"];

    public static List<int> BossNumList = [1, 2, 3];

    public static List<string> AutoBossNameList = [.. AutoBossData.SupportedBossNames];

    public static List<string> AvatarIndexList { get; } = new List<string> { "", "1", "2", "3", "4" };
    public static List<string> LeyLineOutcropTypeList = ["启示之花", "藏金之花"];
    public static List<string> LeyLineOutcropCountryList = ["蒙德", "璃月", "稻妻", "须弥", "枫丹", "纳塔", "挪德卡莱", "至冬"];

    [ObservableProperty]
    private List<string> _autoMusicLevelList = ["传说", "大师", "困难", "普通", "所有"];

    [ObservableProperty]
    private AutoFightViewModel? _autoFightViewModel;

    [ObservableProperty]
    private OneDragonFlowViewModel? _oneDragonFlowViewModel;

    [ObservableProperty]
    private bool _switchAutoFishingEnabled;

    [ObservableProperty]
    private string _switchAutoFishingButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoLeyLineOutcropEnabled;

    [ObservableProperty]
    private string _switchAutoLeyLineOutcropButtonText = "启动";

    [ObservableProperty]
    private bool _scanDropsAfterRewardEnabledUi;

    [ObservableProperty]
    private FrozenDictionary<Enum, string> _fishingTimePolicyDict = Enum.GetValues(typeof(FishingTimePolicy))
        .Cast<FishingTimePolicy>()
        .ToFrozenDictionary(
            e => (Enum)e,
            e => e.GetType()
                .GetField(e.ToString())?
                .GetCustomAttribute<DescriptionAttribute>()?
                .Description ?? e.ToString());

    [ObservableProperty]
    private FrozenDictionary<Enum, string> _enemyTypeDict = Enum.GetValues(typeof(EnemyType))
        .Cast<EnemyType>()
        .ToFrozenDictionary(
            e => (Enum)e,
            e => AutoFriendshipConfig.GetEnemyTypeDisplayName(e));

    private bool saveScreenshotOnKeyTick;
    private bool _suppressScanDropsAfterRewardPrompt;
    private int _scanDropsAfterRewardPromptVersion;
    public bool SaveScreenshotOnKeyTick
    {
        get => Config.CommonConfig.ScreenshotEnabled && saveScreenshotOnKeyTick;
        set => SetProperty(ref saveScreenshotOnKeyTick, value);
    }

    [ObservableProperty]
    private bool _switchArtifactSalvageEnabled;

    [ObservableProperty]
    private FrozenDictionary<Enum, string> _recognitionFailurePolicyDict = Enum.GetValues(typeof(RecognitionFailurePolicy))
        .Cast<RecognitionFailurePolicy>()
        .ToFrozenDictionary(
            e => (Enum)e,
            e => e.GetType()
                .GetField(e.ToString())?
                .GetCustomAttribute<DescriptionAttribute>()?
                .Description ?? e.ToString());

    [ObservableProperty]
    private bool _switchGetGridIconsEnabled;
    [ObservableProperty]
    private string _switchGetGridIconsButtonText = "启动";
    [ObservableProperty]
    private FrozenDictionary<Enum, string> _gridNameDict = Enum.GetValues(typeof(GridScreenName))
        .Cast<GridScreenName>()
        .ToFrozenDictionary(
            e => (Enum)e,
            e => e.GetType()
                .GetField(e.ToString())?
                .GetCustomAttribute<DescriptionAttribute>()?
                .Description ?? e.ToString());

    [ObservableProperty]
    private string _switchGridIconsAccuracyTestButtonText = "运行模型准确率测试";

    /// <summary>数量 OCR 测试当前选择的分类或当前一页模式。</summary>
    [ObservableProperty]
    private InventoryCountComparisonTarget _inventoryCountComparisonTarget = InventoryCountComparisonTarget.CharacterDevelopmentItems;

    /// <summary>数量 OCR 测试下拉框显示的四个目标选项。</summary>
    public FrozenDictionary<Enum, string> InventoryCountComparisonTargetDict { get; } = Enum
        .GetValues<InventoryCountComparisonTarget>()
        .ToFrozenDictionary(
            e => (Enum)e,
            e => e.GetType()
                .GetField(e.ToString())?
                .GetCustomAttribute<DescriptionAttribute>()?
                .Description ?? e.ToString());

    [ObservableProperty]
    private bool _switchAutoRedeemCodeEnabled;

    [ObservableProperty]
    private string _switchAutoRedeemCodeButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoHoeingEnabled;

    [ObservableProperty]
    private string _switchAutoHoeingButtonText = "启动";

    [ObservableProperty]
    private bool _switchAutoFriendshipEnabled;

    [ObservableProperty]
    private string _switchAutoFriendshipButtonText = "启动";

    [ObservableProperty]
    private bool _hasRouteDiff;

    [ObservableProperty]
    private string _routeDiffMessage = "";

    [ObservableProperty]
    private string _multiplayerStatusText = "请先创建或加入房间";

    [ObservableProperty]
    private bool _canStartMultiplayer;

    [ObservableProperty]
    private bool _isRoomHost = true;

    /// <summary>
    /// 是否为房间成员（与 IsRoomHost 相反）
    /// </summary>
    public bool IsRoomMember => !IsRoomHost;

    /// <summary>
    /// 联机角色选择索引：0=房主，1=成员
    /// </summary>
    public int MultiplayerRoleIndex
    {
        get => Config.AutoHoeingConfig.MultiplayerRole == "member" ? 1 : 0;
        set
        {
            if (value == 0)
            {
                Config.AutoHoeingConfig.MultiplayerRole = "host";
                IsRoomHost = true;
            }
            else
            {
                Config.AutoHoeingConfig.MultiplayerRole = "member";
                IsRoomHost = false;
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsRoomMember));
        }
    }

    [ObservableProperty]
    private bool _canSkipPartyWait = false;

    [ObservableProperty]
    private bool _isWaitingForParty = false; // 是否正在等待组队（进入F2页面）

    [ObservableProperty]
    private string _skipPartyWaitHotkeyText = "快捷键：未配置（可在快捷键设置中配置）";

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models.PlayerInfo> _roomPlayers = new();

    [ObservableProperty]
    private string _roomPlayerCount = "0 人";

    [ObservableProperty]
    private string _roomPlayerSummary = "房间玩家 (0人)";

    [ObservableProperty]
    private string _currentRoomCode = "";

    private BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.CoordinatorClient? _coordinatorClient;

    /// <summary>
    /// 内置线路列表
    /// </summary>
    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<BuiltinRouteViewModel> _builtinRoutes = new();

    /// <summary>
    /// 变体偏好摘要（页面上显示当前各总文件夹选了哪个变体）。
    /// route-variant-sync-by-logical-id spec / R15.6。
    /// </summary>
    [ObservableProperty]
    private string _variantPreferenceSummary = "未设置变体偏好（全部跟随默认）";

    /// <summary>
    /// 锄地一条龙是否可见（隐藏功能，点击独立任务标题10次解锁/锁定）
    /// </summary>
    [ObservableProperty]
    private bool _autoHoeingVisible = true;

    /// <summary>
    /// 好感任务自动完成是否可见
    /// </summary>
    [ObservableProperty]
    private bool _autoFriendshipVisible = true;

    /// <summary>
    /// 全局解锁状态，供配置组等其他地方查询
    /// </summary>
    public static bool AutoHoeingUnlocked { get; private set; }

    private int _autoHoeingUnlockClickCount;

    // /// <summary>
    // /// 独立任务标题点击计数，达到10次切换锄地一条龙的显示/隐藏状态
    // /// </summary>
    // [RelayCommand]
    // private void OnSoloTaskTitleClick()
    // {
    //     _autoHoeingUnlockClickCount++;
    //     if (_autoHoeingUnlockClickCount >= 10)
    //     {
    //         _autoHoeingUnlockClickCount = 0;
    //         var newState = !AutoHoeingVisible;
    //         AutoHoeingVisible = newState;
    //         AutoHoeingUnlocked = newState;
    //         Config.CommonConfig.AutoHoeingUnlocked = newState;
    //         Wpf.Ui.Violeta.Controls.Toast.Success(newState ? "锄地一条龙已解锁" : "锄地一条龙已锁定");
    //         WeakReferenceMessenger.Default.Send(new PropertyChangedMessage<object>(this, "AutoHoeingUnlocked", !newState, newState));
    //     }
    // }

    public TaskSettingsPageViewModel(IConfigService configService, INavigationService navigationService, TaskTriggerDispatcher taskTriggerDispatcher)
    {
        Config = configService.Get();
        _navigationService = navigationService;
        _taskDispatcher = taskTriggerDispatcher;
        NormalizeLeyLineOutcropType();
        _scanDropsAfterRewardEnabledUi = Config.AutoLeyLineOutcropConfig.ScanDropsAfterRewardEnabled;

        // 从持久化配置恢复锄地一条龙解锁状态
        // _autoHoeingVisible = Config.CommonConfig.AutoHoeingUnlocked;
        // AutoHoeingUnlocked = Config.CommonConfig.AutoHoeingUnlocked;
        _autoHoeingVisible = true;
        AutoHoeingUnlocked = true;

        //_strategyList = LoadCustomScript(Global.Absolute(@"User\AutoGeniusInvokation"));

        //_combatStrategyList = ["根据队伍自动选择", .. LoadCustomScript(Global.Absolute(@"User\AutoFight"))];

        _domainNameList = ["", .. MapLazyAssets.Get().DomainNameList];
        _autoFightViewModel = new AutoFightViewModel(Config);
        _oneDragonFlowViewModel = new OneDragonFlowViewModel();

        // 初始化快捷键文本
        UpdateSkipPartyWaitHotkeyText();

        // 监听快捷键配置变化
        Config.HotKeyConfig.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(Config.HotKeyConfig.SkipPartyWaitHotkey))
            {
                UpdateSkipPartyWaitHotkeyText();
            }
        };

        // 监听锄地配置变化
        Config.AutoHoeingConfig.PropertyChanged += (sender, e) =>
        {
            if (e.PropertyName == nameof(Config.AutoHoeingConfig.UseFixedDebugRoutes) ||
                e.PropertyName == nameof(Config.AutoHoeingConfig.FixedDebugRoutePath))
            {
                UpdateBuiltinRouteButtonStates();
            }
        };

        // 启动定时器检查等待状态
        StartWaitingStatusChecker();

        // 注册联机锄地快捷键消息
        WeakReferenceMessenger.Default.Register<PropertyChangedMessage<object>>(this, (sender, msg) =>
        {
            if (msg.PropertyName == "TriggerSkipPartyWait")
            {
                if (CanSkipPartyWait && SkipPartyWaitCommand.CanExecute(null))
                {
                    SkipPartyWaitCommand.Execute(null);
                }
            }
        });

        // 初始化内置线路
        InitializeBuiltinRoutes();
        RefreshVariantPreferenceSummary();
    }

    /// <summary>
    /// 初始化内置线路列表
    /// </summary>
    public void InitializeBuiltinRoutes()
    {
        var scanner = new BetterGenshinImpact.GameTask.AutoHoeing.Services.RouteDirectoryScanner();
        var folders = scanner.ScanBuiltinRoutes();

        BuiltinRoutes.Clear();
        foreach (var folder in folders)
        {
            BuiltinRoutes.Add(new BuiltinRouteViewModel
            {
                FolderName = folder.FolderName,
                FullPath = folder.FullPath,
                RouteCount = folder.RouteCount,
                IsSelected = false, // 初始状态不选中，后续通过UpdateBuiltinRouteButtonStates设置
                IsEnabled = false   // 初始状态不可用，后续通过UpdateBuiltinRouteButtonStates设置
            });
        }
        
        // 更新按钮状态
        UpdateBuiltinRouteButtonStates();
    }

    /// <summary>
    /// 选择内置线路
    /// </summary>
    [RelayCommand]
    private void SelectBuiltinRoute(BuiltinRouteViewModel route)
    {
        // 取消所有其他选择
        foreach (var r in BuiltinRoutes)
        {
            r.IsSelected = false;
        }

        // 选中当前路线
        route.IsSelected = true;
        Config.AutoHoeingConfig.SelectedBuiltinRoute = route.FolderName;
    }

    /// <summary>
    /// 重建变体偏好摘要文本（含变体说明），供页面显示当前选择情况。
    /// </summary>
    public void RefreshVariantPreferenceSummary()
    {
        try
        {
            var cfg = Config.AutoHoeingConfig;
            var prefs = cfg.VariantPreferences;
            if (prefs == null || prefs.Count == 0)
            {
                VariantPreferenceSummary = "未设置变体偏好（全部跟随默认）";
                return;
            }
            var dirs = AutoHoeingTask.ResolveAllHoeingRouteDirs(cfg);
            var validFolders = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantNaming.VariantFolders;
            var parts = new List<string>();
            foreach (var kv in prefs.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(kv.Key) || string.IsNullOrEmpty(kv.Value)) continue;
                // 只显示新格式（value = 变体文件夹名 X变体）；旧格式残留（value=文件名）过滤掉
                if (!validFolders.Contains(kv.Value, StringComparer.Ordinal)) continue;
                var desc = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantScanner
                    .ReadVariantDescription(dirs, kv.Key, kv.Value);
                parts.Add(string.IsNullOrEmpty(desc) ? $"{kv.Key}→{kv.Value}" : $"{kv.Key}→{kv.Value}（{desc}）");
            }
            VariantPreferenceSummary = parts.Count == 0
                ? "未设置变体偏好（全部跟随默认）"
                : "当前变体：" + string.Join("；", parts);
        }
        catch (Exception ex)
        {
            App.GetLogger<TaskSettingsPageViewModel>().LogWarning(ex, "[变体偏好] 刷新摘要失败");
        }
    }

    /// <summary>
    /// 打开《联机锄地变体线路制作规则.md》/《联机锄地使用教程.md》（输出根目录）。
    /// </summary>
    [RelayCommand]
    private void OpenHoeingDoc(string fileName)
    {
        try
        {
            var docPath = Path.Combine(AppContext.BaseDirectory, fileName ?? "");
            if (File.Exists(docPath))
                Process.Start(new ProcessStartInfo(docPath) { UseShellExecute = true });
            else
                Toast.Warning($"未找到《{fileName}》，请重新编译以生成该文件");
        }
        catch (Exception ex)
        {
            App.GetLogger<TaskSettingsPageViewModel>().LogWarning(ex, "[变体偏好] 打开说明文档失败: {File}", fileName);
            Toast.Warning("打开说明文档失败，请查看日志");
        }
    }

    /// <summary>
    /// 全局独立任务页的"线路变体偏好"入口（route-variant-sync-by-logical-id spec / R15.6）。
    /// 列出所有总文件夹，点击某个总文件夹弹窗选 A/B/C/D 变体，写入全局
    /// AutoHoeingConfig.VariantPreferences（按总文件夹粒度，整文件夹跟随同一变体）。
    /// </summary>
    [RelayCommand]
    private async Task OpenVariantPreferences()
    {
        try
        {
            var cfg = Config.AutoHoeingConfig;
            var dirs = AutoHoeingTask.ResolveAllHoeingRouteDirs(cfg);
            var topFolders = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantScanner
                .ScanTopFolders(dirs, forceRefresh: true);

            var root = new StackPanel { Margin = new Thickness(12), MinWidth = 360 };

            // 顶部文档按钮（仅制作规则）
            var docRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            var rulesBtn = new Wpf.Ui.Controls.Button { Content = "制作规则" };
            rulesBtn.Click += (_, _) => OpenHoeingDocCommand.Execute("联机锄地变体线路制作规则.md");
            docRow.Children.Add(rulesBtn);
            root.Children.Add(docRow);

            if (topFolders.Count == 0)
            {
                root.Children.Add(new TextBlock
                {
                    Text = "未发现变体线路。请在总线路文件夹下建 A变体/B变体/C变体/D变体 子文件夹并放入对应 _a/_b 后缀的 JSON。",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = System.Windows.SystemColors.GrayTextBrush,
                });
            }
            else
            {
                foreach (var kv in topFolders.OrderBy(k => k.Key, StringComparer.Ordinal))
                {
                    var topName = kv.Key;
                    var availableFolders = kv.Value;
                    if (availableFolders.Count == 0) continue;
                    var repFolder = availableFolders[0];

                    var folderDescs = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var vf in availableFolders)
                        folderDescs[vf] = BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.RouteVariantScanner
                            .ReadVariantDescription(dirs, topName, vf);
                    string DescSuffix(string vf) =>
                        folderDescs.TryGetValue(vf, out var d) && !string.IsNullOrEmpty(d) ? $"（{d}）" : "";

                    var rowGrid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 90 });
                    rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    var label = new TextBlock { Text = topName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0), TextWrapping = TextWrapping.Wrap };
                    Grid.SetColumn(label, 0);
                    rowGrid.Children.Add(label);

                    string Current()
                        => cfg.VariantPreferences != null && cfg.VariantPreferences.TryGetValue(topName, out var f) && !string.IsNullOrEmpty(f)
                            ? $"当前：{f}{DescSuffix(f)}"
                            : $"跟随默认（{repFolder}{DescSuffix(repFolder)}）";

                    var pickBtn = new Wpf.Ui.Controls.Button
                    {
                        Content = Current(),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                    };
                    Grid.SetColumn(pickBtn, 1);
                    rowGrid.Children.Add(pickBtn);

                    var capturedTop = topName;
                    var capturedFolders = availableFolders;
                    var capturedRep = repFolder;
                    pickBtn.Click += async (_, _) =>
                    {
                        try
                        {
                            var optionPanel = new StackPanel { Margin = new Thickness(8) };
                            optionPanel.Children.Add(new TextBlock
                            {
                                Text = $"为「{capturedTop}」选择变体（整个文件夹下所有线路都跑此变体）：",
                                Margin = new Thickness(0, 0, 0, 8),
                                TextWrapping = TextWrapping.Wrap
                            });
                            string? chosen = cfg.VariantPreferences != null && cfg.VariantPreferences.TryGetValue(capturedTop, out var cur) ? cur : null;
                            var group = "g_var_" + capturedTop;
                            var rbDefault = new RadioButton { Content = $"跟随默认（{capturedRep}{DescSuffix(capturedRep)}）", GroupName = group, Margin = new Thickness(0, 2, 0, 2), IsChecked = string.IsNullOrEmpty(chosen) };
                            optionPanel.Children.Add(rbDefault);
                            var radios = new List<RadioButton>();
                            foreach (var f in capturedFolders)
                            {
                                var rb = new RadioButton { Content = $"{f}{DescSuffix(f)}", GroupName = group, Tag = f, Margin = new Thickness(0, 2, 0, 2), IsChecked = string.Equals(chosen, f, StringComparison.Ordinal) };
                                radios.Add(rb);
                                optionPanel.Children.Add(rb);
                            }
                            var pickDialog = new Wpf.Ui.Controls.MessageBox
                            {
                                Title = $"选择变体 - {capturedTop}",
                                Content = optionPanel,
                                PrimaryButtonText = "确定",
                                CloseButtonText = "取消",
                                Owner = Application.Current.MainWindow,
                                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                            };
                            var rr = await pickDialog.ShowDialogAsync();
                            if (rr != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
                            var picked = radios.FirstOrDefault(x => x.IsChecked == true)?.Tag as string;
                            if (string.IsNullOrEmpty(picked))
                                cfg.RemoveVariantPreference(capturedTop);
                            else
                                cfg.SetVariantPreference(capturedTop, picked!);
                            pickBtn.Content = Current();
                        }
                        catch (Exception ex)
                        {
                            App.GetLogger<TaskSettingsPageViewModel>().LogWarning(ex, "[变体偏好] 选择变体弹窗异常");
                        }
                    };
                    root.Children.Add(rowGrid);
                }
            }

            var dialog = new Wpf.Ui.Controls.MessageBox
            {
                Title = "线路变体偏好（全局）",
                Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 520 },
                CloseButtonText = "关闭",
                Owner = Application.Current.MainWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            await dialog.ShowDialogAsync();
            RefreshVariantPreferenceSummary();
        }
        catch (Exception ex)
        {
            App.GetLogger<TaskSettingsPageViewModel>().LogWarning(ex, "[变体偏好] 打开全局变体偏好失败");
            Toast.Warning("打开变体偏好失败，请查看日志");
        }
    }

    /// <summary>
    /// 处理调试路径输入框变化
    /// </summary>
    public void OnDebugRoutePathChanged()
    {
        UpdateBuiltinRouteButtonStates();
    }

    /// <summary>
    /// 更新内置线路按钮状态
    /// </summary>
    private void UpdateBuiltinRouteButtonStates()
    {
        var hasManualPath = !string.IsNullOrWhiteSpace(Config.AutoHoeingConfig.FixedDebugRoutePath);
        var useFixedDebugRoutes = Config.AutoHoeingConfig.UseFixedDebugRoutes;

        // 只有在启用固定调试线路且没有手动路径时，按钮才可用
        var buttonsEnabled = useFixedDebugRoutes && !hasManualPath;

        foreach (var route in BuiltinRoutes)
        {
            route.IsEnabled = buttonsEnabled;
        }

        // 手动路径清空且启用固定调试线路时恢复之前的选择
        if (buttonsEnabled && !string.IsNullOrWhiteSpace(Config.AutoHoeingConfig.SelectedBuiltinRoute))
        {
            var selectedRoute = BuiltinRoutes.FirstOrDefault(r => r.FolderName == Config.AutoHoeingConfig.SelectedBuiltinRoute);
            if (selectedRoute != null)
            {
                selectedRoute.IsSelected = true;
            }
        }
        
        // 如果手动路径非空或未启用固定调试线路，清除所有选择
        if (!buttonsEnabled)
        {
            foreach (var route in BuiltinRoutes)
            {
                route.IsSelected = false;
            }
        }
    }

    partial void OnScanDropsAfterRewardEnabledUiChanged(bool value)
    {
        if (_suppressScanDropsAfterRewardPrompt)
        {
            return;
        }

        if (!value)
        {
            Interlocked.Increment(ref _scanDropsAfterRewardPromptVersion);
            Config.AutoLeyLineOutcropConfig.ScanDropsAfterRewardEnabled = false;
            return;
        }

        var version = Interlocked.Increment(ref _scanDropsAfterRewardPromptVersion);
        _ = ConfirmScanDropsAfterRewardRiskAsync(version);
    }

    private async Task ConfirmScanDropsAfterRewardRiskAsync(int version)
    {
        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "风险提示",
            Content = "开启“领取奖励后扫描掉落物光柱”后，角色会在领奖完成后主动移动拾取。部分地脉花点位或特定配队下，可能因为移动范围较大而卡住。\n\n如果你愿意接受这个风险，请继续开启；否则将保持关闭。",
            PrimaryButtonText = "接受风险并开启",
            CloseButtonText = "不接受，保持关闭",
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var result = await messageBox.ShowDialogAsync();
        var accepted = result == Wpf.Ui.Controls.MessageBoxResult.Primary;

        if (version != _scanDropsAfterRewardPromptVersion)
        {
            return;
        }

        _suppressScanDropsAfterRewardPrompt = true;
        try
        {
            ScanDropsAfterRewardEnabledUi = accepted;
            Config.AutoLeyLineOutcropConfig.ScanDropsAfterRewardEnabled = accepted;
        }
        finally
        {
            _suppressScanDropsAfterRewardPrompt = false;
        }
    }

    private void NormalizeLeyLineOutcropType()
    {
        var type = Config.AutoLeyLineOutcropConfig.LeyLineOutcropType;
        if (type == "蓝花（经验书）")
        {
            Config.AutoLeyLineOutcropConfig.LeyLineOutcropType = "启示之花";
            return;
        }

        if (type == "黄花（摩拉）")
        {
            Config.AutoLeyLineOutcropConfig.LeyLineOutcropType = "藏金之花";
            return;
        }

        if (string.IsNullOrWhiteSpace(type) || !LeyLineOutcropTypeList.Contains(type))
        {
            Config.AutoLeyLineOutcropConfig.LeyLineOutcropType = LeyLineOutcropTypeList[0];
        }
    }


    [RelayCommand]
    private async Task OnSOneDragonFlow()
    {
        if (OneDragonFlowViewModel == null || OneDragonFlowViewModel.SelectedConfig == null)
        {
            OneDragonFlowViewModel.OnNavigatedTo();
            if (OneDragonFlowViewModel == null || OneDragonFlowViewModel.SelectedConfig == null)
            {
                Toast.Warning("未设置任务!");
                return;
            }
        }
        await OneDragonFlowViewModel.OnOneKeyExecute();
    }

    [RelayCommand]
    private async Task OnStopSoloTask()
    {
        CancellationContext.Instance.Cancel();
        SwitchAutoGeniusInvokationEnabled = false;
        SwitchAutoWoodEnabled = false;
        SwitchAutoDomainEnabled = false;
        SwitchAutoBossEnabled = false;
        SwitchAutoFightEnabled = false;
        SwitchAutoMusicGameEnabled = false;
        SwitchAutoAlbumEnabled = false;
        SwitchAutoCookEnabled = false;
        SwitchAutoFishingEnabled = false;
        SwitchAutoLeyLineOutcropEnabled = false;
        SwitchArtifactSalvageEnabled = false;
        SwitchAutoRedeemCodeEnabled = false;
        SwitchAutoStygianOnslaughtEnabled = false;
        SwitchGetGridIconsEnabled = false;
        await Task.Delay(800);
    }

    [RelayCommand]
    private void OnStrategyDropDownOpened(string type)
    {
        AutoFightViewModel?.OnStrategyDropDownOpened(type);
    }

    [RelayCommand]
    public void OnGoToHotKeyPage()
    {
        _navigationService.Navigate(typeof(HotKeyPage));
    }

    [RelayCommand]
    public async Task OnSwitchAutoGeniusInvokation()
    {
        if (GetTcgStrategy(out var content))
        {
            return;
        }

        SwitchAutoGeniusInvokationEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoGeniusInvokationTask(new GeniusInvokationTaskParam(content)));
        SwitchAutoGeniusInvokationEnabled = false;
    }

    public bool GetTcgStrategy(out string content)
    {
        content = string.Empty;
        if (string.IsNullOrEmpty(Config.AutoGeniusInvokationConfig.StrategyName))
        {
            Toast.Warning("请先选择策略");
            return true;
        }

        var path = Global.Absolute(@"User\AutoGeniusInvokation\" + Config.AutoGeniusInvokationConfig.StrategyName + ".txt");

        if (!File.Exists(path))
        {
            Toast.Error("策略文件不存在");
            return true;
        }

        content = File.ReadAllText(path);
        return false;
    }

    [RelayCommand]
    public async Task OnGoToAutoGeniusInvokationUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/tcg.html"));
    }

    [RelayCommand]
    public async Task OnSwitchAutoWood()
    {
        SwitchAutoWoodEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoWoodTask(new WoodTaskParam(AutoWoodRoundNum, AutoWoodDailyMaxCount)));
        SwitchAutoWoodEnabled = false;
    }

    [RelayCommand]
    public async Task OnGoToAutoWoodUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/felling.html"));
    }

    [RelayCommand]
    public async Task OnSwitchAutoFight()
    {
        if (GetFightStrategy(out var path))
        {
            return;
        }

        var param = new AutoFightParam(path, Config.AutoFightConfig);

        SwitchAutoFightEnabled = true;
        // 按策略文件扩展名路由到 JSON 或 TXT 战斗任务
        var fightTask = BetterGenshinImpact.GameTask.AutoFight.Factory.CombatTaskFactoryProvider
            .GetFactory(path).CreateTask(param);
        await new TaskRunner()
            .RunSoloTaskAsync(fightTask);
        SwitchAutoFightEnabled = false;
    }

    [RelayCommand]
    public async Task OnGoToAutoFightUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/domain.html"));
    }

    [RelayCommand]
    public async Task OnSwitchAutoDomain()
    {
        if (GetFightStrategy(out var path))
        {
            return;
        }
        
        Config.AutoDomainEnable = true;
        SwitchAutoDomainEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoDomainTask(new AutoDomainParam(AutoDomainRoundNum, path)));
        SwitchAutoDomainEnabled = false;
        Config.AutoDomainEnable = false;
    }

    public bool GetFightStrategy(out string path)
    {
        return GetFightStrategy(Config.AutoFightConfig.StrategyName, out path);
    }

    public bool GetFightStrategy(string strategyName, out string path)
    {
        if (string.IsNullOrEmpty(strategyName))
        {
            UIDispatcherHelper.Invoke(() => { Toast.Warning("请先在下拉列表配置中选择战斗策略！"); });
            path = string.Empty;
            return true;
        }

        // 按文件存在性解析 .txt / .json 路径（支持 JSON 战斗策略）
        path = BetterGenshinImpact.GameTask.AutoFight.AutoFightParam.ResolveStrategyPath(strategyName);

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            UIDispatcherHelper.Invoke(() => { Toast.Error("当前选择的自动战斗策略文件不存在"); });
            return true;
        }

        return false;
    }

    [RelayCommand]
    private async Task OnSwitchAutoBoss()
    {
        if (GetFightStrategy(Config.AutoBossConfig.StrategyName, out var path))
        {
            return;
        }

        SwitchAutoBossEnabled = true;
        AutoBossParam param = new AutoBossParam(path);
        param.SetAutoBossConfig(Config.AutoBossConfig);
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoBossTask(param));
        SwitchAutoBossEnabled = false;
    }

    [RelayCommand]
    public async Task OnGoToAutoDomainUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/domain.html"));
    }

    [RelayCommand]
    public async Task OnSwitchAutoHoeing()
    {
        SwitchAutoHoeingEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoHoeingTask());
        SwitchAutoHoeingEnabled = false;
    }

    [RelayCommand]
    public async Task OnSwitchAutoFriendship()
    {
        SwitchAutoFriendshipEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoFriendshipTask(Config.AutoFriendshipConfig, null, null, Config.AutoFightConfig));
        SwitchAutoFriendshipEnabled = false;
    }

    [RelayCommand]
    private async Task OnCreateRoom()
    {
        var config = Config.AutoHoeingConfig;
        if (string.IsNullOrEmpty(config.CoordinatorServerUrl))
        {
            Toast.Warning("请先填写服务器地址");
            return;
        }
        // 清理旧连接
        UnsubscribeClientEvents();
        if (_coordinatorClient != null)
            await _coordinatorClient.DisposeAsync();

        var client = new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.CoordinatorClient();
        var connected = await client.ConnectAsync(config.CoordinatorServerUrl, CancellationToken.None);
        if (!connected)
        {
            Toast.Error("连接服务器失败，请检查服务器地址");
            return;
        }
        var whitelist = string.IsNullOrEmpty(config.RoomWhitelist)
            ? null
            : new System.Collections.Generic.List<string>(config.RoomWhitelist.Split(new[] { ',', '，' }, System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries));
        var roomCode = await client.CreateRoomAsync(config.PlayerName, whitelist, config.PlayerUid, config.ExpectedPlayerCount);
        if (roomCode != null)
        {
            _coordinatorClient = client;
            SubscribeClientEvents(client);
            config.CurrentRoomCode = roomCode;
            CurrentRoomCode = roomCode;
            MultiplayerStatusText = $"房间已创建：{roomCode}";
            CanStartMultiplayer = true;
            IsRoomHost = true;
            CanSkipPartyWait = true;
            RoomPlayers.Clear();
            RoomPlayers.Add(new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models.PlayerInfo
            {
                PlayerName = string.IsNullOrEmpty(config.PlayerName) ? "房主" : config.PlayerName
            });
            RoomPlayerCount = "1 人";
            var myName = string.IsNullOrEmpty(config.PlayerName) ? "房主" : config.PlayerName;
            RoomPlayerSummary = $"房间玩家 (1人): {myName}";
            Toast.Success($"房间创建成功，房间码：{roomCode}");
        }
    }

    [RelayCommand]
    private async Task OnJoinRoom()
    {
        var config = Config.AutoHoeingConfig;
        if (string.IsNullOrEmpty(config.CurrentRoomCode))
        {
            Toast.Warning("请先输入房间码");
            return;
        }
        if (string.IsNullOrEmpty(config.CoordinatorServerUrl))
        {
            Toast.Warning("请先填写服务器地址");
            return;
        }
        // 清理旧连接
        UnsubscribeClientEvents();
        if (_coordinatorClient != null)
            await _coordinatorClient.DisposeAsync();

        var client = new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.CoordinatorClient();
        var connected = await client.ConnectAsync(config.CoordinatorServerUrl, CancellationToken.None);
        if (!connected)
        {
            Toast.Error("连接服务器失败，请检查服务器地址");
            return;
        }
        var success = await client.JoinRoomAsync(config.CurrentRoomCode, config.PlayerName, config.PlayerUid);
        if (success)
        {
            _coordinatorClient = client;
            SubscribeClientEvents(client);
            CurrentRoomCode = config.CurrentRoomCode;
            MultiplayerStatusText = $"已加入房间：{config.CurrentRoomCode}";
            CanStartMultiplayer = true;
            IsRoomHost = false;
            CanSkipPartyWait = false;
            RoomPlayerCount = "加入成功";
            Toast.Success($"成功加入房间 {config.CurrentRoomCode}");
        }
        else
        {
            Toast.Error("加入房间失败，请检查房间码");
        }
    }

    [RelayCommand]
    private async Task OnStartMultiplayerHoeing()
    {
        SwitchAutoHoeingEnabled = true;
        await new TaskRunner().RunSoloTaskAsync(new AutoHoeingTask());
        SwitchAutoHoeingEnabled = false;
    }

    [RelayCommand]
    private void OnSkipPartyWait()
    {
        if (!BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingTask.IsWaitingForParty)
        {
            Toast.Warning("当前未在等待组队状态");
            return;
        }
        BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingTask.SkipPartyWait = true;
        Toast.Success("已发送立即开始信号");
    }

    private void UpdateSkipPartyWaitHotkeyText()
    {
        var hotkey = Config.HotKeyConfig.SkipPartyWaitHotkey;
        if (string.IsNullOrEmpty(hotkey))
        {
            SkipPartyWaitHotkeyText = "快捷键：未配置（可在快捷键设置中配置）";
        }
        else
        {
            SkipPartyWaitHotkeyText = $"快捷键：{hotkey}";
        }
    }

    private System.Threading.Timer? _waitingStatusTimer;

    private void StartWaitingStatusChecker()
    {
        _waitingStatusTimer = new System.Threading.Timer(_ =>
        {
            var isWaiting = BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingTask.IsWaitingForParty;
            if (IsWaitingForParty != isWaiting)
            {
                UIDispatcherHelper.Invoke(() =>
                {
                    IsWaitingForParty = isWaiting;
                    CanSkipPartyWait = IsRoomHost && isWaiting;
                });
            }
        }, null, TimeSpan.Zero, TimeSpan.FromMilliseconds(500));
    }

    [RelayCommand]
    private async Task OnCloseRoom()
    {
        var config = Config.AutoHoeingConfig;
        if (string.IsNullOrEmpty(config.CurrentRoomCode) && _coordinatorClient == null)
        {
            Toast.Warning("当前没有房间");
            return;
        }
        if (_coordinatorClient != null)
        {
            UnsubscribeClientEvents();
            await _coordinatorClient.CloseRoomAsync();
            await _coordinatorClient.DisposeAsync();
            _coordinatorClient = null;
        }
        config.CurrentRoomCode = "";
        CurrentRoomCode = "";
        CanStartMultiplayer = false;
        IsRoomHost = true;
        CanSkipPartyWait = false;
        MultiplayerStatusText = "房间已关闭";
        RoomPlayers.Clear();
        RoomPlayerCount = "0 人";
        RoomPlayerSummary = "房间玩家 (0人)";
        Toast.Success("房间已关闭");
    }

    [RelayCommand]
    private void OnCopyRoomCode()
    {
        var code = CurrentRoomCode;
        if (string.IsNullOrEmpty(code))
            code = Config.AutoHoeingConfig.CurrentRoomCode;
        if (string.IsNullOrEmpty(code))
        {
            Toast.Warning("房间码为空");
            return;
        }
        // 剪贴板可能被其他进程占用，重试 3 次
        for (int i = 0; i < 3; i++)
        {
            try
            {
                System.Windows.Clipboard.SetDataObject(code, true);
                Toast.Success($"已复制房间码：{code}");
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(50);
            }
        }
        Toast.Warning("复制失败，剪贴板被占用，请手动复制");
    }
    
    [RelayCommand]
    private void Openutorial()
    {
        var tutorialPath = Path.Combine(AppContext.BaseDirectory, "联机锄地使用教程.md");
        if (File.Exists(tutorialPath))
        {
            Process.Start(new ProcessStartInfo(tutorialPath) { UseShellExecute = true });
        }
        else
        {
            Toast.Warning("未找到文件");
        }
    }

    [RelayCommand]
    private void OpenMultiplayerFightStrategyFile()
    {
        // multiplayer-hoeing-fixed-fight-strategy §4
        // 副作用全部委派给共享 helper，弹窗 (ShowHoeingSettingsDialog) 复用同一份逻辑。
        BetterGenshinImpact.GameTask.AutoFight.MultiplayerFightStrategyFileHelper.OpenForEdit();
    }

    [RelayCommand]
    private async Task OnBrowseRooms()
    {
        var config = Config.AutoHoeingConfig;
        if (string.IsNullOrEmpty(config.CoordinatorServerUrl))
        {
            Toast.Warning("请先填写服务器地址");
            return;
        }
        var client = new BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.CoordinatorClient();
        var connected = await client.ConnectAsync(config.CoordinatorServerUrl, CancellationToken.None);
        if (!connected)
        {
            Toast.Error("连接服务器失败");
            return;
        }
        try
        {
            var rooms = await client.GetOnlineRoomsAsync();
            var dialog = new BetterGenshinImpact.View.Dialogs.RoomBrowserDialog(rooms, async () =>
            {
                return await client.GetOnlineRoomsAsync();
            });
            dialog.Owner = Application.Current.MainWindow;
            dialog.ShowDialog();

            if (!string.IsNullOrEmpty(dialog.SelectedRoomCode))
            {
                config.CurrentRoomCode = dialog.SelectedRoomCode;

                // 清理旧连接，复用浏览器的连接加入房间
                UnsubscribeClientEvents();
                if (_coordinatorClient != null)
                    await _coordinatorClient.DisposeAsync();

                var success = await client.JoinRoomAsync(dialog.SelectedRoomCode, config.PlayerName, config.PlayerUid);
                if (success)
                {
                    _coordinatorClient = client;
                    SubscribeClientEvents(client);
                    CurrentRoomCode = dialog.SelectedRoomCode;
                    MultiplayerStatusText = $"已加入房间：{dialog.SelectedRoomCode}";
                    CanStartMultiplayer = true;
                    IsRoomHost = false;
                    CanSkipPartyWait = false;
                    RoomPlayerCount = "加入成功";
                    Toast.Success($"成功加入房间 {dialog.SelectedRoomCode}");
                    return; // 不 dispose client，已保存
                }
                else
                {
                    Toast.Error("加入房间失败");
                }
            }
        }
        finally
        {
            if (_coordinatorClient != client) // 只在未保存时 dispose
                await client.DisposeAsync();
        }
    }

    private void OnPlayerListUpdated(System.Collections.Generic.List<BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models.PlayerInfo> players)
    {
        UIDispatcherHelper.Invoke(() =>
        {
            RoomPlayers.Clear();
            foreach (var p in players)
                RoomPlayers.Add(p);
            RoomPlayerCount = $"{players.Count} 人";
            RoomPlayerSummary = players.Count == 0
                ? "房间玩家 (0人)"
                : $"房间玩家 ({players.Count}人): {string.Join(", ", players.Select(p => p.PlayerName))}";
        });
    }

    private void SubscribeClientEvents(BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.CoordinatorClient client)
    {
        client.PlayerListUpdated += OnPlayerListUpdated;
    }

    private void UnsubscribeClientEvents()
    {
        if (_coordinatorClient != null)
            _coordinatorClient.PlayerListUpdated -= OnPlayerListUpdated;
    }

    [RelayCommand]
    private async Task OnSwitchAutoStygianOnslaught()
    {
        if (GetFightStrategy(Config.AutoStygianOnslaughtConfig.StrategyName, out var path))
        {
            return;
        }

        SwitchAutoStygianOnslaughtEnabled = true;
        AutoStygianOnslaughtParam param = new AutoStygianOnslaughtParam();
        param.SetAutoStygianOnslaughtConfig(Config.AutoStygianOnslaughtConfig);
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoStygianOnslaughtTask(param, path));
        SwitchAutoStygianOnslaughtEnabled = false;
    }

    [RelayCommand]
    private async Task OnGoToAutoStygianOnslaughtUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/stygian.html"));
    }

    [RelayCommand]
    public async Task OnGoToAutoLeyLineOutcropUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/leyline.html"));
    }


    [RelayCommand]
    public void OnOpenFightFolder()
    {
        AutoFightViewModel?.OnOpenFightFolder();
    }

    [Obsolete]
    [RelayCommand]
    public void OnSwitchAutoTrack()
    {
        // try
        // {
        //     lock (_locker)
        //     {
        //         if (SwitchAutoTrackButtonText == "启动")
        //         {
        //             _cts?.Cancel();
        //             _cts = new CancellationTokenSource();
        //             var param = new AutoTrackParam(_cts);
        //             _taskDispatcher.StartIndependentTask(IndependentTaskEnum.AutoTrack, param);
        //             SwitchAutoTrackButtonText = "停止";
        //         }
        //         else
        //         {
        //             _cts?.Cancel();
        //             SwitchAutoTrackButtonText = "启动";
        //         }
        //     }
        // }
        // catch (Exception ex)
        // {
        //     ThemedMessageBox.Error(ex.Message);
        // }
    }

    [RelayCommand]
    public async Task OnGoToAutoTrackUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/track.html"));
    }

    [Obsolete]
    [RelayCommand]
    public void OnSwitchAutoTrackPath()
    {
        // try
        // {
        //     lock (_locker)
        //     {
        //         if (SwitchAutoTrackPathButtonText == "启动")
        //         {
        //             _cts?.Cancel();
        //             _cts = new CancellationTokenSource();
        //             var param = new AutoTrackPathParam(_cts);
        //             _taskDispatcher.StartIndependentTask(IndependentTaskEnum.AutoTrackPath, param);
        //             SwitchAutoTrackPathButtonText = "停止";
        //         }
        //         else
        //         {
        //             _cts?.Cancel();
        //             SwitchAutoTrackPathButtonText = "启动";
        //         }
        //     }
        // }
        // catch (Exception ex)
        // {
        //     ThemedMessageBox.Error(ex.Message);
        // }
    }

    [RelayCommand]
    private async Task OnGoToAutoTrackPathUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/track.html"));
    }

    [RelayCommand]
    private async Task OnSwitchAutoMusicGame()
    {
        SwitchAutoMusicGameEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoMusicGameTask(new AutoMusicGameParam()));
        SwitchAutoMusicGameEnabled = false;
    }

    [RelayCommand]
    private async Task OnGoToAutoMusicGameUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/music.html"));
    }

    [RelayCommand]
    private async Task OnSwitchAutoAlbum()
    {
        SwitchAutoAlbumEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoAlbumTask(new AutoMusicGameParam()));
        SwitchAutoAlbumEnabled = false;
    }

    [RelayCommand]
    private async Task OnSwitchAutoCook()
    {
        SwitchAutoCookEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoCookTask());
        SwitchAutoCookEnabled = false;
    }

    [RelayCommand]
    private async Task OnSwitchAutoFishing()
    {
        SwitchAutoFishingEnabled = true;
        var param = AutoFishingTaskParam.BuildFromConfig(TaskContext.Instance().Config.AutoFishingConfig, SaveScreenshotOnKeyTick);
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoFishingTask(param));
        SwitchAutoFishingEnabled = false;
    }

    [RelayCommand]
    private async Task OnSwitchAutoLeyLineOutcrop()
    {
        SwitchAutoLeyLineOutcropEnabled = true;
        AutoLeyLineOutcropParam autoLeyLineOutcropParam = new AutoLeyLineOutcropParam();
        autoLeyLineOutcropParam.SetAutoLeyLineOutcropConfig(Config.AutoLeyLineOutcropConfig);
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoLeyLineOutcropTask(autoLeyLineOutcropParam));
        SwitchAutoLeyLineOutcropEnabled = false;
    }

    [RelayCommand]
    private async Task OnGoToAutoFishingUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/fish.html"));
    }

    [RelayCommand]
    private async Task OnGoToTorchPreviousVersionsAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://pytorch.org/get-started/previous-versions"));
    }

    [RelayCommand]
    private void OnOpenLocalScriptRepo()
    {
        AutoFightViewModel?.OnOpenLocalScriptRepo();
    }

    [RelayCommand]
    private async Task OnSwitchArtifactSalvage()
    {
        SwitchArtifactSalvageEnabled = true;
        await new TaskRunner()
            .RunSoloTaskAsync(new AutoArtifactSalvageTask(new AutoArtifactSalvageTaskParam(
                int.Parse(Config.AutoArtifactSalvageConfig.MaxArtifactStar),
                Config.AutoArtifactSalvageConfig.JavaScript,
                Config.AutoArtifactSalvageConfig.ArtifactSetFilter,
                Config.AutoArtifactSalvageConfig.MaxNumToCheck,
                Config.AutoArtifactSalvageConfig.RecognitionFailurePolicy
                )));
        SwitchArtifactSalvageEnabled = false;
    }

    [RelayCommand]
    private async Task OnGoToArtifactSalvageUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/feats/task/artifactSalvage.html"));
    }

    [RelayCommand]
    private async Task OnOpenArtifactSalvageTestOCRWindow()
    {
        ArtifactOcrDialog ocrDialog = new ArtifactOcrDialog(0.70, 0.112, 0.275, 0.50, "圣遗物分解", this.Config.AutoArtifactSalvageConfig.JavaScript);
        if (await ocrDialog.CaptureAsync()) { ocrDialog.ShowDialog(); }
    }

    [RelayCommand]
    private async Task OnCopyArtifactSalvageJavaScriptFromRepository()
    {
        var list = ScriptControlViewModel.LoadAllJsScriptProjects();
        var stackPanel = ScriptControlViewModel.CreateJsScriptSelectionPanel(list, typeof(RadioButton));

        var result = PromptDialog.Prompt("请选择需要复制的JS脚本", "请选择需要复制的JS脚本", stackPanel, new Size(500, 600));
        if (!string.IsNullOrEmpty(result))
        {
            string? selectedFolderName = null;
            foreach (var child in ((Wpf.Ui.Controls.StackPanel)stackPanel.Content).Children)
            {
                if (child is RadioButton { IsChecked: true } radioButton && radioButton.Tag is string folderName)
                {
                    selectedFolderName = folderName;
                }
            }
            if (selectedFolderName == null)
            {
                return;
            }

            ScriptProject scriptProject = new ScriptProject(selectedFolderName);
            string jsCode = await scriptProject.LoadCode();

            var multilineTextBox = new TextBox
            {
                TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Text = jsCode,
                IsReadOnly = true
            };
            var p = new PromptDialog($"{scriptProject.Manifest.Name}\r\n{scriptProject.Manifest.ShortDescription}\r\n\r\n将覆盖现有的JavaScript，是否继续？", $"预览 - {scriptProject.FolderName}", multilineTextBox, null);
            p.Height = 600;
            p.MaxWidth = 800;
            p.ShowDialog();

            if (p.DialogResult != true)
            {
                return;
            }

            this.Config.AutoArtifactSalvageConfig.JavaScript = jsCode;
        }
    }

    [RelayCommand]
    private async Task OnSwitchGetGridIcons()
    {
        try
        {
            SwitchGetGridIconsEnabled = true;
            await new TaskRunner().RunSoloTaskAsync(new GetGridIconsTask(Config.GetGridIconsConfig.GridName, Config.GetGridIconsConfig.StarAsSuffix, Config.GetGridIconsConfig.MaxNumToGet));
        }
        finally
        {
            SwitchGetGridIconsEnabled = false;
        }
    }

    [RelayCommand]
    private void OnGoToGetGridIconsFolder()
    {
        var path = Global.Absolute(@"log\gridIcons\");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    private async Task OnGoToGetGridIconsUrlAsync()
    {
        await Launcher.LaunchUriAsync(new Uri("https://www.bettergi.com/dev/getGridIcons.html"));
    }

    [RelayCommand]
    private async Task OnSwitchGridIconsModelAccuracyTest()
    {
        try
        {
            SwitchGetGridIconsEnabled = true;
            await new TaskRunner().RunSoloTaskAsync(new GridIconsAccuracyTestTask(Config.GetGridIconsConfig.GridName, Config.GetGridIconsConfig.MaxNumToGet));
        }
        finally
        {
            SwitchGetGridIconsEnabled = false;
        }
    }

    /// <summary>启动当前选择目标的数量 OCR 对比任务。</summary>
    [RelayCommand]
    private async Task OnRunInventoryCountComparison()
    {
        try
        {
            SwitchGetGridIconsEnabled = true;
            await new TaskRunner().RunSoloTaskAsync(new InventoryCountComparisonTask(InventoryCountComparisonTarget));
        }
        finally
        {
            SwitchGetGridIconsEnabled = false;
        }
    }

    /// <summary>创建并打开数量 OCR 对比结果根目录。</summary>
    [RelayCommand]
    private void OnGoToInventoryCountComparisonFolder()
    {
        var path = Global.Absolute(@"log\InventoryCountComparison\");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        Process.Start("explorer.exe", path);
    }

    [RelayCommand]
    private async Task OnSwitchAutoRedeemCode()
    {
        var multilineTextBox = new TextBox
        {
            TextWrapping = TextWrapping.Wrap,
            AcceptsReturn = true,
            VerticalAlignment = VerticalAlignment.Stretch,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            PlaceholderText = "请在此输入兑换码，每行一条记录"
        };
        var p = new PromptDialog(
            "输入兑换码",
            "自动使用兑换码",
            multilineTextBox,
            null);
        p.Height = 500;
        p.ShowDialog();
        if (p.DialogResult == true && !string.IsNullOrWhiteSpace(multilineTextBox.Text))
        {
            char[] separators = ['\r', '\n'];
            var codes = multilineTextBox.Text.Split(separators, StringSplitOptions.RemoveEmptyEntries)

           .Select(code => code.Trim())
           .Where(code => !string.IsNullOrEmpty(code))
           .ToList();

            if (codes.Count == 0)
            {
                Toast.Warning("没有有效的兑换码");
                return;
            }

            SwitchAutoRedeemCodeEnabled = true;
            await new TaskRunner()
                .RunSoloTaskAsync(new UseRedemptionCodeTask(codes));
            SwitchAutoRedeemCodeEnabled = false;
        }
    }

    /// <summary>
    /// 重置兑换码每日检查状态（镜像自 FeedWindowViewModel.ResetCheckStatus）。
    /// 行为：清空 AutoRedeemCodeConfig.LastRedeemCodeCheckDates 字典，下次一条龙启动会重新触发检查。
    /// 与 FeedWindowViewModel.ResetCheckStatus 完全等价但不抽公共方法（避免跨 VM 耦合）。
    /// </summary>
    [RelayCommand]
    private void ResetRedeemCodeCheckStatus()
    {
        TaskContext.Instance().Config.AutoRedeemCodeConfig.LastRedeemCodeCheckDates.Clear();
        Toast.Success("已重置检查状态，下次一条龙将重新检查兑换码");
    }
}

/// <summary>
/// 内置线路 UI 视图模型
/// </summary>
public partial class BuiltinRouteViewModel : ObservableObject
{
    /// <summary>
    /// 文件夹名称
    /// </summary>
    [ObservableProperty]
    private string _folderName = "";

    /// <summary>
    /// 文件夹完整路径
    /// </summary>
    [ObservableProperty]
    private string _fullPath = "";

    /// <summary>
    /// 路线文件数量
    /// </summary>
    [ObservableProperty]
    private int _routeCount;

    /// <summary>
    /// 是否被选中
    /// </summary>
    [ObservableProperty]
    private bool _isSelected;

    /// <summary>
    /// 是否启用（手动路径非空时禁用）
    /// </summary>
    [ObservableProperty]
    private bool _isEnabled = true;
}
