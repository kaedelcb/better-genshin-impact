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
/// **[批次 16／D2] 单项前置就绪求值结果**（纯函数产物，**不改动**等待项）。
/// 只承载「哪一项、引用是什么、求值结论是什么、为什么」——**不含**任何发送许可或执行承诺。
/// </summary>
public sealed class LocalWaitPrerequisiteEvaluation
{
    /// <summary>被求值的等待项（原对象；本求值不改动它）。</summary>
    public LocalWaitItem Item { get; init; } = null!;

    /// <summary>就绪结论（三态；不可判定与未就绪**一律不参选**）。</summary>
    public PrerequisiteReadiness Readiness { get; init; } = PrerequisiteReadiness.Undetermined;

    /// <summary>本次求值用到的持久化稳定引用串（null ⇒ 缺引用）。</summary>
    public string? Reference { get; init; }

    /// <summary>人类可读原因（审计用；不得承载发送许可或成功断言）。</summary>
    public string Reason { get; init; } = "";

    /// <summary>是否可参选（**唯一**允许参选的结论是 <see cref="PrerequisiteReadiness.Ready"/>）。</summary>
    public bool Selectable => Readiness == PrerequisiteReadiness.Ready;
}


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
    ///
    /// **[批次 16／D2] 前置就绪不再由过期布尔承载**：就绪**只**由调用方注入的**只读 evaluator** 求得
    /// （见带 <see cref="PrerequisiteEvaluator"/> 的重载）。
    ///
    /// **[批次 16／D2] 旧签名 `SelectNext(items)` 已**废除**，但为满足批约束「冻结合同只允许纯加法」而**保留公开签名**：
    /// 见本类中标注 <c>[Obsolete(error: true)]</c> 的同名重载（调用即抛异常，**不**实现旧语义）。
    /// 保留仅为「签名集合不做减法」；**不**表示 owner 对该重载另有裁决——owner 裁决原文未提及本重载。
    /// **旧语义（缺前置引用 ⇒ 视为已就绪）绝不复活**——它正是 §24.106 C4 的恒 true 硬编码缺陷。
    /// 缺引用／缺求值器**一律不可判定 ⇒ 不参选**；需要选择等待项的调用方**必须**显式注入只读求值器，
    /// 且选择结果仍**只**表示「下一个应重新走完整准入的对象」（不含发送许可）。
    /// </summary>
    /// <remarks>
    /// **[批次 16／D2] 逐步语义（只读）**：
    /// **①保守默认**：<paramref name="evaluator"/> 为 null、或缺引用、或求值器给出未就绪／不可判定
    /// ⇒ 该项不参选（不得当作已就绪，也不得当作「空闲」而抢发）。
    /// **②`ScheduledAt` 仍只参与排序**：未来时刻不阻止参选，也不构成就绪证明。
    /// **③返回值只表示「下一个应重新走完整准入的对象」**，不含发送许可。
    /// **④只读求值视图重载**：<see cref="SelectNextView"/> 与「发送前再次验算」复用**同一**求值结果类型。
    /// </remarks>
    public static LocalWaitItem? SelectNext(IReadOnlyList<LocalWaitItem>? items, PrerequisiteEvaluator? evaluator)
        => SelectNextView(items, ToReadOnlyEvaluator(evaluator));

    /// <summary>
    /// **[批次 16／D2] 旧签名 `SelectNext(items)` 已**废除**，但为满足「冻结合同只允许纯加法」而**保留公开签名**。**
    ///
    /// 保留原因：批约束要求冻结合同**不得做减法**（既有公开签名的删除须 owner 另裁为合同例外）；
    /// 直接删除公开静态方法即构成一次签名减法。故以 <see cref="ObsoleteAttribute"/>（<c>error: true</c>）
    /// **编译期**封死（任何源码调用点立即编译失败），并在**运行期**（反射／旧二进制调用）抛出明确异常。
    ///
    /// **绝不实现旧语义**：旧实现把前置就绪硬编码为 <c>true</c>（§24.106 C4 的恒 true 缺陷）。
    /// 因此本重载**没有任何**「缺引用即视为已就绪」的退回路径——它只会抛异常，要求调用方显式注入只读 evaluator。
    /// </summary>
    [Obsolete("批次 16／D2：`SelectNext(items)` 已废除（前置就绪改由注入的只读 evaluator 求得）。"
        + "请改用 `SelectNext(items, evaluator)`；本重载仅为保留二进制入口而存在，调用即抛异常。", error: true)]
    public static LocalWaitItem? SelectNext(IReadOnlyList<LocalWaitItem>? items)
        => throw new NotSupportedException(
            "批次 16／D2：`SelectNext(items)` 已废除——禁止「缺前置引用 ⇒ 视为已就绪」的旧语义"
            + "（§24.106 C4 恒 true 缺陷）。请改用 `SelectNext(items, evaluator)` 显式注入只读求值器"
            + "（缺引用／缺求值器一律不可判定 ⇒ 不参选）。");

    /// <summary>
    /// **[批次 16／D2] 选择下一个候选（只读求值视图重载）。**
    /// 与 <see cref="SelectNext(IReadOnlyList{LocalWaitItem}, PrerequisiteEvaluator)"/> 同语义；
    /// 提供视图重载以便「发送前再次验算」与选择阶段复用**同一**求值结果类型。
    /// </summary>
    internal static LocalWaitItem? SelectNextView(IReadOnlyList<LocalWaitItem>? items, Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        var waiting = (items ?? []).Where(i => i.State == LocalWaitItemState.Waiting).ToList();
        if (waiting.Count == 0) return null;
        var next = RunningOccupancyArbiter.SelectNextFromWaitSet(
            waiting.Select(i => ToWaitingFacts(i, evaluator)).ToList());
        return next is null ? null : waiting.FirstOrDefault(i => i.ItemId == next.CandidateId);
    }

    /// <summary>
    /// **[批次 16／D2] 只读逐项求值入口**（选择阶段与**发送前再次验算**共用）。
    /// **纯函数**：不改动任何等待项、不读时钟、不做 I/O、不产生发送。
    /// 非 <see cref="LocalWaitItemState.Waiting"/> 项**不参与**（取消项不复活）。
    /// </summary>
    public static List<LocalWaitPrerequisiteEvaluation> EvaluatePrerequisites(
        IReadOnlyList<LocalWaitItem>? items, PrerequisiteEvaluator? evaluator)
        => EvaluatePrerequisitesViewCore(items, ToReadOnlyEvaluator(evaluator));

    /// <summary>
    /// **[批次 16／D2] 只读逐项求值入口（模型层视图重载）**：与
    /// <see cref="EvaluatePrerequisites(IReadOnlyList{LocalWaitItem}, PrerequisiteEvaluator)"/>
    /// **同语义**，返回值类型为模型层 <see cref="MultiplayerHoeingAssistant.Models.PrerequisiteEvaluation"/>
    /// （便于纯反射契约逐项读取）。**本批不添加任何生产消费点**。
    /// </summary>
    internal static List<MultiplayerHoeingAssistant.Models.PrerequisiteEvaluation> EvaluatePrerequisitesModelView(
        IReadOnlyList<LocalWaitItem>? items, Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        var results = new List<MultiplayerHoeingAssistant.Models.PrerequisiteEvaluation>();
        foreach (var item in items ?? [])
        {
            if (item is null) continue;
            if (item.State != LocalWaitItemState.Waiting) continue;
            var (readiness, reason) = EvaluateOne(item, evaluator);
            results.Add(new MultiplayerHoeingAssistant.Models.PrerequisiteEvaluation
            {
                Item = item,
                ItemId = item.ItemId,
                Readiness = readiness,
                Reference = item.PrerequisiteReference,
                Reason = reason,
            });
        }
        return results;
    }

    /// <summary>
    /// **[批次 16／D2] 只读逐项求值入口（服务层视图重载）**——见上；返回
    /// <see cref="LocalWaitPrerequisiteEvaluation"/>，供服务层读 <c>Readiness</c>／<c>Selectable</c>。
    /// </summary>
    public static List<LocalWaitPrerequisiteEvaluation> EvaluatePrerequisitesView(
        IReadOnlyList<LocalWaitItem>? items, PrerequisiteEvaluator? evaluator)
        => EvaluatePrerequisitesViewCore(items, ToReadOnlyEvaluator(evaluator));

    /// <summary>
    /// **[批次 16／D2] 只读逐项求值入口（服务层视图重载；只读求值视图参数）**——见上。
    /// </summary>
    internal static List<LocalWaitPrerequisiteEvaluation> EvaluatePrerequisitesViewCore(
        IReadOnlyList<LocalWaitItem>? items, Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        var results = new List<LocalWaitPrerequisiteEvaluation>();
        foreach (var item in items ?? [])
        {
            if (item is null) continue;
            if (item.State != LocalWaitItemState.Waiting) continue;
            var (readiness, reason) = EvaluateOne(item, evaluator);
            results.Add(new LocalWaitPrerequisiteEvaluation
            {
                Item = item,
                Readiness = readiness,
                Reference = item.PrerequisiteReference,
                Reason = reason,
            });
        }
        return results;
    }

    /// <summary>
    /// **[批次 16／D2] 发送前再次验算**（§24.106 **C5**）：被选出的项在取得发送许可前**重新**求值一次。
    ///
    /// **不得沿用排队时快照直接发送**：本方法**只**依据**当前**引用与**当前**求值器给出结论；
    /// 调用方若以排队时刻的 `PrerequisiteReadiness` 替代本结论，即违反 D2 裁决 A。
    ///
    /// **结论永远不含发送许可**：<see cref="MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision.RequiresFullAdmission"/>
    /// 恒 true——无论通过与否，唯一合法后继都是「重新走一次完整准入」。
    /// **不越过 D1 出口**：本方法**不**发送、**不**给出可发送判定，也**不**把未知结果改写成成功或已证实失败。
    /// </summary>
    public static MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision RevalidateBeforeSend(
        LocalWaitItem item, PrerequisiteEvaluator? evaluator)
        => RevalidateBeforeSend(item, ToReadOnlyEvaluator(evaluator));

    /// <summary>
    /// **[批次 16／D2] 发送前再次验算（只读求值视图重载）**——见上；返回**模型层登记面**类型
    /// <see cref="MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision"/>，
    /// `RequiresFullAdmission` 恒 true。
    /// </summary>
    public static MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision RevalidateBeforeSend(
        LocalWaitItem item, Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        ArgumentNullException.ThrowIfNull(item);
        var (readiness, reason) = item.State == LocalWaitItemState.Waiting
            ? EvaluateOne(item, evaluator)
            : (PrerequisiteReadiness.NotReady, "等待项不在 Waiting 状态：不得据排队快照发送，须重新走完整准入");
        return new MultiplayerHoeingAssistant.Models.LocalWaitPrerequisiteDecision
        {
            Item = item,
            ItemId = item.ItemId,
            Readiness = readiness,
            Reference = item.PrerequisiteReference,
            Reason = reason,
        };
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

    /// <summary>把 <see cref="PrerequisiteEvaluator"/> 适配为只读求值视图（null ⇒ null＝不可判定）。</summary>
    private static Func<LocalWaitItem, PrerequisiteReadiness>? ToReadOnlyEvaluator(PrerequisiteEvaluator? evaluator)
        => evaluator is null ? null : item => evaluator(item.PrerequisiteReference);

    /// <summary>
    /// **[批次 16／D2] 单项前置就绪求值（唯一判定点）**——三态，保守方向唯一：
    /// ①无求值器 ⇒ 不可判定；②缺引用／引用不可求值 ⇒ 不可判定；③求值器抛异常 ⇒ 不可判定（不得让异常逃逸成"已就绪"）；
    /// ④其余取求值器结论。
    /// **`ScheduledAt` 不参与本判定**（它只参与排序）。
    /// </summary>
    private static (PrerequisiteReadiness Readiness, string Reason) EvaluateOne(
        LocalWaitItem item, Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        if (evaluator is null)
            return (PrerequisiteReadiness.Undetermined, "未注入只读 evaluator：前置就绪不可判定（保守不参选）");
        if (string.IsNullOrWhiteSpace(item.PrerequisiteReference))
            return (PrerequisiteReadiness.Undetermined, "缺持久化稳定前置引用串（或为空）：不可判定（保守不参选）");

        PrerequisiteReadiness readiness;
        try
        {
            readiness = evaluator(item);
        }
        catch (Exception)
        {
            // 求值失败**不得**被读成「已就绪」或「空闲」；如实落不可判定
            return (PrerequisiteReadiness.Undetermined, "只读 evaluator 抛异常：不可判定（保守不参选）");
        }

        return readiness switch
        {
            PrerequisiteReadiness.Ready => (readiness, "前置已就绪（可重新走完整准入；**不等于**已获发送许可）"),
            PrerequisiteReadiness.NotReady => (readiness, "前置已确定未就绪：不参选"),
            _ => (PrerequisiteReadiness.Undetermined, "只读 evaluator 未给出结论：不可判定（保守不参选）"),
        };
    }

    /// <summary>
    /// 把等待项投影为选择规则输入（ItemId 作为候选号参与最终兜底，保证顺序确定）。
    /// **不可信来源不参与级别**：<see cref="LocalWaitItem.HasTrustedIdentity"/> 为 false 时，
    /// 其自报 Tier/Priority/最高级标记一律按**最低**处理（不得据自报值越级）；
    /// 执行前必须重新从可信来源取得排序事实。
    ///
    /// **[批次 16／D2] `PrerequisiteReady` 不再恒 true**：它由 <paramref name="evaluator"/> 在**读取时**求得；
    /// 未就绪与**不可判定一律不参选**（§24.106 C4 闭合点）。
    /// **`ScheduledAt` 仍只参与排序**：未来时刻不阻止参选，也不构成就绪证明。
    /// </summary>
    private static WaitingRequestFacts ToWaitingFacts(LocalWaitItem item,
        Func<LocalWaitItem, PrerequisiteReadiness>? evaluator)
    {
        var (readiness, _) = EvaluateOne(item, evaluator);
        return new WaitingRequestFacts
        {
            CandidateId = item.ItemId,
            StableIdentity = item.StableIdentity,
            IsHoeingHighest = item.IsHoeingHighest && item.HasTrustedIdentity,
            Tier = item.HasTrustedIdentity ? item.Tier : ArbitrationTier.Plan,
            Priority = item.HasTrustedIdentity ? item.Priority : int.MinValue,
            ScheduledAt = item.ScheduledAt,
            // 只有「已确定就绪」可参选；未就绪与不可判定都不参选（保守方向唯一）
            PrerequisiteReady = readiness == PrerequisiteReadiness.Ready,
        };
    }
}
