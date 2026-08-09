using BetterGenshinImpact.GameTask.AutoFight;

namespace BetterGenshinImpact.GameTask.AutoFightOfficial;

/// <summary>
/// 公版/茶包版自动战斗引擎路由决策（official-autofight-parallel-engine spec §4.2）。
/// 纯函数，无副作用，便于 PBT。
/// </summary>
public static class OfficialAutoFightRouter
{
    /// <summary>
    /// 判定当前战斗是否走公版引擎。
    /// </summary>
    /// <param name="teapotConfig">
    /// 茶包版自动战斗配置实例（开关 UseOfficialAutoFight 挂在其上）。
    /// 全局作用域传 Config.AutoFightConfig；配置组作用域传 PathingPartyConfig.AutoFightConfig。
    /// </param>
    /// <param name="isMultiplayerHoeing">
    /// 当前是否为联机锄地战斗（由调用方传入，通常为 PathingConditionConfig.MultiplayerFightTimeoutOverride.HasValue）。
    /// 联机锄地锁死茶包版，无视开关（R3.3）。
    /// </param>
    /// <returns>true=走公版引擎；false=走茶包版引擎（默认/联机锄地）。</returns>
    public static bool UseOfficial(AutoFightConfig? teapotConfig, bool isMultiplayerHoeing)
    {
        // R3.3：联机锄地恒走茶包版（含固定策略/万叶覆盖/持续回点等深度定制），无视开关。
        if (isMultiplayerHoeing)
        {
            return false;
        }

        // R3.1/R3.2：其余场景按作用域开关。配置为空时安全回退茶包版。
        return teapotConfig?.UseOfficialAutoFight == true;
    }
}
