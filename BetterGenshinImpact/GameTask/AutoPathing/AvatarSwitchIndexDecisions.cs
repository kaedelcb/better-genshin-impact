namespace BetterGenshinImpact.GameTask.AutoPathing;

/// <summary>
/// 独立理由（R4.2）：被 SurvivalAvatarSwitchService（低血量生存切人执行体）与
/// PathExecutor.TryPartyHealing（吃奶活跃角色扫描）两个不相干流程复用，且消费方 PathExecutor.cs
/// 是与公版同名的高冲突文件 → 独立为零冲突新文件。
/// 低血量切人序号纯函数决策（PBT 友好，无外部依赖）。
/// 历史来源：hoeing-low-hp-avatar-switch-index-out-of-range-fix（spec 已归档，仅存名）。
/// </summary>
public static class AvatarSwitchIndexDecisions
{
    /// <summary>
    /// 生存切人黑名单角色：万叶是赶路/聚怪位，不把伤害交给他（用户拍板：切人目标恒不能是万叶）。
    /// </summary>
    public const string KazuhaName = "枫原万叶";

    /// <summary>万叶判定（按名册名字识别，替代原实现"序号串当名字查"的失效守卫）。</summary>
    public static bool IsKazuha(string? name) => name == KazuhaName;

    /// <summary>
    /// 队伍有效人数：null / 0 / 负数兜底为 4（保持硬编码 4 的旧行为，最小回归）。
    /// </summary>
    public static int EffectiveAvatarCount(int? actualCount)
        => (actualCount is int c && c > 0) ? c : 4;

    /// <summary>
    /// 下一个角色编号：(current % N) + 1，N 为有效人数。
    /// N == 4 时与原式 (current % 4) + 1 逐字节等价。
    /// </summary>
    public static int NextAvatarIndex(int currentIndex, int effectiveCount)
        => (currentIndex % effectiveCount) + 1;
}
