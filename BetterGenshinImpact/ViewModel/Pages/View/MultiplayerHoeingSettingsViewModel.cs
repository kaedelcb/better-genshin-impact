#nullable enable

using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BetterGenshinImpact.ViewModel.Pages.View;

/// <summary>
/// 联机锄地配置弹窗的视图模型（multiplayer-hoeing-dialog-xaml-refactor）。
/// 持有约 35 个标量设置项（数值字段以 string 形态持有，绑定 ui:TextBox），
/// 构造时从配置组 settings 读初值（缺省回退 globalCfg），保存时由 WriteMultiplayerSettings 写回。
///
/// 纯逻辑：构造只接收 settings + globalCfg + groupOptions/groupDefault，不访问 TaskContext 单例、不依赖 WPF 控件，
/// 便于 property-based test 直接 new + 撒输入验证往返等价（保存来源切换的行为等价性守护）。
/// selectedBuiltinRoute / variantPreferences 不在本 VM（由 View code-behind 管理）。
/// </summary>
public partial class MultiplayerHoeingSettingsViewModel : ObservableObject
{
    // ===== A 联机角色 =====
    // hoeing-multiplayer-account-name-config：账户名称（复用单机 AutoHoeingConfig.AccountName，纯本地不同步）
    [ObservableProperty] private string _accountName = "默认账户";
    [ObservableProperty] private bool _multiplayerEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHost))]
    [NotifyPropertyChangedFor(nameof(ShowHostArea))]
    [NotifyPropertyChangedFor(nameof(ShowMemberArea))]
    private string _roleSelection = "房主（创建房间）";

    // ===== B 联机连接 =====
    [ObservableProperty] private string _coordinatorServerUrl = "";
    [ObservableProperty] private string _playerName = "";
    [ObservableProperty] private string _playerUid = "";
    [ObservableProperty] private string _pickupMode = "";

    // ===== 拾取参数（联机/单机共享 AutoHoeingConfig，弹窗配置）=====
    // 与主界面 NumberBox / AutoHoeingConfig 对齐为 int；单机由 solo 控件写同 key，联机由本 VM 写同 key。
    [ObservableProperty] private int _findFInterval = 100;
    [ObservableProperty] private int _pickupDelay = 50;
    [ObservableProperty] private int _rollingDelay = 32;
    [ObservableProperty] private int _scrollCycle = 1000;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreSwitchWeaponArea))]
    private string _multiplayerPartyName = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPreSwitchWeaponArea))]
    private string _multiplayerStartAvatarName = "";

    // ===== C 房间设置（房主本地）=====
    [ObservableProperty] private string _expectedPlayerCount = "";
    [ObservableProperty] private string _roomWhitelist = "";
    [ObservableProperty] private string _hostPartyTimeoutSeconds = "";
    [ObservableProperty] private int _partyTimeoutAction;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MultiWorldCountEnabled))]
    private bool _multiWorldEnabled;
    [ObservableProperty] private string _multiWorldCount = "";

    // ===== C 同步给成员区 =====
    [ObservableProperty] private string _syncPointMinDistance = "";
    [ObservableProperty] private string _startRouteIndex = "";
    [ObservableProperty] private string _routeFilterKeywords = "";
    [ObservableProperty] private string _fightTimeoutSeconds = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FastSyncParamsEnabled))]
    private bool _fastSyncPointEnabled;
    [ObservableProperty] private string _fastSyncPathingDistance = "";
    [ObservableProperty] private string _fastSyncTeleportLoadingDelayMs = "";

    // ===== 共享战斗配额结束同步（multiplayer-shared-fight-end-quorum-sync spec, 房主设置同步成员）=====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SharedFightEndQuorumParamsEnabled))]
    private bool _sharedFightEndQuorumEnabled;
    [ObservableProperty] private string _sharedFightEndQuorumRatio = "";

    // ===== 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop, 房主设置同步成员）=====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpCapDetectAllExpEnabled))]
    private bool _enableExpCapStop;
    // 检测模式：false=只检测精英经验，true=检测所有经验。依附主开关。
    [ObservableProperty] private bool _expCapDetectAllExp;

    // ===== 联机锄地守护自动重开（hoeing-multiplayer-guard-auto-restart, 房主设置同步成员）=====
    [ObservableProperty] private bool _hoeingGuardMode = true;
    [ObservableProperty] private string _guardUnexecutedRouteThreshold = "3";  // string 形态绑 ui:TextBox

    // ===== C 万叶聚物同步 =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KazuhaParamsEnabled))]
    private bool _enableKazuhaSync;
    [ObservableProperty] private string _kazuhaSyncWaitSeconds = "";
    [ObservableProperty] private string _kazuhaSyncTimeoutSeconds = "";
    [ObservableProperty] private string _kazuhaWaitSkillCdSeconds = "";
    [ObservableProperty] private string _kazuhaSecondApproachMaxSteps = "";
    // 万叶回点异常坐标重播种重识别修复：两个纯本地调试参数（string 形态绑 ui:TextBox）
    [ObservableProperty] private string _kazuhaReturnAbnormalCoordThreshold = "";
    [ObservableProperty] private string _kazuhaReturnReseedRetryCount = "";
    [ObservableProperty] private string _kazuhaReturnPreDistanceZeroRetryTimeoutMs = "";

    // ===== 调试（hoeing-multiplayer-lagging-member-catchup）：落后成员逐段追赶，纯本地、成员侧 =====
    [ObservableProperty] private bool _enableLaggingCatchUp;
    [ObservableProperty] private string _lagSegmentThreshold = "";

    // ===== 调试（hoeing-multiworld-host-restart-resume-round）：重开续跑，房主侧 =====
    [ObservableProperty] private bool _multiWorldResumeEnabled;

    // ===== 调试（hoeing-multiplayer-solo-debug-mode）：单人调试模式，纯本地 =====
    [ObservableProperty] private bool _soloDebugMode;

    // ===== 调试：自动启动联机助手，纯本地 =====
    [ObservableProperty] private bool _autoLaunchAssistant;

    // ===== 按周期吃食物（multiplayer-hoeing-auto-eat-food-by-period，纯本地）=====
    [ObservableProperty] private int _medicineFoodSlot1 = 1;
    [ObservableProperty] private int _medicineFoodSlot2 = 2;
    [ObservableProperty] private int _medicineFoodSlot3 = 3;
    [ObservableProperty] private int _medicineFoodSlot4 = 4;
    [ObservableProperty] private string _medicineFoodPeriod1 = "0";
    [ObservableProperty] private string _medicineFoodPeriod2 = "0";
    [ObservableProperty] private string _medicineFoodPeriod3 = "0";
    [ObservableProperty] private string _medicineFoodPeriod4 = "0";
    [ObservableProperty] private string _medicineFoodRouteKeywords1 = "";
    [ObservableProperty] private string _medicineFoodRouteKeywords2 = "";
    [ObservableProperty] private string _medicineFoodRouteKeywords3 = "";
    [ObservableProperty] private string _medicineFoodRouteKeywords4 = "";

    // ===== 线路重试模式（hoeing-multiplayer-route-retry-mode spec，纯本地）=====
    [ObservableProperty] private string _routeRetryModeKeywords = "";

    /// <summary>食物序号下拉可选项 1~4。</summary>
    public IReadOnlyList<int> MedicineSlotOptions { get; } = new[] { 1, 2, 3, 4 };

    // ===== D 成员区 =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetHostEnabled))]
    private string _joinModeSelection = "指定房主名称";
    [ObservableProperty] private string _targetHostName = "";
    [ObservableProperty] private string _memberPartyTimeoutSeconds = "";

    // ===== E 战斗策略 =====
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FightStrategySelectable))]
    private bool _multiplayerUseFixedFightStrategy = true;

    // ===== F 线路设置 =====
    [ObservableProperty] private bool _debugMode;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBuiltinOnlineMode))]
    [NotifyPropertyChangedFor(nameof(ShowManualRoute))]
    [NotifyPropertyChangedFor(nameof(ShowGroupIndex))]
    [NotifyPropertyChangedFor(nameof(BuiltinButtonsEnabled))]
    private string _routeModeSelection = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BuiltinButtonsEnabled))]
    private string _fixedDebugRoutePath = "";
    [ObservableProperty] private string _groupIndex = "";

    // ===== 派生联动属性（只读，等价现状 UpdateXxx）=====
    public bool IsHost => RoleSelection != "成员（加入房间）";
    public System.Windows.Visibility ShowHostArea => IsHost ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ShowMemberArea => IsHost ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    /// <summary>
    /// 开锄前换武器配置区可见性：仅当联机队伍名与联机起始角色名都非空时显示
    /// （hoeing-multiplayer-preswitch-weapon，因换武器执行依赖切队+切角色成功）。
    /// </summary>
    public System.Windows.Visibility ShowPreSwitchWeaponArea =>
        !string.IsNullOrWhiteSpace(MultiplayerPartyName) && !string.IsNullOrWhiteSpace(MultiplayerStartAvatarName)
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    public bool TargetHostEnabled => JoinModeSelection != "随机加入现有房间";
    public bool KazuhaParamsEnabled => EnableKazuhaSync;
    public bool FastSyncParamsEnabled => FastSyncPointEnabled;
    public bool SharedFightEndQuorumParamsEnabled => SharedFightEndQuorumEnabled;
    // 检测模式开关仅在"基于经验判断停止锄地"启用时可编辑（从属关系）。
    public bool ExpCapDetectAllExpEnabled => EnableExpCapStop;
    public bool MultiWorldCountEnabled => MultiWorldEnabled;
    public bool FightStrategySelectable => !MultiplayerUseFixedFightStrategy;
    public bool IsBuiltinOnlineMode => RouteModeDecisions.IsBuiltinOnline(RouteModeSelection);
    public System.Windows.Visibility ShowManualRoute => IsBuiltinOnlineMode ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public System.Windows.Visibility ShowGroupIndex => IsBuiltinOnlineMode ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
    public bool BuiltinButtonsEnabled => IsBuiltinOnlineMode && string.IsNullOrWhiteSpace(FixedDebugRoutePath);

    // ===== 静态下拉项源（供 XAML ItemsSource 绑定）=====
    public IReadOnlyList<string> RoleOptions { get; } = new[] { "房主（创建房间）", "成员（加入房间）" };
    public IReadOnlyList<string> JoinModeOptions { get; } = new[] { "指定房主名称", "随机加入现有房间" };
    public IReadOnlyList<string> PickupOptions { get; } = new[] { "模板匹配拾取狗粮和怪物材料", "模板匹配仅拾取狗粮", "BGI原版拾取", "不拾取" };
    public IReadOnlyList<string> TimeoutActionOptions { get; } = new[] { "超时后结束任务", "超时后以现有人数开始" };
    public IReadOnlyList<string> RouteModeOptions { get; } = new[] { RouteModeDecisions.BuiltinOnline, RouteModeDecisions.SoloDebug };
    public IReadOnlyList<string> GroupOptions { get; }

    /// <summary>
    /// 从配置组 settings 读初值（缺省回退 globalCfg），等价现状 ShowHoeingSettingsDialog 的 GetStr/GetBool/GetInt 语义。
    /// 不访问 TaskContext 单例、不依赖 WPF 控件，便于 PBT。
    /// </summary>
    public MultiplayerHoeingSettingsViewModel(
        Dictionary<string, object?> settings,
        AutoHoeingConfig g,
        IReadOnlyList<string> groupOptions,
        string groupDefault)
    {
        string GetStr(string k, string fb) => settings.TryGetValue(k, out var v) ? v?.ToString() ?? fb : fb;
        bool GetBool(string k, bool fb) => settings.TryGetValue(k, out var v) ? v is true or "True" or "true" : fb;
        int GetInt(string k, int fb) => settings.TryGetValue(k, out var v) && int.TryParse(v?.ToString(), out var n) ? n : fb;

        GroupOptions = groupOptions;

        // hoeing-multiplayer-account-name-config：账户名初值，缺失回退全局配置的 AccountName（R1.3）
        _accountName = GetStr("accountName", g.AccountName);
        // 新建独立任务默认开启联机锄地：settings 缺键时回退 true。
        // 不跟随磁盘存量全局值 g.MultiplayerEnabled——老 config.json 里已持久化 false，
        // 会让"默认开启"对存量安装失效。已保存过该键的任务仍用自身存值，不受影响。
        _multiplayerEnabled = GetBool("multiplayerEnabled", true);
        _roleSelection = GetStr("multiplayerRole", "host") == "member" ? "成员（加入房间）" : "房主（创建房间）";

        _coordinatorServerUrl = GetStr("coordinatorServerUrl", g.CoordinatorServerUrl);
        _playerName = GetStr("playerName", g.PlayerName);
        _playerUid = GetStr("playerUid", g.PlayerUid);
        _pickupMode = GetStr("pickupMode", g.PickupMode);
        // 拾取参数（联机/单机共享 AutoHoeingConfig，缺省回退全局配置）
        _findFInterval = GetInt("findFInterval", g.FindFInterval);
        _pickupDelay = GetInt("pickupDelay", g.PickupDelay);
        _rollingDelay = GetInt("rollingDelay", g.RollingDelay);
        _scrollCycle = GetInt("scrollCycle", g.ScrollCycle);
        _multiplayerPartyName = GetStr("multiplayerPartyName", g.MultiplayerPartyName);
        _multiplayerStartAvatarName = GetStr("multiplayerStartAvatarName", g.MultiplayerStartAvatarName);

        _expectedPlayerCount = GetInt("expectedPlayerCount", g.ExpectedPlayerCount).ToString();
        _roomWhitelist = GetStr("roomWhitelist", g.RoomWhitelist);
        _hostPartyTimeoutSeconds = GetInt("partyTimeoutSeconds", g.PartyTimeoutSeconds).ToString();
        _memberPartyTimeoutSeconds = GetInt("partyTimeoutSeconds", g.PartyTimeoutSeconds).ToString();
        _partyTimeoutAction = GetInt("partyTimeoutAction", g.PartyTimeoutAction);
        _multiWorldEnabled = GetBool("multiWorldEnabled", g.MultiWorldEnabled);
        _multiWorldCount = GetInt("multiWorldCount", g.MultiWorldCount).ToString();

        // 字符串形态读入 double（等价现状 GetStr(key, double.ToString())），不得改成 GetDouble
        _syncPointMinDistance = GetStr("syncPointMinDistance", g.SyncPointMinDistance.ToString());
        _startRouteIndex = GetInt("startRouteIndex", g.StartRouteIndex).ToString();
        _routeFilterKeywords = GetStr("routeFilterKeywords", g.RouteFilterKeywords);
        _fightTimeoutSeconds = GetInt("fightTimeoutSeconds", g.FightTimeoutSeconds).ToString();

        _fastSyncPointEnabled = GetBool("fastSyncPointEnabled", g.FastSyncPointEnabled);
        _fastSyncPathingDistance = GetStr("fastSyncPathingDistance", g.FastSyncPathingDistance.ToString());
        _fastSyncTeleportLoadingDelayMs = GetInt("fastSyncTeleportLoadingDelayMs", g.FastSyncTeleportLoadingDelayMs).ToString();

        _sharedFightEndQuorumEnabled = GetBool("sharedFightEndQuorumEnabled", g.SharedFightEndQuorumEnabled);
        _sharedFightEndQuorumRatio = GetStr("sharedFightEndQuorumRatio", g.SharedFightEndQuorumRatio.ToString());

        // 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop）
        _enableExpCapStop = GetBool("enableExpCapStop", g.EnableExpCapStop);
        _expCapDetectAllExp = GetBool("expCapDetectAllExp", g.ExpCapDetectAllExp);

        // 联机锄地守护自动重开（hoeing-multiplayer-guard-auto-restart）
        _hoeingGuardMode = GetBool("hoeingGuardMode", g.HoeingGuardMode);
        _guardUnexecutedRouteThreshold = GetInt("guardUnexecutedRouteThreshold", g.GuardUnexecutedRouteThreshold).ToString();

        _enableKazuhaSync = GetBool("enableKazuhaSync", g.EnableKazuhaSync);
        _kazuhaSyncWaitSeconds = GetInt("kazuhaSyncWaitSeconds", g.KazuhaSyncWaitSeconds).ToString();
        _kazuhaSyncTimeoutSeconds = GetInt("kazuhaSyncTimeoutSeconds", g.KazuhaSyncTimeoutSeconds).ToString();
        _kazuhaWaitSkillCdSeconds = GetInt("kazuhaWaitSkillCdSeconds", g.KazuhaWaitSkillCdSeconds).ToString();
        _kazuhaSecondApproachMaxSteps = GetInt("kazuhaSecondApproachMaxSteps", g.KazuhaSecondApproachMaxSteps).ToString();
        // 阈值是 double，沿用本 VM 读 double 的惯例（GetStr + g.Xxx.ToString()，见 syncPointMinDistance），不新增 GetDouble
        _kazuhaReturnAbnormalCoordThreshold = GetStr("kazuhaReturnAbnormalCoordThreshold", g.KazuhaReturnAbnormalCoordThreshold.ToString());
        _kazuhaReturnReseedRetryCount = GetInt("kazuhaReturnReseedRetryCount", g.KazuhaReturnReseedRetryCount).ToString();
        _kazuhaReturnPreDistanceZeroRetryTimeoutMs = GetInt("kazuhaReturnPreDistanceZeroRetryTimeoutMs", g.KazuhaReturnPreDistanceZeroRetryTimeoutMs).ToString();

        // hoeing-multiplayer-lagging-member-catchup：落后追赶调试参数（纯本地）
        _enableLaggingCatchUp = GetBool("enableLaggingCatchUp", g.EnableLaggingCatchUp);
        _lagSegmentThreshold = GetInt("lagSegmentThreshold", g.LagSegmentThreshold).ToString();

        // hoeing-multiworld-host-restart-resume-round：重开续跑开关（房主本地，默认开）
        _multiWorldResumeEnabled = GetBool("multiWorldResumeEnabled", g.MultiWorldResumeEnabled);

        // hoeing-multiplayer-solo-debug-mode：单人调试模式开关（纯本地，默认关）
        _soloDebugMode = GetBool("soloDebugMode", g.SoloDebugMode);
        _autoLaunchAssistant = GetBool("autoLaunchAssistant", g.AutoLaunchAssistant);

        _joinModeSelection = GetStr("memberJoinMode", "byHostName") == "random" ? "随机加入现有房间" : "指定房主名称";
        _targetHostName = GetStr("targetHostName", g.TargetHostName);

        _multiplayerUseFixedFightStrategy = GetBool("multiplayerUseFixedFightStrategy", g.MultiplayerUseFixedFightStrategy);

        _debugMode = GetBool("debugMode", g.DebugMode);
        _routeModeSelection = RouteModeDecisions.MapUseFixedToRouteMode(GetBool("useFixedDebugRoutes", g.UseFixedDebugRoutes));
        _fixedDebugRoutePath = GetStr("fixedDebugRoutePath", g.FixedDebugRoutePath);
        _groupIndex = RouteModeDecisions.ResolveSelectedOrDefault(groupOptions, GetStr("groupIndex", groupDefault), groupDefault);

        // multiplayer-hoeing-auto-eat-food-by-period：按周期吃食物 4 行初值（纯本地，缺省回退全局 AutoHoeingConfig）
        _medicineFoodSlot1 = GetInt("medicineFoodSlot1", g.MedicineFoodSlot1);
        _medicineFoodSlot2 = GetInt("medicineFoodSlot2", g.MedicineFoodSlot2);
        _medicineFoodSlot3 = GetInt("medicineFoodSlot3", g.MedicineFoodSlot3);
        _medicineFoodSlot4 = GetInt("medicineFoodSlot4", g.MedicineFoodSlot4);
        _medicineFoodPeriod1 = GetInt("medicineFoodPeriod1", g.MedicineFoodPeriod1).ToString();
        _medicineFoodPeriod2 = GetInt("medicineFoodPeriod2", g.MedicineFoodPeriod2).ToString();
        _medicineFoodPeriod3 = GetInt("medicineFoodPeriod3", g.MedicineFoodPeriod3).ToString();
        _medicineFoodPeriod4 = GetInt("medicineFoodPeriod4", g.MedicineFoodPeriod4).ToString();
        _medicineFoodRouteKeywords1 = GetStr("medicineFoodRouteKeywords1", g.MedicineFoodRouteKeywords1);
        _medicineFoodRouteKeywords2 = GetStr("medicineFoodRouteKeywords2", g.MedicineFoodRouteKeywords2);
        _medicineFoodRouteKeywords3 = GetStr("medicineFoodRouteKeywords3", g.MedicineFoodRouteKeywords3);
        _medicineFoodRouteKeywords4 = GetStr("medicineFoodRouteKeywords4", g.MedicineFoodRouteKeywords4);
        // 线路重试模式（hoeing-multiplayer-route-retry-mode spec，纯本地）
        _routeRetryModeKeywords = GetStr("routeRetryModeKeywords", g.RouteRetryModeKeywords);
    }

    /// <summary>
    /// 联机模式保存：按现状解析语义逐键写回 settings（TryParse 失败的键不写入）。
    /// 不碰 selectedBuiltinRoute / variantPreferences（View code-behind 职责），保证纯逻辑可 PBT。
    /// </summary>
    public void WriteMultiplayerSettings(Dictionary<string, object?> settings)
    {
        // hoeing-multiplayer-account-name-config：写回 accountName 键（复用单机同一配置组通道，R2）
        settings["accountName"] = AccountName;
        settings["multiplayerRole"] = RoleSelection == "成员（加入房间）" ? "member" : "host";
        settings["memberJoinMode"] = JoinModeSelection == "随机加入现有房间" ? "random" : "byHostName";
        settings["targetHostName"] = TargetHostName;
        settings["coordinatorServerUrl"] = CoordinatorServerUrl;
        settings["playerName"] = PlayerName;
        settings["playerUid"] = PlayerUid;
        settings["multiplayerPartyName"] = MultiplayerPartyName;
        settings["multiplayerStartAvatarName"] = MultiplayerStartAvatarName;

        var timeoutText = IsHost ? HostPartyTimeoutSeconds : MemberPartyTimeoutSeconds;
        if (int.TryParse(timeoutText, out var pt)) settings["partyTimeoutSeconds"] = pt;
        settings["partyTimeoutAction"] = PartyTimeoutAction;
        if (int.TryParse(ExpectedPlayerCount, out var ec)) settings["expectedPlayerCount"] = ec;
        settings["roomWhitelist"] = RoomWhitelist;

        if (double.TryParse(SyncPointMinDistance, out var spd)) settings["syncPointMinDistance"] = spd;
        if (int.TryParse(StartRouteIndex, out var sri)) settings["startRouteIndex"] = sri;
        settings["routeFilterKeywords"] = RouteFilterKeywords;
        settings["enableKazuhaSync"] = EnableKazuhaSync;
        settings["multiplayerUseFixedFightStrategy"] = MultiplayerUseFixedFightStrategy;
        if (int.TryParse(KazuhaSyncWaitSeconds, out var ksw)) settings["kazuhaSyncWaitSeconds"] = ksw;
        if (int.TryParse(KazuhaSyncTimeoutSeconds, out var kst)) settings["kazuhaSyncTimeoutSeconds"] = kst;
        if (int.TryParse(KazuhaWaitSkillCdSeconds, out var kwc)) settings["kazuhaWaitSkillCdSeconds"] = kwc;
        if (int.TryParse(KazuhaSecondApproachMaxSteps, out var ksam)) settings["kazuhaSecondApproachMaxSteps"] = ksam;
        if (double.TryParse(KazuhaReturnAbnormalCoordThreshold, out var krt)) settings["kazuhaReturnAbnormalCoordThreshold"] = krt;
        if (int.TryParse(KazuhaReturnReseedRetryCount, out var krc)) settings["kazuhaReturnReseedRetryCount"] = krc;
        if (int.TryParse(KazuhaReturnPreDistanceZeroRetryTimeoutMs, out var kpz)) settings["kazuhaReturnPreDistanceZeroRetryTimeoutMs"] = kpz;

        // hoeing-multiplayer-lagging-member-catchup：落后追赶调试参数（纯本地）
        settings["enableLaggingCatchUp"] = EnableLaggingCatchUp;
        if (int.TryParse(LagSegmentThreshold, out var lst)) settings["lagSegmentThreshold"] = lst;

        // hoeing-multiworld-host-restart-resume-round：重开续跑开关
        settings["multiWorldResumeEnabled"] = MultiWorldResumeEnabled;
        // hoeing-multiplayer-solo-debug-mode：单人调试模式开关（纯本地）
        settings["soloDebugMode"] = SoloDebugMode;
        // 自动启动联机助手（纯本地）
        settings["autoLaunchAssistant"] = AutoLaunchAssistant;
        if (int.TryParse(FightTimeoutSeconds, out var fts)) settings["fightTimeoutSeconds"] = fts;

        settings["fastSyncPointEnabled"] = FastSyncPointEnabled;
        if (double.TryParse(FastSyncPathingDistance, out var fspd)) settings["fastSyncPathingDistance"] = fspd;
        if (int.TryParse(FastSyncTeleportLoadingDelayMs, out var fstd)) settings["fastSyncTeleportLoadingDelayMs"] = fstd;

        settings["sharedFightEndQuorumEnabled"] = SharedFightEndQuorumEnabled;
        if (double.TryParse(SharedFightEndQuorumRatio, out var sfeqr)) settings["sharedFightEndQuorumRatio"] = sfeqr;

        // 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop）
        settings["enableExpCapStop"] = EnableExpCapStop;
        settings["expCapDetectAllExp"] = ExpCapDetectAllExp;

        // 联机锄地守护自动重开（hoeing-multiplayer-guard-auto-restart）：阈值 TryParse 失败不写键，回退默认+ClampThreshold
        settings["hoeingGuardMode"] = HoeingGuardMode;
        if (int.TryParse(GuardUnexecutedRouteThreshold, out var gurt)) settings["guardUnexecutedRouteThreshold"] = gurt;

        settings["debugMode"] = DebugMode;
        settings["useFixedDebugRoutes"] = RouteModeDecisions.MapRouteModeToUseFixed(RouteModeSelection);
        settings["fixedDebugRoutePath"] = FixedDebugRoutePath;
        settings["multiWorldEnabled"] = MultiWorldEnabled;
        if (int.TryParse(MultiWorldCount, out var mwc)) settings["multiWorldCount"] = mwc;
        settings["pickupMode"] = PickupMode;
        // 拾取参数（联机/单机共享，直接写 int）
        settings["findFInterval"] = FindFInterval;
        settings["pickupDelay"] = PickupDelay;
        settings["rollingDelay"] = RollingDelay;
        settings["scrollCycle"] = ScrollCycle;
        settings["groupIndex"] = GroupIndex;

        // 按周期吃食物（multiplayer-hoeing-auto-eat-food-by-period）：序号直接写，周期 TryParse 失败的行不写
        settings["medicineFoodSlot1"] = MedicineFoodSlot1;
        settings["medicineFoodSlot2"] = MedicineFoodSlot2;
        settings["medicineFoodSlot3"] = MedicineFoodSlot3;
        settings["medicineFoodSlot4"] = MedicineFoodSlot4;
        if (int.TryParse(MedicineFoodPeriod1, out var __mfp1)) settings["medicineFoodPeriod1"] = __mfp1;
        if (int.TryParse(MedicineFoodPeriod2, out var __mfp2)) settings["medicineFoodPeriod2"] = __mfp2;
        if (int.TryParse(MedicineFoodPeriod3, out var __mfp3)) settings["medicineFoodPeriod3"] = __mfp3;
        if (int.TryParse(MedicineFoodPeriod4, out var __mfp4)) settings["medicineFoodPeriod4"] = __mfp4;
        settings["medicineFoodRouteKeywords1"] = MedicineFoodRouteKeywords1;
        settings["medicineFoodRouteKeywords2"] = MedicineFoodRouteKeywords2;
        settings["medicineFoodRouteKeywords3"] = MedicineFoodRouteKeywords3;
        settings["medicineFoodRouteKeywords4"] = MedicineFoodRouteKeywords4;
        // 线路重试模式（hoeing-multiplayer-route-retry-mode spec，纯本地）
        settings["routeRetryModeKeywords"] = RouteRetryModeKeywords;
    }
}

