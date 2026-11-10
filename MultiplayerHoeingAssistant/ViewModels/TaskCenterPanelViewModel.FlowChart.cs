using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>任务中心流程图与「走过的路径」展示：只读运行记录，不参与执行。</summary>
public sealed partial class TaskCenterPanelViewModel
{
    private TaskCenterRunTrace _runTrace = TaskCenterRunTrace.Empty;
    private string _runTraceKey = "";

    /// <summary>当前计划最近一次运行在本轮走过的路径。</summary>
    public TaskCenterRunTrace RunTrace => _runTrace;

    /// <summary>运行路径有变化（流程图、计划表据此重画/展开）。</summary>
    internal event Action? RunTraceChanged;

    /// <summary>车道判断出结果时触发（参数：车道节点 Id）；页面在开关打开时展开该车道。</summary>
    internal event Action<string>? LaneDecided;

    /// <summary>页面请求打开流程图（页面代码后置负责建窗口）。</summary>
    internal event Action? FlowChartRequested;

    public RelayCommand ShowFlowChartCommand => new(_ =>
    {
        if (Editing is null) { ShowInlineActionFeedback("请先选择一张计划表，再查看流程图。", true); return; }
        FlowChartRequested?.Invoke();
    });

    private string AutoExpandSettingPath
        => Path.Combine(Path.GetDirectoryName(_host.Workflows.RootDirectory) ?? AssistantDataDirectory.Root,
            "taskcenter-auto-expand-branch.txt");

    private bool? _autoExpandTakenBranch;
    /// <summary>执行时自动展开实际走到的车道（默认开；监控端按本机保存，不影响执行端）。</summary>
    public bool AutoExpandTakenBranch
    {
        get
        {
            if (_autoExpandTakenBranch is { } cached) return cached;
            var value = true;
            try
            {
                var path = IsRemoteCenter ? RemoteAutoExpandPath : AutoExpandSettingPath;
                if (File.Exists(path)) value = File.ReadAllText(path).Trim() != "0";
            }
            catch { /* 读不到按默认开 */ }
            _autoExpandTakenBranch = value;
            return value;
        }
        set
        {
            if (AutoExpandTakenBranch == value) return;
            _autoExpandTakenBranch = value;
            OnPropertyChanged();
            try
            {
                var path = IsRemoteCenter ? RemoteAutoExpandPath : AutoExpandSettingPath;
                using var writing = ConfigFileWriteGate.Acquire(path);
                var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temp, value ? "1" : "0");
                File.Move(temp, path, overwrite: true);
            }
            catch (Exception ex) { _log("[任务中心] 保存「执行时展开走过的分支」失败：" + ex.Message); }
        }
    }

    private static string RemoteAutoExpandPath
        => Path.Combine(AssistantDataDirectory.Root, "taskcenter-auto-expand-branch.txt");

    /// <summary>按当前计划和最新运行列表重算路径；有变化才通知。</summary>
    private void UpdateRunTrace(IEnumerable<WorkflowRunRecord> runs)
    {
        TaskCenterRunTrace next;
        try { next = TaskCenterRunTrace.Build(Editing?.Draft, runs); }
        catch { next = TaskCenterRunTrace.Empty; }
        var key = (Editing?.Draft.WorkflowId ?? "") + "|" + next.Key;
        if (key == _runTraceKey) return;
        var previous = _runTrace;
        _runTrace = next;
        _runTraceKey = key;
        if (Editing?.Draft is { } doc && next.HasRun)
            foreach (var lane in doc.Nodes.Where(n => n.Lanes is not null))
                if (next.LaneChoice(doc, lane) is not null
                    && (previous.RunId != next.RunId || previous.Round != next.Round || previous.LaneChoice(doc, lane) is null))
                    LaneDecided?.Invoke(lane.NodeId);
        RunTraceChanged?.Invoke();
    }
}
