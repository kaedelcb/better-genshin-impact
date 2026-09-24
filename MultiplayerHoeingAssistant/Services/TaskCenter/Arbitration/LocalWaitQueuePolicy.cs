using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 入队判定结果。**不可由调用方构造 <c>SendPermitted=true</c>**：只能经
/// <see cref="LocalWaitQueuePolicy.DecideEnqueue"/> 或静态工厂取得，等待项**永远不携带发送许可**。
/// </summary>
public sealed class LocalWaitEnqueueDecision
{
    private LocalWaitEnqueueDecision(bool enqueued, string reason)
    {
        Enqueued = enqueued;
        Reason = reason;
    }

    public bool Enqueued { get; }

    public string Reason { get; }

    /// <summary>恒为 false（等待项不产生发送权限；此属性不可写、不可伪造）。</summary>
    public bool SendPermitted => false;

    internal static LocalWaitEnqueueDecision Wait(string reason) => new(true, reason);

    internal static LocalWaitEnqueueDecision NotApplicable(string reason) => new(false, reason);
}

/// <summary>清理决策（**纯函数产物**：不改动输入对象，由调用方/Store 应用）。</summary>
public sealed record LocalWaitCleanup(LocalWaitItem Item, string Reason, DateTimeOffset DecidedAtUtc);

/// <summary>
/// 槲寄生 · R5 批次 6：**本地持久等待**的纯函数部分（登记判定、选择、清理、幂等标识）。
/// 依据 owner 裁决 B.1 与批次 4 的 `RunningOccupancyArbiter`：只有"低优先级本地等待"才入队；
/// 可抢占走抢占流程（不入队）、未知/占用者不可核验保守停驻（不入队、零发送）、空闲走常规准入（不入队）。
/// 纯函数契约：无副作用、不读时钟（时间由调用方传入）、不做 I/O。
/// </summary>
public static class LocalWaitQueuePolicy
{
    /// <summary>按相遇判定决定是否登记等待；等待登记**从不**授予发送权限。</summary>
    public static LocalWaitEnqueueDecision DecideEnqueue(RunningEncounter encounter)
    {
        ArgumentNullException.ThrowIfNull(encounter);
        return encounter.Verdict switch
        {
            RunningEncounterVerdict.WaitLocally => LocalWaitEnqueueDecision.Wait(
                "低优先级本地持久等待（未获准入前零发送，不进入 BGI 执行队列）"),
            RunningEncounterVerdict.PreemptNow => LocalWaitEnqueueDecision.NotApplicable(
                "可抢占：走抢占流程（入队不适用）"),
            RunningEncounterVerdict.ProceedIdle => LocalWaitEnqueueDecision.NotApplicable(
                "已确认空闲：走常规准入（入队不适用）"),
            _ => LocalWaitEnqueueDecision.NotApplicable(encounter.Reason),
        };
    }

    /// <summary>由稳定身份确定性派生等待项标识（同身份 ⇒ 同标识 ⇒ 幂等登记）。</summary>
    public static string DeriveItemId(string stableIdentity)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(stableIdentity ?? string.Empty));
        return "wait-" + Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    /// <summary>
    /// 从等待集合里挑下一个执行者：复用批次 4 的选择规则（最高级→级别→优先级→有值时刻先于 null→身份→候选号），
    /// 但**只考虑** <see cref="LocalWaitItemState.Waiting"/> 项。空集/无 Waiting 项 ⇒ null。
    /// 注意：返回值只表示"下一个应重新比较的对象"，**不含**发送许可——仍需走完整准入。
    /// </summary>
    public static LocalWaitItem? SelectNext(IReadOnlyList<LocalWaitItem>? items)
    {
        var waiting = (items ?? []).Where(i => i.State == LocalWaitItemState.Waiting).ToList();
        if (waiting.Count == 0) return null;
        var next = RunningOccupancyArbiter.SelectNextFromWaitSet(waiting.Select(ToWaitingFacts).ToList());
        return next is null ? null : waiting.FirstOrDefault(i => i.ItemId == next.CandidateId);
    }

    /// <summary>
    /// 清理不再有效的等待项（对应流程/票据失效、前置不满足等）：**纯函数**——不改动输入对象，
    /// 返回"应置为 Cancelled"的决策集合（含原因与决策时刻），由 Store 应用并落盘。
    /// </summary>
    public static List<LocalWaitCleanup> Cleanup(IReadOnlyList<LocalWaitItem>? items,
        Func<LocalWaitItem, string?> invalidationReason, DateTimeOffset nowUtc)
    {
        var cleaned = new List<LocalWaitCleanup>();
        foreach (var item in items ?? [])
        {
            if (item.State != LocalWaitItemState.Waiting) continue;
            if (invalidationReason(item) is not { } reason) continue;
            cleaned.Add(new LocalWaitCleanup(item, reason, nowUtc));
        }
        return cleaned;
    }

    /// <summary>
    /// 把等待项投影为选择规则输入（ItemId 作为候选号参与最终兜底，保证顺序确定）。
    /// **不可信来源不参与级别**：<see cref="LocalWaitItem.HasTrustedIdentity"/> 为 false 时，
    /// 其自报 Tier/Priority/最高级标记一律按**最低**处理（不得据自报值越级）；
    /// 执行前必须重新从可信来源取得排序事实。
    /// </summary>
    private static WaitingRequestFacts ToWaitingFacts(LocalWaitItem item) => new()
    {
        CandidateId = item.ItemId,
        StableIdentity = item.StableIdentity,
        IsHoeingHighest = item.IsHoeingHighest && item.HasTrustedIdentity,
        Tier = item.HasTrustedIdentity ? item.Tier : ArbitrationTier.Plan,
        Priority = item.HasTrustedIdentity ? item.Priority : int.MinValue,
        ScheduledAt = item.ScheduledAt,
        PrerequisiteReady = true,
    };
}
