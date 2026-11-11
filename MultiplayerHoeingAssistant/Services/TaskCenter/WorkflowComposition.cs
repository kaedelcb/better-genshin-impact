using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>作者结构保留在节点上；稳定身份路径在每次装载时派生，绝不按显示顺序执行未选车道。</summary>
public static class WorkflowComposition
{
    public static bool HasComposition(WorkflowDocument doc) => doc.Nodes.Any(n => n.Kind is "control.flow" or "control.lanes");
    public static bool IsLaneMember(WorkflowDocument doc, string nodeId) => doc.Nodes.Any(n => n.Lanes is { } l
        && (l.YesNodeIds.Contains(nodeId) || l.NoNodeIds.Contains(nodeId)));

    public static bool HasInlineDecision(WorkflowNode node) => node.Kind == "control.lanes" && node.Lanes?.Decision is not null;

    /// <summary>新车道内置选择步骤；旧计划只读解析原流程，绝不移动、复制或重建其稳定身份。</summary>
    public static WorkflowFlowDefinition? GetDecision(WorkflowDocument doc, WorkflowNode lane)
    {
        if (lane.Lanes is not { } definition) return null;
        if (definition.Decision is not null) return definition.Decision;
        var sources = doc.Nodes.Where(n => n.NodeId == definition.SourceFlowNodeId && n.Kind == "control.flow").Take(2).ToArray();
        return sources.Length == 1 ? sources[0].Flow : null;
    }

    public static string GetDecisionOwnerId(WorkflowDocument doc, WorkflowNode lane)
        => HasInlineDecision(lane) ? lane.NodeId : lane.Lanes?.SourceFlowNodeId ?? lane.NodeId;

    public static IReadOnlyList<string> Validate(WorkflowDocument doc)
    {
        if (!HasComposition(doc)) return [];
        var errors = new List<string>();
        var nodes = doc.Nodes.GroupBy(n => n.NodeId).ToDictionary(g => g.Key, g => g.ToArray());
        if (nodes.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value.Length != 1))
            errors.Add("编排要求每行有唯一非空身份");
        if (doc.Nodes.Any(n => n.Kind is "control.condition" or "control.end"
            || n.Path is not null && n.ExtensionData?.ContainsKey("compositionDerivedPath") != true))
            errors.Add("基础编排不能与旧自定义路径混用，请先使用单一编排结构");
        var owned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in doc.Nodes)
        {
            if (n.Kind == "control.flow")
            {
                if (n.Flow is not { } f) { errors.Add($"流程 {n.NodeId} 缺少节点定义"); continue; }
                errors.AddRange(TaskFlowNodeCatalog.Validate(f));
            }
            if (n.Kind != "control.lanes") continue;
            if (n.Lanes is not { } lane) { errors.Add($"车道 {n.NodeId} 缺少定义"); continue; }
            if (lane.Decision is { } decision)
            {
                if (!string.IsNullOrWhiteSpace(lane.SourceFlowNodeId))
                    errors.Add($"车道 {n.NodeId} 不能同时使用内置条件和旧流程来源");
                if (string.IsNullOrWhiteSpace(decision.ExitStepId))
                    errors.Add($"车道 {n.NodeId} 需要选择决定上/下车道的条件");
                errors.AddRange(TaskFlowNodeCatalog.Validate(decision));
            }
            else if (!nodes.TryGetValue(lane.SourceFlowNodeId, out var source) || source.Length != 1
                || source[0].Kind != "control.flow" || string.IsNullOrEmpty(source[0].Flow?.ExitStepId))
                errors.Add($"车道 {n.NodeId} 需要内置的选择条件；旧配对则须保留原流程");
            foreach (var id in lane.YesNodeIds.Concat(lane.NoNodeIds))
            {
                if (!owned.Add(id)) errors.Add($"任务 {id} 被多个车道重复引用");
                if (!nodes.TryGetValue(id, out var resource) || resource.Length != 1 || !resource[0].Kind.StartsWith("resource.", StringComparison.Ordinal))
                    errors.Add($"车道任务 {id} 不存在或不是普通任务");
            }
        }
        var main = doc.Nodes.Where(n => !IsLaneMember(doc, n.NodeId)).ToList();
        foreach (var flow in main.Where(n => n.Flow?.ExitStepId is { Length: > 0 }))
        {
            var index = main.IndexOf(flow);
            var matches = main.Where(n => n.Lanes is { Decision: null } l && l.SourceFlowNodeId == flow.NodeId).ToArray();
            if (matches.Length != 1 || index + 1 >= main.Count || main[index + 1] != matches[0])
                errors.Add($"流程 {flow.NodeId} 的两个出口须与紧随其后的唯一车道配对");
        }
        errors.AddRange(ValidateJumps(doc));
        foreach (var step in doc.Nodes.SelectMany(n => new[] { n.Flow, n.Lanes?.Decision }).OfType<WorkflowFlowDefinition>()
            .SelectMany(f => TaskFlowNodeCatalog.Flatten(f.Steps)))
            if (TaskFlowNodeCatalog.IsTrigger(step.Kind) && step.TriggerScope == "task"
                && (!nodes.TryGetValue(step.TargetNodeId, out var t) || t.Length != 1 || !t[0].Kind.StartsWith("resource.", StringComparison.Ordinal)))
                errors.Add("触发器绑定的目标任务不存在，请重新绑定");
        return errors;
    }

    public static void Compile(WorkflowDocument doc)
    {
        if (!HasComposition(doc)) return;
        var errors = Validate(doc);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        var main = doc.Nodes.Where(n => !IsLaneMember(doc, n.NodeId)).ToList();
        var byId = doc.Nodes.ToDictionary(n => n.NodeId);
        foreach (var n in doc.Nodes)
        {
            n.Path = new WorkflowPath();
            n.ExtensionData ??= new();
            n.ExtensionData["compositionDerivedPath"] = JsonSerializer.SerializeToElement(true);
            if (!n.Kind.StartsWith("control.", StringComparison.Ordinal) && !n.Strategies.Any(s => s.Kind == "flow.route"))
                n.Strategies.Add(new WorkflowStrategy { Kind = "flow.route" });
        }
        for (var i = 0; i < main.Count; i++)
        {
            var n = main[i];
            var next = i + 1 < main.Count ? main[i + 1].NodeId : "$end";
            n.Path!.Next = next;
            // 流程行跳转只在正常执行完时使用（PathSuccessor）；过滤、停用、跳过当前仍走 Next。
            if (n.Kind == "control.flow" && string.IsNullOrEmpty(n.Flow?.ExitStepId) && JumpTarget(doc, n.Flow?.JumpTo) is { } flowJump)
                n.Path.Jump = flowJump;
            if (n.Lanes is not { } l) continue;
            var after = next;
            var source = l.Decision is not null ? n : byId[l.SourceFlowNodeId];
            // 路线走完后的去向：设置了跳转就去目标行，否则汇合到下一行。旧配对车道不支持跳转（Validate 拦截）。
            var yesAfter = l.Decision is not null ? JumpTarget(doc, l.YesJumpTo) ?? after : after;
            var noAfter = l.Decision is not null ? JumpTarget(doc, l.NoJumpTo) ?? after : after;
            source.Path!.Yes = l.YesNodeIds.FirstOrDefault() ?? yesAfter;
            source.Path.No = l.NoNodeIds.FirstOrDefault() ?? noAfter;
            source.Path.Next = after; // 明确跳过整个配对结构时不泄漏任一分支。
            foreach (var (ids, end) in new[] { (l.YesNodeIds, yesAfter), (l.NoNodeIds, noAfter) })
                for (var k = 0; k < ids.Count; k++) byId[ids[k]].Path!.Next = k + 1 < ids.Count ? ids[k + 1] : end;
        }
    }

    /// <summary>跳转目标在计划里实际进入的行：旧配对车道换算为它的条件流程（仍先判断再选路线）；空或不存在返回 null。</summary>
    internal static string? JumpTarget(WorkflowDocument doc, string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var node = doc.Nodes.FirstOrDefault(n => n.NodeId == target);
        if (node is null) return null;
        return node.Lanes is { Decision: null } legacy && !string.IsNullOrEmpty(legacy.SourceFlowNodeId) ? legacy.SourceFlowNodeId : target;
    }

    /// <summary>该行是否设置了跳转（流程行整行，或车道任一路线）。</summary>
    public static bool HasJump(WorkflowNode node)
        => node.Kind == "control.flow" && !string.IsNullOrWhiteSpace(node.Flow?.JumpTo)
           || node.Kind == "control.lanes" && (!string.IsNullOrWhiteSpace(node.Lanes?.YesJumpTo) || !string.IsNullOrWhiteSpace(node.Lanes?.NoJumpTo));

    public const string JumpMarkerKind = "flow.jump";

    /// <summary>
    /// 按实际跳转补齐或移除旧版保护标记：旧版执行端不认识 flow.jump，会拒绝执行而不是忽略跳转按顺序执行。
    /// 由 WorkflowStore.Save 调用，网页、导入、远程等任何来源漏加都在保存时纠正；没有跳转的计划不变。
    /// </summary>
    public static void SyncJumpMarkers(WorkflowDocument doc)
    {
        foreach (var n in doc.Nodes)
        {
            var has = n.Strategies.Any(s => s.Kind == JumpMarkerKind);
            if (HasJump(n) && !has) n.Strategies.Add(new WorkflowStrategy { Kind = JumpMarkerKind });
            else if (!HasJump(n) && has) n.Strategies.RemoveAll(s => s.Kind == JumpMarkerKind);
        }
    }

    /// <summary>跳转规则问题（进入启动前预检，阻止启动）。</summary>
    public static IReadOnlyList<string> ValidateJumps(WorkflowDocument doc) => JumpIssues(doc).Select(i => i.Message).ToArray();

    /// <summary>
    /// 逐行的跳转问题（界面按来源行标红用）：只能跳到来源之后的主计划行（按显示顺序），不能往回跳、跳到自身、跳进车道；
    /// 旧配对车道及其条件流程、车道的条件定义不能设置跳转。
    /// </summary>
    public static IReadOnlyList<(string SourceNodeId, string Message)> JumpIssues(WorkflowDocument doc)
    {
        var issues = new List<(string, string)>();
        var main = doc.Nodes.Where(n => !IsLaneMember(doc, n.NodeId)).ToList();
        void Check(WorkflowNode source, string? target, string label)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            var name = source.Flow?.Name ?? source.Lanes?.Name ?? source.NodeId;
            if (doc.Nodes.Count(n => n.NodeId == target) != 1)
            { issues.Add((source.NodeId, $"「{name}」{label}的跳转目标已不存在，请重新选择")); return; }
            if (IsLaneMember(doc, target))
            { issues.Add((source.NodeId, $"「{name}」{label}不能跳进车道里的任务，只能跳到主计划行")); return; }
            var from = main.FindIndex(n => n.NodeId == source.NodeId);
            var to = main.FindIndex(n => n.NodeId == JumpTarget(doc, target));
            if (from < 0 || to <= from)
                issues.Add((source.NodeId, $"「{name}」{label}的跳转目标在它前面或就是它自己，只能往后跳，请修改"));
        }
        foreach (var n in doc.Nodes)
        {
            if (n.Lanes?.Decision?.JumpTo is { Length: > 0 })
                issues.Add((n.NodeId, $"车道「{n.Lanes.Name}」的选择条件不能设置跳转"));
            if (n.Kind == "control.flow" && n.Flow is { } flow && !string.IsNullOrWhiteSpace(flow.JumpTo))
            {
                if (!string.IsNullOrEmpty(flow.ExitStepId))
                    issues.Add((n.NodeId, $"「{flow.Name}」是旧版车道的条件流程，不能设置跳转"));
                else Check(n, flow.JumpTo, "执行完后");
            }
            if (n.Kind == "control.lanes" && n.Lanes is { } lane
                && (!string.IsNullOrWhiteSpace(lane.YesJumpTo) || !string.IsNullOrWhiteSpace(lane.NoJumpTo)))
            {
                if (lane.Decision is null) { issues.Add((n.NodeId, $"旧版车道「{lane.Name}」不支持跳转，请改用新版车道")); continue; }
                Check(n, lane.YesJumpTo, "「满足条件」路线");
                Check(n, lane.NoJumpTo, "「其他情况」路线");
            }
        }
        return issues;
    }

    /// <summary>
    /// 每个跳转跳过的主计划行（来源与目标之间、按执行顺序）。key = 来源行，value = 按路线分组的被跳过行。
    /// 用于计划表说明「跳过 #3」；只做展示，不影响执行。
    /// </summary>
    public static IReadOnlyList<(string SourceNodeId, string? Route, string TargetNodeId, IReadOnlyList<string> Skipped)> JumpSpans(WorkflowDocument doc)
    {
        var spans = new List<(string, string?, string, IReadOnlyList<string>)>();
        var main = doc.Nodes.Where(n => !IsLaneMember(doc, n.NodeId)).ToList();
        var integrated = doc.Nodes.Where(n => n.Lanes is { Decision: null } l && !string.IsNullOrEmpty(l.SourceFlowNodeId))
            .Select(n => n.Lanes!.SourceFlowNodeId).ToHashSet(StringComparer.Ordinal);
        void Add(WorkflowNode source, string? route, string? target)
        {
            if (JumpTarget(doc, target) is not { } resolved) return;
            var from = main.FindIndex(n => n.NodeId == source.NodeId);
            var to = main.FindIndex(n => n.NodeId == resolved);
            if (from < 0 || to <= from) return;
            var skipped = main.Skip(from + 1).Take(to - from - 1).Where(n => !integrated.Contains(n.NodeId)).Select(n => n.NodeId).ToArray();
            spans.Add((source.NodeId, route, target!, skipped));
        }
        foreach (var n in main)
        {
            if (n.Kind == "control.flow" && string.IsNullOrEmpty(n.Flow?.ExitStepId)) Add(n, null, n.Flow?.JumpTo);
            if (n.Lanes is { Decision: not null } lane) { Add(n, "yes", lane.YesJumpTo); Add(n, "no", lane.NoJumpTo); }
        }
        return spans;
    }

    /// <summary>
    /// 从首行沿编排去向走不到的主计划行（例如被流程跳转无条件跳过、又没有车道路线跳到它）。只用于提示，不阻止启动。
    /// 车道两条路线都视为可走；有跳转的流程行按正常执行走跳转。计划本身有校验问题时不计算（返回空）。
    /// </summary>
    public static IReadOnlyList<string> UnreachableRows(WorkflowDocument doc)
    {
        if (!HasComposition(doc) || Validate(doc).Count > 0) return [];
        var copy = JsonSerializer.Deserialize<WorkflowDocument>(JsonSerializer.Serialize(doc))!;
        Compile(copy);
        var byId = copy.Nodes.ToDictionary(n => n.NodeId);
        var entry = copy.Nodes.FirstOrDefault(n => !IsLaneMember(copy, n.NodeId));
        if (entry is null) return [];
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>();
        stack.Push(entry.NodeId);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            if (!seen.Add(id) || !byId.TryGetValue(id, out var n)) continue;
            var targets = n.Path?.Jump is { Length: > 0 } jump
                ? new[] { jump, n.Path.Yes, n.Path.No }
                : new[] { n.Path?.Next, n.Path?.Yes, n.Path?.No };
            foreach (var t in targets) if (t is { Length: > 0 } && t != "$end") stack.Push(t);
        }
        var integrated = copy.Nodes.Where(n => n.Lanes is { Decision: null } l && !string.IsNullOrEmpty(l.SourceFlowNodeId))
            .Select(n => n.Lanes!.SourceFlowNodeId).ToHashSet(StringComparer.Ordinal);
        return copy.Nodes.Where(n => !IsLaneMember(copy, n.NodeId) && !integrated.Contains(n.NodeId) && !seen.Contains(n.NodeId))
            .Select(n => n.NodeId).ToArray();
    }

    internal static string LayoutKey(WorkflowDocument doc)
    {
        // 旧配对的运行键逐字保持；新车道只绑定结构，不因改名或条件参数调整而丢掉已落盘的选路。
        if (!doc.Nodes.Any(HasInlineDecision)) return JsonSerializer.Serialize(doc.Nodes
            .Where(n => n.Flow is not null || n.Lanes is not null)
            .Select(n => new { n.NodeId, Exit = n.Flow?.ExitStepId, n.Lanes }));
        return JsonSerializer.Serialize(new
        {
            Kind = "inline-lanes-v1",
            Rows = doc.Nodes.Where(n => n.Flow is not null || n.Lanes is not null).Select(n => new
            {
                n.NodeId, Exit = n.Flow?.ExitStepId, Source = n.Lanes?.SourceFlowNodeId,
                DecisionExit = n.Lanes?.Decision?.ExitStepId, Yes = n.Lanes?.YesNodeIds, No = n.Lanes?.NoNodeIds
            })
        });
    }
}

public static class TaskFlowNodeCatalog
{
    public static bool IsTrigger(string kind) => kind is StartupStepKinds.TimerTrigger or StartupStepKinds.LogTrigger or StartupStepKinds.Watchdog;
    public static IReadOnlyList<StartupStepKinds.KindInfo> All => StartupStepKinds.All.Where(k => k.NodeType == "condition"
        || k.Key is StartupStepKinds.SetRegister or StartupStepKinds.Wait or StartupStepKinds.StartProgram
        || IsTrigger(k.Key)).ToArray();
    public static IEnumerable<StartupStep> Flatten(IEnumerable<StartupStep> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            foreach (var c in Flatten(s.TrueSteps.Concat(s.FalseSteps).Concat(s.FireSteps).Concat(s.BeforeAttemptSteps))) yield return c;
        }
    }
    public static IReadOnlyList<string> Validate(WorkflowFlowDefinition flow)
    {
        var errors = new List<string>();
        var steps = Flatten(flow.Steps).ToArray();
        if (steps.Any(s => string.IsNullOrWhiteSpace(s.Id)) || steps.GroupBy(s => s.Id).Any(g => g.Count() > 1))
            errors.Add("流程内部节点身份重复或缺失");
        if (flow.ExitStepId is { Length: > 0 } exit && (flow.Steps.LastOrDefault() is not { NodeType: "condition" } end || end.Id != exit || !end.Enabled || end.TrueSteps.Count > 0 || end.FalseSteps.Count > 0))
            errors.Add("出口必须是流程主链最后一个判断节点");
        foreach (var s in steps)
        {
            var kind = All.FirstOrDefault(k => k.Key == s.Kind);
            if (kind is null || kind.NodeType != s.NodeType) errors.Add($"任务流程不支持节点 {s.Kind} 或其类型不符");
            if (IsTrigger(s.Kind))
            {
                if (s.TriggerScope is not ("flow" or "task" or "round")) errors.Add("触发器作用范围无效");
                if (s.Kind == StartupStepKinds.LogTrigger && string.IsNullOrWhiteSpace(s.LogKeyword)) errors.Add("日志触发器关键字不能为空");
                if (s.Kind == StartupStepKinds.TimerTrigger && !TimeOnly.TryParse(s.TriggerTime, out _)) errors.Add("定时时刻无效");
                if (s.Kind == StartupStepKinds.Watchdog && !StartupStepKinds.IsWatchableKind(s.WatchKind)) errors.Add("电子狗条件无效");
                foreach (var cb in Flatten(s.FireSteps.Concat(s.BeforeAttemptSteps)))
                    if (IsTrigger(cb.Kind) || cb.Kind is StartupStepKinds.StartProgram or StartupStepKinds.ManualConfirm)
                        errors.Add("后台子链不能嵌套触发器、启动程序或人工确认");
            }
            if (!StartupStatusSource.All.Contains(s.StatusSource)) errors.Add("节点状态来源无效");
            if (s.Kind == StartupStepKinds.Wait && s.WaitSeconds < 0) errors.Add("等待时间不能小于零");
            if (s.Kind == StartupStepKinds.TimeRange && (!TimeOnly.TryParse(s.TimeStart, out _) || !TimeOnly.TryParse(s.TimeEnd, out _))) errors.Add("时间区间参数无效");
            if (s.Kind == StartupStepKinds.Weekday && s.Weekdays.Any(d => d is < 1 or > 7)) errors.Add("星期参数无效");
            if (s.Kind == StartupStepKinds.ProcessRunning && string.IsNullOrWhiteSpace(s.ProcessName)) errors.Add("请填写需要判断的进程");
            if (s.Kind == StartupStepKinds.BgiTaskName && string.IsNullOrWhiteSpace(s.TaskName)) errors.Add("请填写需要判断的任务名");
            if (s.Kind is StartupStepKinds.SetRegister or StartupStepKinds.JudgeRegister)
            {
                try { RegisterBank.ValidateSelection(s.Kind == StartupStepKinds.SetRegister ? s.RegisterWrite?.Values : s.RegisterCondition?.Values); }
                catch (ArgumentException ex) { errors.Add(ex.Message); }
            }
        }
        return errors;
    }
}
