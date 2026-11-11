using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>基础表结构行；只渲染与编辑，不安装或拥有触发器。</summary>
public sealed class CompositionPlanRow : UserControl
{
    private Border? _frame;
    private readonly Expander _expander = new() { IsExpanded = false, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0) };
    private NodeEditVm? _node;
    private TaskCenterPanelViewModel? _host;
    private ContextMenu? _activeLaneMenu;
    private bool _restoring;
    private void RestoreExpansion()
    {
        _restoring = true;
        try { _expander.IsExpanded = _node is { IsComposition: true } node && View?.IsRowExpanded(node.NodeId) == true; }
        finally { _restoring = false; }
    }
    private void SnapshotChanged()
    {
        if (_expander.IsExpanded && _node?.Model.Lanes is not null && _activeLaneMenu?.IsOpen != true) Draw();
    }
    private void TraceChanged()
    {
        if (_expander.IsExpanded && _node?.Model.Flow is not null) Draw();
    }
    private BasicTaskCenterView? View
    {
        get
        {
            DependencyObject? parent = this;
            while (parent is not null) { if (parent is BasicTaskCenterView view) return view; parent = VisualTreeHelper.GetParent(parent); }
            return null;
        }
    }
    public CompositionPlanRow()
    {
        CompositionUiTheme.Apply(this);
        _expander.SetResourceReference(StyleProperty, "CompositionExpander");
        // The shared expander has generous padding for dialogs. Plan rows use a compact
        // single-line header while keeping the same purple/gold interaction states.
        _expander.Template = (ControlTemplate)XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="Expander">
              <StackPanel>
                <ToggleButton IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}"
                              Foreground="{TemplateBinding Foreground}" Background="Transparent" BorderThickness="0"
                              Padding="0" HorizontalContentAlignment="Stretch" FocusVisualStyle="{x:Null}">
                  <ToggleButton.Template><ControlTemplate TargetType="ToggleButton">
                    <Border x:Name="HeaderBorder" Background="Transparent" CornerRadius="4">
                      <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                        <Path x:Name="Arrow" Data="M 2,1 L 7,6 L 2,11" Stroke="#E8C96D" StrokeThickness="1.5" HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/>
                        <ContentPresenter Grid.Column="1" VerticalAlignment="Center" TextElement.Foreground="{TemplateBinding Foreground}"/>
                      </Grid>
                    </Border>
                    <ControlTemplate.Triggers>
                      <Trigger Property="IsChecked" Value="True"><Setter TargetName="Arrow" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter></Trigger>
                      <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="HeaderBorder" Property="Background" Value="#29473B59"/></Trigger>
                      <Trigger Property="IsKeyboardFocused" Value="True"><Setter TargetName="HeaderBorder" Property="Background" Value="#3A473B59"/></Trigger>
                    </ControlTemplate.Triggers>
                  </ControlTemplate></ToggleButton.Template>
                  <ContentPresenter ContentSource="Header"/>
                </ToggleButton>
                <ContentPresenter x:Name="Body" ContentSource="Content" Visibility="Collapsed"/>
              </StackPanel>
              <ControlTemplate.Triggers><Trigger Property="IsExpanded" Value="True"><Setter TargetName="Body" Property="Visibility" Value="Visible"/></Trigger></ControlTemplate.Triggers>
            </ControlTemplate>
            """);
        Content = _frame = new Border { BorderBrush = B("#51455D"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = B("#2B253F"), Child = _expander, Margin = new Thickness(0, 0, 0, 1) };
        DataContextChanged += (_, e) =>
        {
            if (_node is not null) _node.PropertyChanged -= Changed;
            _node = e.NewValue as NodeEditVm;
            RestoreExpansion();
            if (_node is not null) _node.PropertyChanged += Changed;
            Draw();
        };
        _expander.Expanded += (_, _) =>
        {
            if (_restoring) return;
            if (_node is { } node) View?.RememberRowExpansion(node.NodeId, true);
            Draw();
        };
        _expander.Collapsed += (_, _) =>
        {
            _expander.Content = null;
            if (!_restoring && _node is { } node) View?.RememberRowExpansion(node.NodeId, false);
        };
        Loaded += (_, _) =>
        {
            if (_host is not null) { _host.SnapshotRefreshed -= SnapshotChanged; _host.RunTraceChanged -= TraceChanged; }
            RestoreExpansion();
            _host = View?.CompositionHost;
            if (_host is not null) { _host.SnapshotRefreshed += SnapshotChanged; _host.RunTraceChanged += TraceChanged; }
            if (_node is not null) { _node.PropertyChanged -= Changed; _node.PropertyChanged += Changed; }
            Draw();
        };
        Unloaded += (_, _) => { if (_node is not null) _node.PropertyChanged -= Changed; if (_host is not null) { _host.SnapshotRefreshed -= SnapshotChanged; _host.RunTraceChanged -= TraceChanged; } };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeEditVm.DisplayName) or nameof(NodeEditVm.CompositionSummary) or nameof(NodeEditVm.ResourceIndex)
            or nameof(NodeEditVm.RunState) or nameof(NodeEditVm.RowNumber) or nameof(NodeEditVm.JumpText) or nameof(NodeEditVm.RowNotice))
            Draw();
    }
    internal void ExpandContents() => _expander.IsExpanded = true;
    private static SolidColorBrush B(string c) => new((Color)ColorConverter.ConvertFromString(c));
    private static TextBlock Text(string text, bool gold = false) => new() { Text = text, Foreground = B(gold ? "#EAD391" : "#EEE8F5"),
        VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(3, 2, 6, 2) };
    private static Button ActionButton(string text, Action action)
    {
        var b = new Button { Content = text, Background = B("#3B3350"), Foreground = B("#E8DFF2"), BorderBrush = B("#6D607B"),
            Padding = new Thickness(6, 3, 6, 3), Margin = new Thickness(3, 2, 0, 2), FontSize = 11 };
        CompositionUiTheme.StyleButton(b);
        b.Click += (_, e) => { e.Handled = true; action(); }; return b;
    }
    private void Draw()
    {
        if (_node is not { IsComposition: true } node)
        {
            _expander.Header = null; _expander.Content = null; return;
        }
        var header = new Grid { Height = 27, ToolTip = node.CompositionSummary };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(25) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var number = Text(node.RowNumber.Length > 0 ? node.RowNumber : $"{node.ResourceIndex:00}", true); number.FontSize = 11; header.Children.Add(number);
        var kind = Text(node.Model.Flow is null ? "车道" : "流程", true);
        kind.FontSize = 11; kind.Margin = new Thickness(0); kind.HorizontalAlignment = HorizontalAlignment.Center;
        var badge = new Border { Child = kind, Background = B("#403547"), CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(3, 1, 3, 1), Margin = new Thickness(0, 0, 5, 0) };
        Grid.SetColumn(badge, 1); header.Children.Add(badge);
        var name = Text(node.DisplayName); name.FontWeight = FontWeights.SemiBold; name.ToolTip = node.DisplayName;
        Grid.SetColumn(name, 2); header.Children.Add(name);
        var decision = node.Model.Lanes is not null && View?.CompositionHost?.Editing is { } editing
            ? MultiplayerHoeingAssistant.Services.WorkflowComposition.GetDecision(editing.Draft, node.Model) : null;
        var final = decision?.Steps.FirstOrDefault(s => s.Id == decision.ExitStepId);
        var summary = final is null ? node.CompositionSummary : LanesEditorWindow.Describe(final)
            + " → " + (node.Model.Lanes!.YesName == "是" ? "路线 1" : node.Model.Lanes.YesName)
            + "；其他 → " + (node.Model.Lanes.NoName == "否" ? "路线 2" : node.Model.Lanes.NoName);
        // 有跳转时摘要后面接上去向，例如「…；执行完后 → #5 至冬材料」。
        if (node.HasJump) summary += "　" + node.JumpText;
        var hint = Text(summary); hint.FontSize = 11; hint.Foreground = B(node.HasJump ? "#C9B4F0" : "#C7BCD8"); hint.ToolTip = summary;
        hint.Margin = new Thickness(7, 0, 6, 0); Grid.SetColumn(hint, 3); header.Children.Add(hint);
        var edit = ActionButton(node.Model.Flow is null ? "设置车道" : "编辑步骤", () => { if (node.Model.Flow is not null) View?.OpenFlowEditor(node); else View?.EditLanes(node); });
        edit.Height = edit.MinHeight = 24; edit.Padding = new Thickness(6, 1, 6, 1); edit.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(edit, 4); header.Children.Add(edit);
        // 最近一次运行本轮的状态（车道按选中的路线显示），与普通任务行、流程图同口径。
        // 车道行的结果就是本轮选中的路线（branchYes/branchNo → CondTrue/CondFalse）；旧配对车道的结果记在原流程行上，不在这里重复。
        var laneChoice = node.Model.Lanes is not null ? node.RunState switch { NodeRunState.CondTrue => true, NodeRunState.CondFalse => (bool?)false, _ => null } : null;
        var runText = laneChoice is { } chose ? (chose ? "→ 满足条件" : "→ 其他情况") : node.RunStateText;
        if (runText.Length > 0)
        {
            var running = laneChoice is null && node.RunState == NodeRunState.Running;
            var failed = laneChoice is null && node.RunState == NodeRunState.Failed;
            var label = new TextBlock { Text = runText, FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                Foreground = B(running ? "#241D3D" : failed ? "#F0B3A3" : "#6FD8CE") };
            var runBadge = new Border { Child = label, CornerRadius = new CornerRadius(9), Padding = new Thickness(7, 1, 7, 1),
                Margin = new Thickness(4, 0, 2, 0), VerticalAlignment = VerticalAlignment.Center, BorderThickness = new Thickness(1),
                Background = B(running ? "#E8C96D" : failed ? "#33EF5350" : "#1F6FD8CE"), BorderBrush = B(running ? "#E8C96D" : failed ? "#7A4545" : "#3F6A60"),
                ToolTip = "最近一次运行本轮的执行状态" };
            header.ColumnDefinitions.Insert(4, new() { Width = GridLength.Auto });
            Grid.SetColumn(edit, 5);
            Grid.SetColumn(runBadge, 4); header.Children.Add(runBadge);
        }
        // 跳转有问题：整行标红，并在标题下方写明原因（计划因此不能启动）；不可达只提示。
        if (_frame is not null) _frame.BorderBrush = B(node.HasJumpProblem ? "#C9614F" : "#51455D");
        if (node.HasRowNotice)
        {
            var wrap = new StackPanel();
            wrap.Children.Add(header);
            var notice = Text(node.RowNotice); notice.FontSize = 11; notice.TextWrapping = TextWrapping.Wrap; notice.TextTrimming = TextTrimming.None;
            notice.Foreground = B(node.HasJumpProblem ? "#F0A08C" : node.IsUnreachable ? "#9C97C0" : "#C9B4F0"); notice.Margin = new Thickness(59, 0, 6, 3);
            wrap.Children.Add(notice);
            _expander.Header = wrap;
        }
        else _expander.Header = header;
        if (!_expander.IsExpanded) return;
        var content = new StackPanel { Margin = new Thickness(20, 2, 5, 5) };
        if (node.Model.Flow is { } flow)
        {
            foreach (var (s, i) in flow.Steps.Select((s, i) => (s, i)))
            {
                var text = $"{i + 1:00}　{TaskFlowEditorWindow.DescribeStep(s)}";
                if (s.Id == flow.ExitStepId) text += "　[路线选择条件]";
                var walked = StepTraceText(View?.CompositionHost?.RunTrace.Step(node.NodeId, s.Id));
                var line = Text(text + walked);
                if (walked.Length > 0) line.Foreground = B("#6FD8CE");
                content.Children.Add(line);
                void Explain(string when, List<StartupStep> children)
                {
                    if (children.Count == 0) return;
                    var detail = Text(when + "：" + string.Join("；", children.Select(TaskFlowEditorWindow.DescribeStep)));
                    detail.TextWrapping = TextWrapping.Wrap; detail.TextTrimming = TextTrimming.None;
                    detail.FontSize = 11; detail.Foreground = B("#C7BCD8"); detail.Margin = new Thickness(20, 2, 4, 5);
                    content.Children.Add(detail);
                }
                Explain(s.Kind == StartupStepKinds.LogTrigger ? "日志出现时" : "触发时", s.FireSteps);
                Explain("任务每次开始前", s.BeforeAttemptSteps);
                Explain("条件满足时", s.TrueSteps); Explain("不满足时", s.FalseSteps);
            }
            if (flow.Steps.Count == 0) content.Children.Add(Text("暂无步骤"));
        }
        if (node.Model.Lanes is { } lanes)
        {
            content.Children.Add(Text(final is null ? "设置选择条件后，只执行下面的一条路线" : "先判断：" + LanesEditorWindow.Describe(final), true));
            var routes = new Grid();
            routes.ColumnDefinitions.Add(new()); routes.ColumnDefinitions.Add(new());
            routes.RowDefinitions.Add(new() { Height = GridLength.Auto }); routes.RowDefinitions.Add(new() { Height = GridLength.Auto });
            AddLane(routes, node, true, lanes.YesName, lanes.YesNodeIds);
            AddLane(routes, node, false, lanes.NoName, lanes.NoNodeIds);
            void Arrange(double width)
            {
                bool wide = width >= 540;
                Grid.SetColumnSpan(routes.Children[0], wide ? 1 : 2);
                Grid.SetColumnSpan(routes.Children[1], wide ? 1 : 2);
                Grid.SetColumn(routes.Children[1], wide ? 1 : 0); Grid.SetRow(routes.Children[1], wide ? 0 : 1);
            }
            Arrange(ActualWidth - 35); routes.SizeChanged += (_, e) => { if (e.WidthChanged) Arrange(e.NewSize.Width); };
            content.Children.Add(routes);
            content.Children.Add(Text(node.HasJump ? "路线走完后：" + node.JumpText : "车道中的任务执行完，继续下一项任务", true));
        }
        _expander.Content = content;
    }
    private static string StepTraceText(NodeRunState? state) => state switch
    {
        NodeRunState.Running => "　· 执行中",
        NodeRunState.Success => "　· ✓ 本轮已完成",
        NodeRunState.CondTrue => "　· 本轮 → 是",
        NodeRunState.CondFalse => "　· 本轮 → 否",
        NodeRunState.Failed => "　· 未完成",
        _ => "",
    };
    private void AddLane(Panel parent, NodeEditVm owner, bool yes, string title, List<string> ids)
    {
        var body = new StackPanel { Margin = new Thickness(8, 3, 8, 5) };
        var frame = new Border { Child = body, Background = B("#28233A"), BorderBrush = B("#463B52"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(1, 2, 3, 2) };
        parent.Children.Add(frame);
        var header = new DockPanel();
        var add = ActionButton("＋任务", () => View?.AddLaneTask(owner, yes)); DockPanel.SetDock(add, Dock.Right); header.Children.Add(add);
        var decisionOwner = editingOwner();
        string editingOwner() => View?.CompositionHost?.Editing is { } doc
            ? MultiplayerHoeingAssistant.Services.WorkflowComposition.GetDecisionOwnerId(doc.Draft, owner.Model) : owner.NodeId;
        var status = View?.CompositionHost?.LaneRunStatus(decisionOwner, yes);
        var display = title is "是" or "否" ? (yes ? "路线 1" : "路线 2") : title;
        header.Children.Add(Text((yes ? "满足条件 → " : "其他情况 → ") + display + (string.IsNullOrEmpty(status) ? "" : "　·　" + status), true)); body.Children.Add(header);
        // 走过的路线描边点亮，本轮没走的路线灰显（与启动中心分支灰显同口径）。
        if (status == "已选中此车道") frame.BorderBrush = B(yes ? "#6FD8CE" : "#E88A6F");
        else if (status == "本轮未选择") frame.Opacity = 0.45;
        var boundHost = View?.CompositionHost;
        var editing = boundHost?.Editing;
        if (ids.Count == 0) body.Children.Add(Text("还没有任务，点击右侧“＋任务”添加；留空则直接继续。"));
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i]; var vm = editing?.Nodes.FirstOrDefault(n => n.NodeId == id);
            if (vm is null) { body.Children.Add(Text("任务引用缺失，请重新绑定")); continue; }
            var row = new DockPanel { Margin = new Thickness(4, 1, 1, 1) };
            var workflowId = editing!.Draft.WorkflowId;
            void WithMember(Action<WorkflowEditVm, NodeEditVm, NodeEditVm> action)
            {
                if (!ReferenceEquals(View?.CompositionHost, boundHost) || boundHost?.Editing is not { } live || live.Draft.WorkflowId != workflowId) return;
                var current = live.Nodes.FirstOrDefault(n => n.NodeId == owner.NodeId);
                if (current?.Model.Lanes is not { } group || !(yes ? group.YesNodeIds : group.NoNodeIds).Contains(id)) return;
                var member = live.Nodes.FirstOrDefault(n => n.NodeId == id);
                if (member is not null) action(live, current, member);
            }
            void Move(int d) => WithMember((live, current, member) => { live.MoveLaneTask(current, yes, member.NodeId, d); Draw(); });
            var more = new Button { Content = "⋯", ToolTip = "任务操作", MinHeight = 22, Height = 22, Padding = new Thickness(6, 0, 6, 0) };
            CompositionUiTheme.StyleButton(more);
            var menu = new ContextMenu { PlacementTarget = more, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            void Item(string title, Action run, bool enabled = true)
            {
                var item = new MenuItem { Header = title, IsEnabled = enabled };
                item.Click += (_, _) => run(); menu.Items.Add(item);
            }
            Item("详情", () => WithMember((_, _, member) => View?.OpenLaneTask(member, this)));
            Item("上移", () => Move(-1), i > 0);
            Item("下移", () => Move(1), i + 1 < ids.Count);
            Item("移除", () => WithMember((live, _, member) =>
            {
                if (View?.CompositionHost?.ConfirmDeleteJumpTarget(member) == false) return;
                live.RemoveNodeCommand.Execute(member); live.CompositionChanged(); Draw();
            }));
            menu.Opened += (_, _) => _activeLaneMenu = menu;
            menu.Closed += (_, _) => { if (ReferenceEquals(_activeLaneMenu, menu)) _activeLaneMenu = null; };
            more.ContextMenu = menu; more.Click += (_, e) => { e.Handled = true; menu.IsOpen = true; };
            DockPanel.SetDock(more, Dock.Right); row.Children.Add(more);
            // 车道里的任务用 #N.M 编号，与跳转目标、流程图同口径。
            var label = (vm.RowNumber.Length > 0 ? vm.RowNumber : $"{i + 1:00}") + "　" + vm.DisplayName;
            var name = ActionButton(label, () => View?.OpenLaneResourceTasks(vm, this));
            name.Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = B("#EEE8F5") };
            name.MinHeight = 24; name.Padding = new Thickness(3, 0, 3, 0); name.Margin = new Thickness(0);
            name.ToolTip = vm.DisplayName;
            name.HorizontalContentAlignment = HorizontalAlignment.Stretch; name.BorderThickness = new Thickness(0); name.Background = Brushes.Transparent;
            row.Children.Add(name); body.Children.Add(row);
        }
    }
}
