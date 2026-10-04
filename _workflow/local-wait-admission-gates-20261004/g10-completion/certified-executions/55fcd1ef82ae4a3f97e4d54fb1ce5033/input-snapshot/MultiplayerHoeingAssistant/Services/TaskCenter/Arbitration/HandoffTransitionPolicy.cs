using System;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>交接事件（§4.1/§4.1a 转换判定表的输入；权威事实由调用方核实后传入，本策略不自行探测）。</summary>
public enum HandoffEventKind
{
    /// <summary>取消受理回执到达（≠已退出；受理词为待验证合同）。</summary>
    CancelReceiptAccepted,
    /// <summary>权威退出集合四词（cancelled/failed/succeeded/skipped）到达且身份+目标 epoch+动作关联匹配。</summary>
    AuthoritativeExitMatched,
    /// <summary>A6 挂起/移交事实确认（PreemptionGate/takeover ticket 语义）。</summary>
    A6SuspendHandoverConfirmed,
    /// <summary>抢占方终态（仅触发 settle，不解除压制）。</summary>
    PreemptorTerminal,
    /// <summary>已选择原票据恢复（恢复尚未确认）。</summary>
    RestoreChosen,
    /// <summary>原任务恢复执行已确认（权威快照出现）。</summary>
    RestoreConfirmed,
    /// <summary>已确认不恢复且协议终结。</summary>
    NoRestoreProtocolEnded,
    /// <summary>超时/关联不符/事实未知。</summary>
    TimeoutOrMismatchOrUnknown,
    /// <summary>Stop/F11/epoch 变化（停止状态与票据失效优先）。</summary>
    StopOrF11OrEpochChanged,
    /// <summary>迟到的 succeeded 到达。</summary>
    LateSucceededArrived,
}

/// <summary>交接转换结果（§4.1a 冻结：每种合法后继的压制/准入语义）。</summary>
public sealed record HandoffTransition(
    HandoffPhase Next,
    /// <summary>无关候选压制是否保持（settle/恢复未确认期间必须保持）。</summary>
    bool SuppressionHeld,
    /// <summary>是否允许授权方进入提交准入（仅授权抢占方；调用方负责授权核对）。</summary>
    bool MayAdmitAuthorizedPreemptor,
    /// <summary>是否允许空闲仲裁（Idle）——仅「确认不恢复且协议终结且无占用无未决提交」。原任务恢复走运行跟踪，非 Idle。</summary>
    bool EnterIdle,
    string Reason);

/// <summary>
/// 槲寄生 · R5.1 安全交接转换判定（冻结稿 v5 §4.1/§4.1a 转换表即合同——本类即两表的可测实现）。
/// 纯函数无副作用；§4.1 权威退出判定唯一引用点=IsAuthoritativeExitWord（§8 引用本处，不另立集合）。
/// </summary>
public static class HandoffTransitionPolicy
{
    /// <summary>权威退出集合（§4.1 唯一定义点）：cancelled/failed/succeeded/skipped。
    /// skipped=权威退出语义（交接可放行），业务含义不推断、不映射 skippedUser；映射待真实线路验证（§8/R-8）。
    /// 终态只证明该执行退出——不自动证明租约/F11/票据/其他未决提交允许启动。</summary>
    public static bool IsAuthoritativeExitWord(string? terminal)
        => terminal is "cancelled" or "failed" or "succeeded" or "skipped";

    /// <summary>
    /// 持久化阶段推进合法性（§4.1/§4.1a 转换表的阶段后继子集，唯一引用点——ArbitrationLeaseStore.TryAdvanceIntentPhase 引用本处，不另立白名单）。
    /// 合法对（不含 →None：终态消解一律走 TryResolveIntent 证据路径）：
    /// - from == to：幂等保持（重复事件不退化进度）；
    /// - PreemptRequested→Confirming（取消受理回执）；
    /// - PreemptRequested/Confirming→SettlePending（抢占方终态；Settle/Restore 阶段对重复终态幂等保持，不在此列——
    ///   RestorePending→SettlePending 属恢复责任倒退，冻结表无此后继，禁止经待对账绕行达成）；
    /// - SettlePending→RestorePending（已选择原票据恢复）；
    /// - 任意→ReconcilePending（超时/未知/未冻结转换的保守后继）；
    /// - ReconcilePending→X：调用方须按持久化的 ReconcileFromPhase 以本表校验（视同从原责任阶段出发），禁止降级恢复责任。
    /// </summary>
    public static bool IsLegalPhaseAdvance(HandoffPhase from, HandoffPhase to)
        => from == to
           || (from, to) switch
           {
               (HandoffPhase.PreemptRequested, HandoffPhase.Confirming) => true,
               (HandoffPhase.PreemptRequested or HandoffPhase.Confirming, HandoffPhase.SettlePending) => true,
               (HandoffPhase.SettlePending, HandoffPhase.RestorePending) => true,
               (_, HandoffPhase.ReconcilePending) => true,
               _ => false,
           };

    /// <summary>
    /// 交接转换判定（每条可测）。executionFreeAndNoPending=「无执行占用且无未决提交」权威事实，
    /// 仅 NoRestoreProtocolEnded 的 Idle 后继消费它；false → 保守待对账。
    /// </summary>
    public static HandoffTransition Evaluate(HandoffPhase current, HandoffEventKind evt, bool executionFreeAndNoPending)
    {
        // §4.1：停止状态与票据失效优先；迟到的 succeeded 不撤销已生效停止（B1 实施约束）。
        if (evt == HandoffEventKind.StopOrF11OrEpochChanged)
            return new HandoffTransition(current, true, false, false,
                "停止/F11/epoch 变化优先：交接状态保持，无关候选继续压制，待对账。");
        if (evt == HandoffEventKind.LateSucceededArrived)
            return new HandoffTransition(current, true, false, false,
                "迟到的 succeeded 不撤销已生效停止、不改变交接状态（§4.1）。");
        if (evt == HandoffEventKind.TimeoutOrMismatchOrUnknown)
            return new HandoffTransition(HandoffPhase.ReconcilePending, true, false, false,
                "超时/关联不符/事实未知：保守待对账，禁止新启动（missPolicy 只处置本次触发）。");

        return (current, evt) switch
        {
            // 受理≠已退出：受理只进 Confirming 等待权威事实。
            (HandoffPhase.PreemptRequested, HandoffEventKind.CancelReceiptAccepted) =>
                new(HandoffPhase.Confirming, true, false, false, "取消受理回执≠已退出：进入 Confirming 等权威终态。"),
            // 重复受理回执幂等（会诊二轮 P2-⑤：不得把正常确认流程退化为对账）。
            (HandoffPhase.Confirming, HandoffEventKind.CancelReceiptAccepted) =>
                new(HandoffPhase.Confirming, true, false, false, "重复受理回执：保持 Confirming（幂等，继续等权威终态）。"),
            // §4.1 普通抢占确认：权威退出四词+身份/epoch/关联匹配 → 允许交接（授权方进入提交准入）。
            (HandoffPhase.Confirming, HandoffEventKind.AuthoritativeExitMatched) =>
                new(HandoffPhase.None, false, true, false, "被切执行权威退出已确认（身份+epoch+关联匹配）：允许交接，授权方进入提交准入。"),
            // §4.1a：A6 挂起/移交确认 → 仅授权抢占方进入提交准入（不套用取消重跑路径）。
            (HandoffPhase.PreemptRequested or HandoffPhase.Confirming, HandoffEventKind.A6SuspendHandoverConfirmed) =>
                new(HandoffPhase.None, true, true, false, "A6 挂起/移交已确认：仅授权抢占方进入提交准入（压制保持至 settle）。"),
            // 抢占方终态 → 仅 SettlePending，不直接解除票据压制；
            // settle/restore 期间重复或重放的抢占方终态幂等保持（会诊二轮 P2-⑤：不回退已选择的恢复进度）。
            (HandoffPhase.SettlePending or HandoffPhase.RestorePending, HandoffEventKind.PreemptorTerminal) =>
                new(current, true, false, false, "重复/重放的抢占方终态：保持当前 settle/restore 进度（幂等）。"),
            // 六轮 P2：与 IsLegalPhaseAdvance 唯一判定对齐——仅 PreemptRequested/Confirming 可凭抢占方终态进 SettlePending；
            // None（无交接上下文，事件无从关联）/ReconcilePending（待对账期间事实未澄清，保留原责任约束）/未知枚举
            // 一律落入下方保守兜底 → ReconcilePending，禁止待对账期间凭单一事件降级或重启责任阶段。
            (HandoffPhase.PreemptRequested or HandoffPhase.Confirming, HandoffEventKind.PreemptorTerminal) =>
                new(HandoffPhase.SettlePending, true, false, false, "抢占方终态：仅进入 SettlePending，无关候选保持压制。"),
            // 已选择原票据恢复但恢复未确认 → RestorePending，不得另建替代作业。
            (HandoffPhase.SettlePending, HandoffEventKind.RestoreChosen) =>
                new(HandoffPhase.RestorePending, true, false, false, "已选择原票据恢复（未确认）：RestorePending，保持压制，不得另建替代作业。"),
            // 原任务恢复执行已确认 → 转入原任务运行跟踪，非 Idle。
            (HandoffPhase.RestorePending, HandoffEventKind.RestoreConfirmed) =>
                new(HandoffPhase.None, true, false, false, "原任务恢复执行已确认：转入原任务运行跟踪（非 Idle，不释放占用判定）。"),
            // 确认不恢复且协议终结：须同时确认无执行占用、无未决提交，才允许空闲仲裁。
            (HandoffPhase.SettlePending, HandoffEventKind.NoRestoreProtocolEnded) when executionFreeAndNoPending =>
                new(HandoffPhase.None, false, false, true, "已确认不恢复且协议终结（无占用/无未决提交）：允许空闲仲裁（Idle）。"),
            (HandoffPhase.SettlePending, HandoffEventKind.NoRestoreProtocolEnded) =>
                new(HandoffPhase.ReconcilePending, true, false, false, "协议终结但占用/未决提交未证实清空：保守待对账，禁止推导空闲。"),
            // 其余组合=非法/未知转换 → 保守待对账（禁止推导空闲、禁止新启动）。
            _ => new HandoffTransition(HandoffPhase.ReconcilePending, true, false, false,
                $"未冻结的转换（{current}+{evt}）：保守待对账，禁止新启动。"),
        };
    }
}

/// <summary>提交结果三态（§4.2）：受理/确定拒绝/结果未知。</summary>
public enum SubmissionVerdict
{
    /// <summary>受理（accepted/queued/adopted 三词兼容，ed04355d 已观测）——不等于正在执行。</summary>
    Accepted,
    /// <summary>确定拒绝（既有副作用前拒绝白名单七码，R4.10 IsPreSideEffectRejection）。</summary>
    Rejected,
    /// <summary>结果未知（发送窗口崩溃/超时/result_unknown/无码等）→ 待对账，禁止换键重跑、禁止回 Idle 重提交。</summary>
    Unknown,
}

/// <summary>§4.2 提交结果三态分类（纯函数）。受理词表与既有三词兼容一致；拒绝复用 R4.10 白名单（不另立词表）。</summary>
public static class SubmissionResultClassifier
{
    /// <summary>受理词表（已观测，ext.task.start）：accepted/queued/adopted。</summary>
    public static bool IsAcceptanceWord(string? word)
        => word is "accepted" or "queued" or "adopted";

    /// <summary>分类远端回执词/错误码：受理词→Accepted；副作用前拒绝白名单→Rejected；其余一律 Unknown（不猜）。</summary>
    public static SubmissionVerdict Classify(string? receiptWord, string? errorCode)
    {
        if (IsAcceptanceWord(receiptWord)) return SubmissionVerdict.Accepted;
        if (BgiWorkflowExecutionBoundary.IsPreSideEffectRejection(errorCode)) return SubmissionVerdict.Rejected;
        return SubmissionVerdict.Unknown;
    }
}
