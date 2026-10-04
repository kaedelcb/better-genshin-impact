using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>节点出现身份：nodeId + 序列位置 + 出现序号 + 循环轮次（§3.4 身份合同在流程侧的落地）。</summary>
public sealed record WorkflowNodeOccurrence(string NodeId, int SequenceIndex, int Occurrence, int LoopIteration);

/// <summary>节点闸门动作：放行 / 过滤跳过（不阻塞后续）/ 响亮拒绝。</summary>
public enum NodeGateAction
{
    Proceed,
    Skip,
    Reject,
}

public sealed record NodeGateDecision(NodeGateAction Action, string? Reason)
{
    public static readonly NodeGateDecision ProceedInstance = new(NodeGateAction.Proceed, null);
}

/// <summary>流程预检结果（D3/D4/D13：可预览与可生产执行分开）。</summary>
public sealed record WorkflowPreflight(
    bool Executable,
    IReadOnlyList<string> BlockingReasons,
    IReadOnlyList<string> Warnings);

/// <summary>
/// 槲寄生 · 任务中心——Planner（R4.4，R4 分解 D9）。
/// v1 语义映射：nodes[] = 顺序链；顶层 loop = 结构性循环（惰性推进，无业务轮次封顶）；
/// 顶层 triggers/terminal 作用于隐式流程根；节点 strategies 各自作用域；
/// condition.weekdays = 过滤语义（运行边界求值，不命中跳过该资源，不阻塞后续节点）。
/// 不复制配置内容：计划只引用 WorkflowDocument 的节点与策略实例。
/// </summary>
public sealed class WorkflowPlan
{
    private readonly WorkflowDocument _doc;
    private readonly int[] _occurrenceTotals; // 每个 nodeId 在链中的出现总次数（出现序号分配用）

    public WorkflowPlan(WorkflowDocument doc)
    {
        _doc = doc;
        _occurrenceTotals = new int[doc.Nodes.Count];
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < doc.Nodes.Count; i++)
        {
            seen.TryGetValue(doc.Nodes[i].NodeId, out var n);
            _occurrenceTotals[i] = n; // 该节点此前已出现次数 = 本次出现序号
            seen[doc.Nodes[i].NodeId] = n + 1;
        }
    }

    public WorkflowDocument Document => _doc;

    /// <summary>未支持类型（D3 第二级：可预览，阻止执行）。</summary>
    public IReadOnlyList<string> UnsupportedKinds => WorkflowKindCatalog.FindUnsupportedKinds(_doc);

    /// <summary>首轮首个节点出现（空链返回 null）。</summary>
    public WorkflowNodeOccurrence? FirstOccurrence()
        => _doc.Nodes.Count == 0 ? null : OccurrenceAt(0, 0);

    /// <summary>
    /// 下一节点出现（惰性推进）：越过链尾且无循环 → null（本流程边界完成）；
    /// 有结构性循环 → 回到链首、轮次 +1（无轮次封顶，业务循环仅由退出条件控制，D9）。
    /// </summary>
    public WorkflowNodeOccurrence? Next(WorkflowNodeOccurrence current)
    {
        var nextIndex = current.SequenceIndex + 1;
        if (nextIndex < _doc.Nodes.Count)
            return OccurrenceAt(nextIndex, current.LoopIteration);
        if (_doc.Loop is null || _doc.Nodes.Count == 0)
            return null;
        return OccurrenceAt(0, current.LoopIteration + 1);
    }

    /// <summary>取出现对应的节点（序列位置寻址，不按名称）。</summary>
    public WorkflowNode NodeAt(WorkflowNodeOccurrence occurrence) => _doc.Nodes[occurrence.SequenceIndex];

    /// <summary>
    /// 按稳定出现身份定位（B1：nodeId+出现序号与序列坐标解耦；修订插入/删除节点后重定位，
    /// 旧 SequenceIndex 绝不直接用于新定义寻址）。loopIteration 由调用方透传（身份的一部分）。
    /// </summary>
    public bool TryLocate(string nodeId, int occurrenceOrdinal, int loopIteration, out WorkflowNodeOccurrence occurrence)
    {
        var count = -1;
        for (var i = 0; i < _doc.Nodes.Count; i++)
        {
            if (_doc.Nodes[i].NodeId != nodeId) continue;
            count++;
            if (count == occurrenceOrdinal)
            {
                occurrence = new WorkflowNodeOccurrence(nodeId, i, occurrenceOrdinal, loopIteration);
                return true;
            }
        }
        occurrence = default!;
        return false;
    }

    /// <summary>
    /// 流程预检（运行前；D4：先预检再执行任何前置副作用）。
    /// singleNativeSupported = 执行端 task.single.native 能力实况；
    /// prerequisiteKinds/terminalKinds = 执行端前置/收尾能力实况（R4.6 I1；null = 测试接缝不检查）。
    /// </summary>
    public WorkflowPreflight Preflight(bool singleNativeSupported,
        IReadOnlySet<string>? prerequisiteKinds = null, IReadOnlySet<string>? terminalKinds = null,
        bool suppressConfigCompletionSupported = true)
    {
        var blocking = new List<string>();
        var warnings = new List<string>();

        if (UnsupportedKinds.Count > 0)
            blocking.Add("存在未支持的类型（可预览，阻止执行）：" + string.Join("、", UnsupportedKinds));
        if (_doc.Nodes.Count == 0)
            blocking.Add("流程无节点");
        foreach (var trigger in _doc.Triggers.Where(t => t.Kind is "trigger.timeFixed" or "trigger.timeFlexible"))
        {
            if (WorkflowTriggerSchedule.Resolve(trigger, DateTimeOffset.Now, out var reason) is null
                && reason?.StartsWith("已过期", StringComparison.Ordinal) != true)
                blocking.Add("触发器参数无效：" + reason);
        }
        if (string.Equals(_doc.Activation?.Status, "candidate-ready", StringComparison.Ordinal))
            blocking.Add("迁移候选（candidate-ready）只可预览，正式激活由 R5 事务迁移完成（D13）");

        // R4.6 E3'：每流程至多一个收尾动作（收尾身份 $flow#0；多收尾无逐动作水位，响亮拒绝）
        if (_doc.Terminal.Count > 1)
            blocking.Add($"收尾动作至多一个（当前 {_doc.Terminal.Count} 个）：多收尾无逐动作水位，阻止执行（E3'）");

        // R4.6 I1：能力协商预检——引用的前置/收尾类型必须全部在执行端能力目录内（缺失即阻止执行）
        if (prerequisiteKinds is not null)
        {
            var missing = _doc.Nodes.SelectMany(n => n.Strategies)
                .Where(s => s.Kind.StartsWith("prerequisite.", StringComparison.Ordinal)
                            && !prerequisiteKinds.Contains(s.Kind))
                .Select(s => s.Kind).Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                blocking.Add("执行端缺少前置能力（阻止执行）：" + string.Join("、", missing));
        }
        if (terminalKinds is not null)
        {
            var missing = _doc.Terminal
                .Where(a => !terminalKinds.Contains(a.Kind))
                .Select(a => a.Kind).Distinct(StringComparer.Ordinal)
                .OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
                blocking.Add("执行端缺少收尾能力（阻止执行）：" + string.Join("、", missing));
        }

        // R4.6 E2-8'：前置兑换码身份来源——策略自身 uid 或同节点 prerequisite.account 的 uid（均无则响亮拒绝）
        // R4.8 §4.5（一轮 I2）账号合同：每节点至多一个 account；account 存在但 uid 空白→拒绝（不得当无账号策略省略
        // expectedUid）；redeemCode 显式 uid 与 account uid 冲突→拒绝（歧义身份不猜）
        foreach (var node in _doc.Nodes)
        {
            var accounts = node.Strategies.Where(s => s.Kind == "prerequisite.account").ToList();
            if (accounts.Count > 1)
                blocking.Add($"节点 {node.NodeId} 有 {accounts.Count} 个 prerequisite.account（每节点至多一个，账号语义歧义阻止执行）");
            var accountUid = accounts.Count == 1 ? accounts[0].GetString("uid") : null;
            if (accounts.Count == 1 && string.IsNullOrWhiteSpace(accountUid))
                blocking.Add($"节点 {node.NodeId} 的 prerequisite.account 缺少 uid（存在账号策略即要求完整身份，阻止执行）");
            foreach (var s in node.Strategies)
            {
                var redeemUid = s.Kind == "prerequisite.redeemCode" ? s.GetString("uid") : null;
                if (s.Kind == "prerequisite.redeemCode"
                    && string.IsNullOrWhiteSpace(redeemUid)
                    && string.IsNullOrWhiteSpace(accountUid))
                    blocking.Add($"节点 {node.NodeId} 的 prerequisite.redeemCode 缺少 uid（策略参数与同节点账号策略均无），严格合同拒绝");
                if (s.Kind == "prerequisite.redeemCode"
                    && !string.IsNullOrWhiteSpace(redeemUid) && !string.IsNullOrWhiteSpace(accountUid)
                    && !string.Equals(redeemUid, accountUid, StringComparison.Ordinal))
                    blocking.Add($"节点 {node.NodeId} 的 prerequisite.redeemCode uid 与同节点 prerequisite.account uid 不一致（账号歧义阻止执行）");
            }
        }

        // R4.6 B6/E4'（四轮阻断 7）：任务中心资源提交固定 suppress=true——执行端缺 suppress 能力时
        // 引用整龙/配置组的流程会在配置边界触发原生收尾（含关机类），响亮拒绝
        if (!suppressConfigCompletionSupported
            && _doc.Nodes.Any(n => n.Kind is "resource.oneDragonConfig" or "resource.configGroup"))
            blocking.Add("执行端缺少 execution.suppressConfigCompletionAction 能力：任务中心资源提交固定 suppress=true，缺能力将触发原生收尾（阻止执行）");

        if (singleNativeSupported) return new WorkflowPreflight(blocking.Count == 0, blocking, warnings);
        // 单项原生能力未开放：逐节点闸门拒绝，不连坐整龙资源（D4）
        var singleCount = _doc.Nodes.Count(n => n.Kind == "resource.singleTask");
        if (singleCount > 0)
            warnings.Add($"含 {singleCount} 个单项任务节点：task.single.native=false，执行到该节点将响亮拒绝（不连坐其他节点）");

        return new WorkflowPreflight(blocking.Count == 0, blocking, warnings);
    }

    /// <summary>
    /// 节点闸门（运行边界逐节点求值；顺序 = 类型拒绝 → 单项能力 → 条件过滤，先拒绝再评估副作用策略）。
    /// </summary>
    public NodeGateDecision EvaluateNode(WorkflowNodeOccurrence occurrence, DateTimeOffset now, bool singleNativeSupported)
    {
        var node = NodeAt(occurrence);

        if (!WorkflowKindCatalog.NodeKinds.Contains(node.Kind))
            return new NodeGateDecision(NodeGateAction.Reject, "未支持的节点类型：" + node.Kind);

        if (node.Kind == "resource.singleTask" && !singleNativeSupported)
            return new NodeGateDecision(NodeGateAction.Reject,
                "task.single.native=false：单项任务原生执行能力未开放，响亮拒绝（不进入执行、不记成功、不授权成功收尾）");

        // condition.weekdays 过滤语义：不命中跳过该资源，不阻塞后续节点（D9）
        // 迁移激活：legacyFiltered 节点跳过（旧连续计划按账号绑定过滤，保留过滤标记，不偷偷纳入）
        if (node.ExtensionData is not null
            && node.ExtensionData.TryGetValue("legacyFiltered", out var filteredEl)
            && filteredEl.ValueKind == System.Text.Json.JsonValueKind.True)
        {
            return new NodeGateDecision(NodeGateAction.Skip, "旧连续计划按账号绑定过滤（legacyFiltered），跳过（不纳入执行）");
        }
        foreach (var strategy in node.Strategies)
        {
            if (strategy.Kind != "condition.weekdays") continue;
            if (!WorkflowWeekdayFilter.Matches(strategy, now))
                return new NodeGateDecision(NodeGateAction.Skip, "星期条件不命中（过滤，非阻塞）");
        }
        return NodeGateDecision.ProceedInstance;
    }

    private WorkflowNodeOccurrence OccurrenceAt(int sequenceIndex, int loopIteration)
        => new(_doc.Nodes[sequenceIndex].NodeId, sequenceIndex, _occurrenceTotals[sequenceIndex], loopIteration);
}

/// <summary>
/// 星期过滤（C04）：v1 中文星期名（周一~周日），dayBoundary=localMidnight（本地日期语义，
/// 非游戏服 4 点；R1 迁移已标注）。运行边界对当前时刻求值。
/// </summary>
public static class WorkflowWeekdayFilter
{
    private static readonly Dictionary<string, DayOfWeek> NameMap = new(StringComparer.Ordinal)
    {
        ["周一"] = DayOfWeek.Monday,
        ["周二"] = DayOfWeek.Tuesday,
        ["周三"] = DayOfWeek.Wednesday,
        ["周四"] = DayOfWeek.Thursday,
        ["周五"] = DayOfWeek.Friday,
        ["周六"] = DayOfWeek.Saturday,
        ["周日"] = DayOfWeek.Sunday,
    };

    public static bool Matches(WorkflowStrategy strategy, DateTimeOffset now)
    {
        var days = strategy.GetStringArray("days");
        if (days is null || days.Count == 0) return true; // 无 days = 不过滤（形状留痕由报告承担）
        var boundary = strategy.GetString("dayBoundary") ?? "localMidnight";
        // v1 仅本地午夜日界；其他日界未知 → 响亮不猜（按不命中处理并留痕）
        if (!string.Equals(boundary, "localMidnight", StringComparison.Ordinal)) return false;
        return days.Any(d => NameMap.TryGetValue(d, out var dow) && now.DayOfWeek == dow);
    }
}

/// <summary>
/// 触发器时刻计算（C07）：trigger.time + missPolicy。
/// 区分本地午夜/游戏服 4 点/联机宽限（§7.3 时间合同：不全局套同一规则）。
/// </summary>
public static class WorkflowTriggerSchedule
{
    /// <summary>
    /// 下一次触发时刻（本机时间）。missPolicy=nextDay：当日已过则顺延明天。
    /// 未知 missPolicy / 时间形状非法 → null + reason（响亮不猜）。
    /// </summary>
    public static DateTimeOffset? NextFire(WorkflowTrigger trigger, DateTimeOffset now, out string? reason)
    {
        reason = null;
        if (trigger.Kind is not ("trigger.time" or "trigger.timeFixed" or "trigger.timeFlexible"))
        {
            reason = "未支持的触发器类型：" + trigger.Kind;
            return null;
        }
        if (trigger.Kind is "trigger.timeFixed" or "trigger.timeFlexible")
            return Resolve(trigger, now, out reason)?.ScheduledAt;
        var time = trigger.GetString("time");
        if (!TimeOnly.TryParse(time, out var at))
        {
            reason = "触发时间形状非法：" + (time ?? "null");
            return null;
        }
        var missPolicyRaw = trigger.GetString("missPolicy") ?? "nextDay";
        // 未知 missPolicy 响亮拒绝（不套用保守映射）
        if (!string.Equals(missPolicyRaw, "nextDay", StringComparison.Ordinal)
            && !string.Equals(missPolicyRaw, "skip", StringComparison.Ordinal))
        {
            reason = "未支持的 missPolicy：" + missPolicyRaw;
            return null;
        }
        var missPolicy = TaskCenterMechanismPolicy.ParseMissPolicy(missPolicyRaw);
        var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, at.Hour, at.Minute, 0, now.Offset);
        // R5.4 IP3：使用 MechanismPolicy.ResolveMissedFire（skip=放弃，nextDay=顺延次日）
        var resolved = TaskCenterMechanismPolicy.ResolveMissedFire(todayAt, now, missPolicy);
        if (resolved is null) reason = "已过期且 missPolicy=skip（放弃，不补跑）";
        return resolved;
    }
    /// <summary>固定到点或灵活启动窗口；旧time保留原nextDay缺省。</summary>
    public static WorkflowTriggerTiming? Resolve(WorkflowTrigger trigger, DateTimeOffset now, out string? reason)
    {
        reason = null;
        if (trigger.Kind == "trigger.time")
        {
            var at = NextFire(trigger, now, out reason);
            return at is null ? null : new(trigger.Kind, at.Value, null);
        }
        if (trigger.Kind is not ("trigger.timeFixed" or "trigger.timeFlexible"))
        { reason = "未支持的触发器：" + trigger.Kind; return null; }
        if (!TimeOnly.TryParse(trigger.GetString("time") ?? trigger.GetString("at"), out var start))
        { reason = "启动时间须为HH:mm"; return null; }
        var due = new DateTimeOffset(now.Date + start.ToTimeSpan(), now.Offset);
        var policy = trigger.GetString("missPolicy") ?? "skip";
        if (policy is not ("skip" or "nextDay"))
        { reason = "missPolicy须为skip或nextDay"; return null; }
        if (trigger.Kind == "trigger.timeFixed")
        {
            var fire = TaskCenterMechanismPolicy.ResolveMissedFire(due, now, TaskCenterMechanismPolicy.ParseMissPolicy(policy));
            if (fire is null) { reason = "已过期：固定型跳过本次，不补跑"; return null; }
            return new(trigger.Kind, fire.Value, null);
        }
        if (!TimeOnly.TryParse(trigger.GetString("until"), out var end) || end == start)
        { reason = "灵活型须设置不同于启动时间的窗口结束时间until（HH:mm）"; return null; }
        var until = new DateTimeOffset(due.Date + end.ToTimeSpan(), due.Offset);
        if (until <= due) until = until.AddDays(1);
        if (due > now && until.AddDays(-1) > now) { due = due.AddDays(-1); until = until.AddDays(-1); }
        if (now >= until)
        {
            if (policy == "skip") { reason = "已过期：灵活窗口已结束，不补跑"; return null; }
            due = due.AddDays(1); until = until.AddDays(1);
        }
        return new(trigger.Kind, due, until);
    }
}

/// <summary>
/// 结构性循环时刻计算（C08/C09）：每轮开始/跨天截止重新定义，不复制旧 startTime 缺陷（R1 迁移注记）。
/// D9/B9 定案公式：轮次窗口 = [本地起点, 下一本地起点)，时区 = 本机（v1 不套游戏服 4 点/联机宽限）。
/// immediate：紧接上一轮。scheduled：起点未到 → 等今日起点；已错过今日起点 →
/// skipAcrossDays=true（默认，与 R1 样例一致）跳过本轮排下一起点（不补跑跨天轮次），
/// skipAcrossDays=false 在本轮窗口内立即补跑（返回 now）。
/// </summary>
public static class WorkflowLoopSchedule
{
    public static DateTimeOffset? NextRoundStart(WorkflowLoop loop, DateTimeOffset now, out string? reason)
    {
        reason = null;
        switch (loop.Mode)
        {
            case "immediate":
                // C09 循环截止：deadline 过期后不再继续（截止后不会继续）
                var deadline = loop.GetString("deadline");
                if (!string.IsNullOrEmpty(deadline) && TimeOnly.TryParse(deadline, out var deadlineAt))
                {
                    var todayDeadline = new DateTimeOffset(now.Year, now.Month, now.Day, deadlineAt.Hour, deadlineAt.Minute, 0, now.Offset);
                    if (now >= todayDeadline)
                    {
                        reason = "已过循环截止时间（deadline=" + deadline + "），不再继续";
                        return null;
                    }
                }
                return now;
            case "scheduled":
            {
                var time = loop.GetString("time");
                if (!TimeOnly.TryParse(time, out var at))
                {
                    reason = "循环时间形状非法：" + (time ?? "null");
                    return null;
                }
                var skipAcrossDays = loop.GetBool("skipAcrossDays") ?? true; // 默认跨天跳过（保守不补跑）
                var todayAt = new DateTimeOffset(now.Year, now.Month, now.Day, at.Hour, at.Minute, 0, now.Offset);
                // C09 循环截止：deadline 过期后不再继续（截止后不会继续）
                var schedDeadline = loop.GetString("deadline");
                if (!string.IsNullOrEmpty(schedDeadline) && TimeOnly.TryParse(schedDeadline, out var schedDeadlineAt))
                {
                    var todayDeadline = new DateTimeOffset(now.Year, now.Month, now.Day, schedDeadlineAt.Hour, schedDeadlineAt.Minute, 0, now.Offset);
                    if (now >= todayDeadline)
                    {
                        reason = "已过循环截止时间（deadline=" + schedDeadline + "），不再继续";
                        return null;
                    }
                }
                if (todayAt > now) return todayAt; // 本轮起点未到：等今日起点
                return skipAcrossDays ? todayAt.AddDays(1) : now; // 已错过起点：跨天跳过 / 窗口内立即补跑
            }
            default:
                reason = "未支持的循环模式：" + loop.Mode;
                return null;
        }
    }
}
