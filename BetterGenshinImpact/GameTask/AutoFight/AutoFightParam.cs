using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.Model;
using BetterGenshinImpact.GameTask.Model;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightParam : BaseTaskParam<AutoFightTask>
{
    public class FightFinishDetectConfig
    {
        public string BattleEndProgressBarColor { get; set; } = "";

        public string BattleEndProgressBarColorTolerance { get; set; } = "";
        public bool FastCheckEnabled = false;
        public string FastCheckParams = "";
        public string CheckEndDelay = "";
        public string BeforeDetectDelay = "";
        public bool RotateFindEnemyEnabled = false;
        public int GoDistance { get; set; } = 500;
        public bool RotationMode { get; set; } = true;
        public bool EndModel { get; set; } = true;
    
        public double FastCheckDelay { get; set; } = 0.1;

        public bool ReturnToFightPointEnabled { get; set; } = false;
        public int ReturnToFightPointIntervalMs { get; set; } = 1000;
        public double ReturnToFightPointTriggerDistance { get; set; } = 15;
        public double ReturnToFightPointStopDistance { get; set; } = 10;
        public bool ReturnToFightPointTimeTriggerEnabled { get; set; } = false;
        public int ReturnToFightPointTimeTriggerSeconds { get; set; } = 5;
        
        public int FightWaitNotEndTime { get; set; } = 0;
        
        public bool PaimonEndModel { get; set; } = false;
        
        public bool DoubleEndEnbled { get; set; } = false;
        
        public int DoubleEndDelay { get; set; } = 750;
    }

    public AutoFightParam(string path, AutoFightConfig autoFightConfig) : base(null, null)
    {
        CombatStrategyPath = path;
        Timeout = autoFightConfig.Timeout;
        FightFinishDetectEnabled = autoFightConfig.FightFinishDetectEnabled;
        PickDropsAfterFightEnabled = autoFightConfig.PickDropsAfterFightEnabled;
        PickDropsAfterFightSeconds = autoFightConfig.PickDropsAfterFightSeconds;
        KazuhaPickupEnabled = autoFightConfig.KazuhaPickupEnabled;
        ActionSchedulerByCd = autoFightConfig.ActionSchedulerByCd;
        MavuikaMotorcycleCheckEnabled = autoFightConfig.MavuikaMotorcycleCheckEnabled;
        ArlecchinoBurstLowHpGateEnabled = autoFightConfig.ArlecchinoBurstLowHpGateEnabled;
        ArlecchinoAutoEnabled = autoFightConfig.ArlecchinoAutoEnabled;
        QiKong = autoFightConfig.QiKong;

        FinishDetectConfig.FastCheckEnabled = autoFightConfig.FinishDetectConfig.FastCheckEnabled;
        FinishDetectConfig.FastCheckParams = autoFightConfig.FinishDetectConfig.FastCheckParams;
        FinishDetectConfig.CheckEndDelay = autoFightConfig.FinishDetectConfig.CheckEndDelay;
        FinishDetectConfig.BeforeDetectDelay = autoFightConfig.FinishDetectConfig.BeforeDetectDelay;
        FinishDetectConfig.RotateFindEnemyEnabled = autoFightConfig.FinishDetectConfig.RotateFindEnemyEnabled;
        FinishDetectConfig.GoDistance = autoFightConfig.FinishDetectConfig.GoDistance;
        FinishDetectConfig.RotationMode = autoFightConfig.FinishDetectConfig.RotationMode;
        FinishDetectConfig.EndModel = autoFightConfig.FinishDetectConfig.EndModel;
        FinishDetectConfig.FastCheckDelay = autoFightConfig.FinishDetectConfig.FastCheckDelay;
        FinishDetectConfig.ReturnToFightPointEnabled = autoFightConfig.FinishDetectConfig.ReturnToFightPointEnabled;
        FinishDetectConfig.ReturnToFightPointIntervalMs = autoFightConfig.FinishDetectConfig.ReturnToFightPointIntervalMs;
        FinishDetectConfig.ReturnToFightPointTriggerDistance = autoFightConfig.FinishDetectConfig.ReturnToFightPointTriggerDistance;
        FinishDetectConfig.ReturnToFightPointStopDistance = autoFightConfig.FinishDetectConfig.ReturnToFightPointStopDistance;
        FinishDetectConfig.ReturnToFightPointTimeTriggerEnabled = autoFightConfig.FinishDetectConfig.ReturnToFightPointTimeTriggerEnabled;
        FinishDetectConfig.ReturnToFightPointTimeTriggerSeconds = autoFightConfig.FinishDetectConfig.ReturnToFightPointTimeTriggerSeconds;
        FinishDetectConfig.FightWaitNotEndTime = autoFightConfig.FinishDetectConfig.FightWaitNotEndTime;
        FinishDetectConfig.PaimonEndModel = autoFightConfig.FinishDetectConfig.PaimonEndModel;
        FinishDetectConfig.DoubleEndEnbled = autoFightConfig.FinishDetectConfig.DoubleEndEnbled;
        FinishDetectConfig.DoubleEndDelay = autoFightConfig.FinishDetectConfig.DoubleEndDelay;

        KazuhaPartyName = autoFightConfig.KazuhaPartyName;
        OnlyPickEliteDropsMode = autoFightConfig.OnlyPickEliteDropsMode;
        BattleThresholdForLoot = autoFightConfig.BattleThresholdForLoot ?? BattleThresholdForLoot;
        //下面参数固定，只取自动战斗里面的
        FinishDetectConfig.BattleEndProgressBarColor = TaskContext.Instance().Config.AutoFightConfig.FinishDetectConfig.BattleEndProgressBarColor;
        FinishDetectConfig.BattleEndProgressBarColorTolerance = TaskContext.Instance().Config.AutoFightConfig.FinishDetectConfig.BattleEndProgressBarColorTolerance;

        GuardianAvatar = autoFightConfig.GuardianAvatar;
        GuardianCombatSkip = autoFightConfig.GuardianCombatSkip;
        GuardianAvatarHold = autoFightConfig.GuardianAvatarHold;
        CountryName = autoFightConfig.CountryName;
        
        BurstEnabled = autoFightConfig.BurstEnabled;
        ExpKazuhaPickup = autoFightConfig.ExpKazuhaPickup;
        IsFirstCheck = autoFightConfig.FinishDetectConfig.IsFirstCheck;
        RotaryFactor = autoFightConfig.FinishDetectConfig.RotaryFactor;
        SwimmingEnabled = autoFightConfig.SwimmingEnabled;
        TakeMedicineEnabled = autoFightConfig.TakeMedicineEnabled;
        MedicineInterval = autoFightConfig.MedicineInterval;
        CheckInterval = autoFightConfig.CheckInterval;
        RecoverMaxCount = autoFightConfig.RecoverMaxCount;
        EndBloodCheackEnabled = autoFightConfig.EndBloodCheackEnabled;
        CheckBeforeBurst = autoFightConfig.FinishDetectConfig.CheckBeforeBurst;
        AutoCombatEq = autoFightConfig.AutoCombatEq;
        UseEqList = autoFightConfig.UseEqList;
        QinDoublePickUp = autoFightConfig.QinDoublePickUp;
        UseSkillList = autoFightConfig.UseSkillList;
        QRecoverAvatar = autoFightConfig.QRecoverAvatar;
        KazuhaTime = autoFightConfig.KazuhaTime;
    }

    public FightFinishDetectConfig FinishDetectConfig { get; set; } = new();

    public string CombatStrategyPath { get; set; }

    /// <summary>
    /// 根据策略名称解析完整路径（支持 .txt 和 .json）
    /// 优先返回存在的文件，都不存在时返回 .txt 路径
    /// </summary>
    public static string ResolveStrategyPath(string strategyName)
    {
        if ("根据队伍自动选择".Equals(strategyName) || string.IsNullOrEmpty(strategyName))
        {
            return Global.Absolute(@"User\AutoFight\");
        }
        var txtPath = Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
        if (System.IO.File.Exists(txtPath))
            return txtPath;
        var jsonPath = Global.Absolute(@"User\AutoFight\" + strategyName + ".json");
        if (System.IO.File.Exists(jsonPath))
            return jsonPath;
        return txtPath;
    }

    public bool FightFinishDetectEnabled { get; set; } = false;
    public bool PickDropsAfterFightEnabled { get; set; } = false;
    public int PickDropsAfterFightSeconds { get; set; } = 15;
    public int BattleThresholdForLoot { get; set; } = -1;
    public int Timeout { get; set; } = 120;

    // 由当前地图追踪战斗点临时注入；null 表示不启动经验/摩拉奖励结束检测。
    internal RewardEndDetectionConfig? RewardEndDetection { get; set; }

    public bool KazuhaPickupEnabled = true;
    public string ActionSchedulerByCd = "";

    /// <summary>
    /// 玛薇卡摩托状态检测开关（透传自配置组 AutoFightConfig）。
    /// 位置2 (AutoFightTask.cs 非重击分支) 通过 _taskParam 读取本字段决定是否执行摩托检测。默认 false。
    /// </summary>
    public bool MavuikaMotorcycleCheckEnabled { get; set; } = false;

    /// <summary>
    /// 阿蕾奇诺红血才放Q 门控开关（透传自配置组 / 全局 AutoFightConfig）。
    /// AutoFightTask 在战斗启动时把本值注入到每个 Avatar 实例，使 KeyPress / UseBurst 读到正确实例的开关。默认 false。
    /// </summary>
    public bool ArlecchinoBurstLowHpGateEnabled { get; set; } = false;
    public bool ArlecchinoAutoEnabled { get; set; } = false;
    public int QiKong { get; set; } = 0;
    public string KazuhaPartyName;
    public string OnlyPickEliteDropsMode = "";
    public string GuardianAvatar { get; set; } = string.Empty;
    public bool GuardianCombatSkip { get; set; } = false;
    public bool GuardianAvatarHold = false;
    public string?[] CountryName = ["自动"];
    public bool BurstEnabled { get; set; } = false;
    public bool ExpKazuhaPickup  { get; set; } = false;
    
    public bool QinDoublePickUp { get; set; } = false;
    
    public bool IsFirstCheck { get; set; } = true;
    
    public int RotaryFactor { get; set; } = 10;
    
    public static bool SwimmingEnabled  { get; set; } = false;
    
    public bool TakeMedicineEnabled { get; set; } = false;
    
    public int MedicineInterval { get; set; } = 1500;
    
    public int CheckInterval { get; set; } =  100;

    /// <summary>
    /// 联机锄地 + 万叶玩家专用：开启战斗中"持续回点"模式。
    /// 设为 true 后 <see cref="AutoFightSeek.SeekAndFightAsync"/> 内部从
    /// "retryDis 单次入参快照 + Task.Run + Wait(2000) + Delay(5000) 节流" 切换为
    /// "主循环内每轮实时距离 + await MoveTo(isPoint: false) + 上次回点完成时间最小间隔节流"。
    /// 默认 false 保持原行为完全等价；单机所有现存调用点不感知本次变更。
    /// 字段不写入 AutoFightConfig 持久化层，亦不暴露 UI——仅在 AutoFightHandler
    /// 判定"联机锄地 + 当前为万叶玩家"时显式 set true。
    /// 详见 .kiro/specs/multiplayer-kazuha-pre-cast-positioning/design.md §3.1。
    /// </summary>
    public bool KazuhaContinuousReturn { get; set; } = false;
    
    public int RecoverMaxCount { get; set; } =  5;
    
    public bool EndBloodCheackEnabled { get; set; } = false;
    
    public bool CheckBeforeBurst { get; set; } = false;
    
    public bool AutoCombatEq { get; set; } = false;
    
    public string UseEqList { get; set; } = "1,2,3,4";
    
    public string UseSkillList { get; set; } = "1,2,3,4";
    
    public bool QRecoverAvatar { get; set; } = false;
    
    public int KazuhaTime { get; set; } = 1500;
    
    public AutoFightParam(string? strategyName = null) : base(null, null)
    {
        SetCombatStrategyPath(strategyName);
        SetDefault();
    }

    /// <summary>  
    /// 设置战斗策略路径
    /// </summary>  
    /// <param name="strategyName">策略名称</param>  
    public void SetCombatStrategyPath(string? strategyName = null)
    {
        if (string.IsNullOrEmpty(strategyName))
        {
            strategyName = TaskContext.Instance().Config.AutoFightConfig.StrategyName;
        }

        CombatStrategyPath = ResolveStrategyPath(strategyName);
    }

    public void SetDefault()
    {
        var autoFightConfig = TaskContext.Instance().Config.AutoFightConfig;
        Timeout = autoFightConfig.Timeout;
        FightFinishDetectEnabled = autoFightConfig.FightFinishDetectEnabled;
        PickDropsAfterFightEnabled = autoFightConfig.PickDropsAfterFightEnabled;
        PickDropsAfterFightSeconds = autoFightConfig.PickDropsAfterFightSeconds;
        KazuhaPickupEnabled = autoFightConfig.KazuhaPickupEnabled;
        ActionSchedulerByCd = autoFightConfig.ActionSchedulerByCd;
        MavuikaMotorcycleCheckEnabled = autoFightConfig.MavuikaMotorcycleCheckEnabled;
        ArlecchinoBurstLowHpGateEnabled = autoFightConfig.ArlecchinoBurstLowHpGateEnabled;
        ArlecchinoAutoEnabled = autoFightConfig.ArlecchinoAutoEnabled;
        QiKong = autoFightConfig.QiKong;

        FinishDetectConfig.FastCheckEnabled = autoFightConfig.FinishDetectConfig.FastCheckEnabled;
        FinishDetectConfig.FastCheckParams = autoFightConfig.FinishDetectConfig.FastCheckParams;
        FinishDetectConfig.CheckEndDelay = autoFightConfig.FinishDetectConfig.CheckEndDelay;
        FinishDetectConfig.BeforeDetectDelay = autoFightConfig.FinishDetectConfig.BeforeDetectDelay;
        FinishDetectConfig.RotateFindEnemyEnabled = autoFightConfig.FinishDetectConfig.RotateFindEnemyEnabled;
        FinishDetectConfig.GoDistance = autoFightConfig.FinishDetectConfig.GoDistance;
        FinishDetectConfig.EndModel = autoFightConfig.FinishDetectConfig.EndModel;
        FinishDetectConfig.FastCheckDelay = autoFightConfig.FinishDetectConfig.FastCheckDelay;
        FinishDetectConfig.ReturnToFightPointEnabled = autoFightConfig.FinishDetectConfig.ReturnToFightPointEnabled;
        FinishDetectConfig.ReturnToFightPointIntervalMs = autoFightConfig.FinishDetectConfig.ReturnToFightPointIntervalMs;
        FinishDetectConfig.ReturnToFightPointTriggerDistance = autoFightConfig.FinishDetectConfig.ReturnToFightPointTriggerDistance;
        FinishDetectConfig.ReturnToFightPointStopDistance = autoFightConfig.FinishDetectConfig.ReturnToFightPointStopDistance;
        FinishDetectConfig.ReturnToFightPointTimeTriggerEnabled = autoFightConfig.FinishDetectConfig.ReturnToFightPointTimeTriggerEnabled;
        FinishDetectConfig.ReturnToFightPointTimeTriggerSeconds = autoFightConfig.FinishDetectConfig.ReturnToFightPointTimeTriggerSeconds;
        FinishDetectConfig.FightWaitNotEndTime = autoFightConfig.FinishDetectConfig.FightWaitNotEndTime;
        FinishDetectConfig.PaimonEndModel = autoFightConfig.FinishDetectConfig.PaimonEndModel;
        FinishDetectConfig.DoubleEndEnbled = autoFightConfig.FinishDetectConfig.DoubleEndEnbled;
        FinishDetectConfig.DoubleEndDelay = autoFightConfig.FinishDetectConfig.DoubleEndDelay;

        KazuhaPartyName = autoFightConfig.KazuhaPartyName;
        OnlyPickEliteDropsMode = autoFightConfig.OnlyPickEliteDropsMode;
        BattleThresholdForLoot = autoFightConfig.BattleThresholdForLoot ?? BattleThresholdForLoot;
        //下面参数固定，只取自动战斗里面的
        FinishDetectConfig.BattleEndProgressBarColor = autoFightConfig.FinishDetectConfig.BattleEndProgressBarColor;
        FinishDetectConfig.BattleEndProgressBarColorTolerance = autoFightConfig.FinishDetectConfig.BattleEndProgressBarColorTolerance;

        GuardianAvatar = autoFightConfig.GuardianAvatar;
        GuardianCombatSkip = autoFightConfig.GuardianCombatSkip;
        GuardianAvatarHold = autoFightConfig.GuardianAvatarHold;
        SwimmingEnabled = autoFightConfig.SwimmingEnabled;
        QinDoublePickUp = autoFightConfig.QinDoublePickUp;
        CountryName = autoFightConfig.CountryName;
        KazuhaTime = autoFightConfig.KazuhaTime;
    }
}