using System;
using System.Collections.Generic;
using System.Linq;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 槲寄生 · R5 批次 4（A6）**运行中占用与到来者相遇**的冻结纯函数。
/// 依据 owner 裁决 B.1 最小裁决矩阵：
/// ①上线锄地／一键锄地恒为最高优先级，**同级（含两种最高级相遇）由后来者打断当前**；
/// ②其余任务可配置优先级，默认同级后来的打断当前，低优先级**不得**打断高优先级（改为本地持久等待）；
/// ③抢占必须能绑定被切任务身份并完成权威退出确认——身份/级别/优先级不可核验时**保守停驻**；
/// ④未知事实**不是空闲**（零发送、不换键补发）。
/// 纯函数契约：无副作用、不读时钟、不生成随机、不探测环境；同输入同输出。
/// </summary>
public static class RunningOccupancyArbiter
{
    /// <summary>到来者 vs 当前占用者：返回相遇判定（含原因与可抢占目标身份）。</summary>
    public static RunningEncounter Decide(RunningOccupantFacts occupant, IncomingRequestFacts incoming)
    {
        ArgumentNullException.ThrowIfNull(occupant);
        ArgumentNullException.ThrowIfNull(incoming);

        if (occupant.State == OccupantFactsState.Unknown)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.HoldFactsUnknown,
                "权威占用事实未知：不当作空闲，零发送（引用=" + (occupant.Reference ?? "无") + "）",
                null);
        }

        if (occupant.State == OccupantFactsState.Idle)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.ProceedIdle,
                "已确认空闲：可按常规准入继续（准入判定另行执行）",
                null);
        }

        // 占用中：先要求"能绑定被切任务身份"，否则不得抢占（B.1 ③）。
        if (!HasBindablePreemptTarget(occupant))
        {
            return new RunningEncounter(
                RunningEncounterVerdict.HoldUnknownOccupant,
                "占用者身份不可核验（无法绑定被切任务/退出确认）：保守停驻，零新发送",
                null);
        }

        // 到来者的级别/优先级只能来自**可信入口映射**：自报 key 不得被当作最高级或高级别的证明。
        if (!incoming.HasTrustedIdentity)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.WaitLocally,
                "到来者的级别/优先级来源不可信（例如远程自报 key）：不得据此抢占，按本地等待处理",
                null);
        }

        if (incoming.IsHoeingHighest)
        {
            // owner B.1 例外（书面）：最高级锄地类"无论当前是什么任务"都打断，因此**不需要**比较占用者级别；
            // 仍要求占用者身份可核验（否则上面已停驻）与后续退出确认。
            return new RunningEncounter(
                RunningEncounterVerdict.PreemptNow,
                "最高级锄地类到来：无论当前占用级别（含同为最高级）均由后来者打断（owner B.1；不比较占用者级别）",
                occupant.ExecutionInstanceId);
        }

        // 占用者已证明是 B.1 最高级（上线锄地／一键锄地）：普通任务不得打断，改为本地等待。
        if (occupant.HighestClass == true)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.WaitLocally,
                "占用者已证明为最高级锄地类：普通任务不得打断，按本地持久等待（当前结束后重新比较）",
                null);
        }

        // 锄地类但**无法证明**是否最高级：级别不可确定，普通任务保守停驻（不得据此抢占锄地占用者）。
        if (occupant.HighestClass is null && occupant.HoeingClass)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.HoldUnknownOccupant,
                "占用者为锄地类但无法证明是否最高级：级别不可确定，普通任务保守停驻（零新发送）",
                null);
        }

        if (occupant.Tier is null || occupant.Priority is null)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.HoldUnknownOccupant,
                "占用者的级别/优先级未知：无法完成比较，保守停驻（零新发送）",
                null);
        }

        var occupantTier = occupant.Tier.Value;
        var occupantPriority = occupant.Priority.Value;
        if (incoming.Tier > occupantTier
            || (incoming.Tier == occupantTier && incoming.Priority > occupantPriority))
        {
            return new RunningEncounter(
                RunningEncounterVerdict.PreemptNow,
                "到来者级别更高：打断当前任务并确认其退出后执行到来者",
                occupant.ExecutionInstanceId);
        }

        if (incoming.Tier == occupantTier && incoming.Priority == occupantPriority)
        {
            return new RunningEncounter(
                RunningEncounterVerdict.PreemptNow,
                "同级别同级：由后来者打断当前（owner B.1 相遇规则）",
                occupant.ExecutionInstanceId);
        }

        return new RunningEncounter(
            RunningEncounterVerdict.WaitLocally,
            "到来者级别更低：本地持久等待，当前结束后重新比较（等待不是 BGI 已入队/已受理）",
            null);
    }

    /// <summary>
    /// 当前占用结束后从**本地等待集合**挑下一个执行者：最高级锄地类优先 → 级别降序 → 优先级降序 →
    /// 计划时刻升序（null 最后）→ 稳定身份 Ordinal 兜底；前置未就绪者不参选。空集/全部不合格返回 null。
    /// </summary>
    public static WaitingRequestFacts? SelectNextFromWaitSet(IReadOnlyList<WaitingRequestFacts>? waitSet)
    {
        if (waitSet is null || waitSet.Count == 0) return null;
        return waitSet
            .Where(w => w.PrerequisiteReady)
            .OrderByDescending(w => w.IsHoeingHighest)
            .ThenByDescending(w => (int)w.Tier)
            .ThenByDescending(w => w.Priority)
            // 时刻：**有值者一律先于 null**（独立排序键，避免"真实 MaxValue"与 null 打平后次序不确定）。
            .ThenBy(w => w.ScheduledAt.HasValue ? 0 : 1)
            .ThenBy(w => w.ScheduledAt ?? default)
            .ThenBy(w => w.StableIdentity, StringComparer.Ordinal)
            .ThenBy(w => w.CandidateId, StringComparer.Ordinal) // 全键相同时的最终确定性兜底（输入排列无关）
            .FirstOrDefault();
    }

    /// <summary>
    /// 恢复资格（S8b／resume）：必须持有有效票据，且当前占用不能是别的执行根；
    /// 事实未知 ⇒ 保守停驻；占用者正是被暂停身份 ⇒ 拒绝（已在运行，不得重复恢复）。
    /// </summary>
    public static ResumeVerdict DecideResume(RunningOccupantFacts occupant, ResumptionFacts resumption)
    {
        ArgumentNullException.ThrowIfNull(occupant);
        ArgumentNullException.ThrowIfNull(resumption);

        if (!resumption.HasValidTicket) return ResumeVerdict.RefuseTicketInvalid;
        if (string.IsNullOrWhiteSpace(resumption.SuspendedIdentity)) return ResumeVerdict.RefuseSuspendedIdentityUnknown;
        if (occupant.State == OccupantFactsState.Unknown) return ResumeVerdict.HoldFactsUnknown;
        if (occupant.State == OccupantFactsState.Idle) return ResumeVerdict.AllowResume;

        // 占用者身份不可核验 / 被暂停实例未知 ⇒ 无法比较，保守停驻（不得说成"他人占用"或"已在运行"）。
        // 双方身份都必须是**合法非空 GUID**；并按 GUID **值**比较（不接受字符串大小写/格式差异被当成不同身份）。
        if (!HasBindablePreemptTarget(occupant)
            || !TryParseInstanceId(resumption.SuspendedExecutionInstanceId, out var suspendedInstanceId))
        {
            return ResumeVerdict.HoldFactsUnknown;
        }

        if (TryParseInstanceId(occupant.ExecutionInstanceId, out var currentInstanceId)
            && currentInstanceId == suspendedInstanceId)
        {
            return ResumeVerdict.RefuseAlreadyOwner;
        }

        return ResumeVerdict.RefuseOccupiedByOther;
    }

    /// <summary>抢占目标必须**可绑定**：可信身份标记为真且执行实例 ID 是合法非空 GUID（否则不得返回 PreemptNow）。</summary>
    private static bool HasBindablePreemptTarget(RunningOccupantFacts occupant)
        => occupant.HasTrustedIdentity
           && TryParseInstanceId(occupant.ExecutionInstanceId, out _);

    /// <summary>执行实例 ID 必须是合法非空 GUID（其余一律视为身份未知）。</summary>
    private static bool TryParseInstanceId(string? raw, out Guid instanceId)
        => Guid.TryParse(raw, out instanceId) && instanceId != Guid.Empty;
}
