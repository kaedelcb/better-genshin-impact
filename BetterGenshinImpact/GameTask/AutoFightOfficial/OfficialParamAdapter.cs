using TeapotParam = BetterGenshinImpact.GameTask.AutoFight.AutoFightParam;

namespace BetterGenshinImpact.GameTask.AutoFightOfficial;

/// <summary>
/// official-autofight-parallel-engine spec §4.3：把各入口已构建的茶包版 AutoFightParam
/// 映射为公版 AutoFightParam，供 E2~E7 路由到公版引擎时复用。
///
/// 映射规则：
/// - 公版独有字段（索敌 EnableCombatTargeting/LockLostWaitTime/TargetingDetectionInterval、
///   伤害识别 DamageNumberRecognitionMode/DrawRecognitionResults、SkipFightEndCheckWhenEnemyVisible、
///   ExpBasedPickupEnabled）由公版 config 经构造函数自动填充。
/// - 两版共有的行为字段从茶包 param 覆盖，保留各入口的定制微调（如 E4/E5 关闭结束检测、E6 地脉花多项关闭）。
/// - 茶包独有字段（玛薇卡摩托/阿蕾奇诺/吃药复活/战斗中回点/AutoCombatEq 等）不映射——
///   即"选公版即得公版行为"（已确认降级 D1/点 A）。
/// </summary>
public static class OfficialParamAdapter
{
    public static AutoFightParam FromTeapot(TeapotParam teapot, AutoFightOfficialConfig officialConfig)
    {
        // 复用茶包已解析的策略路径（策略文件目录两版共享，公版工厂按扩展名路由）
        var param = new AutoFightParam(teapot.CombatStrategyPath, officialConfig);

        // ── 两版共有的顶层行为字段：从茶包 param 覆盖（保留入口定制）──
        param.Timeout = teapot.Timeout;
        param.FightFinishDetectEnabled = teapot.FightFinishDetectEnabled;
        param.PickDropsAfterFightEnabled = teapot.PickDropsAfterFightEnabled;
        param.PickDropsAfterFightSeconds = teapot.PickDropsAfterFightSeconds;
        param.BattleThresholdForLoot = teapot.BattleThresholdForLoot;
        param.KazuhaPickupEnabled = teapot.KazuhaPickupEnabled;
        param.ActionSchedulerByCd = teapot.ActionSchedulerByCd;
        param.KazuhaPartyName = teapot.KazuhaPartyName;
        param.OnlyPickEliteDropsMode = teapot.OnlyPickEliteDropsMode;
        param.GuardianAvatar = teapot.GuardianAvatar;
        param.GuardianCombatSkip = teapot.GuardianCombatSkip;
        param.GuardianAvatarHold = teapot.GuardianAvatarHold;
        param.CheckBeforeBurst = teapot.CheckBeforeBurst;
        param.IsFirstCheck = teapot.IsFirstCheck;
        param.RotaryFactor = teapot.RotaryFactor;
        param.BurstEnabled = teapot.BurstEnabled;
        param.QinDoublePickUp = teapot.QinDoublePickUp;

        // ── 两版共有的结束检测子字段：从茶包 FinishDetectConfig 覆盖 ──
        param.FinishDetectConfig.FastCheckEnabled = teapot.FinishDetectConfig.FastCheckEnabled;
        param.FinishDetectConfig.FastCheckParams = teapot.FinishDetectConfig.FastCheckParams;
        param.FinishDetectConfig.CheckEndDelay = teapot.FinishDetectConfig.CheckEndDelay;
        param.FinishDetectConfig.BeforeDetectDelay = teapot.FinishDetectConfig.BeforeDetectDelay;
        param.FinishDetectConfig.RotateFindEnemyEnabled = teapot.FinishDetectConfig.RotateFindEnemyEnabled;

        return param;
    }
}
