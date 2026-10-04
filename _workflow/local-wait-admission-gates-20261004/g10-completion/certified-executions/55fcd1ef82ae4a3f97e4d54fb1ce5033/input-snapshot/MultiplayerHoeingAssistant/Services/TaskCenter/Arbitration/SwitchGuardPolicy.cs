namespace MultiplayerHoeingAssistant.Services
{
    /// <summary>
    /// R5.1 冻结稿 v5 §7 旧版切换守卫判定纯函数——「旧版不满足静止前置不得执行」由切换/启动控制面落实；
    /// 本类只交付判定（R5.1），端到端接线归 R5.6。
    /// 纪律注释：先封锁生产准入再取静止事实；封锁持续至切换提交接管完成或安全回退；
    /// 固定锁序=切换锁→租约锁；控制面崩溃重启须重新检查或恢复持久化闸门，不得复用旧通过结果。
    /// </summary>
    public sealed record SwitchGuardFacts(bool HasRunningExecution, bool HasPendingSubmission, bool HasPendingHandoff, bool HasRecoveryDuty);

    /// <summary>
    /// 切换守卫判定结果。
    /// </summary>
    /// <param name="Allowed">是否允许执行旧版切换。</param>
    /// <param name="Reason">不允许时的原因代码；允许时为 <c>null</c>。</param>
    public sealed record SwitchGuardVerdict(bool Allowed, string? Reason);

    /// <summary>
    /// 切换守卫判定纯函数（R5.1）。仅交付判定，不承担端到端接线。
    /// </summary>
    public static class SwitchGuardPolicy
    {
        /// <summary>
        /// 依据静止事实给出切换判定。优先顺序固定不可调：pending_handoff 先于 pending_recovery 先于 not_quiescent。
        /// </summary>
        /// <param name="f">待判定的切换静止事实。</param>
        /// <returns>判定结论；不允许时携带原因代码，允许时原因为 <c>null</c>。</returns>
        public static SwitchGuardVerdict Evaluate(SwitchGuardFacts f)
        {
            if (f.HasPendingHandoff)
            {
                return new SwitchGuardVerdict(false, "pending_handoff");
            }

            if (f.HasRecoveryDuty)
            {
                return new SwitchGuardVerdict(false, "pending_recovery");
            }

            if (f.HasRunningExecution || f.HasPendingSubmission)
            {
                return new SwitchGuardVerdict(false, "not_quiescent");
            }

            return new SwitchGuardVerdict(true, null);
        }
    }
}
