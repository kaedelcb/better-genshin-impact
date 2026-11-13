using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>绑定弹窗的完成动作编辑器；目录属于被编辑的执行端，迟到响应不得改变新目标。</summary>
internal sealed class CompletionActionPanel
{
    private readonly Func<string, string, string, Task<CompletionTargetCatalog?>> _loadCatalog;
    private readonly IReadOnlyList<string> _groups;
    private readonly IReadOnlyList<string> _oneClicks;
    private readonly bool _supported;
    private readonly TaskConflictPolicySettings _initialSettings;
    private (string Type, string Key) _targetIdentity;
    private readonly StackPanel _layer2Panel = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly StackPanel _specifiedPanel = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _catalogStatus;
    private CompletionTargetCatalog? _catalog;
    private string _workflowId;
    private string _workflowName;
    private string _nodeId;
    private string _nodeName;
    private string _revision;
    private bool _updating;
    private bool _loading;
    private bool _confirmRevisionOnLoad;
    private long _request;

    internal StackPanel Root { get; } = new() { Margin = new Thickness(0, 10, 0, 0) };
    internal ComboBox PolicyCombo { get; }
    internal ComboBox ActionCombo { get; }
    internal ComboBox TypeCombo { get; }
    internal ComboBox NameCombo { get; }
    internal ComboBox StartModeCombo { get; }
    internal ComboBox NodeCombo { get; }
    internal TextBox PriorityBox { get; }
    internal Button RefreshButton { get; }
    internal Task CatalogLoadTask { get; private set; } = Task.CompletedTask;

    private sealed record WorkflowChoice(string Id, string Label);
    private sealed record NodeChoice(CompletionTargetNode Node, int Index)
    {
        public string Label => $"{Index + 1}. {Node.Name}";
    }

    internal CompletionActionPanel(TaskConflictPolicySettings settings, IReadOnlyList<string> groups,
        IReadOnlyList<string> oneClicks, string description,
        Func<string, string, string, Task<CompletionTargetCatalog?>> loadCatalog, bool supported = true,
        Func<ComboBox>? createCombo = null)
    {
        _groups = groups;
        _oneClicks = oneClicks;
        _loadCatalog = loadCatalog;
        _supported = supported;
        _initialSettings = CopySettings(settings);
        _workflowId = settings.SpecifiedWorkflowId;
        _workflowName = settings.SpecifiedTaskName;
        _nodeId = settings.SpecifiedStartTaskId;
        _nodeName = settings.SpecifiedStartTaskName;
        _revision = settings.SpecifiedConfigRevision;
        _updating = true;
        var gold = new SolidColorBrush(Color.FromRgb(0xE8, 0xC9, 0x6D));
        var dim = new SolidColorBrush(Color.FromRgb(0x9C, 0x97, 0xC0));
        var inner = new StackPanel { IsEnabled = supported };
        Root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Padding = new Thickness(10, 8, 10, 10), Child = inner
        });
        inner.Children.Add(Text("完成后动作", gold, 12));
        inner.Children.Add(Text(description, dim));
        PolicyCombo = Combo();
        PolicyCombo.ItemsSource = new[] { "恢复任务", "执行动作" };
        inner.Children.Add(PolicyCombo);
        ActionCombo = Combo();
        ActionCombo.ItemsSource = new[] { "停止", "执行指定任务" };
        _layer2Panel.Children.Add(ActionCombo);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        TypeCombo = Combo();
        TypeCombo.Width = 96;
        TypeCombo.Margin = new Thickness(0, 0, 8, 0);
        TypeCombo.ItemsSource = new[] { "配置组", "一条龙", "计划表" };
        row.Children.Add(TypeCombo);
        NameCombo = Combo();
        Grid.SetColumn(NameCombo, 1);
        row.Children.Add(NameCombo);
        _specifiedPanel.Children.Add(row);
        _specifiedPanel.Children.Add(Text("选择执行目标后，可从头执行或从其中一个已启用的任务开始。", dim));
        StartModeCombo = Combo();
        StartModeCombo.Margin = new Thickness(0, 8, 0, 0);
        StartModeCombo.ItemsSource = new[] { "从头开始执行", "从某个任务开始执行" };
        _specifiedPanel.Children.Add(StartModeCombo);
        NodeCombo = Combo();
        NodeCombo.DisplayMemberPath = nameof(NodeChoice.Label);
        NodeCombo.Margin = new Thickness(0, 6, 0, 0);
        _specifiedPanel.Children.Add(NodeCombo);
        RefreshButton = new Button { Content = "刷新任务目录", Margin = new Thickness(0, 6, 0, 0), Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Left };
        _specifiedPanel.Children.Add(RefreshButton);
        _catalogStatus = Text("", dim);
        _specifiedPanel.Children.Add(_catalogStatus);
        _specifiedPanel.Children.Add(Text("任务优先级（32 位整数，越大越优先）", gold, 11));
        PriorityBox = new TextBox
        {
            Text = settings.SpecifiedTaskPriority.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Padding = new Thickness(6, 3, 6, 3),
            Background = new SolidColorBrush(Color.FromRgb(0x1F, 0x1D, 0x3A)), Foreground = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x56, 0x50, 0x78))
        };
        _specifiedPanel.Children.Add(PriorityBox);
        _layer2Panel.Children.Add(_specifiedPanel);
        inner.Children.Add(_layer2Panel);
        PolicyCombo.SelectedIndex = settings.Policy == TaskConflictPolicy.Resume ? 0 : 1;
        ActionCombo.SelectedIndex = settings.Policy == TaskConflictPolicy.RunSpecified ? 1 : 0;
        TypeCombo.SelectedIndex = settings.SpecifiedTaskType switch { "onedragon" => 1, "workflow" => 2, _ => 0 };
        SetNames(settings.SpecifiedTaskName);
        StartModeCombo.SelectedIndex = settings.SpecifiedStartMode switch { "beginning" => 0, "node" => 1, _ => -1 };
        UpdateVisibility();
        _targetIdentity = TargetIdentity;
        _updating = false;
        PolicyCombo.SelectionChanged += (_, _) => UpdateVisibility();
        ActionCombo.SelectionChanged += (_, _) => UpdateVisibility();
        TypeCombo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _updating = true;
            _workflowId = _workflowName = "";
            SetNames("");
            NameCombo.SelectedIndex = NameCombo.Items.Count > 0 ? 0 : -1;
            _updating = false;
            TargetChanged();
        };
        NameCombo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            // SelectionChanged 与 editable Text 的更新顺序受模板影响；先同步本次选中的名称。
            if (NameCombo.IsEditable && NameCombo.SelectedItem is string selected)
            {
                _updating = true;
                NameCombo.Text = selected;
                _updating = false;
            }
            TargetChanged();
        };
        NameCombo.AddHandler(TextBox.TextChangedEvent, new TextChangedEventHandler((_, _) =>
        {
            if (!_updating && NameCombo.IsEditable) TargetChanged();
        }));
        StartModeCombo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            _nodeId = _nodeName = "";
            _updating = true;
            NodeCombo.SelectedIndex = -1;
            _updating = false;
            _revision = _catalog?.Target?.Revision ?? "";
            _confirmRevisionOnLoad = _loading;
            UpdateVisibility();
            ShowCatalogStatus();
        };
        StartModeCombo.DropDownClosed += (_, _) => ConfirmCurrentStart();
        NodeCombo.DropDownClosed += (_, _) => ConfirmCurrentStart();
        NodeCombo.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            var node = (NodeCombo.SelectedItem as NodeChoice)?.Node;
            _nodeId = node?.TaskId ?? "";
            _nodeName = node?.Name ?? "";
            _revision = _catalog?.Target?.Revision ?? "";
            ShowCatalogStatus();
        };
        RefreshButton.Click += (_, _) =>
        {
            // 刷新不把过期节点自动改成从头；由用户重新选择起点确认当前版本。
            _revision = "";
            CatalogLoadTask = RefreshCatalogAsync();
        };
        if (!supported)
            Root.Children.Add(Text("对方尚未支持执行起点设置，请更新执行端并重新打开绑定。", gold, 11));
        else CatalogLoadTask = RefreshCatalogAsync();

        ComboBox Combo() => createCombo?.Invoke() ?? new ComboBox
        {
            Background = new SolidColorBrush(Color.FromRgb(0x26, 0x23, 0x4E)), Foreground = Brushes.White,
            FontSize = 12, Padding = new Thickness(6, 3, 6, 3), VerticalAlignment = VerticalAlignment.Center
        };
    }

    private string Type => TypeCombo.SelectedIndex switch { 1 => "onedragon", 2 => "workflow", _ => "group" };
    private string Name => Type == "workflow" ? (NameCombo.SelectedItem as WorkflowChoice)?.Label ?? _workflowName : NameCombo.Text.Trim();
    private string WorkflowId => Type == "workflow" ? (NameCombo.SelectedItem as WorkflowChoice)?.Id ?? _workflowId : "";
    private (string Type, string Key) TargetIdentity => (Type, Type == "workflow" ? WorkflowId : Name);
    private TaskConflictPolicy Policy => PolicyCombo.SelectedIndex != 1 ? TaskConflictPolicy.Resume
        : ActionCombo.SelectedIndex == 1 ? TaskConflictPolicy.RunSpecified : TaskConflictPolicy.Stop;

    private void SetNames(string selected)
    {
        NameCombo.ItemsSource = null;
        NameCombo.DisplayMemberPath = "";
        NameCombo.IsEditable = Type != "workflow";
        if (Type == "workflow")
        {
            NameCombo.DisplayMemberPath = nameof(WorkflowChoice.Label);
            var workflows = (_catalog?.Workflows ?? []).Select(w => new WorkflowChoice(w.WorkflowId, w.Name)).ToList();
            if (_workflowId.Length != 0 && workflows.All(w => w.Id != _workflowId))
                workflows.Add(new(_workflowId, selected));
            NameCombo.ItemsSource = workflows;
            NameCombo.SelectedItem = workflows.FirstOrDefault(w => w.Id == _workflowId);
        }
        else
        {
            NameCombo.ItemsSource = Type == "onedragon" ? _oneClicks : _groups;
            NameCombo.Text = selected;
        }
    }

    private void TargetChanged()
    {
        if (Type == "workflow" && NameCombo.SelectedItem is WorkflowChoice workflow)
            (_workflowId, _workflowName) = (workflow.Id, workflow.Label);
        var identity = TargetIdentity;
        // 模板回填文本、同目标 SelectedItem/Text 同步和计划表标签刷新都不是换目标。
        if (identity == _targetIdentity) return;
        _targetIdentity = identity;
        _nodeId = _nodeName = _revision = "";
        _confirmRevisionOnLoad = false;
        _catalog = _catalog is null ? null : _catalog with { Target = null };
        _updating = true;
        NodeCombo.ItemsSource = null;
        StartModeCombo.SelectedIndex = 0;
        _updating = false;
        UpdateVisibility();
        CatalogLoadTask = RefreshCatalogAsync(confirmNewTarget: true, debounce: true);
    }

    internal async Task RefreshCatalogAsync(bool confirmNewTarget = false, bool debounce = false)
    {
        var request = ++_request;
        var (type, name, id) = (Type, Name, WorkflowId);
        var identity = TargetIdentity;
        _loading = true;
        _catalogStatus.Text = "正在读取执行端任务目录…";
        CompletionTargetCatalog? catalog;
        try
        {
            if (debounce) await Task.Delay(250);
            if (request != _request) return;
            catalog = await _loadCatalog(type, name, id);
        }
        catch (Exception ex) { catalog = new([], null, "读取目录失败：" + ex.Message); }
        if (request != _request || identity != TargetIdentity) return;
        _loading = false;
        _catalog = catalog ?? new([], null, "未取得执行端任务目录，请重新读取。");
        if (_catalog.Target is { } target
            && (target.Type, target.Type == "workflow" ? target.WorkflowId : target.Name.Trim()) != identity)
            _catalog = _catalog with { Target = null, Error = "目录目标不匹配，请重新读取。" };
        _updating = true;
        if (type == "workflow") SetNames(name);
        var choices = (_catalog.Target?.Nodes ?? []).Select((n, i) => new NodeChoice(n, i)).Where(n => n.Node.Enabled).ToArray();
        NodeCombo.ItemsSource = choices;
        NodeCombo.SelectedItem = choices.FirstOrDefault(n => n.Node.TaskId == _nodeId);
        if ((confirmNewTarget || _confirmRevisionOnLoad) && StartModeCombo.SelectedIndex == 0)
            _revision = _catalog.Target?.Revision ?? "";
        _confirmRevisionOnLoad = false;
        _updating = false;
        ShowCatalogStatus();
    }

    /// <summary>未编辑或不可编辑时无完成动作写入；普通绑定不依赖起点目录可用性。</summary>
    internal bool TryGetChanges(out TaskConflictPolicySettings? settings, out string? error)
    {
        settings = null;
        error = null;
        if (!_supported) return true;
        var candidate = ReadSettings(out var priorityValid);
        var before = CompletionPolicyFields.Parameters(_initialSettings);
        if (candidate.Policy == TaskConflictPolicy.RunSpecified)
            before["specifiedName"] = _initialSettings.SpecifiedTaskName.Trim();
        var changed = (Policy == TaskConflictPolicy.RunSpecified && !priorityValid)
            || CompletionPolicyFields.Parameters(candidate).Any(pair =>
                // 同一个稳定计划身份的显示名变化不表示用户编辑了完成动作。
                !(pair.Key == "specifiedName" && candidate.SpecifiedTaskType == "workflow"
                    && _initialSettings.SpecifiedTaskType == "workflow")
                && !Equals(before[pair.Key], pair.Value));
        if (!changed) return true;
        if (!TryGetSettings(out var edited, out error)) return false;
        settings = edited;
        return true;
    }

    internal bool TryGetSettings(out TaskConflictPolicySettings settings, out string? error)
    {
        settings = ReadSettings(out var priorityValid);
        error = !_supported ? "执行端尚未支持执行起点设置，请升级后重新读取。"
            : Policy != TaskConflictPolicy.RunSpecified ? null
            : !priorityValid ? "指定任务优先级必须是 32 位整数。"
            : StartModeCombo.SelectedIndex < 0 ? "旧配置沿用原入口；保存指定任务前请选择从头或从某个任务开始。"
            : _loading ? "任务目录正在读取，请稍后保存。"
            : CompletionTargetCatalogService.Validate(settings, _catalog ?? new([], null, "请先读取任务目录。"));
        return error is null;
    }

    private TaskConflictPolicySettings ReadSettings(out bool priorityValid)
    {
        priorityValid = int.TryParse(PriorityBox.Text.Trim(), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var priority);
        if (Policy != TaskConflictPolicy.RunSpecified)
        {
            // 恢复/停止的隐藏起点字段继续沿用保存值，不能在普通策略编辑时升级旧入口。
            var preserved = CopySettings(_initialSettings);
            preserved.Policy = Policy;
            return preserved;
        }
        return new()
        {
            Policy = Policy, SpecifiedTaskType = Type, SpecifiedTaskName = Name,
            SpecifiedTaskPriority = priority,
            SpecifiedStartMode = StartModeCombo.SelectedIndex switch { 0 => "beginning", 1 => "node", _ => "default" },
            SpecifiedStartTaskId = _nodeId, SpecifiedStartTaskName = _nodeName,
            SpecifiedConfigRevision = _revision, SpecifiedWorkflowId = WorkflowId
        };
    }

    private static TaskConflictPolicySettings CopySettings(TaskConflictPolicySettings settings) => new()
    {
        Policy = settings.Policy, SpecifiedTaskType = settings.SpecifiedTaskType,
        SpecifiedTaskName = settings.SpecifiedTaskName, SpecifiedTaskPriority = settings.SpecifiedTaskPriority,
        SpecifiedStartMode = settings.SpecifiedStartMode, SpecifiedStartTaskId = settings.SpecifiedStartTaskId,
        SpecifiedStartTaskName = settings.SpecifiedStartTaskName, SpecifiedConfigRevision = settings.SpecifiedConfigRevision,
        SpecifiedWorkflowId = settings.SpecifiedWorkflowId
    };

    private void ConfirmCurrentStart()
    {
        if (_updating || _loading || _catalog?.Target == null) return;
        if (StartModeCombo.SelectedIndex == 0)
        {
            _nodeId = _nodeName = "";
            _revision = _catalog.Target.Revision;
        }
        else if (StartModeCombo.SelectedIndex == 1 && NodeCombo.SelectedItem is NodeChoice selected)
        {
            _nodeId = selected.Node.TaskId;
            _nodeName = selected.Node.Name;
            _revision = _catalog.Target.Revision;
        }
        ShowCatalogStatus();
    }

    private void ShowCatalogStatus()
    {
        TryGetSettings(out _, out var error);
        _catalogStatus.Text = error ?? (_catalog?.Target is { } target
            ? $"已读取 {target.Nodes.Count(n => n.Enabled)} 个已启用任务。" : _catalog?.Error ?? "");
    }

    private void UpdateVisibility()
    {
        _layer2Panel.Visibility = PolicyCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        _specifiedPanel.Visibility = ActionCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        NodeCombo.Visibility = StartModeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static TextBlock Text(string text, Brush brush, double size = 10) => new()
    {
        Text = text, FontSize = size, Foreground = brush, TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 6)
    };
}
