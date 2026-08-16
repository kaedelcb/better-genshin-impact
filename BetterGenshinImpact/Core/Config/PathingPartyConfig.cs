using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoEat;
using BetterGenshinImpact.GameTask.AutoFight;
using BetterGenshinImpact.GameTask.AutoFightOfficial;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using Serilog.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using BetterGenshinImpact.GameTask.AutoTrackPath.Model;

namespace BetterGenshinImpact.Core.Config;

public enum RecoverTiming
{
    AnyWaypoint,
    OnlyTeleport,
    Never
}

/// <summary>
/// 从旧字段 OnlyInTeleportRecover 迁移到 RecoverTiming 枚举的共享方法
/// </summary>
internal static class RecoverTimingMigration
{
    public static RecoverTiming Migrate(bool onlyInTeleportRecover)
        => onlyInTeleportRecover ? RecoverTiming.OnlyTeleport : RecoverTiming.AnyWaypoint;
}

[Serializable]
public partial class PathingPartyConfig : ObservableObject, IJsonOnDeserialized
{
    // 配置是否启用，不启用会使用地图追踪内的条件配置
    [ObservableProperty]
    private bool _enabled = true;
    
    // 是否启用自动拾取
    [ObservableProperty]
    private bool _autoPickEnabled = true;
    // 切换到队伍的名称
    [ObservableProperty]
    private string _partyName = string.Empty;

    [JsonIgnore]
    public bool SkipPartySwitch { get; set; }
    
    // 切换队伍前是否前往须弥七天神像
    [ObservableProperty]
    private bool _isVisitStatueBeforeSwitchParty = false;
        
    // 主要行走追踪的角色编号
    [ObservableProperty]
    private string _mainAvatarIndex = string.Empty;

    // [盾角]使用元素战技的角色编号
    [ObservableProperty]
    private string _guardianAvatarIndex = string.Empty;

    // [盾角]使用元素战技的时间间隔(s)
    [ObservableProperty]
    private string _guardianElementalSkillSecondInterval = string.Empty;

    // [盾角]使用元素战技的方式 长按/短按
    [ObservableProperty]
    private bool _guardianElementalSkillLongPress = false;

    // // normal_attack 配置几号位
    // [ObservableProperty]
    // private string _normalAttackAvatarIndex = string.Empty;
    //
    // // elemental_skill 配置几号位
    // [ObservableProperty]
    // private string _elementalSkillAvatarIndex = string.Empty;

    // // hydro_collect 配置几号位
    // [ObservableProperty]
    // private string _hydroCollectAvatarIndex = string.Empty;
    //
    // // electro_collect 配置几号位
    // [ObservableProperty]
    // private string _electroCollectAvatarIndex = string.Empty;
    //
    // // anemo_collect 配置几号位
    // [ObservableProperty]
    // private string _anemoCollectAvatarIndex = string.Empty;

    [JsonIgnore]
    public List<string> AvatarIndexList { get; } = ["", "1", "2", "3", "4"];

    // 只在传送传送点时复活
    [ObservableProperty]
    private bool _onlyInTeleportRecover = false;

    // 低血量回复时机
    private RecoverTiming? _recoverTiming;

    public RecoverTiming RecoverTiming
    {
        get
        {
            if (_recoverTiming is null)
            {
                // 首次读取时从旧字段自动迁移
                _recoverTiming = RecoverTimingMigration.Migrate(_onlyInTeleportRecover);
            }
            return _recoverTiming.Value;
        }
        set => SetProperty(ref _recoverTiming, value);
    }

    //允许在jsScript脚本中使用此地图追踪配置
    [ObservableProperty]
    private bool _jsScriptUseEnabled = true;
    
    //允许在此调度器中（一般在JS脚本中）调用自动战斗任务时，采用此追踪配置里的战斗策略
    [ObservableProperty]
    private bool _soloTaskUseFightEnabled = true;
    
    //不在某时执行
    [ObservableProperty] 
    private string _skipDuring = "";
    
    // 使用小道具的间隔时间
    [ObservableProperty]
    private int _useGadgetIntervalMs = 0;

    // 启用进入剧情自动脱离
    [ObservableProperty]
    private bool _autoSkipEnabled = true;
    
    // 自动冲刺启用
    [ObservableProperty]
    private bool _autoRunEnabled = true;
    
    // 启用自动吃药功能
    [ObservableProperty]
    private bool _autoEatEnabled = false;
    
    // 地图追踪红血切人
    [ObservableProperty]
    private bool _redBloodSwitchOnly = false;

    /// <summary>
    /// 自动吃食物配置
    /// 供JS脚本使用
    /// </summary>
    [ObservableProperty]
    private AutoEatConfig _autoEatConfig = new();

    //在连续执行时是否隐藏
    [ObservableProperty]
    private bool _hideOnRepeat = false;
    
    //执行周期配置
    [ObservableProperty]
    private PathingPartyTaskCycleConfig _taskCycleConfig = new();
    
    //任务完成跳过执行配置
    [ObservableProperty]
    private TaskCompletionSkipRuleConfig _taskCompletionSkipRuleConfig = new();
    //优先执行其他配置组
    [ObservableProperty]
    private PreExecutionPriorityConfig _preExecutionPriorityConfig = new();

    //启用自动战斗配置
    [ObservableProperty]
    private bool _autoFightEnabled = true;

    [ObservableProperty]
    private AutoFightConfig _autoFightConfig = new();

    /// <summary>
    /// 公版自动战斗配置（official-autofight-parallel-engine spec，与茶包版 AutoFightConfig 并存，配置组作用域）
    /// </summary>
    [ObservableProperty]
    private AutoFightOfficialConfig _autoFightOfficialConfig = new();
    
    [ObservableProperty]
    private int _distance = 45;
    
    // 公版/新版赶路角色列表（UseNewHurrySystem == true）
    [JsonIgnore]
    public List<string> NewHurryOnAvatarList { get; } = ["","自动","玛薇卡","闲云","桑多涅","恰斯卡","流浪者","伊法","希诺宁","法尔伽","夜兰"];

    // 茶包版赶路角色列表（UseNewHurrySystem == false）
    [JsonIgnore]
    public List<string> TeapotHurryOnAvatarList { get; } = ["","自动","玛薇卡","瓦雷莎","希诺宁"];

    // 当前生效版本对应的角色列表（业务逻辑与 InitializeTravelMode 使用）
    [JsonIgnore]
    public List<string> HurryOnAvatarList => UseNewHurrySystem ? NewHurryOnAvatarList : TeapotHurryOnAvatarList;
    
    [JsonIgnore]
    public List<string> TravelModeList { get; } = ["精准靠近","连续赶路"];

    // 茶包版记忆的赶路角色（方案 A：两个版本各记各的，切换时互不影响）。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HurryOnAvatar))]
    private string _teapotHurryOnAvatar = "";

    // 公版/新版记忆的赶路角色（方案 A：两个版本各记各的，切换时互不影响）。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HurryOnAvatar))]
    private string _newHurryOnAvatar = "";

    // 当前生效的赶路角色：按版本返回对应记忆字段。
    // 只读计算属性，业务逻辑（PathExecutor / SkillBoostHelper）保持零改动地读取它。
    // 切换版本时通过 UseNewHurrySystem 的 NotifyPropertyChangedFor 触发本属性刷新，
    // 因此各版本各自的选择在来回切换时天然保留，不会互相覆盖或显示空白。
    [JsonIgnore]
    public string HurryOnAvatar => UseNewHurrySystem ? NewHurryOnAvatar : TeapotHurryOnAvatar;
    
    [ObservableProperty]
    private string _travelMode = "精准靠近";
    
    [ObservableProperty]
    private bool _mwkFlyEnabled = true;

    // 玛薇卡挑飞触发距离阈值：>=0（无上限），默认 75；0 表示不挑飞。
    // 默认值与改动前硬编码常量一致，保证向后兼容与零回归。
    [ObservableProperty]
    private int _mwkFlyJumpDistance = 75;

    // 负数钳制到 0（R4.2）；空/非数字由 WPF 绑定失败自动保留旧值（R4.1），不进入此方法。
    partial void OnMwkFlyJumpDistanceChanged(int value)
    {
        var clamped = MwkFlyJumpDecisions.Clamp(value);
        if (clamped != value)
        {
            MwkFlyJumpDistance = clamped;
        }
    }
    
    // 切换赶路系统版本会改变 HurryOnAvatarList（可选列表）与 HurryOnAvatar（当前生效角色，
    // 按版本返回对应记忆字段）。二者都需在切换时刷新，否则 ComboBox 不实时更新（旧 bug）。
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HurryOnAvatarList))]
    [NotifyPropertyChangedFor(nameof(HurryOnAvatar))]
    private bool _useNewHurrySystem = false;

    // 旧配置迁移：历史版本只存单一 hurryOnAvatar 字段。反序列化完成后（此时 UseNewHurrySystem
    // 已确定，与字段读取顺序无关），把旧值回填到当时激活版本对应的记忆字段，避免升级后丢选择。
    // 新配置不再写出 hurryOnAvatar（HurryOnAvatar 已 [JsonIgnore]），故本迁移只对旧配置生效一次。
    // public 属性 + public set（供 STJ 反序列化写入旧键）+ private get（序列化时无可访问 getter，
    // STJ 自动跳过，不再写出该键）。故意不用 [JsonInclude]：它对非 public 属性会在运行时抛异常。
    [JsonPropertyName("hurryOnAvatar")]
    public string? LegacyHurryOnAvatar { private get; set; }

    void IJsonOnDeserialized.OnDeserialized()
    {
        if (!string.IsNullOrEmpty(LegacyHurryOnAvatar))
        {
            // 仅当目标版本记忆字段为空（未被新格式覆盖）时才迁移，避免覆盖新写入的值。
            if (UseNewHurrySystem)
            {
                if (string.IsNullOrEmpty(NewHurryOnAvatar) && NewHurryOnAvatarList.Contains(LegacyHurryOnAvatar))
                {
                    NewHurryOnAvatar = LegacyHurryOnAvatar;
                }
            }
            else
            {
                if (string.IsNullOrEmpty(TeapotHurryOnAvatar) && TeapotHurryOnAvatarList.Contains(LegacyHurryOnAvatar))
                {
                    TeapotHurryOnAvatar = LegacyHurryOnAvatar;
                }
            }
            LegacyHurryOnAvatar = null;
        }
    }

    [ObservableProperty]
    private bool _switchToWalkEnabled = false;

    [ObservableProperty]
    private bool _mwkJumpFlyEnabled = true;

    /// <summary>
    /// 玛薇卡跳飞启用距离（米），必须大于 <see cref="Distance"/>，越界时自动使用 Distance+1 的值。
    /// </summary>
    [ObservableProperty]
    private int _mwkJumpFlyDistance = 75;

    partial void OnMwkJumpFlyDistanceChanged(int value)
    {
        if (value <= Distance)
        {
            MwkJumpFlyDistance = Distance + 1;
        }
    }

    [ObservableProperty]
    private double _mwkJumpFlyIntervalSeconds = 1.0;

    /// <summary>
    /// 玛薇卡在车上禁用冲刺。6命玛薇卡酌情勾选，节约夜魂值。
    /// </summary>
    [ObservableProperty]
    private bool _mwkDisableSprintEnabled = false;

    /// <summary>
    /// 跳飞前额外冲刺次数。6命玛薇卡可选，每次上车后前若干次跳飞改为冲刺跳飞，速度更快，夜魂值消耗更高，推荐3次。
    /// 0 表示不使用冲刺跳飞。
    /// </summary>
    [ObservableProperty]
    private int _mwkJumpFlySprintCount = 0;

    [ObservableProperty]
    private double _approachStopDistance = 25;

    [ObservableProperty]
    private string? _recoverAvatarIndex = null;
    
    [ObservableProperty]
    private bool _quicklySkip = false;
    
    [ObservableProperty]
    private int _combatScriptEndDelayMs = 900;
    
    [ObservableProperty]
    private bool _disableAutoFetchDispatch = false;
    
    public static OtherConfig OtherConfig { get; set; } = TaskContext.Instance().Config.OtherConfig;
    
    // 自动吃药次数记录
    private static  bool _isDisableAutoFetchDispatch = false;
    public static bool IsDisableAutoFetchDispatch
    {
        get =>  OtherConfig.AutoFetchDispatchAdventurersGuildCountry == "无" ? true : false;
        set => _isDisableAutoFetchDispatch = value;
    }
    
    public static PathingPartyConfig BuildDefault()
    {
        // 即便是不启用的情况下也设置默认值，减少后续使用的判断
        var pathingConditionConfig = TaskContext.Instance().Config.PathingConditionConfig;
        return new PathingPartyConfig
        {
            OnlyInTeleportRecover = pathingConditionConfig.OnlyInTeleportRecover,
            RecoverTiming = pathingConditionConfig.RecoverTiming,
            UseGadgetIntervalMs = pathingConditionConfig.UseGadgetIntervalMs,
            AutoEatEnabled = pathingConditionConfig.AutoEatEnabled,
            RedBloodSwitchOnly = pathingConditionConfig.RedBloodSwitchOnly,
        };
    }

    /// <summary>
    /// 纯函数：给定任务入口类型，判定经由该入口构造的 PathExecutor 是否应豁免"配置组地图追踪切队"。
    /// 地图追踪任务（Pathing）使用 PartyName 切队是正当用途，不豁免；
    /// JS 脚本任务与各独立任务（SoloTask）有自己的切队逻辑，应豁免。
    /// 无副作用、无外部依赖，便于属性测试。
    /// </summary>
    public static bool ShouldSkipPartySwitch(SoloTaskEntryKind entry)
    {
        return entry != SoloTaskEntryKind.Pathing;
    }

    /// <summary>
    /// 浅拷贝当前配置并将 SkipPartySwitch 置为 true，用于 JS / SoloTask 入口，
    /// 避免原地 mutate 被同一配置组多个分支共享的 PathingConfig 实例（防止污染后续 Pathing 任务）。
    /// 除 SkipPartySwitch 外所有字段与原实例保持一致（引用类型字段沿用同一引用，行为不变）。
    /// </summary>
    public PathingPartyConfig CloneForSoloTask()
    {
        var clone = (PathingPartyConfig)MemberwiseClone();
        clone.SkipPartySwitch = true;
        return clone;
    }
}

/// <summary>
/// 任务入口类型，用于判定是否应豁免配置组地图追踪切队。
/// </summary>
public enum SoloTaskEntryKind
{
    Pathing,            // 地图追踪任务：正当切队，不豁免
    Javascript,         // JS 脚本任务：豁免
    SoloTask            // 独立任务（锄地/好感等）：豁免
}
