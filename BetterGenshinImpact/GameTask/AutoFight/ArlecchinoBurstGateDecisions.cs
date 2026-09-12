namespace BetterGenshinImpact.GameTask.AutoFight;

/// <summary>
/// 独立理由（R4.2）：消费方 Model/Avatar.cs 是与公版同名的高冲突文件（茶包战斗引擎改造的主战场），
/// 决策独立为零冲突新文件正当。
/// 阿蕾奇诺元素爆发红血门控的纯函数决策（PBT 友好，无外部依赖、无屏幕采样）。
/// 历史来源：arlecchino-q-low-hp-gate（spec 已归档，仅存名）。
/// 红血状态来源：CombatHealthDetector.IsRedBlood（B7 检测层）。
/// </summary>
public static class ArlecchinoBurstGateDecisions
{
    /// <summary>
    /// 阿蕾奇诺角色名（硬编码）。
    /// </summary>
    public const string ArlecchinoName = "阿蕾奇诺";

    /// <summary>
    /// 是否应对本次 Q 释放执行红血门控（即"先检测红血、非红血则跳过 Q"）。
    /// 仅当开关开启且角色名为阿蕾奇诺时返回 true。
    /// 返回 false 表示不门控，按既有逻辑正常放 Q（开关关闭 / 非阿蕾奇诺）。
    /// </summary>
    /// <param name="gateEnabled">阿蕾奇诺红血门控开关（运行期注入到 Avatar 的当前配置组/独立任务值）。</param>
    /// <param name="avatarName">当前出战角色名。</param>
    public static bool ShouldGate(bool gateEnabled, string? avatarName)
    {
        if (!gateEnabled)
        {
            return false;
        }

        return avatarName == ArlecchinoName;
    }
}
