using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoEat;
using BetterGenshinImpact.GameTask.AutoFight;
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

[Serializable]
public partial class PathingPartyConfig : ObservableObject
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
    
    [ObservableProperty]
    private int _distance = 45;

    /// <summary>
    /// 接近停车距离，到达此距离时触发接近逻辑（下车/切人/退出赶路状态）。
    /// 强制小于等于 <see cref="Distance"/>，越界时自动使用 Distance 的值。
    /// </summary>
    [ObservableProperty]
    private int _approachStopDistance = 25;
    
    [JsonIgnore]
    public List<string> HurryOnAvatarList { get; } = ["","自动","玛薇卡","闲云","桑多涅","恰斯卡","流浪者","伊法","希诺宁","瓦雷莎"];
    
    [JsonIgnore]
    public List<string> TravelModeList { get; } = ["精准靠近","连续赶路"];
    
    [ObservableProperty]
    private string _hurryOnAvatar = "";
    
    [ObservableProperty]
    private string _travelMode = "精准靠近";
    
    /// <summary>
    /// 接近节点时切人步行（火神同款），适用于除闲云外所有角色
    /// </summary>
    [ObservableProperty]
    private bool _switchToWalkEnabled = false;
    
    [ObservableProperty]
    private bool _mwkFlyEnabled = true;

    /// <summary>
    /// 玛薇卡跳飞开关
    /// </summary>
    [ObservableProperty]
    private bool _mwkJumpFlyEnabled = true;

    /// <summary>
    /// 玛薇卡跳飞间隔（秒）
    /// </summary>
    [ObservableProperty]
    private double _mwkJumpFlyIntervalSeconds = 1.4;
    
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
