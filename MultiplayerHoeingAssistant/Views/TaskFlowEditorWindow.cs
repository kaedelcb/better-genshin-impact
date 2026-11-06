using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Markup;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>F2：局部草稿、双栏节点库、内部子链及明确的出口。应用前不触碰计划。</summary>
public sealed class TaskFlowEditorWindow : Window
{
    private readonly WorkflowFlowDefinition _draft;
    private readonly IReadOnlyList<WorkflowNode> _resources;
    private readonly IReadOnlyList<StartupStep> _allowedPrograms;
    private readonly TreeView _tree = new();
    private readonly StackPanel _parameters = new();
    private readonly StackPanel _catalog = new();
    private readonly TextBlock _addContext = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _emptyHint = new();
    private ChainChoice? _addTarget;
    private StartupStep? _insertAfter;
    private readonly Button _moveUp;
    private readonly Button _moveDown;
    private readonly Button _copyStep;
    private readonly Button _removeStep;
    private readonly TextBox _name = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    private readonly List<Func<string?>> _checks = [];
    private readonly Dictionary<string, string> _numberTexts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _numberErrors = new(StringComparer.Ordinal);
    private Slot? _selected;
    private bool _loading;
    private readonly bool _requireExit;
    private sealed record Slot(StartupStep Step, List<StartupStep> Chain);
    private sealed record ChainChoice(string Name, List<StartupStep> Chain, bool Background)
    { public override string ToString() => Name; }

    public WorkflowFlowDefinition Result => _draft;
    internal TreeView StepTreeForTest => _tree;
    internal StackPanel ParameterPanelForTest => _parameters;
    internal Func<WorkflowFlowDefinition, string?>? ApplyToPlan { get; set; }

    public TaskFlowEditorWindow(WorkflowFlowDefinition source, IEnumerable<WorkflowNode> resources, string? selectedStepId = null, IReadOnlyList<StartupStep>? allowedPrograms = null, bool requireExit = false)
    {
        _draft = JsonSerializer.Deserialize<WorkflowFlowDefinition>(JsonSerializer.Serialize(source))!;
        _requireExit = requireExit;
        _allowedPrograms = allowedPrograms ?? [];
        _resources = resources.Where(n => n.Kind.StartsWith("resource.", StringComparison.Ordinal)).ToArray();
        Title = requireExit ? "添加车道 · 配置出口流程" : "编辑流程";
        Width = 880; Height = 700; MinWidth = 700; MinHeight = 510;
        MaxHeight = SystemParameters.WorkArea.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush("#29243F"); Foreground = Brush("#F4F2FA");
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13;
        CompositionUiTheme.Apply(this);
        Resources[typeof(TextBox)] = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" TargetType="TextBox">
                <Setter Property="Background" Value="#211D35"/><Setter Property="Foreground" Value="#F4F2FA"/>
                <Setter Property="BorderBrush" Value="#756781"/><Setter Property="Padding" Value="5,3"/>
                <Setter Property="MinHeight" Value="26"/>
            </Style>
            """);
        _tree.ItemContainerStyle = (Style)XamlReader.Parse("""
            <Style xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" TargetType="TreeViewItem">
              <Setter Property="Foreground" Value="#F4F2FA"/><Setter Property="HorizontalContentAlignment" Value="Stretch"/>
              <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="TreeViewItem">
                <Grid><Grid.ColumnDefinitions><ColumnDefinition Width="18"/><ColumnDefinition Width="*"/></Grid.ColumnDefinitions>
                  <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                  <ToggleButton x:Name="Arrow" IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" Background="Transparent" BorderThickness="0" Foreground="#D9C78E" Focusable="False"><ToggleButton.Template><ControlTemplate TargetType="ToggleButton"><Border Background="Transparent"><TextBlock x:Name="Chevron" Text="›" HorizontalAlignment="Center" VerticalAlignment="Center" RenderTransformOrigin="0.5,0.5"/></Border><ControlTemplate.Triggers><Trigger Property="IsChecked" Value="True"><Setter TargetName="Chevron" Property="RenderTransform"><Setter.Value><RotateTransform Angle="90"/></Setter.Value></Setter></Trigger></ControlTemplate.Triggers></ControlTemplate></ToggleButton.Template></ToggleButton>
                  <Border x:Name="Selection" Grid.Column="1" Padding="5,4" Margin="0,1" BorderThickness="1" BorderBrush="Transparent" CornerRadius="3">
                    <ContentPresenter ContentSource="Header" HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"/>
                  </Border>
                  <ItemsPresenter x:Name="Children" Grid.Row="1" Grid.Column="1"/>
                </Grid>
                <ControlTemplate.Triggers>
                  <Trigger Property="IsExpanded" Value="False"><Setter TargetName="Children" Property="Visibility" Value="Collapsed"/></Trigger>
                  <Trigger Property="HasItems" Value="False"><Setter TargetName="Arrow" Property="Visibility" Value="Hidden"/></Trigger>
                  <Trigger Property="IsSelected" Value="True"><Setter TargetName="Selection" Property="Background" Value="#4A3F59"/><Setter TargetName="Selection" Property="BorderBrush" Value="#C9AC64"/></Trigger>
                </ControlTemplate.Triggers>
              </ControlTemplate></Setter.Value></Setter>
            </Style>
            """);

        Resources[typeof(TreeViewItem)] = _tree.ItemContainerStyle;
        var root = new Grid { Margin = new Thickness(18), Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = new GridLength(200) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;
        var heading = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        _name.Text = _draft.Name; _name.Width = 230; _name.Margin = new Thickness(10, 0, 0, 0);
        _name.TextChanged += (_, _) => _draft.Name = _name.Text.Trim();
        heading.Children.Add(new TextBlock { Text = "流程名称", VerticalAlignment = VerticalAlignment.Center, Foreground = Brush("#E8C96D"), FontSize = 17 });
        heading.Children.Add(_name); root.Children.Add(heading);
        var columns = new Grid();
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1.25, GridUnitType.Star) });
        columns.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(columns, 1); root.Children.Add(columns);
        var left = new DockPanel { Margin = new Thickness(0, 0, 16, 10) };
        var ops = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _moveUp = Button("上移", () => Move(-1)); _moveDown = Button("下移", () => Move(1));
        _copyStep = Button("复制", Copy); _removeStep = Button("移除", Remove);
        ops.Children.Add(_moveUp); ops.Children.Add(_moveDown); ops.Children.Add(_copyStep); ops.Children.Add(_removeStep);
        if (_requireExit)
        {
            ops.Children.Add(Button("作为车道选择条件", SetExit));
            ops.Children.Add(Button("取消选择条件", () => { _draft.ExitStepId = null; RefreshTree(_selected?.Step.Id); }));
        }
        DockPanel.SetDock(ops, Dock.Bottom); left.Children.Add(ops);
        var sequenceHeading = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var addMain = Button("＋ 添加步骤", () => SetAddTarget(new("流程步骤", _draft.Steps, false)));
        DockPanel.SetDock(addMain, Dock.Right); sequenceHeading.Children.Add(addMain);
        sequenceHeading.Children.Add(new TextBlock { Text = "按顺序执行", Foreground = Brush("#E8C96D"), VerticalAlignment = VerticalAlignment.Center, FontSize = 14 });
        left.Children.Add(sequenceHeading);
        DockPanel.SetDock(left.Children[^1], Dock.Top);
        _tree.Background = Brush("#211D35"); _tree.Foreground = Foreground; _tree.BorderBrush = Brush("#625572");
        ScrollViewer.SetHorizontalScrollBarVisibility(_tree, ScrollBarVisibility.Disabled);
        _tree.Loaded += (_, _) =>
        {
            _tree.ApplyTemplate();
            if (FindTreeScroll(_tree) is { } scroll) CompositionUiTheme.StyleScroll(scroll);
        };
        _tree.SelectedItemChanged += (_, _) =>
        {
            var tag = (_tree.SelectedItem as TreeViewItem)?.Tag;
            if (tag is Slot slot) Select(slot);
            else if (tag is ChainChoice choice) SetAddTarget(choice);
        };
        var treeArea = new Grid();
        _emptyHint.Text = "还没有步骤\n\n点击“添加步骤”，再从右侧选择要做的事情。";
        _emptyHint.Foreground = Brush("#C9BED5"); _emptyHint.TextWrapping = TextWrapping.Wrap; _emptyHint.Margin = new Thickness(18, 22, 18, 0); _emptyHint.IsHitTestVisible = false;
        treeArea.Children.Add(_tree); treeArea.Children.Add(_emptyHint);
        left.Children.Add(treeArea); columns.Children.Add(left);
        var right = new DockPanel();
        Grid.SetColumn(right, 1); columns.Children.Add(right);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "选择要做的事情", Foreground = Brush("#E8C96D"), FontWeight = FontWeights.SemiBold, FontSize = 14 });
        _addContext.Foreground = Brush("#D4C5DC"); _addContext.Margin = new Thickness(0, 8, 0, 5); title.Children.Add(_addContext);
        title.Children.Add(Button("返回流程步骤", () => SetAddTarget(new("流程步骤", _draft.Steps, false))));
        DockPanel.SetDock(title, Dock.Top); right.Children.Add(title);
        var catalogScroll = new ScrollViewer { Content = _catalog, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        CompositionUiTheme.StyleScroll(catalogScroll); right.Children.Add(catalogScroll);
        var parameterFrame = new Border { BorderBrush = Brush("#665872"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0) };
        var parameterScroll = new ScrollViewer { Content = _parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        CompositionUiTheme.StyleScroll(parameterScroll); parameterFrame.Child = parameterScroll;
        Grid.SetRow(parameterFrame, 2); root.Children.Add(parameterFrame);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var apply = Button("应用到计划", Apply); apply.MinWidth = 105; CompositionUiTheme.StyleButton(apply, primary: true);
        var cancel = Button("取消", () => DialogResult = false); cancel.IsCancel = true;
        DockPanel.SetDock(apply, Dock.Right); DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(apply); footer.Children.Add(cancel);
        footer.Children.Add(_error); Grid.SetRow(footer, 3); root.Children.Add(footer);
        if (requireExit) _error.Text = "选择决定车道的条件；上方车道在条件满足时执行，下方车道在不满足时执行。";
        SetAddTarget(new("流程步骤", _draft.Steps, false));
        RefreshTree(selectedStepId);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static ScrollViewer? FindTreeScroll(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer viewer) return viewer;
            if (FindTreeScroll(child) is { } nested) return nested;
        }
        return null;
    }
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 5, 4), MinHeight = 28 };
        CompositionUiTheme.StyleButton(button);
        button.Click += (_, _) => action(); return button;
    }

    private static string Label(StartupStep s) => string.IsNullOrWhiteSpace(s.Name)
        ? StartupStepKinds.Find(s.Kind)?.DisplayName ?? s.Kind : s.Name;
    private static string FireLabel(StartupStep step) => step.Kind switch
    {
        StartupStepKinds.LogTrigger => "日志出现时",
        StartupStepKinds.TimerTrigger => "到点时",
        _ => "检测条件满足时"
    };
    private bool IsBackground(StartupStep step) => WorkflowEditSteps().Any(p => IsTrigger(p.Step)
        && (Contains(p.Step.FireSteps, step) || Contains(p.Step.BeforeAttemptSteps, step)));
    private ChainChoice ContextFor(List<StartupStep> chain)
    {
        if (ReferenceEquals(chain, _draft.Steps)) return new("流程步骤", chain, false);
        foreach (var p in WorkflowEditSteps())
        {
            var s = p.Step;
            if (ReferenceEquals(chain, s.FireSteps)) return new(Label(s) + " → " + FireLabel(s), chain, true);
            if (ReferenceEquals(chain, s.BeforeAttemptSteps)) return new(Label(s) + " → 任务每次开始前", chain, true);
            if (ReferenceEquals(chain, s.TrueSteps)) return new(Label(s) + " → 条件满足时", chain, IsBackground(s));
            if (ReferenceEquals(chain, s.FalseSteps)) return new(Label(s) + " → 不满足时", chain, IsBackground(s));
        }
        return new("当前步骤中的动作", chain, false);
    }
    private void SetAddTarget(ChainChoice choice, StartupStep? after = null)
    {
        _addTarget = choice; _insertAfter = after;
        if (after is null)
        {
            _selected = null; _parameters.Children.Clear();
            _parameters.Children.Add(new TextBlock { Text = "从右侧选择要添加的动作。添加位置：" + choice.Name, Foreground = Brush("#C9BED5"), TextWrapping = TextWrapping.Wrap });
        }
        _addContext.Text = "添加到：" + choice.Name + (after is null ? "" : "（在所选步骤后）");
        UpdateOperations();
        RefreshCatalog();
    }
    private void UpdateOperations()
    {
        var index = _selected is { } p ? p.Chain.IndexOf(p.Step) : -1;
        var chain = _selected?.Chain;
        _moveUp.IsEnabled = index > 0 && _selected!.Step.Id != _draft.ExitStepId && chain![index - 1].Id != _draft.ExitStepId;
        _moveDown.IsEnabled = index >= 0 && index + 1 < chain!.Count && _selected!.Step.Id != _draft.ExitStepId && chain[index + 1].Id != _draft.ExitStepId;
        _copyStep.IsEnabled = _selected is not null; _removeStep.IsEnabled = _selected is not null;
    }
    private static string RegisterText(IReadOnlyList<int?>? values, string operation)
        => values is null ? "尚未设置"
            : string.Join("，", values.Select((v, i) => v is null ? null : $"R{i + 1}{operation}{v}").Where(x => x is not null));
    public static string DescribeStep(StartupStep s) => s.Kind switch
    {
        StartupStepKinds.Wait => $"等待 {s.WaitSeconds} 秒，再做下一步",
        StartupStepKinds.SetRegister => RegisterText(s.RegisterWrite?.Values, "设为") is { Length: > 0 } v ? v + "；其他位保持" : "尚未选择要设置的寄存器",
        StartupStepKinds.JudgeRegister => RegisterText(s.RegisterCondition?.Values, "=") is { Length: > 0 } v ? v : "请选择要判断的寄存器",
        StartupStepKinds.LogTrigger => (string.IsNullOrWhiteSpace(s.LogKeyword) ? "等待填写日志关键字" : $"出现「{s.LogKeyword}」")
            + $" · 随后执行 {s.FireSteps.Count} 个动作",
        StartupStepKinds.TimerTrigger => $"{s.TriggerTime} 到点后执行 {s.FireSteps.Count} 个动作",
        StartupStepKinds.Watchdog => $"每 {s.WatchIntervalSeconds} 秒检查 · 满足时执行 {s.FireSteps.Count} 个动作",
        StartupStepKinds.Weekday => "星期：" + string.Join("、", s.Weekdays.Where(d => d >= 1 && d <= 7).Select(d => new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }[d - 1])),
        StartupStepKinds.TimeRange => $"{s.TimeStart} 至 {s.TimeEnd}",
        StartupStepKinds.ManualConfirm => string.IsNullOrWhiteSpace(s.ConfirmMessage) ? "等待人工选择" : s.ConfirmMessage,
        StartupStepKinds.StartProgram => string.IsNullOrWhiteSpace(s.Path) ? "选择执行端已配置的辅助程序" : System.IO.Path.GetFileName(s.Path),
        StartupStepKinds.BgiTaskName => $"当前任务{(s.ExpectRunning ? "包含" : "不包含")}「{s.TaskName}」",
        StartupStepKinds.ProcessRunning => $"{s.ProcessName} {(s.ExpectRunning ? "正在运行" : "未运行")}",
        _ => s.ExpectRunning ? "判断是否正在运行" : "判断是否未运行"
    };
    private FrameworkElement StepHeader(StartupStep s, int index)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 1, 0, 2) };
        panel.Children.Add(new TextBlock
        {
            Text = $"{index + 1:00}　" + (s.Enabled ? "" : "已停用 · ") + Label(s)
                + (_draft.ExitStepId == s.Id ? "　[用于选择车道]" : ""),
            FontWeight = FontWeights.SemiBold, Foreground = Foreground, TextTrimming = TextTrimming.CharacterEllipsis
        });
        panel.Children.Add(new TextBlock
        {
            Text = DescribeStep(s), Foreground = Brush("#C6BAD5"), FontSize = 11, Margin = new Thickness(24, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap, MaxWidth = 340
        });
        return panel;
    }
    private void RefreshStepHeaders()
    {
        void Walk(ItemsControl owner)
        {
            foreach (var item in owner.Items.OfType<TreeViewItem>())
            {
                if (item.Tag is Slot slot) item.Header = StepHeader(slot.Step, slot.Chain.IndexOf(slot.Step));
                Walk(item);
            }
        }
        Walk(_tree);
    }
    private void RefreshTree(string? select = null)
    {
        _loading = true; _tree.Items.Clear();
        void Add(ItemsControl container, List<StartupStep> chain)
        {
            foreach (var (s, index) in chain.Select((step, index) => (step, index)))
            {
                var item = new TreeViewItem { Header = StepHeader(s, index),
                    Tag = new Slot(s, chain), Foreground = Foreground, IsExpanded = true };
                container.Items.Add(item);
                if (s.NodeType == "condition" && _draft.ExitStepId != s.Id)
                {
                    AddGroup(item, "条件满足时", s.TrueSteps, IsBackground(s), Label(s));
                    AddGroup(item, "不满足时", s.FalseSteps, IsBackground(s), Label(s));
                }
                if (IsTrigger(s))
                {
                    AddGroup(item, FireLabel(s), s.FireSteps, true, Label(s));
                    if (s.TriggerScope == "task") AddGroup(item, "任务每次开始前", s.BeforeAttemptSteps, true, Label(s));
                }
                if (s.Id == select) { item.IsSelected = true; _selected = (Slot)item.Tag; }
            }
        }
        void AddGroup(ItemsControl owner, string title, List<StartupStep> steps, bool background, string stepName)
        {
            var choice = new ChainChoice(stepName + " → " + title, steps, background);
            var header = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 4, 0, 2) };
            var add = Button("＋ 添加动作", () => SetAddTarget(choice));
            add.FontSize = 11; add.Padding = new Thickness(7, 3, 7, 3); add.MinHeight = 26;
            AutomationProperties.SetName(add, title + "添加动作");
            add.Click += (_, e) => e.Handled = true;
            DockPanel.SetDock(add, Dock.Right); header.Children.Add(add);
            header.Children.Add(new TextBlock { Text = title + $" · {steps.Count} 个动作", Foreground = Brush("#DDCFAC"), VerticalAlignment = VerticalAlignment.Center });
            var group = new TreeViewItem { Header = header, Tag = choice, IsExpanded = true };
            owner.Items.Add(group); Add(group, steps);
            if (steps.Count == 0) group.Items.Add(new TreeViewItem
            {
                Header = new TextBlock { Text = "暂无动作，点击上方“添加动作”", Foreground = Brush("#ADA1BE"), FontSize = 11 },
                Focusable = false, IsHitTestVisible = false
            });
        }
        Add(_tree, _draft.Steps);
        _emptyHint.Visibility = _draft.Steps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _loading = false;
        var selected = WorkflowEditSteps().FirstOrDefault(s => s.Step.Id == select)
            ?? WorkflowEditSteps().FirstOrDefault();
        Select(selected);
    }

    private IEnumerable<Slot> WorkflowEditSteps()
    {
        IEnumerable<Slot> Walk(List<StartupStep> chain)
        {
            foreach (var s in chain)
            {
                yield return new Slot(s, chain);
                foreach (var p in Walk(s.TrueSteps).Concat(Walk(s.FalseSteps)).Concat(Walk(s.FireSteps)).Concat(Walk(s.BeforeAttemptSteps))) yield return p;
            }
        }
        return Walk(_draft.Steps);
    }
    private static bool IsTrigger(StartupStep step) => step.Kind is StartupStepKinds.LogTrigger or StartupStepKinds.TimerTrigger or StartupStepKinds.Watchdog;
    private void Select(Slot? slot)
    {
        if (_loading) return;
        _selected = slot; _parameters.Children.Clear(); _checks.Clear();
        if (slot is null) { SetAddTarget(new("流程步骤", _draft.Steps, false)); return; }
        SetAddTarget(ContextFor(slot.Chain), slot.Step);
        var s = slot.Step;
        _parameters.Children.Add(new TextBlock { Text = Label(s), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E8C96D") });
        Text("步骤名称（可选）", s.Name, v => s.Name = v);
        Check("启用这一步", s.Enabled, v => s.Enabled = v);
        if (IsTrigger(s)) TriggerParameters(s);
        else ConditionOrAction(s, s.Kind);
    }
    private static bool Contains(IEnumerable<StartupStep> chain, StartupStep target)
        => chain.Any(s => ReferenceEquals(s, target) || Contains(s.TrueSteps.Concat(s.FalseSteps).Concat(s.FireSteps).Concat(s.BeforeAttemptSteps), target));

    private void RefreshCatalog()
    {
        _catalog.Children.Clear();
        var background = _addTarget?.Background == true;
        var permitted = new HashSet<string>(StringComparer.Ordinal) {
            StartupStepKinds.Wait, StartupStepKinds.SetRegister, StartupStepKinds.StartProgram,
            StartupStepKinds.TimeRange, StartupStepKinds.Weekday, StartupStepKinds.BgiRunning,
            StartupStepKinds.GameRunning, StartupStepKinds.ProcessRunning, StartupStepKinds.BgiTaskRunning,
            StartupStepKinds.BgiTaskName, StartupStepKinds.ManualConfirm, StartupStepKinds.JudgeRegister,
            StartupStepKinds.TimerTrigger, StartupStepKinds.Watchdog, StartupStepKinds.LogTrigger };
        foreach (var group in TaskFlowNodeCatalog.All.Where(k => permitted.Contains(k.Key))
                     .Where(k => !background || k.Key is StartupStepKinds.Wait or StartupStepKinds.SetRegister || k.NodeType == "condition" && k.Key != StartupStepKinds.ManualConfirm)
                     .GroupBy(k => k.NodeType == "condition" ? "判断" : k.Key is StartupStepKinds.TimerTrigger or StartupStepKinds.Watchdog or StartupStepKinds.LogTrigger ? "触发器" : "动作"))
        {
            _catalog.Children.Add(new TextBlock { Text = group.Key, Foreground = Brush("#CFC2DF"), Margin = new Thickness(0, 4, 0, 5) });
            var wrap = new WrapPanel(); _catalog.Children.Add(wrap);
            foreach (var kind in group)
            {
                var b = Button("＋ " + kind.DisplayName, () => Add(kind.Key));
                b.ToolTip = kind.Description; wrap.Children.Add(b);
            }
        }
    }

    private void Add(string kind)
    {
        if (_addTarget is not { } choice) return;
        var s = StartupStepKinds.Create(kind);
        if (IsTrigger(s)) s.TriggerScope = "round";
        var at = _insertAfter is not null && choice.Chain.Contains(_insertAfter)
            ? choice.Chain.IndexOf(_insertAfter) + 1 : -1;
        if (choice.Chain == _draft.Steps && _draft.ExitStepId is { } exit)
        {
            var end = choice.Chain.FindIndex(x => x.Id == exit);
            if (end >= 0 && (at < 0 || at > end)) at = end;
        }
        choice.Chain.Insert(at >= 0 ? at : choice.Chain.Count, s);
        RefreshTree(s.Id);
    }
    private void Move(int delta)
    {
        if (_selected is not { } p) return;
        var i = p.Chain.IndexOf(p.Step); var j = i + delta;
        if (i < 0 || j < 0 || j >= p.Chain.Count || p.Step.Id == _draft.ExitStepId || p.Chain[j].Id == _draft.ExitStepId) return;
        (p.Chain[i], p.Chain[j]) = (p.Chain[j], p.Chain[i]); RefreshTree(p.Step.Id);
    }
    private void Copy()
    {
        if (_selected is not { } p) return;
        var copy = JsonSerializer.Deserialize<StartupStep>(JsonSerializer.Serialize(p.Step))!;
        void Renew(StartupStep step) { step.Id = Guid.NewGuid().ToString("N"); foreach (var c in step.TrueSteps.Concat(step.FalseSteps).Concat(step.FireSteps).Concat(step.BeforeAttemptSteps)) Renew(c); }
        Renew(copy); copy.Name = Label(copy) + " 副本";
        var index = p.Chain.IndexOf(p.Step); p.Chain.Insert(p.Step.Id == _draft.ExitStepId ? index : index + 1, copy); RefreshTree(copy.Id);
    }
    private void Remove()
    {
        if (_selected is not { } p) return;
        if (p.Step.Id == _draft.ExitStepId) _draft.ExitStepId = null;
        p.Chain.Remove(p.Step); RefreshTree();
    }
    private void SetExit()
    {
        if (_selected is not { } p || !ReferenceEquals(p.Chain, _draft.Steps) || p.Step.NodeType != "condition" || !p.Step.Enabled)
        { _error.Text = "请先选择一个已启用的判断步骤，再将它用于选择车道。"; return; }
        if (p.Step.TrueSteps.Count != 0 || p.Step.FalseSteps.Count != 0)
        { _error.Text = "这个判断已经有后续动作；请使用单独的最后一步判断来选择车道。"; return; }
        p.Chain.Remove(p.Step); p.Chain.Add(p.Step); _draft.ExitStepId = p.Step.Id; _error.Text = ""; RefreshTree(p.Step.Id);
    }

    private static void ThemeCombo(ComboBox combo)
    {
        CompositionUiTheme.StyleCombo(combo);
    }

    private void Text(string label, string value, Action<string> change)
    {
        var line = Row(label);
        var box = new TextBox { Text = value, MinWidth = 190, MaxWidth = 550, Margin = new Thickness(4, 2, 0, 2) };
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => { change(box.Text); RefreshStepHeaders(); }; line.Children.Add(box);
    }
    private StackPanel Row(string label)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        line.Children.Add(new TextBlock { Text = label, MinWidth = 104, Foreground = Brush("#D0C7DB"), VerticalAlignment = VerticalAlignment.Center });
        _parameters.Children.Add(line); return line;
    }
    private void Number(string label, int value, int min, int max, Action<int> change)
    {
        var key = (_selected?.Step.Id ?? "") + ":" + label;
        var line = Row(label); var box = new TextBox { Text = _numberTexts.GetValueOrDefault(key, value.ToString()), Width = 85, Margin = new Thickness(4, 2, 0, 2) };
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) =>
        {
            _numberTexts[key] = box.Text;
            if (int.TryParse(box.Text, out var n) && n >= min && n <= max) { _numberErrors.Remove(key); change(n); RefreshStepHeaders(); }
            else _numberErrors[key] = $"{label}须为 {min}–{max} 的整数。";
        };
        line.Children.Add(box);
        _checks.Add(() => int.TryParse(box.Text, out var n) && n >= min && n <= max ? null : $"{label}须为 {min}–{max} 的整数。");
    }
    private void Check(string label, bool value, Action<bool> change)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4), Foreground = Foreground };
        CompositionUiTheme.StyleCheck(check);
        check.Checked += (_, _) => { change(true); RefreshStepHeaders(); }; check.Unchecked += (_, _) => { change(false); RefreshStepHeaders(); }; _parameters.Children.Add(check);
    }
    private void Choose(string label, string[] names, int selected, Action<int> change)
    {
        var combo = new ComboBox { ItemsSource = names, SelectedIndex = Math.Clamp(selected, 0, names.Length - 1), MinWidth = 165 };
        AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) { change(combo.SelectedIndex); RefreshStepHeaders(); } };
        ThemeCombo(combo);
        Row(label).Children.Add(combo);
    }
    private void Time(string label, string value, Action<string> change)
    {
        TimeOnly.TryParse(value, out var at);
        var line = Row(label);
        var hour = new ComboBox { ItemsSource = Enumerable.Range(0, 24).Select(n => n.ToString("00")).ToArray(), SelectedIndex = at.Hour, Width = 55 };
        var minute = new ComboBox { ItemsSource = Enumerable.Range(0, 60).Select(n => n.ToString("00")).ToArray(), SelectedIndex = at.Minute, Width = 55 };
        ThemeCombo(hour); ThemeCombo(minute);
        AutomationProperties.SetName(hour, label + "小时"); AutomationProperties.SetName(minute, label + "分钟");
        line.Children.Add(hour); line.Children.Add(new TextBlock { Text = " : ", Foreground = Foreground, VerticalAlignment = VerticalAlignment.Center }); line.Children.Add(minute);
        void Save() { change($"{hour.SelectedIndex:00}:{minute.SelectedIndex:00}"); RefreshStepHeaders(); }
        hour.SelectionChanged += (_, _) => Save(); minute.SelectionChanged += (_, _) => Save();
    }
    private void Register(StartupStep s, bool condition)
    {
        if (condition) s.RegisterCondition ??= new(); else s.RegisterWrite ??= new();
        var values = condition ? s.RegisterCondition!.Values : s.RegisterWrite!.Values;
        while (values.Count < RegisterBank.Count) values.Add(null);
        var wrap = new WrapPanel(); _parameters.Children.Add(wrap);
        for (var i = 0; i < RegisterBank.Count; i++)
        {
            var index = i; var box = new StackPanel { Margin = new Thickness(0, 4, 8, 4) };
            box.Children.Add(new TextBlock { Text = $"R{i + 1}", Foreground = Foreground });
            var choices = new[] { condition ? "忽略" : "保持", "0", "1" };
            var combo = new ComboBox { ItemsSource = choices, SelectedIndex = values[i] is { } v ? v + 1 : 0, Width = 58 };
            ThemeCombo(combo);
            AutomationProperties.SetName(combo, $"R{i + 1} " + (condition ? "判断" : "设置"));
            combo.SelectionChanged += (_, _) => { values[index] = combo.SelectedIndex == 0 ? null : combo.SelectedIndex - 1; RefreshStepHeaders(); };
            box.Children.Add(combo); wrap.Children.Add(box);
        }
        if (condition) Choose("匹配方式", ["全部成立（且）", "任一成立（或）"], s.RegisterCondition!.Match == "any" ? 1 : 0, i => s.RegisterCondition.Match = i == 0 ? "all" : "any");
    }
    private void ConditionOrAction(StartupStep s, string kind)
    {
        switch (kind)
        {
            case StartupStepKinds.SetRegister: Register(s, false); break;
            case StartupStepKinds.JudgeRegister: Register(s, true); break;
            case StartupStepKinds.Wait: Number("等待秒数", s.WaitSeconds, 0, 86400, v => s.WaitSeconds = v); break;
            case StartupStepKinds.TimeRange: Time("开始时间", s.TimeStart, v => s.TimeStart = v); Time("结束时间", s.TimeEnd, v => s.TimeEnd = v); break;
            case StartupStepKinds.Weekday:
                var wrap = new WrapPanel(); _parameters.Children.Add(wrap);
                for (var i = 1; i <= 7; i++)
                {
                    var day = i; var c = new CheckBox { Content = new[] { "周一", "周二", "周三", "周四", "周五", "周六", "周日" }[i - 1], IsChecked = s.Weekdays.Contains(i), Margin = new Thickness(0, 5, 10, 5), Foreground = Foreground };
                    CompositionUiTheme.StyleCheck(c);
                    c.Checked += (_, _) => { if (!s.Weekdays.Contains(day)) s.Weekdays.Add(day); RefreshStepHeaders(); }; c.Unchecked += (_, _) => { s.Weekdays.Remove(day); RefreshStepHeaders(); }; wrap.Children.Add(c);
                }
                break;
            case StartupStepKinds.ManualConfirm:
                Text("确认提示", s.ConfirmMessage, v => s.ConfirmMessage = v);
                Number("超时秒数（0不限）", s.ConfirmTimeoutSeconds, 0, 86400, v => s.ConfirmTimeoutSeconds = v);
                Check("超时选择「是」", s.ConfirmTimeoutGoTrue, v => s.ConfirmTimeoutGoTrue = v); break;
            case StartupStepKinds.StartProgram:
                var targets = _allowedPrograms.Select(p => string.IsNullOrWhiteSpace(p.Name) ? p.Path : p.Name).ToArray();
                if (targets.Length == 0) _parameters.Children.Add(new TextBlock { Text = "请先在执行端启动中心配置辅助程序目标，再刷新计划目录。", Foreground = Brush("#E8D6A2"), TextWrapping = TextWrapping.Wrap });
                else
                {
                    var selected = _allowedPrograms.ToList().FindIndex(p => p.Path == s.Path && p.Arguments == s.Arguments && p.WindowsUserName == s.WindowsUserName);
                    void Use(int i) { var p = _allowedPrograms[i]; s.Path = p.Path; s.Arguments = p.Arguments; s.WindowsUserName = p.WindowsUserName; }
                    if (selected < 0 && string.IsNullOrEmpty(s.Path)) { selected = 0; Use(0); }
                    Choose("已配置辅助程序", targets, selected, Use);
                }
                break;
            case StartupStepKinds.ProcessRunning: Text("进程名称", s.ProcessName, v => s.ProcessName = v); goto case StartupStepKinds.BgiRunning;
            case StartupStepKinds.BgiTaskName: Text("任务名包含", s.TaskName, v => s.TaskName = v); goto case StartupStepKinds.BgiRunning;
            case StartupStepKinds.BgiRunning:
            case StartupStepKinds.GameRunning:
            case StartupStepKinds.BgiTaskRunning:
                Check("期望正在运行 / 名称包含", s.ExpectRunning, v => s.ExpectRunning = v);
                Choose("状态来源", ["本会话", "BGI 启动顺序", "Windows 用户"], Array.IndexOf(StartupStatusSource.All, s.StatusSource), i => { s.StatusSource = StartupStatusSource.All[i]; Select(_selected); });
                if (s.StatusSource == StartupStatusSource.StartupOrder) Number("BGI 顺序", s.StatusTargetOrder, 1, 100, v => s.StatusTargetOrder = v);
                if (s.StatusSource == StartupStatusSource.UserName) Text("Windows 用户", s.StatusTargetUser, v => s.StatusTargetUser = v);
                break;
        }
    }
    private void TriggerParameters(StartupStep s)
    {
        var scopes = new[] { "round", "flow", "task" };
        Choose("保持多久", ["这一轮计划内", "这些流程步骤执行期间", "指定任务执行期间"], Array.IndexOf(scopes, s.TriggerScope), i => { s.TriggerScope = scopes[i]; RefreshTree(s.Id); });
        if (s.TriggerScope == "task")
        {
            var combo = new ComboBox { ItemsSource = _resources, DisplayMemberPath = "Ref.Config", MinWidth = 180, SelectedItem = _resources.FirstOrDefault(n => n.NodeId == s.TargetNodeId) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is WorkflowNode n) s.TargetNodeId = n.NodeId; };
            ThemeCombo(combo);
            AutomationProperties.SetName(combo, "监听哪个任务");
            Row("监听哪个任务").Children.Add(combo);
        }
        switch (s.Kind)
        {
            case StartupStepKinds.LogTrigger: Text("日志出现这些文字", s.LogKeyword, v => s.LogKeyword = v); Check("每次出现都执行动作", s.WatchRepeat, v => s.WatchRepeat = v); break;
            case StartupStepKinds.TimerTrigger: Time("触发时刻", s.TriggerTime, v => s.TriggerTime = v); Check("范围内每天重复", s.RepeatDaily, v => s.RepeatDaily = v); break;
            case StartupStepKinds.Watchdog:
                var kinds = StartupStepKinds.WatchableKinds;
                Choose("盯梢条件", kinds.Select(k => StartupStepKinds.Find(k)?.DisplayName ?? k).ToArray(), Array.IndexOf(kinds, s.WatchKind), i => { s.WatchKind = kinds[i]; Select(_selected); });
                Number("检测间隔（秒）", s.WatchIntervalSeconds, 1, 86400, v => s.WatchIntervalSeconds = v);
                Number("复核间隔（秒）", s.WatchConfirmSeconds, 1, 3600, v => s.WatchConfirmSeconds = v);
                Number("复核次数", s.WatchConfirmTimes, 1, 100, v => s.WatchConfirmTimes = v);
                Check("触发后继续盯梢", s.WatchRepeat, v => s.WatchRepeat = v);
                ConditionOrAction(s, s.WatchKind); break;
        }
        _parameters.Children.Add(new TextBlock { Text = "在左侧“" + FireLabel(s) + "”旁点击“添加动作”，设置发生后要做什么。有效时间结束后会自动停止监听。", Foreground = Brush("#C9BED5"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    }

    private void Apply()
    {
        if (_requireExit && string.IsNullOrWhiteSpace(_draft.ExitStepId))
        { _error.Text = "请选择用于决定车道的条件。"; return; }
        var currentIds = WorkflowEditSteps().Select(p => p.Step.Id + ":").ToArray();
        var numericError = _numberErrors.FirstOrDefault(p => currentIds.Any(id => p.Key.StartsWith(id, StringComparison.Ordinal)));
        if (numericError.Value is { } message) { _error.Text = message; return; }
        foreach (var check in _checks) if (check() is { } error) { _error.Text = error; return; }
        if (string.IsNullOrWhiteSpace(_draft.Name)) { _error.Text = "请填写流程名称。"; return; }
        if (_draft.Steps.Count == 0) { _error.Text = "请至少添加一个节点。"; return; }
        foreach (var slot in WorkflowEditSteps())
        {
            var s = slot.Step;
            try
            {
                if (s.Kind == StartupStepKinds.SetRegister) RegisterBank.ValidateSelection(s.RegisterWrite?.Values);
                if (s.Kind == StartupStepKinds.JudgeRegister) RegisterBank.ValidateSelection(s.RegisterCondition?.Values);
                if (s.Kind == StartupStepKinds.StartProgram && !_allowedPrograms.Any(p => p.Path == s.Path && p.Arguments == s.Arguments && p.WindowsUserName == s.WindowsUserName)) throw new ArgumentException("请选择执行端启动中心已配置的辅助程序目标。");
                if (s.Kind == StartupStepKinds.LogTrigger && string.IsNullOrWhiteSpace(s.LogKeyword)) throw new ArgumentException("请填写日志关键字。");
                if (IsTrigger(s) && s.TriggerScope == "task" && !_resources.Any(n => n.NodeId == s.TargetNodeId)) throw new ArgumentException("请选择触发器绑定的任务。");
            }
            catch (Exception ex) { _error.Text = Label(s) + "：" + ex.Message; RefreshTree(s.Id); return; }
        }
        var invalid = TaskFlowNodeCatalog.Validate(_draft);
        if (invalid.Count > 0) { _error.Text = string.Join("；", invalid); return; }
        try
        {
            if (ApplyToPlan?.Invoke(_draft) is { } applyError)
            { _error.Text = applyError; return; }
        }
        catch (Exception ex) { _error.Text = "未能应用到计划：" + ex.Message; return; }
        DialogResult = true;
    }
}
