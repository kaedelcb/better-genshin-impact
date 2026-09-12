namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 独立理由（R4.2）：消费方 Model/Avatar.cs 是与公版同名的高冲突文件（茶包战斗引擎改造的主战场），
/// 决策独立为零冲突新文件正当。
/// 阿蕾奇诺普攻自动EQ 放 Q 决策纯函数（PBT 友好，无外部依赖、无屏幕采样）。
/// 历史来源：arlecchino-auto-eq-cd-threshold-config（spec 已归档，仅存名）。
/// </summary>
public static class ArlecchinoAutoEqDecisions
{
    /// <summary>
    /// 契空放 Q 的 CD 门控：当前元素战技（E）剩余 CD 严格大于阈值时才允许放 Q。
    /// 语义等价于 Avatar.cs 中原硬编码的 (cc &gt; SkillCdForQ)。
    /// </summary>
    /// <param name="currentSkillCd">当前元素战技（E）剩余冷却时间。</param>
    /// <param name="skillCdForQ">契空放 Q 所需的 E 技能 CD 阈值（AutoFightConfig.SkillCdForQ）。</param>
    public static bool ShouldReleaseQByCd(double currentSkillCd, int skillCdForQ)
        => currentSkillCd > skillCdForQ;
}
