using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 自动战斗配置
/// </summary>
[Serializable]
public partial class AutoFightConfig : ObservableObject
{
    [ObservableProperty] private string _strategyName = "根据队伍自动选择";
    
    /// <summary>
    /// 战斗策略国家列表（策略文件名检测）
    /// </summary>
    [ObservableProperty]
    private string[] _countryNamesList = { "自动", "挪德卡莱","纳塔", "枫丹", "须弥", "稻妻", "璃月", "蒙德", "精英", "小怪" };
    
    /// <summary>
    /// 自动战斗策略所属国家
    /// </summary>
    [ObservableProperty] private string?[] _countryName = ["自动"];

    /// <summary>
    /// 英文逗号分割 强制指定队伍角色
    /// </summary>
    [ObservableProperty] private string _teamNames = "";
    
    /// <summary> 战斗策略自动EQ </summary>
    [ObservableProperty] private bool _autoCombatEq = false;

    /// <summary>
    /// 检测战斗结束
    /// </summary>
    [ObservableProperty]
    private bool _fightFinishDetectEnabled = true;
    /// <summary>
    /// 根据技能CD优化出招人员
    /// 根据填入人或人和cd，来决定当此人元素战技cd未结束时，跳过此人出招，来优化战斗流程，可填入人名或人名数字（用逗号分隔），
    /// 多种用分号分隔，例如:白术;钟离,12;，如果人名，则用内置cd检查，如果是人名和数字，则把数字当做出招cd(秒)。
    /// </summary>
    [ObservableProperty] private string _actionSchedulerByCd = "";

    /// <summary>
    /// 玛薇卡摩托状态检测开关。
    /// 开启后自动检测玛薇卡是否处于摩托状态，非重击命令下摩托，重击命令自动上摩托。
    /// 默认 false（关闭）：关闭时跳过 Avatar.cs 重击分支与 AutoFightTask.cs 非重击分支的摩托检测/开收摩托按键。
    /// 注意：位置1(Avatar.cs)读全局战斗配置实例；位置2(AutoFightTask.cs)读当前配置组实例(经 AutoFightParam 透传)。
    /// </summary>
    [ObservableProperty] private bool _mavuikaMotorcycleCheckEnabled = false;

    /// <summary>
    /// 阿蕾奇诺红血才放Q 开关。
    /// 开启后，阿蕾奇诺仅在当前出战角色红血时才释放元素爆发Q（KeyPress 与 UseBurst 两处入口）。
    /// 默认 false（关闭）：关闭时阿蕾奇诺正常释放Q，与本功能引入前等价。
    /// 注意：Avatar.cs 读全局战斗配置实例(TaskContext.Instance().Config.AutoFightConfig)。
    /// </summary>
    [ObservableProperty] private bool _arlecchinoBurstLowHpGateEnabled = false;
    
    [ObservableProperty] private bool _arlecchinoAutoEnabled = false;

    [ObservableProperty] private int _qiKong = 0;

    /// <summary>
    /// 契空放 Q 时元素战技（E）剩余 CD 阈值：仅当 cc &gt; SkillCdForQ 且未处于契空状态才触发放 Q（红血不受此限制）。
    /// 默认 5，与本功能引入前 Avatar.cs 中硬编码的 (cc &gt; 5) 逐字节等价。
    /// 注意：Avatar.cs 读全局战斗配置实例 TaskContext.Instance().Config.AutoFightConfig。
    /// </summary>
    [ObservableProperty] private int _skillCdForQ = 5;

    /// <summary>
    /// 恰斯卡特化 — X 轴灵敏度系数（默认 1.0）。
    /// 控制 e(hold) 旋转搜索时水平移动的幅度。
    /// </summary>
    [ObservableProperty] private double _chascaXSensitivity = 1.0;

    /// <summary>
    /// 恰斯卡特化 — Y 轴灵敏度系数（默认 1.0）。
    /// 控制 e(hold) 旋转搜索时垂直下压的幅度。
    /// </summary>
    [ObservableProperty] private double _chascaYSensitivity = 1.0;

    /// <summary>
    /// 恰斯卡传奇模式 — 旋转间隔（秒，默认 0.75）。
    /// 子弹模式稳定超过此时间后触发旋转搜索。
    /// </summary>
    [ObservableProperty] private double _chascaLegendaryRotateInterval = 0.75;

    /// <summary>
    /// 恰斯卡 — 无血条旋转次数上限（默认 12）。
    /// 连续旋转搜索此次数仍未找到血条则落地退出。
    /// </summary>
    [ObservableProperty] private int _chascaRotateCountLimit = 12;

    /// <summary>
    /// 特化逻辑帧间隔（毫秒，默认 50）。
    /// 供恰斯卡飞行/桑多涅重击等特化循环使用，控制每帧 Sleep 时长。
    /// </summary>
    [ObservableProperty] private int _specializedFrameIntervalMs = 50;

    /// <summary>
    /// 桑多涅（Sandrone）重击时间序列（字符串，格式待定）。
    /// </summary>
    [ObservableProperty] private string _sandroneChargeTimeSequence = "";

    /// <summary>
    /// 桑多涅重击预瞄点 X 坐标（默认 840）。
    /// </summary>
    [ObservableProperty] private int _sandroneChargePreAimX = 840;

    /// <summary>
    /// 桑多涅（木偶）重击旋转速度系数（默认 1.0）。
    /// </summary>
    [ObservableProperty] private double _sandroneChargeRotateSpeed = 1.0;

    /// <summary>
    /// 只拾取精英掉落
    /// Closed ：关闭功能
    /// AllowAutoPickupForNonElite: 非精英允许自动拾取：战斗过程中掉落脚下的可以自动拾取，但不会执行万叶拾取和拾取配置逻辑。
    /// DisableAutoPickupForNonElite: 非精英关闭拾取：战斗过程中掉落到脚下的也不会自动拾取。
    /// </summary>
    [ObservableProperty] private string _onlyPickEliteDropsMode = "Closed";
    [Serializable]
    public partial class FightFinishDetectConfig : ObservableObject
    {
        /// <summary>
        /// 判断战斗结束读条颜色，不同帧率可能下会有些不同，默认为95,235,255
        /// </summary>
        [ObservableProperty]
        private string _battleEndProgressBarColor = "";

        /// <summary>
        /// 对于上方颜色地偏差值，即±某个值，例如 6或6,6,6，前者表示所有偏差值都一样，后者则可以分别设置
        /// </summary>
        [ObservableProperty]
        private string _battleEndProgressBarColorTolerance = "";
        
        
        /// <summary>
        /// 快速检查战斗结束，在一轮脚本中，可以每隔一定秒数（默认为5）或指定角色操作后，去检查（在每个角色完成该轮脚本时）。
        /// </summary>
        [ObservableProperty]
        private bool _fastCheckEnabled = false;
        
        /// <summary>
        /// 旋转寻找敌人位置
        /// </summary>
        [ObservableProperty]
        private bool _rotateFindEnemyEnabled = false;
        
        /// <summary>
        /// 快速检查战斗结束的参数，可填入数字和人名，多种用分号分隔，例如:15,白术;钟离;，如果是数字（小于等于0则不会根据时间去检查），则指定检查间隔，如果是人名，则该角色执行一轮操作后进行检查。同时每轮结束后检查不变。
        /// </summary>
        [ObservableProperty]
        private string _fastCheckParams = "";
        
        /// <summary>
        /// 检查战斗结束的延时，即角色，默认为1.5秒。也可以指定特定角色之后延时多少时间检查。格式如：2.5;白术,1.5;钟离,1.0;
        /// </summary>
        [ObservableProperty]
        private string _checkEndDelay = "0.4;钟离,1.4;";

        /// <summary>
        /// 按下切换队伍后去检查屏幕色块的延迟，默认为0.45秒。若频繁误判可以适当提高这个值。确保这个延迟不会真的把队伍配置界面切出来。
        /// </summary>
        [ObservableProperty]
        private string _beforeDetectDelay = "0.4";
        
        /// <summary>
        /// 旋转寻找敌人位置的旋转因子，默认为12（范围1-13），越大越快。
        /// </summary>
        [ObservableProperty]
        private int _rotaryFactor = 12;
        
        /// <summary>
        /// 是否是第一次检查和面敌。
        /// </summary>
        [ObservableProperty]
        private bool _isFirstCheck = false;
        
        /// <summary>
        /// GoDistance 寻敌移动距离
        /// </summary>
        [ObservableProperty]
        private int _goDistance = 500;
        
        /// <summary>
        /// 是有元素爆发前检查战斗结束
        /// </summary>
        [ObservableProperty]
        private bool _checkBeforeBurst = false;
        
        /// 旋转寻找敌人模式
        [ObservableProperty]
        private bool _rotationMode = true;
        
        //检查结束方式
        [ObservableProperty]
        private bool _endModel = true;
        
        //快速检查方式的延时，默认为0.15秒
        [ObservableProperty]
        private double _fastCheckDelay = 0.1;
        
        // 战斗中回点（通用版）配置 — 距离触发组
        [ObservableProperty]
        private bool _returnToFightPointEnabled = false;

        [ObservableProperty]
        private int _returnToFightPointIntervalMs = 1000;

        [ObservableProperty]
        private double _returnToFightPointTriggerDistance = 15;

        [ObservableProperty]
        private double _returnToFightPointStopDistance = 10;

        // 战斗中回点（通用版）配置 — 时间触发组（需 RotateFindEnemyEnabled = true）
        [ObservableProperty]
        private bool _returnToFightPointTimeTriggerEnabled = false;

        [ObservableProperty]
        private int _returnToFightPointTimeTriggerSeconds = 5;

        /// <summary>
        /// UI DataTrigger 用：距离配置是否合法（仅校验范围 0~150 与触发距离 &gt; 0；不再强制顺序）。
        /// 参与 setter 自动变更通知（OnReturnToFightPointTriggerDistanceChanged / OnReturnToFightPointStopDistanceChanged）。
        /// </summary>
        public bool IsReturnToFightPointDistanceLegal =>
            _returnToFightPointStopDistance >= 0
            && _returnToFightPointStopDistance <= 150
            && _returnToFightPointTriggerDistance > 0
            && _returnToFightPointTriggerDistance <= 150;

        partial void OnReturnToFightPointTriggerDistanceChanged(double value)
            => OnPropertyChanged(nameof(IsReturnToFightPointDistanceLegal));

        partial void OnReturnToFightPointStopDistanceChanged(double value)
            => OnPropertyChanged(nameof(IsReturnToFightPointDistanceLegal));

        //开战前等待时间，默认为3秒，确保引战
        [ObservableProperty] 
        private int _fightWaitNotEndTime = 0;

        //派蒙结束检查模式
        [ObservableProperty] 
        private bool _paimonEndModel = false;
        
        //派蒙模式下的二次检查
        [ObservableProperty]
        private bool _doubleEndEnbled = false;
        
        //二次检查的延时，默认为750毫秒
        [ObservableProperty]
        private int _doubleEndDelay = 750;
    }
    /// <summary>
    /// 战斗结束相关配置
    /// </summary>   
    [ObservableProperty]
    private FightFinishDetectConfig _finishDetectConfig = new();
    
    /// <summary>
    /// 检测战斗结束，默认为每轮脚本后检查
    /// </summary>
    [ObservableProperty]
    private bool _pickDropsAfterFightEnabled = false;
    /// <summary>
    /// 检测战斗结束，默认为每轮脚本后检查
    /// </summary>
    [ObservableProperty]
    private int _pickDropsAfterFightSeconds = 15;

    /// <summary>
    /// 拾取战斗人次阈值,当战斗人次小于一定次数，就结束战斗情况下，不触发拾取掉落物和万叶拾取后拾取，只有不小于2时才生效。
    /// </summary>
    [ObservableProperty]
    private int? _battleThresholdForLoot;
    /// <summary>
    /// 战斗结束后，如果存在枫原万叶，则使用该角色捡材料
    /// </summary>
    [ObservableProperty]
    private bool _kazuhaPickupEnabled = true;
    
    [ObservableProperty]
    private string _guardianAvatar = string.Empty;
    
    [ObservableProperty]
    private bool _guardianCombatSkip = false;
    
    [ObservableProperty]
    private bool _guardianAvatarHold = false;
     [ObservableProperty]
     private bool _burstEnabled = false;
     
     [ObservableProperty]
     private bool _expKazuhaPickup = false;
    
     [ObservableProperty]
     private bool _qinDoublePickUp = false;
     
    [ObservableProperty]
    private bool _swimmingEnabled = true;

    /// <summary>
    /// 战斗结束后，如果不存在万叶，则切换至存在万叶的队伍（基于开启万叶拾取情况下）
    /// </summary>
    [ObservableProperty]
    private string _kazuhaPartyName = "";

    /// <summary>
    /// 战斗超时，单位秒
    /// </summary>
    [ObservableProperty]
    private int _timeout = 120;
    
    [ObservableProperty]
    private bool _takeMedicineEnabled = false;
    
    [ObservableProperty]
    private int _medicineInterval = 1500;
    
    [ObservableProperty]
    private int _checkInterval = 200;
    
    [ObservableProperty]
    private int _recoverMaxCount = 5;
    
    [ObservableProperty]
    private bool _endBloodCheackEnabled = false;
    
    [ObservableProperty]
    private bool _qRecoverAvatar = false;
    
    [ObservableProperty]
    private string _useEqList = "1,2,3,4";
    
    [ObservableProperty]
    private string _useSkillList = "1,2,3,4";
    
    [ObservableProperty]
    private int _kazuhaTime = 1500;
}
