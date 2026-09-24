namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// [A4.2] 批次内一个期望项（配置组/一条龙）的跟踪状态。
/// 串行语义：仅当 0..i-1 全部 TerminalConfirmed 才提交第 i 项（单泵串行的助手侧镜像）。
/// </summary>
public sealed class BatchExpectedItem
{
    public BatchExpectedItem(string name, bool isOneDragon)
    {
        Name = name;
        IsOneDragon = isOneDragon;
    }

    /// <summary>配置组名 / 一条龙配置名（下发参数用）。注意：快照附着**不按名**匹配，实际按 RequestKey（见 Decider 注释）。</summary>
    public string Name { get; }

    public bool IsOneDragon { get; }

    /// <summary>PendingSubmit → Submitted → TerminalConfirmed（单调推进，不回退；纪元失效除外见 Decider）。</summary>
    public BatchItemState State { get; set; } = BatchItemState.PendingSubmit;

    /// <summary>提交应答拿到的 jobId（== taskHandle 别名）。应答帧丢失时为 null，靠 RequestKey 幂等键附着找回。</summary>
    public string? JobId { get; set; }
    public string RequestKey { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>提交尝试次数（重提交限次的依据）。</summary>
    public int SubmitAttempts { get; set; }

    /// <summary>[A6] 可重试瞬态拒绝/失败（queue_full、task_busy、preempt_timeout）的已重试次数。
    /// 与 SubmitAttempts 分列：拒绝重试不消耗"已附着作业丢失"的重提交预算（ADR-2026-09-16 第 4 条）。</summary>
    public int RejectionRetries { get; set; }

    /// <summary>[A6] 首次可重试瞬态拒绝/失败的 UTC 时间（单项 2 分钟重试预算的起点）；null = 尚未发生。</summary>
    public DateTime? FirstRejectionUtc { get; set; }

    /// <summary>终态确认时的取消标记（F11 语义：批次应用户取消收尾而非成功收尾）。</summary>
    public bool? TerminalWasCancelled { get; set; }

    /// <summary>终态确认时的 errorCode（failed 时非空）。</summary>
    public string? TerminalErrorCode { get; set; }

    /// <summary>是否真正被 BGI 接受启动过（提交成功/幂等命中/按名附着找回均算）。
    /// 业务拒绝（如配置组不存在）保持 false——空批次（全部项未启动）不得触发 RunSpecified 收尾，
    /// 否则"完成后执行指定任务"会在一条都没锄的情况下误启动指定任务（实机事故 2026-09-13）。</summary>
    public bool Started { get; set; }
}

public enum BatchItemState
{
    PendingSubmit,
    Submitted,
    TerminalConfirmed,
}

/// <summary>
/// [A4.2] reconcile 决策的观察输入（BGI 作业快照的最小投影）。
/// 刻意不引用 SDK 类型（BgiJobInfo），保持纯 BCL 可单测；由调用方从 ext.job.list 应答转换。
/// </summary>
public sealed record BatchJobObservation(
    string JobId,
    string? Name,
    int? Generation,
    /// <summary>queued / running / cancelling / succeeded / failed / cancelled / rejected。
    /// [A6] BGI 新增终态（如让位 preempted 以 cancelled+errorCode=preempted 投影）按字符串透传：
    /// 未知值不进 IsTerminal（按非成功等待处理），绝不因新值崩溃。</summary>
    string State,
    bool WasCancelled,
    string? ErrorCode,
    string? IdempotencyKey = null,
    string? Kind = null,
    string? ParentJobId = null)
{
    public bool IsTerminal => State is "succeeded" or "failed" or "cancelled" or "rejected";
}

/// <summary>[A6] 提交拒绝/作业失败的错误码分类（ADR-2026-09-16 第 4 条：瞬态拒绝分类）。</summary>
public enum BatchSubmitRejectionKind
{
    /// <summary>可重试瞬态：queue_full / task_busy / preempt_timeout——单项 2 分钟预算内退避重试。</summary>
    RetryableTransient,

    /// <summary>终态 + 响亮通知（绝不自动重发）：manual_stop_cooldown——F11 是用户最终权威（D1，d4dc54a9 教训）。</summary>
    ManualStopCooldown,

    /// <summary>其余永久性拒绝/失败：维持既有终态跳过语义。</summary>
    Permanent,
}

/// <summary>[D5] 一个"未决项"的证据条目（只陈述事实，不改写结果）。
/// ErrorCode 原样保留（例如 lost_job/result_unknown/task_failed 等），**不得**改写成成功，
/// **也不得**据以断言"已证实失败"。</summary>
public sealed record BatchUnresolvedItem(int Index, string Name, bool Cancelled, string? ErrorCode)
{
    /// <summary>诊断用稳定文本：`[未决:组A=lost_job]`（取消项为 `[未决:组A=cancelled]`）。</summary>
    public override string ToString()
        => $"[未决:{Name}={(ErrorCode is null ? (Cancelled ? "cancelled" : "unknown") : Cancelled ? $"{ErrorCode},cancelled" : ErrorCode)}]";
}

/// <summary>reconcile 一拍的动作输出（调用方据此执行副作用：下发/确认/收尾）。</summary>
public abstract record BatchReconcileAction
{
    /// <summary>提交第 Index 项（首次或重提交）。调用方须用幂等键（rid/key 由 SDK 层保证）。</summary>
    public sealed record Submit(int Index) : BatchReconcileAction;

    /// <summary>第 Index 项的作业在快照中出现（按 RequestKey 幂等键附着找回 jobId）：记录 JobId。</summary>
    public sealed record Attach(int Index, string JobId) : BatchReconcileAction;

    /// <summary>第 Index 项作业已达终态：确认并推进（ cancelled/errorCode 入档）。</summary>
    public sealed record ConfirmTerminal(int Index, bool Cancelled, string? ErrorCode) : BatchReconcileAction;

    /// <summary>第 Index 项已提交但快照中查无此作业（同纪元 not_found = 句柄淘汰/帧丢失）：按同一请求键重提交。</summary>
    public sealed record Resubmit(int Index) : BatchReconcileAction;

    /// <summary>[A6] 第 Index 项作业终态失败但错误码是可重试瞬态（preempt_timeout 等）且在重试预算内：
    /// 退回 PendingSubmit 由后续拍退避重发。调用方应用：State=PendingSubmit、JobId=null、RejectionRetries++、FirstRejectionUtc ??= now。</summary>
    public sealed record RetryFromFailure(int Index, string ErrorCode) : BatchReconcileAction;

    /// <summary>某在跑项被用户取消（F11 语义）：批次按用户取消收尾，不再推进后续项。</summary>
    public sealed record AbortUserCancelled(int Index) : BatchReconcileAction;

    /// <summary>[D5] 批次收口 - 全成功：**全部期望项均已确认为成功终态**（每项 Started ∧ TerminalConfirmed
    /// ∧ TerminalWasCancelled != true ∧ TerminalErrorCode == null）。
    /// **唯一**获准触发"完成后动作"（RunSpecified 收尾）的完成动作；调用方**只**可对本动作执行收尾。
    /// 空批次（0 期望项）落在本类（空集合的全成功为真）——其安全另有调用方的空批次守卫，
    /// 见 BatchExpectedItem.Started 的事故注解（2026-09-13）：本动作**不**代表"真的锄过地"。</summary>
    public sealed record CompleteSucceeded() : BatchReconcileAction;

    /// <summary>[D5] 批次收口 - 有未确认/未决项：全部期望项已到终态，但**至少一项**不是可证成功
    /// （lost_job / result_unknown / task_failed / 取消收尾 / 从未真正启动的业务拒绝项等）。
    /// **不携带任何"成功/可收尾"许可**：调用方**不得**据此执行 RunSpecified 收尾。
    /// Unresolved 逐项承载事实证据（名字 + 原样 errorCode + 取消标记），调用方可据此显示/登记；
    /// 未决**不等于**已证实失败，也不得被改写成成功。存在本动作时调用方必须走"未成功收尾"路径
    /// （与既有 coordinated 失败/取消收尾一致），仍不得推进生产门或 R5.8 签署。</summary>
    public sealed record CompleteWithUnresolved(IReadOnlyList<BatchUnresolvedItem> Unresolved) : BatchReconcileAction;

    /// <summary>本拍无事可做（有项在队/在跑，等事件或下一拍）。</summary>
    public sealed record Wait() : BatchReconcileAction;

    /// <summary>BGI 纪元变化（进程重启）：Submitted 未确认项全部退回 PendingSubmit 重对账（§4.2）。</summary>
    public sealed record EpochChanged() : BatchReconcileAction;
}

/// <summary>
/// [A4.2] 批次 reconcile 决策器（K8s 控制器风格水平触发对账，总计划 §4.5）：
/// 输入 = 期望批次（含状态）+ BGI 注册表快照 + 纪元匹配标记；输出 = 本拍动作序列。
/// 纯函数零副作用零依赖——批次推进、F11 取消、终态确认的判定全部集中在此，单测可穷尽。
/// 不变量：①同时至多一项在飞（串行）；②已 TerminalConfirmed 的项是事实，任何情况下不回退；
/// ③jobId 未知的项可经 **RequestKey（幂等键）** 在快照中附着找回（提交应答帧丢失的自愈）。
/// [A6] ④瞬态拒绝/失败分类（ClassifySubmitRejection/CanRetryRejection）：可重试码在单项 2 分钟
/// 预算内输出 RetryFromFailure 退回重发；manual_stop_cooldown 与未知码绝不重试（ADR-2026-09-16）。
/// [D5] ⑤完成类型拆分：批次收口只有两种动作——CompleteSucceeded（全部项可证成功，**唯一**可触发
/// 完成后动作/RunSpecified 收尾）与 CompleteWithUnresolved（全部项已到终态但存在未确认/未决项，
/// **不授予**成功或收尾许可）。未知结果绝不改写为成功，也不改写为已证实失败。
/// </summary>
public static class BatchReconcileDecider
{
    /// <summary>重提交上限：超过后按失败终态确认（避免死循环重提）。</summary>
    public const int MaxSubmitAttempts = 3;

    /// <summary>[A6] 可重试瞬态拒绝/失败的单项重试时间预算（ADR-2026-09-16：每项 2min）。</summary>
    public static readonly TimeSpan RejectionRetryBudget = TimeSpan.FromMinutes(2);

    /// <summary>[A6] 可重试瞬态拒绝/失败的重试次数硬帽（沿用 MaxSubmitAttempts 风格，
    /// 防止事件高频唤醒时在时间预算内无限拍重发）。</summary>
    public const int MaxRejectionRetries = 12;

    /// <summary>[A6] 提交拒绝/作业失败错误码分类（ADR-2026-09-16 第 4 条）。null/未知码 → Permanent。</summary>
    public static BatchSubmitRejectionKind ClassifySubmitRejection(string? errorCode) => errorCode switch
    {
        "queue_full" or "task_busy" or "preempt_timeout" => BatchSubmitRejectionKind.RetryableTransient,
        "manual_stop_cooldown" => BatchSubmitRejectionKind.ManualStopCooldown,
        _ => BatchSubmitRejectionKind.Permanent,
    };

    /// <summary>[A6] 可重试瞬态拒绝/失败是否仍在重试预算内（次数硬帽 ∧ 单项 2 分钟时间预算）。
    /// 非可重试码恒 false；firstRejectionUtc 为预算起点（调用方在首次拒绝时落档）。</summary>
    public static bool CanRetryRejection(string? errorCode, int retriesSoFar, DateTime firstRejectionUtc, DateTime nowUtc)
        => ClassifySubmitRejection(errorCode) == BatchSubmitRejectionKind.RetryableTransient
           && retriesSoFar < MaxRejectionRetries
           && nowUtc - firstRejectionUtc < RejectionRetryBudget;

    public static IReadOnlyList<BatchReconcileAction> Decide(
        IReadOnlyList<BatchExpectedItem> items,
        IReadOnlyList<BatchJobObservation> jobs,
        bool epochMatch,
        int generation,
        DateTime nowUtc)
    {
        var actions = new List<BatchReconcileAction>();
        // [D5] 空批次（0 期望项）归类：**CompleteSucceeded**。依据：全成功判据是"全部项均可证成功"，
        // 空集合上为真（vacuous truth），且空批次不含任何未确认结果，故不属"有未决"。
        // 但这**不等于**"真的锄过地"：空批次误触发 RunSpecified 的实机事故（2026-09-13）由调用方的
        // BatchExpectedItem.Started 守卫兜住——本决策器的职责只是"有无未决结果"，不承担"是否值得收尾"。
        if (items.Count == 0)
        {
            actions.Add(new BatchReconcileAction.CompleteSucceeded());
            return actions;
        }

        // 纪元变化：Submitted 未确认项的 jobId 全部失效（BGI 重启 = 在跑在队全丢），退回待提交
        if (!epochMatch)
        {
            if (items.Any(i => i.State == BatchItemState.Submitted))
            {
                actions.Add(new BatchReconcileAction.EpochChanged());
                return actions;
            }
            // 无在飞项时纪元失配无实际影响（新提交本来就面向新纪元），照常决策
        }

        var jobsById = jobs.GroupBy(j => j.JobId).ToDictionary(g => g.Key, g => g.First());
        // 本拍内确认的项（动作由调用方正式应用；此处仅作本拍后续步骤的推进依据，绝不改写 items 状态）
        var confirmedThisTick = new HashSet<int>();

        // 1) 附着找回：已提交但无 jobId 的项，按 RequestKey（幂等键）在快照里找活跃作业
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.State != BatchItemState.Submitted || item.JobId is not null)
            {
                continue;
            }

            var match = jobs.FirstOrDefault(j => j.IdempotencyKey == item.RequestKey);
            if (match is not null)
            {
                actions.Add(new BatchReconcileAction.Attach(i, match.JobId));
            }
        }

        // 2) 终态确认 / 取消检测 / 丢失重提交（只看已附着 jobId 的在飞项）
        var inFlightIndex = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.State != BatchItemState.Submitted)
            {
                continue;
            }

            BatchJobObservation? job = item.JobId is not null && jobsById.TryGetValue(item.JobId, out var byId)
                ? byId
                : null;
            // Attach 刚找回的项本拍即可用
            if (job is null && item.JobId is null)
            {
                var attach = actions.OfType<BatchReconcileAction.Attach>().FirstOrDefault(a => a.Index == i);
                if (attach is not null && jobsById.TryGetValue(attach.JobId, out var attached))
                {
                    job = attached;
                }
            }

            if (job is null)
            {
                if (item.JobId is not null)
                {
                    // A known accepted handle disappearing does not prove it never executed.
                    // Never replay side effects after terminal eviction or lost authority.
                    actions.Add(new BatchReconcileAction.ConfirmTerminal(i, false, "lost_job"));
                    confirmedThisTick.Add(i);
                    inFlightIndex = i;
                }
                else if (item.SubmitAttempts < MaxSubmitAttempts)
                {
                    // No acknowledgement: reuse the same request key while the server's
                    // idempotency window is valid, never create another execution attempt.
                    actions.Add(new BatchReconcileAction.Resubmit(i));
                    inFlightIndex = i;
                }
                else
                {
                    actions.Add(new BatchReconcileAction.ConfirmTerminal(i, false, "result_unknown"));
                    confirmedThisTick.Add(i);
                }
                continue;
            }

            if (!job.IsTerminal)
            {
                inFlightIndex = i; // 在队/在跑：本拍等待
                continue;
            }

            if ((job.State == "cancelled" || job.WasCancelled)
                && (job.ErrorCode is null or "cancelled_user" or "manual_stop_cooldown"))
            {
                // F11 语义：取消优先——先确认该项终态，再由 Abort 指示收尾（调用方保证不再推进）
                actions.Add(new BatchReconcileAction.ConfirmTerminal(i, true, job.ErrorCode));
                actions.Add(new BatchReconcileAction.AbortUserCancelled(i));
                return actions;
            }

            // [A6] 终态失败/拒绝但错误码是可重试瞬态（preempt_timeout 等）且预算内：
            // 退回重发（调用方应用后退避到下一拍），不按终态收口。预算耗尽落入下方常规终态确认。
            if (job.State != "succeeded"
                && CanRetryRejection(job.ErrorCode, item.RejectionRetries, item.FirstRejectionUtc ?? nowUtc, nowUtc))
            {
                actions.Add(new BatchReconcileAction.RetryFromFailure(i, job.ErrorCode!));
                inFlightIndex = i; // 仍视为在飞：本拍不推进后续项，退回重发由调用方应用
                continue;
            }

            // succeeded/failed/rejected：确认并推进（failed 记 errorCode，与旧循环"记日志继续下一组"同语义）
            actions.Add(new BatchReconcileAction.ConfirmTerminal(i, job.WasCancelled,
                job.ErrorCode ?? (job.State == "succeeded" ? null : "task_failed")));
            confirmedThisTick.Add(i);
        }

        // 3) 全部确认 → 完成（[D5] 拆分两类）
        if (items.Select((it, idx) => (it, idx))
                 .All(t => t.it.State == BatchItemState.TerminalConfirmed
                           || confirmedThisTick.Contains(t.idx)))
        {
            // [D5 必改（gpt-6-sol 评审第 1 项）] 未决判据必须按"本拍动作已应用后"的逐项状态分类，
            // **不能**只读 items 上的旧字段：本拍新 ConfirmTerminal 的项在 items 里仍是确认前状态
            // （TerminalWasCancelled/TerminalErrorCode 依旧为 null），只读旧字段会把本拍才确认的
            // lost_job / result_unknown / task_failed / 重试耗尽的 preempt_timeout 误判为"可证成功"，
            // 从而在同一拍产出 CompleteSucceeded —— 直接违反"只有全成功才能执行完成后动作"。
            // 故此处先做**本拍确认投影**（confirmedThisTick 内的项以本拍动作载荷为准），再逐项判未决。
            var pendingConfirm = actions.OfType<BatchReconcileAction.ConfirmTerminal>()
                .GroupBy(c => c.Index).ToDictionary(g => g.Key, g => g.Last());

            (bool Started, bool Cancelled, string? ErrorCode) Projected(int idx)
            {
                if (pendingConfirm.TryGetValue(idx, out var confirm))
                {
                    // 本拍确认：结果以本拍动作为准（不可证成功者一律入未决）
                    return (true, confirm.Cancelled, confirm.ErrorCode);
                }

                var it = items[idx];
                return (it.Started, it.TerminalWasCancelled == true, it.TerminalErrorCode);
            }

            // 未决判据逐项展开（"可证成功"= 真正启动过 ∧ 未取消收尾 ∧ 无 errorCode）：
            //   a. Started == false：业务拒绝（配置组不存在等）→ 从未执行，不构成成功；
            //   b. Cancelled == true：应用户取消收尾（F11）→ 不是成功收尾；
            //   c. ErrorCode != null：lost_job / result_unknown / task_failed / 瞬态重试耗尽后的
            //      preempt_timeout|queue_full 等 → 结果未知或非成功，**原样承载**，不改写成成功或"已证实失败"。
            // 本分支的**可达子情形**说明：
            //   ① 本拍带用户取消的项在第 257–264 行已提前 return，故此处不会出现"本拍新确认的取消项"；
            //      仍可出现"此前各拍已 TerminalConfirmed 的取消项"（用户取消 ⇒ CompleteWithUnresolved）。
            //   ② 真实失败（failed/rejected）、本拍 lost_job/result_unknown、本拍瞬态重试耗尽后的终态确认，
            //      经上面的本拍投影后都会命中 (c) ⇒ CompleteWithUnresolved。
            //   ③ 只有全部项 (a)(b)(c) 投影后都不成立时才产出 CompleteSucceeded —— 即 D5 要求的"仅全成功"。
            var unresolved = items.Select((it, idx) => (it, idx))
                .Select(t =>
                {
                    var p = Projected(t.idx);
                    return (t.it, t.idx, p.Started, p.Cancelled, p.ErrorCode);
                })
                .Where(t => !(t.Started && !t.Cancelled && t.ErrorCode is null))
                .Select(t => new BatchUnresolvedItem(t.idx, t.it.Name, t.Cancelled, t.ErrorCode))
                .ToList();
            if (unresolved.Count == 0)
            {
                actions.Add(new BatchReconcileAction.CompleteSucceeded());
            }
            else
            {
                actions.Add(new BatchReconcileAction.CompleteWithUnresolved(unresolved));
            }
            return actions;
        }

        // 4) 有在飞项 → 等待
        if (inFlightIndex >= 0)
        {
            actions.Add(new BatchReconcileAction.Wait());
            return actions;
        }

        // 5) 提交下一项（0..i-1 已全部确认的第一个 PendingSubmit）
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.State == BatchItemState.PendingSubmit)
            {
                actions.Add(new BatchReconcileAction.Submit(i));
                return actions;
            }
            if (item.State == BatchItemState.Submitted && !confirmedThisTick.Contains(i))
            {
                // 无 jobId 且快照未命中的待附着项：等下一拍（附着或重提交判定需要新快照）
                actions.Add(new BatchReconcileAction.Wait());
                return actions;
            }
        }

        // 防御：不可达（全部确认已在 3 返回）；保守等待
        actions.Add(new BatchReconcileAction.Wait());
        return actions;
    }
}
