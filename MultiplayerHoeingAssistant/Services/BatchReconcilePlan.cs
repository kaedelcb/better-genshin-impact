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

    /// <summary>配置组名 / 一条龙配置名（下发参数 + 快照按名附着的匹配键）。</summary>
    public string Name { get; }

    public bool IsOneDragon { get; }

    /// <summary>PendingSubmit → Submitted → TerminalConfirmed（单调推进，不回退；纪元失效除外见 Decider）。</summary>
    public BatchItemState State { get; set; } = BatchItemState.PendingSubmit;

    /// <summary>提交应答拿到的 jobId（== taskHandle 别名）。应答帧丢失时为 null，靠按名附着找回。</summary>
    public string? JobId { get; set; }

    /// <summary>提交尝试次数（重提交限次的依据）。</summary>
    public int SubmitAttempts { get; set; }

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
    /// <summary>queued / running / cancelling / succeeded / failed / cancelled / rejected。</summary>
    string State,
    bool WasCancelled,
    string? ErrorCode)
{
    public bool IsTerminal => State is "succeeded" or "failed" or "cancelled" or "rejected";
}

/// <summary>reconcile 一拍的动作输出（调用方据此执行副作用：下发/确认/收尾）。</summary>
public abstract record BatchReconcileAction
{
    /// <summary>提交第 Index 项（首次或重提交）。调用方须用幂等键（rid/key 由 SDK 层保证）。</summary>
    public sealed record Submit(int Index) : BatchReconcileAction;

    /// <summary>第 Index 项的作业在快照中出现（含按名附着找回 jobId）：记录 JobId。</summary>
    public sealed record Attach(int Index, string JobId) : BatchReconcileAction;

    /// <summary>第 Index 项作业已达终态：确认并推进（ cancelled/errorCode 入档）。</summary>
    public sealed record ConfirmTerminal(int Index, bool Cancelled, string? ErrorCode) : BatchReconcileAction;

    /// <summary>第 Index 项已提交但快照中查无此作业（同纪元 not_found = 句柄淘汰/帧丢失）：重提交。</summary>
    public sealed record Resubmit(int Index) : BatchReconcileAction;

    /// <summary>某在跑项被用户取消（F11 语义）：批次按用户取消收尾，不再推进后续项。</summary>
    public sealed record AbortUserCancelled(int Index) : BatchReconcileAction;

    /// <summary>全部期望项终态确认：批次完成，调用方推进完成后动作（RunSpecified 收尾）。</summary>
    public sealed record Complete() : BatchReconcileAction;

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
/// ③jobId 未知的项可经 generation+name 在快照中附着找回（提交应答帧丢失的自愈）。
/// </summary>
public static class BatchReconcileDecider
{
    /// <summary>重提交上限：超过后按失败终态确认（避免死循环重提）。</summary>
    public const int MaxSubmitAttempts = 3;

    public static IReadOnlyList<BatchReconcileAction> Decide(
        IReadOnlyList<BatchExpectedItem> items,
        IReadOnlyList<BatchJobObservation> jobs,
        bool epochMatch,
        int generation)
    {
        var actions = new List<BatchReconcileAction>();
        if (items.Count == 0)
        {
            actions.Add(new BatchReconcileAction.Complete());
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

        // 1) 附着找回：已提交但无 jobId 的项，按 generation+name 在快照里找活跃作业
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (item.State != BatchItemState.Submitted || item.JobId is not null)
            {
                continue;
            }

            var match = jobs.FirstOrDefault(j =>
                j.Generation == generation && string.Equals(j.Name, item.Name, StringComparison.Ordinal));
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
                    // 已附着却查无此作业：同纪元 not_found = 句柄淘汰/应答帧丢失 → 限次重提交
                    if (item.SubmitAttempts < MaxSubmitAttempts)
                    {
                        actions.Add(new BatchReconcileAction.Resubmit(i));
                    }
                    else
                    {
                        actions.Add(new BatchReconcileAction.ConfirmTerminal(i, false, "lost_job"));
                        confirmedThisTick.Add(i);
                    }
                    inFlightIndex = i;
                }
                continue;
            }

            if (!job.IsTerminal)
            {
                inFlightIndex = i; // 在队/在跑：本拍等待
                continue;
            }

            if (job.State == "cancelled" || job.WasCancelled)
            {
                // F11 语义：取消优先——先确认该项终态，再由 Abort 指示收尾（调用方保证不再推进）
                actions.Add(new BatchReconcileAction.ConfirmTerminal(i, true, job.ErrorCode));
                actions.Add(new BatchReconcileAction.AbortUserCancelled(i));
                return actions;
            }

            // succeeded/failed/rejected：确认并推进（failed 记 errorCode，与旧循环"记日志继续下一组"同语义）
            actions.Add(new BatchReconcileAction.ConfirmTerminal(i, false, job.ErrorCode));
            confirmedThisTick.Add(i);
        }

        // 3) 全部确认 → 完成
        if (items.Select((it, idx) => (it, idx))
                 .All(t => t.it.State == BatchItemState.TerminalConfirmed
                           || confirmedThisTick.Contains(t.idx)))
        {
            actions.Add(new BatchReconcileAction.Complete());
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
