namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>本机达上限上报状态机的动作（OnFightNodeExpResultAsync 据此决定是否通知服务端）。</summary>
public enum ExpCapReportAction
{
    /// <summary>无动作（状态未翻转）。</summary>
    None,

    /// <summary>上报达上限（未上报→已上报翻转）。</summary>
    Report,

    /// <summary>撤回上报（已上报→未上报翻转）。</summary>
    Clear,
}

/// <summary>
/// 联机锄地"基于经验判断达上限"的纯决策函数（multiplayer-hoeing-exp-cap-stop）。
/// 无外部依赖，供 PBT 直接撒输入验证。
/// </summary>
public static class ExpCapDecisions
{
    /// <summary>连续无经验判定阈值（固定 2，不可配）。</summary>
    public const int ConsecutiveNoExpThreshold = 2;

    /// <summary>
    /// 下一个"连续无经验计数"：hasExp==true 归零；false 则 +1。
    /// 仅对"有效战斗节点"调用（复苏/跳段/取消的节点由调用方跳过，不调本函数）。
    /// </summary>
    public static int NextCount(int prevCount, bool hasExp)
        => hasExp ? 0 : prevCount + 1;

    /// <summary>
    /// 根据本节点结果 + 当前上报态，决定应执行的动作（含撤回）。仅状态翻转时返回非 None（幂等）。
    /// - 未上报 ∧ 无经验 ∧ count≥阈值 → Report
    /// - 已上报 ∧ 有经验               → Clear
    /// - 其余                         → None
    /// </summary>
    public static ExpCapReportAction NextReportAction(int count, bool hasExp, bool alreadyReported)
    {
        if (!alreadyReported && !hasExp && count >= ConsecutiveNoExpThreshold)
            return ExpCapReportAction.Report;
        if (alreadyReported && hasExp)
            return ExpCapReportAction.Clear;
        return ExpCapReportAction.None;
    }

    /// <summary>是否启用本功能（配置开 ∧ 已连接）。IsConnected==false 时不上报（无法送达服务端）。</summary>
    public static bool IsEnabled(bool enableExpCapStop, bool isConnected)
        => enableExpCapStop && isConnected;

    // === 团队 arming 门控兜底（multiplayer-hoeing-exp-cap-stop R7，后补）===

    /// <summary>连续无经验"无条件兜底自点亮 arming"阈值（固定 5，不可配）。
    /// 覆盖"全队进房即满级、谁都吃不到经验、团队 arming 永远点不亮"的死锁。R7.2。</summary>
    public const int ConsecutiveNoExpUnconditionalThreshold = 5;

    /// <summary>是否应"兜底自点亮团队 arming"：连续无经验计数达无条件阈值即为真。
    /// 纯函数，PBT 友好。R7.2。仅决定"是否发 arming 信号"，不影响 R3 上报/撤回。</summary>
    public static bool ShouldForceArm(int count)
        => count >= ConsecutiveNoExpUnconditionalThreshold;
}
