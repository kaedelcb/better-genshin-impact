using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>跳转目标下拉框的一项：Id 为空表示「继续下一行」。</summary>
public sealed record JumpTargetChoice(string? NodeId, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 任务中心编号与跳转目标（Docs/design/taskcenter-jump-plan-20261111.md 第 2 阶段）。
/// 编号按计划表显示顺序：主计划行 #1、#2…，车道里的任务 #3.1、#3.2…；只在界面显示，跳转保存的是目标行 Id。
/// </summary>
public sealed partial class WorkflowEditVm
{
    /// <summary>每行的编号（NodeId → "#3" / "#3.1"）。旧配对车道的条件流程不单独编号，与车道共用。</summary>
    public IReadOnlyDictionary<string, string> RowNumbers()
    {
        var numbers = new Dictionary<string, string>(StringComparer.Ordinal);
        var rows = MainPlanNodes;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i].Model;
            var no = "#" + (i + 1);
            numbers[row.NodeId] = no;
            if (row.Lanes is not { } lane) continue;
            if (!string.IsNullOrEmpty(lane.SourceFlowNodeId)) numbers[lane.SourceFlowNodeId] = no;
            var k = 0;
            foreach (var id in lane.YesNodeIds.Concat(lane.NoNodeIds)) numbers[id] = $"{no}.{++k}";
        }
        return numbers;
    }

    /// <summary>编号 + 名称，例如「#5 至冬材料」；找不到时返回 null。</summary>
    public string? RowLabel(string? nodeId)
    {
        if (string.IsNullOrEmpty(nodeId)) return null;
        var node = Nodes.FirstOrDefault(n => n.NodeId == nodeId);
        if (node is null) return null;
        return RowNumbers().TryGetValue(nodeId, out var no) ? no + " " + node.DisplayName : node.DisplayName;
    }

    /// <summary>
    /// 某行可选的跳转目标：第一项「继续下一行」，其后是它之后的主计划行（不含自身、不含车道里的任务）。
    /// 新建、尚未加入计划的流程或车道（sourceNodeId 为空）按「加在末尾」处理，没有可跳的目标。
    /// 当前已保存但不合法的目标（例如被拖到前面）也列出并注明，避免下拉框把它悄悄换掉。
    /// </summary>
    public IReadOnlyList<JumpTargetChoice> JumpTargetChoices(string? sourceNodeId, string? current)
    {
        var choices = new List<JumpTargetChoice> { new(null, "继续下一行") };
        var rows = MainPlanNodes;
        var from = sourceNodeId is null ? rows.Count : rows.ToList().FindIndex(n => n.NodeId == sourceNodeId);
        var numbers = RowNumbers();
        for (var i = from + 1; i < rows.Count && from >= 0; i++)
            choices.Add(new(rows[i].NodeId, $"跳到 {numbers[rows[i].NodeId]} {rows[i].DisplayName}"));
        if (!string.IsNullOrEmpty(current) && choices.All(c => c.NodeId != current))
            choices.Add(new(current, RowLabel(current) is { } label ? $"跳到 {label}（在前面，不能执行）" : "跳到 已删除的行（请重新选择）"));
        return choices;
    }

    /// <summary>各行的跳转问题（NodeId → 原因），用于计划表标红。</summary>
    public IReadOnlyDictionary<string, string> JumpProblems()
    {
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (source, message) in WorkflowComposition.JumpIssues(Draft))
            problems[source] = problems.TryGetValue(source, out var prior) ? prior + "；" + message : message;
        return problems;
    }

    /// <summary>走不到的主计划行（只提示）。</summary>
    public IReadOnlySet<string> UnreachableRows() => WorkflowComposition.UnreachableRows(Draft).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// 删除前确认：被跳到的行删除后，指向它的跳转会改回「继续下一行」，先问用户。
    /// 返回 null 表示没有跳转指向它（无需确认）；否则返回提示文字。删除流程或车道时包括它们带走的行。
    /// </summary>
    internal string? DeleteJumpWarning(NodeEditVm node)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal) { node.NodeId };
        if (node.IsComposition) ids.UnionWith(CompositionGroupIds(node));
        var sources = JumpSourcesTo(ids).Where(s => !ids.Contains(s.NodeId)).ToArray();
        if (sources.Length == 0) return null;
        var names = string.Join("、", sources.Select(s => RowLabel(s.NodeId) ?? s.DisplayName));
        return $"「{RowLabel(node.NodeId) ?? node.DisplayName}」是 {names} 的跳转目标。\n删除后，这些跳转会改回「继续下一行」。\n\n确定删除吗？";
    }

    /// <summary>设置流程行跳转目标；null = 继续下一行。</summary>
    internal void SetFlowJump(NodeEditVm node, string? target)
    {
        if (!Nodes.Contains(node) || node.Model.Flow is not { } flow || flow.JumpTo == target) return;
        RememberSchedule();
        flow.JumpTo = string.IsNullOrWhiteSpace(target) ? null : target;
        node.NotifyCompositionChanged(); CompositionChanged();
    }

    /// <summary>重算每行的编号、跳转说明、跳转问题与不可达提示（计划结构变化后调用；只是展示，不改计划）。</summary>
    internal void RefreshJumpLabels()
    {
        IReadOnlyDictionary<string, string> numbers, problems;
        IReadOnlySet<string> unreachable;
        IReadOnlyList<(string SourceNodeId, string? Route, string TargetNodeId, IReadOnlyList<string> Skipped)> spans;
        try { numbers = RowNumbers(); problems = JumpProblems(); unreachable = UnreachableRows(); spans = WorkflowComposition.JumpSpans(Draft); }
        catch { return; } // 编辑中途结构暂时不完整时保持上一次的显示
        string Route(string? route) => route switch { "yes" => "满足条件", "no" => "其他情况", _ => "" };
        // 被跳过的行：正常顺序走到它的路线会被跳过，只有从别处跳进来（或来源被停用/过滤）才执行。
        var entered = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var s in spans)
        {
            var target = JumpTarget(s.TargetNodeId);
            if (target is null) continue;
            if (!entered.TryGetValue(target, out var list)) entered[target] = list = [];
            list.Add(numbers.GetValueOrDefault(s.SourceNodeId, "") + (s.Route is null ? "" : " " + Route(s.Route)));
        }
        foreach (var node in Nodes)
        {
            var jump = node.JumpSummary(this);
            var mySpans = spans.Where(s => s.SourceNodeId == node.NodeId && s.Skipped.Count > 0).ToArray();
            if (mySpans.Length > 0)
                jump += "（跳过 " + string.Join("、", mySpans.SelectMany(s => s.Skipped).Distinct().Select(id => numbers.GetValueOrDefault(id, ""))) + "）";
            // 只靠跳转进入：前一行设了跳转，正常顺序不会走到这里。
            var from = entered.TryGetValue(node.NodeId, out var sources) && spans.Any(s => s.Skipped.Contains(node.NodeId))
                ? "从 " + string.Join("、", sources) + " 跳到这里" : "";
            node.SetJumpLabels(numbers.GetValueOrDefault(node.NodeId, ""), jump,
                problems.GetValueOrDefault(node.NodeId, ""), unreachable.Contains(node.NodeId), from);
        }
    }

    private string? JumpTarget(string target) => WorkflowComposition.JumpTarget(Draft, target) is { } resolved
        ? Nodes.FirstOrDefault(n => n.NodeId == resolved || n.Model.Lanes?.SourceFlowNodeId == resolved)?.NodeId : null;
}

public sealed partial class NodeEditVm
{
    private string _rowNumber = "", _jumpText = "", _jumpProblem = "";
    private bool _isUnreachable;
    // 以下都是只读展示属性（没有 setter）：未保存修改与撤销快照只采集可写属性，展示字段不能混进编辑状态。
    /// <summary>界面编号，例如「#3」「#3.1」。</summary>
    public string RowNumber => _rowNumber;
    /// <summary>跳转说明，例如「执行完后 → #5 至冬材料」；没有跳转为空。</summary>
    public string JumpText => _jumpText;
    public bool HasJump => _jumpText.Length > 0;
    /// <summary>跳转设置有问题时的原因（计划因此不能启动，来源行标红）。</summary>
    public string JumpProblem => _jumpProblem;
    public bool HasJumpProblem => _jumpProblem.Length > 0;
    /// <summary>没有路径会走到这一行（只提示，不阻止启动）。</summary>
    public bool IsUnreachable => _isUnreachable;
    /// <summary>行下方的提示：跳转问题优先，其次是不可达，再次是「从哪里跳进来」。</summary>
    public string RowNotice => HasJumpProblem ? "⚠ " + JumpProblem
        : IsUnreachable ? "没有路径会走到这一行：前面的流程会无条件跳过它，执行时不会执行。"
        : _jumpedFrom.Length > 0 ? "↪ " + _jumpedFrom + "（按顺序经过前一行时会被跳过）" : "";
    public bool HasRowNotice => RowNotice.Length > 0;
    private string _jumpedFrom = "";
    /// <summary>只靠跳转进入这一行时，写明从哪里跳进来，例如「从 #1 其他情况 跳到这里」。</summary>
    public string JumpedFrom => _jumpedFrom;

    internal void SetJumpLabels(string number, string jump, string problem, bool unreachable, string jumpedFrom = "")
    {
        var changed = new List<string>();
        if (_rowNumber != number) { _rowNumber = number; changed.Add(nameof(RowNumber)); }
        if (_jumpText != jump) { _jumpText = jump; changed.AddRange([nameof(JumpText), nameof(HasJump)]); }
        if (_jumpProblem != problem) { _jumpProblem = problem; changed.AddRange([nameof(JumpProblem), nameof(HasJumpProblem), nameof(RowNotice), nameof(HasRowNotice)]); }
        if (_isUnreachable != unreachable) { _isUnreachable = unreachable; changed.AddRange([nameof(IsUnreachable), nameof(RowNotice), nameof(HasRowNotice)]); }
        if (_jumpedFrom != jumpedFrom) { _jumpedFrom = jumpedFrom; changed.AddRange([nameof(JumpedFrom), nameof(RowNotice), nameof(HasRowNotice)]); }
        foreach (var name in changed.Distinct()) OnPropertyChanged(name);
    }

    /// <summary>该行的跳转描述，例如「执行完后 → #5 至冬材料」；没有跳转返回空。车道按路线分别描述。</summary>
    internal string JumpSummary(WorkflowEditVm editing)
    {
        string Target(string? id) => editing.RowLabel(id) ?? "已删除的行";
        if (Model.Flow?.JumpTo is { Length: > 0 } f) return "执行完后 → " + Target(f);
        if (Model.Lanes is not { } lane) return "";
        var parts = new List<string>();
        if (lane.YesJumpTo is { Length: > 0 } y) parts.Add("满足条件路线走完 → " + Target(y));
        if (lane.NoJumpTo is { Length: > 0 } n) parts.Add("其他情况路线走完 → " + Target(n));
        return string.Join("；", parts);
    }
}
