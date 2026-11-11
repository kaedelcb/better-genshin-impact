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

    /// <summary>删除确认（测试可替换）：参数为提示文字，返回 true 继续删除。</summary>
    internal Func<string, bool> ConfirmDelete { get; set; } = message =>
    {
        var owner = System.Windows.Application.Current?.MainWindow;
        return (owner is null
            ? System.Windows.MessageBox.Show(message, "删除跳转目标", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No)
            : System.Windows.MessageBox.Show(owner, message, "删除跳转目标", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No))
            == System.Windows.MessageBoxResult.Yes;
    };

    /// <summary>删除前检查：这一行（或流程/车道带走的行）被跳转指向时先确认。返回 false 表示用户取消。</summary>
    internal bool ConfirmDeleteJumpTarget(NodeEditVm node)
        => Editing?.DeleteJumpWarning(node) is not { } warning || ConfirmDelete(warning);

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

    /// <summary>按当前计划和最新运行列表重算路径；计划表行标志每次重设，流程图等只在有变化时通知。</summary>
    private void UpdateRunTrace(IEnumerable<WorkflowRunRecord> runs)
    {
        TaskCenterRunTrace next;
        try { next = TaskCenterRunTrace.Build(Editing?.Draft, runs); }
        catch { next = TaskCenterRunTrace.Empty; }
        var key = (Editing?.Draft.WorkflowId ?? "") + "|" + next.Key;
        var previous = _runTrace;
        _runTrace = next;
        // 计划表每行的运行标志：每次刷新都按当前行重设（切换计划、重建草稿后不残留旧标志）。
        // 必须在 _runTrace 更新之后设：行标志变化会触发车道行重画，重画时读取的是 RunTrace。
        if (Editing is { } editing)
            foreach (var row in editing.Nodes)
            {
                row.RunWaitText = row.NodeId == next.WaitingNodeId ? next.WaitingText : null;
                row.RunState = next.Node(row.NodeId);
            }
        if (key == _runTraceKey) return;
        _runTraceKey = key;
        if (Editing?.Draft is { } doc && next.HasRun)
            foreach (var lane in doc.Nodes.Where(n => n.Lanes is not null))
                if (next.LaneChoice(doc, lane) is not null
                    && (previous.RunId != next.RunId || previous.Round != next.Round || previous.LaneChoice(doc, lane) is null))
                    LaneDecided?.Invoke(lane.NodeId);
        RunTraceChanged?.Invoke();
    }
}

public sealed partial class NodeEditVm
{
    private NodeRunState _runState;
    /// <summary>该行在最近一次运行本轮的状态（只读展示，来自运行记录）。</summary>
    public NodeRunState RunState
    {
        get => _runState;
        internal set
        {
            // 只是展示运行记录，不是编辑：不走 NodeEditVm.SetProperty（那里会记撤销步骤）。
            if (_runState == value) return;
            _runState = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(RunStateText)); OnPropertyChanged(nameof(HasRunState));
        }
    }
    public bool HasRunState => RunState != NodeRunState.None;
    private string? _runWaitText;
    /// <summary>本行正在等待时段开始时的说明（替代「执行中…」显示）。</summary>
    public string? RunWaitText
    {
        get => _runWaitText;
        internal set
        {
            if (_runWaitText == value) return;
            _runWaitText = value;
            OnPropertyChanged(); OnPropertyChanged(nameof(RunStateText));
        }
    }
    /// <summary>与流程图徽标同口径的文字。</summary>
    public string RunStateText => RunState switch
    {
        NodeRunState.Running => RunWaitText ?? "执行中…",
        NodeRunState.Success => "✓ 已完成",
        NodeRunState.Failed => "✗ 未完成",
        NodeRunState.Skipped => "已跳过",
        NodeRunState.CondTrue => "→ 满足",
        NodeRunState.CondFalse => "→ 其他",
        _ => "",
    };
}
