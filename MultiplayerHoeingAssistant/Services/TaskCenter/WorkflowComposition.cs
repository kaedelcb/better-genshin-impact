using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>作者结构保留在节点上；稳定身份路径在每次装载时派生，绝不按显示顺序执行未选车道。</summary>
public static class WorkflowComposition
{
    public static bool HasComposition(WorkflowDocument doc) => doc.Nodes.Any(n => n.Kind is "control.flow" or "control.lanes");
    public static bool IsLaneMember(WorkflowDocument doc, string nodeId) => doc.Nodes.Any(n => n.Lanes is { } l
        && (l.YesNodeIds.Contains(nodeId) || l.NoNodeIds.Contains(nodeId)));

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
            if (!nodes.TryGetValue(lane.SourceFlowNodeId, out var source) || source.Length != 1
                || source[0].Kind != "control.flow" || string.IsNullOrEmpty(source[0].Flow?.ExitStepId))
                errors.Add($"车道 {n.NodeId} 须绑定一个有出口的流程");
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
            var matches = main.Where(n => n.Lanes?.SourceFlowNodeId == flow.NodeId).ToArray();
            if (matches.Length != 1 || index + 1 >= main.Count || main[index + 1] != matches[0])
                errors.Add($"流程 {flow.NodeId} 的两个出口须与紧随其后的唯一车道配对");
        }
        foreach (var step in doc.Nodes.Where(n => n.Flow is not null).SelectMany(n => TaskFlowNodeCatalog.Flatten(n.Flow!.Steps)))
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
            if (n.Lanes is not { } l) continue;
            var after = next;
            var source = byId[l.SourceFlowNodeId];
            source.Path!.Yes = l.YesNodeIds.FirstOrDefault() ?? after;
            source.Path.No = l.NoNodeIds.FirstOrDefault() ?? after;
            source.Path.Next = after; // 明确跳过整个配对结构时不泄漏任一分支。
            foreach (var ids in new[] { l.YesNodeIds, l.NoNodeIds })
                for (var k = 0; k < ids.Count; k++) byId[ids[k]].Path!.Next = k + 1 < ids.Count ? ids[k + 1] : after;
        }
    }

    internal static string LayoutKey(WorkflowDocument doc) => JsonSerializer.Serialize(doc.Nodes
        .Where(n => n.Flow is not null || n.Lanes is not null)
        .Select(n => new { n.NodeId, Exit = n.Flow?.ExitStepId, n.Lanes }));
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
