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
    private readonly Expander _expander = new() { IsExpanded = false, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0) };
    private NodeEditVm? _node;
    private TaskCenterPanelViewModel? _host;
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
        Content = new Border { BorderBrush = B("#786846"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Background = B("#302A46"), Child = _expander, Margin = new Thickness(0, 1, 0, 2) };
        DataContextChanged += (_, e) =>
        {
            if (_node is not null) _node.PropertyChanged -= Changed;
            _node = e.NewValue as NodeEditVm;
            if (_node is not null) _node.PropertyChanged += Changed;
            Draw();
        };
        Loaded += (_, _) =>
        {
            if (_host is not null) _host.SnapshotRefreshed -= Draw;
            _host = View?.CompositionHost;
            if (_host is not null) _host.SnapshotRefreshed += Draw;
            if (_node is not null) { _node.PropertyChanged -= Changed; _node.PropertyChanged += Changed; }
            Draw();
        };
        Unloaded += (_, _) => { if (_node is not null) _node.PropertyChanged -= Changed; if (_host is not null) _host.SnapshotRefreshed -= Draw; };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NodeEditVm.DisplayName) or nameof(NodeEditVm.CompositionSummary) or nameof(NodeEditVm.ResourceIndex))
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
        if (_node is not { } node) return;
        var header = new Grid { Height = 28, ToolTip = node.CompositionSummary };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(25) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(34) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1.15, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var number = Text($"{node.ResourceIndex:00}", true); number.FontSize = 11; header.Children.Add(number);
        var kind = Text(node.Model.Flow is null ? "车道" : "流程", true); kind.FontSize = 11; Grid.SetColumn(kind, 1); header.Children.Add(kind);
        var name = Text(node.DisplayName); name.FontWeight = FontWeights.SemiBold; name.ToolTip = node.DisplayName;
        Grid.SetColumn(name, 2); header.Children.Add(name);
        var decision = node.Model.Lanes is not null && View?.CompositionHost?.Editing is { } editing
            ? MultiplayerHoeingAssistant.Services.WorkflowComposition.GetDecision(editing.Draft, node.Model) : null;
        var final = decision?.Steps.FirstOrDefault(s => s.Id == decision.ExitStepId);
        var summary = final is null ? node.CompositionSummary : LanesEditorWindow.Describe(final)
            + " → " + (node.Model.Lanes!.YesName == "是" ? "路线 1" : node.Model.Lanes.YesName)
            + "；其他 → " + (node.Model.Lanes.NoName == "否" ? "路线 2" : node.Model.Lanes.NoName);
        var hint = Text(summary); hint.FontSize = 11; hint.Foreground = B("#C7BCD8"); hint.ToolTip = summary;
        hint.Margin = new Thickness(7, 0, 6, 0); Grid.SetColumn(hint, 3); header.Children.Add(hint);
        var edit = ActionButton(node.Model.Flow is null ? "设置车道" : "编辑步骤", () => { if (node.Model.Flow is not null) View?.OpenFlowEditor(node); else View?.EditLanes(node); });
        edit.Height = edit.MinHeight = 24; edit.Padding = new Thickness(6, 1, 6, 1); edit.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(edit, 4); header.Children.Add(edit); _expander.Header = header;
        var content = new StackPanel { Margin = new Thickness(20, 5, 4, 8) };
        if (node.Model.Flow is { } flow)
        {
            foreach (var (s, i) in flow.Steps.Select((s, i) => (s, i)))
            {
                var text = $"{i + 1:00}　{TaskFlowEditorWindow.DescribeStep(s)}";
                if (s.Id == flow.ExitStepId) text += "　[路线选择条件]";
                content.Children.Add(Text(text));
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
            content.Children.Add(Text(node.CompositionSummary, true));
        }
        if (node.Model.Lanes is { } lanes)
        {
            content.Children.Add(Text(final is null ? "设置选择条件后，只执行下面的一条路线" : "先判断：" + LanesEditorWindow.Describe(final), true));
            AddLane(content, node, true, lanes.YesName, lanes.YesNodeIds);
            AddLane(content, node, false, lanes.NoName, lanes.NoNodeIds);
            content.Children.Add(Text("车道中的任务执行完，继续下一项任务", true));
        }
        _expander.Content = content;
    }
    private void AddLane(Panel parent, NodeEditVm owner, bool yes, string title, List<string> ids)
    {
        var body = new StackPanel { Margin = new Thickness(8, 3, 8, 5) };
        parent.Children.Add(new Border { Child = body, Background = B("#28233A"), BorderBrush = B("#5A5068"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Margin = new Thickness(0, 3, 0, 3) });
        var header = new DockPanel();
        var add = ActionButton("＋任务", () => View?.AddLaneTask(owner, yes)); DockPanel.SetDock(add, Dock.Right); header.Children.Add(add);
        var decisionOwner = editingOwner();
        string editingOwner() => View?.CompositionHost?.Editing is { } doc
            ? MultiplayerHoeingAssistant.Services.WorkflowComposition.GetDecisionOwnerId(doc.Draft, owner.Model) : owner.NodeId;
        var status = View?.CompositionHost?.LaneRunStatus(decisionOwner, yes);
        var display = title is "是" or "否" ? (yes ? "路线 1" : "路线 2") : title;
        header.Children.Add(Text((yes ? "满足条件 → " : "其他情况 → ") + display + (string.IsNullOrEmpty(status) ? "" : "　·　" + status), true)); body.Children.Add(header);
        var editing = View?.CompositionHost?.Editing;
        if (ids.Count == 0) body.Children.Add(Text("还没有任务，点击右侧“＋任务”添加；留空则直接继续。"));
        for (var i = 0; i < ids.Count; i++)
        {
            var id = ids[i]; var vm = editing?.Nodes.FirstOrDefault(n => n.NodeId == id);
            if (vm is null) { body.Children.Add(Text("任务引用缺失，请重新绑定")); continue; }
            var row = new DockPanel { Margin = new Thickness(4, 1, 1, 1) };
            void Move(int d) { editing!.MoveLaneTask(owner, yes, id, d); Draw(); }
            var up = ActionButton("上移", () => Move(-1)); var down = ActionButton("下移", () => Move(1));
            up.IsEnabled = i > 0; down.IsEnabled = i + 1 < ids.Count;
            var remove = ActionButton("移除", () => { editing!.RemoveNodeCommand.Execute(vm); editing.CompositionChanged(); Draw(); });
            var settings = ActionButton("详情", () => View?.OpenLaneTask(vm, this));
            foreach (var button in new[] { remove, settings, down, up }) { DockPanel.SetDock(button, Dock.Right); row.Children.Add(button); }
            var name = ActionButton($"{i + 1:00}　{vm.DisplayName}", () => View?.OpenLaneResourceTasks(vm, this));
            name.Content = new TextBlock { Text = $"{i + 1:00}　{vm.DisplayName}", TextTrimming = TextTrimming.CharacterEllipsis, Foreground = B("#EEE8F5") };
            name.ToolTip = vm.DisplayName;
            name.HorizontalContentAlignment = HorizontalAlignment.Stretch; name.BorderThickness = new Thickness(0); name.Background = Brushes.Transparent;
            row.Children.Add(name); body.Children.Add(row);
        }
    }
}
