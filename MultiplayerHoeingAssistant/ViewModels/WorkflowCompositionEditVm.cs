using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class WorkflowEditVm
{
    public IReadOnlyList<NodeEditVm> MainPlanNodes
    {
        get
        {
            var members = Draft.Nodes.Where(n => n.Lanes is not null)
                .SelectMany(n => n.Lanes!.YesNodeIds.Concat(n.Lanes.NoNodeIds)).ToHashSet(StringComparer.Ordinal);
            return Nodes.Where(n => (n.IsResource || n.IsComposition) && !members.Contains(n.NodeId)).ToArray();
        }
    }

    internal NodeEditVm AddFlow(WorkflowFlowDefinition definition)
    {
        RememberSchedule();
        var model = new WorkflowNode { NodeId = NewNodeId(), Kind = "control.flow", Flow = definition };
        Draft.Nodes.Add(model);
        var vm = new NodeEditVm(model);
        Nodes.Add(vm); Renumber(); SelectedNode = vm;
        CompositionChanged();
        return vm;
    }

    internal NodeEditVm AddPairedLanes(NodeEditVm source)
    {
        if (source.Model.Flow is not { ExitStepId: { Length: > 0 } } || !Nodes.Contains(source))
            throw new InvalidOperationException("请先选择有出口判断的流程，再添加车道。");
        if (Draft.Nodes.Any(n => n.Lanes?.SourceFlowNodeId == source.NodeId))
            throw new InvalidOperationException("这个流程已经有配对车道。");
        RememberSchedule();
        var model = new WorkflowNode { NodeId = NewNodeId(), Kind = "control.lanes",
            Lanes = new WorkflowLanesDefinition { SourceFlowNodeId = source.NodeId } };
        var index = Nodes.IndexOf(source) + 1;
        Draft.Nodes.Insert(index, model);
        var vm = new NodeEditVm(model);
        Nodes.Insert(index, vm); Renumber(); SelectedNode = vm; CompositionChanged();
        return vm;
    }

    internal void ReplaceFlow(NodeEditVm node, WorkflowFlowDefinition flow)
    {
        if (!Nodes.Contains(node)) return;
        if (flow.ExitStepId is null && Draft.Nodes.Any(n => n.Lanes?.SourceFlowNodeId == node.NodeId))
            throw new InvalidOperationException("此流程已连接车道，请保留出口判断，或先删除配对车道。");
        RememberSchedule(); node.Model.Flow = flow; node.NotifyCompositionChanged(); CompositionChanged();
    }

    internal void AppendLaneTask(NodeEditVm lanes, bool yes, CatalogSourceVm source)
    {
        if (lanes.Model.Lanes is null || !Nodes.Contains(lanes)) return;
        RememberSchedule();
        var count = Nodes.Count;
        AppendSourceIndex = AppendSources.IndexOf(source);
        AppendNodeCommand.Execute(null);
        if (Nodes.Count != count + 1) throw new InvalidOperationException("资源目录已变化，请重新选择。");
        var added = Nodes[^1];
        (yes ? lanes.Model.Lanes.YesNodeIds : lanes.Model.Lanes.NoNodeIds).Add(added.NodeId);
        lanes.NotifyCompositionChanged(); CompositionChanged(); SelectedNode = lanes;
    }

    internal void MoveLaneTask(NodeEditVm lanes, bool yes, string nodeId, int delta)
    {
        var list = yes ? lanes.Model.Lanes?.YesNodeIds : lanes.Model.Lanes?.NoNodeIds;
        if (list is null) return;
        var at = list.IndexOf(nodeId); var to = at + delta;
        if (at < 0 || to < 0 || to >= list.Count) return;
        RememberSchedule(); (list[at], list[to]) = (list[to], list[at]);
        lanes.NotifyCompositionChanged(); CompositionChanged();
    }

    internal void RenameLanes(NodeEditVm lanes, string name, string yesName, string noName)
    {
        if (lanes.Model.Lanes is not { } data) return;
        RememberSchedule();
        data.Name = name.Trim(); data.YesName = yesName.Trim(); data.NoName = noName.Trim();
        lanes.NotifyCompositionChanged(); CompositionChanged();
    }

    private HashSet<string> CompositionGroup(NodeEditVm node)
    {
        var root = node.Model.Lanes is { } lane
            ? Nodes.FirstOrDefault(n => n.NodeId == lane.SourceFlowNodeId) ?? node : node;
        var ids = new HashSet<string>(StringComparer.Ordinal) { root.NodeId };
        if (root.Model.Flow is not null)
            foreach (var pair in Draft.Nodes.Where(n => n.Lanes?.SourceFlowNodeId == root.NodeId))
            {
                ids.Add(pair.NodeId);
                ids.UnionWith(pair.Lanes!.YesNodeIds.Concat(pair.Lanes.NoNodeIds));
            }
        return ids;
    }

    internal bool RemoveComposition(NodeEditVm node)
    {
        if (!node.IsComposition) return false;
        RememberSchedule();
        // Deleting just the lane row removes its branch resources and clears the source exit.
        var ids = node.Model.Lanes is { } lane
            ? lane.YesNodeIds.Concat(lane.NoNodeIds).Append(node.NodeId).ToHashSet(StringComparer.Ordinal)
            : CompositionGroup(node);
        if (node.Model.Lanes is { } deleted && Nodes.FirstOrDefault(n => n.NodeId == deleted.SourceFlowNodeId)?.Model.Flow is { } flow)
            flow.ExitStepId = null;
        if (Draft.ExtensionData?.TryGetValue(TaskCenterPanelViewModel.StartNodeExtensionKey, out var marker) == true
            && marker.ValueKind == JsonValueKind.String && marker.GetString() is { } start && ids.Contains(start))
            Draft.ExtensionData.Remove(TaskCenterPanelViewModel.StartNodeExtensionKey);
        foreach (var vm in Nodes.Where(n => ids.Contains(n.NodeId)).ToArray())
        { Nodes.Remove(vm); Draft.Nodes.Remove(vm.Model); }
        Renumber(); CompositionChanged(); return true;
    }

    internal bool DuplicateComposition(NodeEditVm source)
    {
        if (!source.IsComposition) return false;
        RememberSchedule();
        var ids = CompositionGroup(source);
        var originals = Nodes.Where(n => ids.Contains(n.NodeId)).ToArray();
        var copies = originals.Select(n => JsonSerializer.Deserialize<WorkflowNode>(JsonSerializer.Serialize(n.Model))!).ToArray();
        for (var i = 0; i < copies.Length; i++) originals[i].ApplyToModel(copies[i]);
        var mapping = originals.ToDictionary(n => n.NodeId, _ => NewNodeId(), StringComparer.Ordinal);
        // NewNodeId checks the existing graph; explicitly ensure uniqueness among new rows as well.
        foreach (var id in mapping.Keys.ToArray()) mapping[id] = "n-" + Guid.NewGuid().ToString("N");
        foreach (var copy in copies)
        {
            copy.NodeId = mapping[copy.NodeId]; copy.Path = null;
            copy.Strategies.RemoveAll(s => s.Kind == "flow.route");
            if (copy.Lanes is { } lanes)
            {
                lanes.SourceFlowNodeId = mapping.GetValueOrDefault(lanes.SourceFlowNodeId, lanes.SourceFlowNodeId);
                lanes.YesNodeIds = lanes.YesNodeIds.Select(id => mapping[id]).ToList();
                lanes.NoNodeIds = lanes.NoNodeIds.Select(id => mapping[id]).ToList();
            }
            if (copy.Flow is { } flow)
            {
                var steps = EnumerateSteps(flow.Steps).ToArray();
                var stepIds = steps.ToDictionary(s => s.Id, _ => Guid.NewGuid().ToString("N"));
                flow.ExitStepId = flow.ExitStepId is { } exit ? stepIds.GetValueOrDefault(exit) : null;
                foreach (var step in steps)
                {
                    step.Id = stepIds[step.Id];
                    step.TargetNodeId = mapping.GetValueOrDefault(step.TargetNodeId, step.TargetNodeId);
                }
                flow.Name += " 副本";
            }
        }
        var insert = Nodes.Select((n,i) => (n,i)).Where(p => ids.Contains(p.n.NodeId)).Max(p => p.i) + 1;
        foreach (var copy in copies)
        { Draft.Nodes.Insert(insert, copy); Nodes.Insert(insert++, new NodeEditVm(copy)); }
        Renumber(); SelectedNode = Nodes.First(n => n.NodeId == mapping[source.NodeId]); CompositionChanged();
        return true;
    }

    internal bool MoveComposition(NodeEditVm source, NodeEditVm target, bool after)
    {
        if (!source.IsComposition && !target.IsComposition) return false;
        var sourceIds = source.IsComposition ? CompositionGroup(source) : new HashSet<string> { source.NodeId };
        var targetIds = target.IsComposition ? CompositionGroup(target) : new HashSet<string> { target.NodeId };
        if (sourceIds.Overlaps(targetIds)) return true;
        RememberSchedule();
        var moving = Nodes.Where(n => sourceIds.Contains(n.NodeId)).ToArray();
        var remaining = Nodes.Where(n => !sourceIds.Contains(n.NodeId)).ToList();
        var indexes = remaining.Select((n,i) => (n,i)).Where(p => targetIds.Contains(p.n.NodeId)).Select(p => p.i).ToArray();
        if (indexes.Length == 0) return true;
        var at = after ? indexes.Max() + 1 : indexes.Min();
        remaining.InsertRange(at, moving);
        Nodes.Clear(); Draft.Nodes.Clear();
        foreach (var vm in remaining) { Nodes.Add(vm); Draft.Nodes.Add(vm.Model); }
        Renumber(); SelectedNode = source; CompositionChanged(); return true;
    }

    internal static IEnumerable<StartupStep> EnumerateSteps(IEnumerable<StartupStep> steps)
    {
        foreach (var step in steps)
        {
            yield return step;
            foreach (var nested in EnumerateSteps(step.TrueSteps.Concat(step.FalseSteps).Concat(step.FireSteps).Concat(step.BeforeAttemptSteps)))
                yield return nested;
        }
    }

    internal void CompositionChanged()
    {
        foreach (var vm in Nodes.Where(n => n.IsComposition)) vm.NotifyCompositionChanged();
        OnPropertyChanged(nameof(MainPlanNodes));
    }
}

public sealed partial class NodeEditVm
{
    public bool IsComposition => Model.Kind is "control.flow" or "control.lanes";
    public string CompositionSummary => Model.Flow is { } f
        ? f.ExitStepId is null ? $"{f.Steps.Count} 个节点 · 完成后继续主线" : $"{f.Steps.Count} 个节点 · 是 / 否出口"
        : Model.Lanes is { } l ? $"是 → {l.YesName}（{l.YesNodeIds.Count}）　否 → {l.NoName}（{l.NoNodeIds.Count}）" : "";
    internal void NotifyCompositionChanged()
    { OnPropertyChanged(nameof(DisplayName)); OnPropertyChanged(nameof(CompositionSummary)); }
}

public sealed partial class TaskCenterPanelViewModel
{
    public Func<IReadOnlyList<StartupStep>>? AllowedProgramsProvider { get; set; }
    public IReadOnlyList<StartupStep> AllowedTaskPrograms => _remoteTarget is null
        ? AllowedProgramsProvider?.Invoke() ?? [] : _remoteSnapshot?.AllowedTaskPrograms ?? [];
    internal string LaneRunStatus(string sourceFlowId, bool yes)
    {
        var runs = _remoteTarget is null ? _host.ListRunOverview().Active.Concat(_host.ListRunOverview().History)
            : (_remoteSnapshot?.Active ?? []).Concat(_remoteSnapshot?.History ?? []);
        var run = runs.Where(r => r.WorkflowId == SelectedWorkflowId).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        if (run is null) return "";
        var round = run.Cursor?.LoopIteration ?? run.NodeOutcomes.Select(o => o.LoopIteration).DefaultIfEmpty(0).Max();
        var decision = run.NodeOutcomes.LastOrDefault(o => o.NodeId == sourceFlowId && o.LoopIteration == round);
        if (decision?.Result is not ("branchYes" or "branchNo")) return "等待出口判断";
        var chosen = decision.Result == "branchYes";
        return chosen == yes ? "已选中此车道" : "本轮未选择";
    }
    public event Action<string, string?>? CompositionLocateRequested;
    public bool LocateFlowStep(string workflowId, string? flowNodeId, string? stepId)
    {
        var item = Flows.FirstOrDefault(f => f.WorkflowId == workflowId);
        if (item is null) return false;
        SelectFlowCommand.Execute(item);
        if (flowNodeId is null) return true;
        var node = Editing?.Nodes.FirstOrDefault(n => n.NodeId == flowNodeId);
        if (node is null) return false;
        Editing!.SelectedNode = node;
        CompositionLocateRequested?.Invoke(flowNodeId, stepId);
        return true;
    }
}
