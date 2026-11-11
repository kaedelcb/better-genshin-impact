using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// 任务中心「走过的路径」只读投影：从计划最近一次运行的节点结果与流程步骤检查点，
/// 推出本轮每个计划节点、每个流程步骤的运行态（与启动中心 <see cref="NodeRunState"/> 同口径）。
/// 只读运行记录，不写回、不参与执行与恢复判断；记录缺项时显示为未走到，不猜测。
/// </summary>
public sealed class TaskCenterRunTrace
{
    public static readonly TaskCenterRunTrace Empty = new();

    private readonly Dictionary<string, NodeRunState> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NodeRunState> _steps = new(StringComparer.Ordinal);

    public string? RunId { get; private init; }
    public WorkflowRunState? State { get; private init; }
    /// <summary>展示的轮次（0 起）：运行游标所在轮次，没有游标时取已落盘结果的最大轮次。</summary>
    public int Round { get; private init; }
    public bool IsActive { get; private init; }
    public bool HasRun => RunId is not null;
    /// <summary>正在「等待时段开始」的行与说明（例如「等到 17:00」）；没有时为 null。</summary>
    public string? WaitingNodeId { get; private init; }
    public string? WaitingText { get; private init; }

    /// <summary>变化检测键：运行态有变化才需要重画。</summary>
    public string Key { get; private init; } = "";

    public NodeRunState Node(string nodeId) => _nodes.GetValueOrDefault(nodeId);
    public NodeRunState Step(string ownerNodeId, string stepId) => _steps.GetValueOrDefault(ownerNodeId + "/" + stepId);

    private readonly HashSet<string> _jumpedOver = new(StringComparer.Ordinal);
    /// <summary>本轮被跳转跳过的行（流程执行完或车道路线走完后跳到后面，中间的行没执行）。车道成员按所在车道计。</summary>
    public bool JumpedOver(string nodeId) => _jumpedOver.Contains(nodeId);
    public IReadOnlyCollection<string> JumpedOverRows => _jumpedOver;

    /// <summary>车道本轮的选择：true=满足条件路线，false=其他路线，null=尚未判断。</summary>
    public bool? LaneChoice(WorkflowDocument doc, WorkflowNode lane)
        => Node(WorkflowComposition.GetDecisionOwnerId(doc, lane)) switch
        {
            NodeRunState.CondTrue => true,
            NodeRunState.CondFalse => false,
            _ => null,
        };

    private static bool IsActiveState(WorkflowRunState s) => s is WorkflowRunState.Planned or WorkflowRunState.Running
        or WorkflowRunState.Waiting or WorkflowRunState.Paused or WorkflowRunState.Completing
        or WorkflowRunState.LocalWaitParking or WorkflowRunState.OnlineHoeingParking;

    /// <summary>取该计划最近创建的一次运行，推出本轮路径。没有运行时返回 <see cref="Empty"/>。</summary>
    public static TaskCenterRunTrace Build(WorkflowDocument? doc, IEnumerable<WorkflowRunRecord> runs)
    {
        if (doc?.WorkflowId is not { Length: > 0 } workflowId) return Empty;
        var run = runs.Where(r => r.WorkflowId == workflowId).OrderByDescending(r => r.CreatedAt).FirstOrDefault();
        if (run is null) return Empty;
        var round = run.Cursor?.LoopIteration ?? run.NodeOutcomes.Select(o => o.LoopIteration).DefaultIfEmpty(0).Max();
        var active = IsActiveState(run.State);
        var trace = new TaskCenterRunTrace
        {
            RunId = run.RunId, State = run.State, Round = round, IsActive = active,
            Key = $"{run.RunId}|{run.RecordRevision}|{run.State}|{round}",
            WaitingNodeId = active && run.State == WorkflowRunState.Waiting && run.Wait is { Kind: WorkflowRunner.TimeWindowStartWaitKind }
                ? run.Cursor?.NodeId : null,
            WaitingText = active && run.State == WorkflowRunState.Waiting && run.Wait is { Kind: WorkflowRunner.TimeWindowStartWaitKind } wait
                ? $"等到 {wait.NextTriggerAt:HH:mm}" : null,
        };
        foreach (var outcome in run.NodeOutcomes.Where(o => o.LoopIteration == round))
            trace._nodes[outcome.NodeId] = outcome.Result switch
            {
                "succeeded" => NodeRunState.Success,
                "branchYes" => NodeRunState.CondTrue,
                "branchNo" => NodeRunState.CondFalse,
                "skippedUser" or "skippedFilter" => NodeRunState.Skipped,
                "" => NodeRunState.None,
                _ => NodeRunState.Failed,
            };
        if (active && run.Cursor is { } cursor && cursor.LoopIteration == round && !trace._nodes.ContainsKey(cursor.NodeId))
            trace._nodes[cursor.NodeId] = NodeRunState.Running;

        if (run.ExtensionData is { Count: > 0 } data)
        {
            // 流程步骤检查点：taskFlowStep:{流程所在节点}:{轮次}:{步骤}（见 TaskFlowRuntime）。只读本轮。
            foreach (var owner in doc.Nodes.Where(n => n.Flow is not null || n.Lanes?.Decision is not null).Select(n => n.NodeId))
            {
                var prefix = $"taskFlowStep:{owner}:{round}:";
                foreach (var pair in data)
                {
                    if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal)) continue;
                    var stepId = pair.Key[prefix.Length..];
                    if (stepId.Contains(':') || pair.Value.ValueKind != JsonValueKind.String) continue;
                    var state = pair.Value.GetString() switch
                    {
                        "done" => NodeRunState.Success,
                        "yes" or "conditionYes" => NodeRunState.CondTrue,
                        "no" or "conditionNo" => NodeRunState.CondFalse,
                        "waiting" or "executing" => active ? NodeRunState.Running : NodeRunState.Failed,
                        _ => NodeRunState.None,
                    };
                    if (state != NodeRunState.None) trace._steps[owner + "/" + stepId] = state;
                }
            }
            // 内部判断执行完后检查点统一改写为 done，选了哪一支只能从子链留下的检查点看出；两支都没有记录时保持「已完成」。
            foreach (var node in doc.Nodes)
                foreach (var flow in new[] { node.Flow, node.Lanes?.Decision })
                    if (flow is not null) trace.ResolveBranches(node.NodeId, flow.Steps);
        }
        trace.MarkJumpedOver(doc);
        return trace;
    }

    /// <summary>
    /// 推出本轮被跳转跳过的主计划行：跳转来源本轮已经实际经过（流程执行成功，或车道选中的路线已走完），
    /// 来源与目标之间、本轮没有结果的行视为被跳过。车道成员随所在车道一起计。只读，不影响执行。
    /// </summary>
    private void MarkJumpedOver(WorkflowDocument doc)
    {
        var main = doc.Nodes.Where(n => !WorkflowComposition.IsLaneMember(doc, n.NodeId)).ToList();
        void Mark(string sourceId, string? target)
        {
            if (WorkflowComposition.JumpTarget(doc, target) is not { } resolved) return;
            var from = main.FindIndex(n => n.NodeId == sourceId);
            var to = main.FindIndex(n => n.NodeId == resolved);
            if (from < 0 || to <= from) return;
            for (var i = from + 1; i < to; i++)
            {
                var row = main[i];
                if (_nodes.ContainsKey(row.NodeId)) continue; // 本轮另经别的路线执行过，不算跳过
                _jumpedOver.Add(row.NodeId);
                if (row.Lanes is { } l)
                    foreach (var id in l.YesNodeIds.Concat(l.NoNodeIds)) if (!_nodes.ContainsKey(id)) _jumpedOver.Add(id);
            }
        }
        foreach (var n in main)
        {
            if (n.Kind == "control.flow" && Node(n.NodeId) == NodeRunState.Success) Mark(n.NodeId, n.Flow?.JumpTo);
            if (n.Lanes is not { Decision: not null } lane) continue;
            var chose = LaneChoice(doc, n);
            if (chose is null) continue;
            var route = chose.Value ? lane.YesNodeIds : lane.NoNodeIds;
            // 路线走完才算跳出：空路线判断后即跳；否则路线最后一个任务本轮已有结果。
            var finished = route.Count == 0 || _nodes.ContainsKey(route[^1]) && Node(route[^1]) != NodeRunState.Running;
            if (finished) Mark(n.NodeId, chose.Value ? lane.YesJumpTo : lane.NoJumpTo);
        }
    }

    private void ResolveBranches(string owner, IEnumerable<StartupStep> steps)
    {
        foreach (var s in steps)
        {
            ResolveBranches(owner, s.TrueSteps); ResolveBranches(owner, s.FalseSteps);
            ResolveBranches(owner, s.FireSteps); ResolveBranches(owner, s.BeforeAttemptSteps);
            if (s.NodeType != "condition" || Step(owner, s.Id) != NodeRunState.Success) continue;
            var yes = Touched(owner, s.TrueSteps); var no = Touched(owner, s.FalseSteps);
            if (yes != no) _steps[owner + "/" + s.Id] = yes ? NodeRunState.CondTrue : NodeRunState.CondFalse;
        }
    }

    private bool Touched(string owner, IEnumerable<StartupStep> steps)
        => steps.Any(s => Step(owner, s.Id) != NodeRunState.None
            || Touched(owner, s.TrueSteps) || Touched(owner, s.FalseSteps));
}
