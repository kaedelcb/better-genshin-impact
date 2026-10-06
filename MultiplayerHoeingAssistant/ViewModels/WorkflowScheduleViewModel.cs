using System.Collections.ObjectModel;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class WorkflowEditVm
{
    private readonly Stack<ScheduleUndo> _scheduleUndo = new();
    public ObservableCollection<string> Lanes { get; } = ["主车道"];
    private NodeEditVm? _selectedNode;
    public NodeEditVm? SelectedNode { get => _selectedNode; set => SetProperty(ref _selectedNode, value); }

    internal void InitializeSchedule()
    {
        if (Draft.ExtensionData?.TryGetValue("scheduleLanes", out var lanes) == true && lanes.ValueKind == JsonValueKind.Array)
            foreach (var lane in lanes.EnumerateArray().Skip(1))
                if (lane.ValueKind == JsonValueKind.String && lane.GetString() is { Length: > 0 } name) Lanes.Add(name);
        InitializeUndo();
    }
    public void AddLane()
    {
        RememberSchedule(); EnablePaths(); Lanes.Add("支线 " + Lanes.Count); StoreLanes();
    }
    public void RemoveLane(int index)
    {
        if (index <= 0 || index >= Lanes.Count) return;
        if(Nodes.Any(n=>n.LaneIndex<=index && n.LaneIndex+n.LaneSpan>index))
            throw new InvalidOperationException("这条车道仍有任务或被跨列节点覆盖，请先移走任务或调整覆盖，再删除车道。");
        RememberSchedule();_scheduleChanging=true;
        try
        {
            string Shift(string target)
            {
                var parts=target.Split(':');
                if(parts.Length>=2 && (parts[0] is "lane" or "time") && int.TryParse(parts[1],out var lane))
                {
                    if(lane==index)return "removed:"+target;
                    if(lane>index){parts[1]=(lane-1).ToString();return string.Join(':',parts);}
                }
                return target;
            }
            foreach(var node in Nodes)
            {
                if(node.LaneIndex>index)node.LaneIndex--;
                node.NextTarget=Shift(node.NextTarget);node.YesTarget=Shift(node.YesTarget);node.NoTarget=Shift(node.NoTarget);
            }
            Lanes.RemoveAt(index);StoreLanes();
        }
        finally{_scheduleChanging=false;}
    }
    private void StoreLanes()
    {
        Draft.ExtensionData ??= new();
        Draft.ExtensionData["scheduleLanes"] = JsonSerializer.SerializeToElement(Lanes);
    }
    public void ScheduleNode(NodeEditVm node, int? minute, int lane = 0, bool remember = true)
    {
        if (!Nodes.Contains(node)) return;
        if (remember) RememberSchedule();
        _scheduleChanging=true;
        try
        {
        node.ScheduleTimeText = minute is { } value ? $"{value / 60:00}:{value % 60:00}" : "";
        node.LaneIndex = node.Model.Kind=="control.end" ? 0 : Math.Clamp(lane, 0, Lanes.Count - 1);
        node.LaneSpan = Math.Min(node.LaneSpan,Lanes.Count-node.LaneIndex);
        // Execution order follows the schedule; the same stable node remains a single entry.
        if (minute is not null)
        {
            var ordered = Nodes.OrderBy(n => n.Model.Kind=="control.end" ? int.MaxValue : n.ScheduleMinute ?? int.MaxValue).ThenBy(n=>n.Model.Kind=="control.end").ToArray();
            for (var i = 0; i < ordered.Length; i++)
            { Nodes.Move(Nodes.IndexOf(ordered[i]), i); }
            Draft.Nodes.Clear(); Draft.Nodes.AddRange(Nodes.Select(n => n.Model)); Renumber();
        }
        SelectedNode = node;
        }finally{_scheduleChanging=false;}
    }
    public NodeEditVm? AddScheduledResource(CatalogSourceVm source, int? minute, int lane)
    {
        var index = AppendSources.IndexOf(source); if (index < 0) return null;
        RememberSchedule(); AppendSourceIndex = index; AppendNodeCommand.Execute(null);
        var node = Nodes.LastOrDefault();
        if (node is not null) ScheduleNode(node, minute, lane, remember: false);
        return node;
    }
    public void RememberSchedule() => _scheduleUndo.Push(CaptureUndo());
    public bool UndoSchedule()=>_scheduleUndo.TryPop(out var undo) && RestoreUndo(undo);

}

public sealed partial class NodeEditVm
{
    private string? _resourceDisplayName;
    public string DisplayName => Model.Kind == "control.condition" ? "◇ 判断" : Model.Kind == "control.end" ? "🏁 结束流程" : _resourceDisplayName ?? (Model.Ref?.TaskId is { Length: > 0 } task ? ConfigName + " · " + task : ConfigName);
    internal void SetResourceDisplayName(string? value) { _resourceDisplayName=value;OnPropertyChanged(nameof(DisplayName)); }
    private string _scheduleTimeText = "", _scheduleUntilText = "";
    private int _scheduleModeIndex, _laneIndex;
    public string ScheduleTimeText { get => _scheduleTimeText; set { if (SetProperty(ref _scheduleTimeText, value)) OnPropertyChanged(nameof(ScheduleMinute)); } }
    public int? ScheduleMinute => TimeOnly.TryParseExact(ScheduleTimeText, "HH:mm", out var time) ? time.Hour * 60 + time.Minute : null;
    public string ScheduleUntilText { get => _scheduleUntilText; set => SetProperty(ref _scheduleUntilText, value); }
    public int ScheduleModeIndex { get => _scheduleModeIndex; set { if (value is >= 0 and <= 2) SetProperty(ref _scheduleModeIndex, value); } }
    public int LaneIndex { get => _laneIndex; set => SetProperty(ref _laneIndex, value); }
    private string _originalSchedule = "";
    internal void InitializeSchedule()
    {
        var schedule = Model.Strategies.LastOrDefault(s => s.Kind == "schedule.time");
        _scheduleTimeText = schedule?.GetString("time") ?? "";
        _scheduleUntilText = schedule?.GetString("until") ?? "";
        _scheduleModeIndex = schedule?.GetString("mode") switch { "fixed" => 1, "flexible" => 2, _ => 0 };
        if (Model.ExtensionData?.TryGetValue("scheduleLane", out var lane) == true && lane.TryGetInt32(out var index)) _laneIndex = index;
        InitializePath();
        _originalSchedule = ScheduleKey;
    }
    private string ScheduleKey => $"{ScheduleTimeText}|{ScheduleModeIndex}|{ScheduleUntilText}|{LaneIndex}";
    internal void ApplySchedule(WorkflowNode target)
    {
        ApplyPath(target);
        if (ScheduleKey == _originalSchedule) return;
        if (ScheduleTimeText.Length > 0 && ScheduleMinute is null) throw new InvalidOperationException("节点时间须为HH:mm。");
        if (ScheduleTimeText.Length > 0 && ScheduleModeIndex == 2 && !TimeOnly.TryParseExact(ScheduleUntilText,"HH:mm",out _))
            throw new InvalidOperationException("灵活窗口结束时间须为HH:mm。");
        var existing = target.Strategies.LastOrDefault(s => s.Kind == "schedule.time");
        if (ScheduleTimeText.Length == 0) target.Strategies.RemoveAll(s => s.Kind == "schedule.time");
        else
        {
            if (existing is null) { existing = new WorkflowStrategy { Kind = "schedule.time" }; target.Strategies.Add(existing); }
            existing.Params ??= new();
            existing.Params["time"] = JsonSerializer.SerializeToElement(ScheduleTimeText);
            existing.Params["mode"] = JsonSerializer.SerializeToElement(ScheduleModeIndex switch { 1 => "fixed", 2 => "flexible", _ => "sequence" });
            existing.Params["until"] = JsonSerializer.SerializeToElement(ScheduleUntilText);
        }
        target.ExtensionData ??= new(); target.ExtensionData["scheduleLane"] = JsonSerializer.SerializeToElement(LaneIndex);
    }
}
