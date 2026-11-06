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
    private readonly ComboBox _location = new();
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
                  <ToggleButton x:Name="Arrow" IsChecked="{Binding IsExpanded, RelativeSource={RelativeSource TemplatedParent}, Mode=TwoWay}" Content="›" Background="Transparent" BorderThickness="0" Foreground="#D9C78E" Focusable="False"/>
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
        ops.Children.Add(Button("上移", () => Move(-1))); ops.Children.Add(Button("下移", () => Move(1)));
        ops.Children.Add(Button("复制", Copy)); ops.Children.Add(Button("移除", Remove));
        ops.Children.Add(Button("设为出口判断", SetExit));
        ops.Children.Add(Button("取消出口", () => { _draft.ExitStepId = null; RefreshTree(_selected?.Step.Id); }));
        DockPanel.SetDock(ops, Dock.Bottom); left.Children.Add(ops);
        left.Children.Add(new TextBlock { Text = "执行顺序（是/否和触发子链可展开）", Foreground = Brush("#E8C96D"), Margin = new Thickness(0, 0, 0, 6) });
        DockPanel.SetDock(left.Children[^1], Dock.Top);
        _tree.Background = Brush("#211D35"); _tree.Foreground = Foreground; _tree.BorderBrush = Brush("#625572");
        _tree.SelectedItemChanged += (_, _) => Select((_tree.SelectedItem as TreeViewItem)?.Tag as Slot);
        left.Children.Add(_tree); columns.Children.Add(left);
        var right = new DockPanel();
        Grid.SetColumn(right, 1); columns.Children.Add(right);
        var title = new StackPanel();
        title.Children.Add(new TextBlock { Text = "添加启动中心节点", Foreground = Brush("#E8C96D"), FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "插入到", Foreground = Brush("#C4BBD3"), Margin = new Thickness(0, 7, 0, 2) });
        _location.Margin = new Thickness(0, 0, 0, 8);
        ThemeCombo(_location);
        _location.SelectionChanged += (_, _) => RefreshCatalog();
        title.Children.Add(_location); DockPanel.SetDock(title, Dock.Top); right.Children.Add(title);
        right.Children.Add(new ScrollViewer { Content = _catalog, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var parameterFrame = new Border { BorderBrush = Brush("#665872"), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 10, 0, 0) };
        parameterFrame.Child = new ScrollViewer { Content = _parameters, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(parameterFrame, 2); root.Children.Add(parameterFrame);
        var footer = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var apply = Button("应用到计划", Apply); apply.MinWidth = 105; apply.Background = Brush("#E8C96D"); apply.Foreground = Brush("#241D35");
        var cancel = Button("取消", () => DialogResult = false); cancel.IsCancel = true;
        DockPanel.SetDock(apply, Dock.Right); DockPanel.SetDock(cancel, Dock.Right); footer.Children.Add(apply); footer.Children.Add(cancel);
        footer.Children.Add(_error); Grid.SetRow(footer, 3); root.Children.Add(footer);
        if (requireExit) _error.Text = "添加车道：先添加一个判断节点，选中后点击“设为出口判断”，再应用到计划。";
        RefreshTree(selectedStepId);
    }

    private static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static Button Button(string text, Action action)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 5, 4),
            Foreground = Brush("#F4F2FA"), Background = Brush("#3A3451"), BorderBrush = Brush("#6C607D"), MinHeight = 28 };
        button.Click += (_, _) => action(); return button;
    }

    private static string Label(StartupStep s) => string.IsNullOrWhiteSpace(s.Name)
        ? StartupStepKinds.Find(s.Kind)?.DisplayName ?? s.Kind : s.Name;
    private void RefreshTree(string? select = null)
    {
        _loading = true; _tree.Items.Clear();
        void Add(ItemsControl container, List<StartupStep> chain)
        {
            foreach (var s in chain)
            {
                var item = new TreeViewItem { Header = (s.Enabled ? "" : "已禁用 · ") + Label(s) + (_draft.ExitStepId == s.Id ? "　[出口判断]" : ""),
                    Tag = new Slot(s, chain), Foreground = Foreground, IsExpanded = true };
                container.Items.Add(item);
                if (s.NodeType == "condition" && _draft.ExitStepId != s.Id)
                {
                    var yes = new TreeViewItem { Header = "是", IsExpanded = true, Foreground = Brush("#ACD8C2") };
                    var no = new TreeViewItem { Header = "否", IsExpanded = true, Foreground = Brush("#D2BAE6") };
                    item.Items.Add(yes); item.Items.Add(no); Add(yes, s.TrueSteps); Add(no, s.FalseSteps);
                }
                if (IsTrigger(s))
                {
                    var fire = new TreeViewItem { Header = "触发时执行", IsExpanded = true, Foreground = Brush("#E8D6A2") };
                    item.Items.Add(fire); Add(fire, s.FireSteps);
                    var init = new TreeViewItem { Header = "每次目标任务尝试前", IsExpanded = true, Foreground = Brush("#C0CBD4") };
                    item.Items.Add(init); Add(init, s.BeforeAttemptSteps);
                }
                if (s.Id == select) { item.IsSelected = true; _selected = (Slot)item.Tag; }
            }
        }
        Add(_tree, _draft.Steps);
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
        var choices = new List<ChainChoice> { new("主链", _draft.Steps, false) };
        if (slot is { } target)
        {
            var bg = WorkflowEditSteps().Any(p => IsTrigger(p.Step) &&
                (Contains(p.Step.FireSteps, target.Step) || Contains(p.Step.BeforeAttemptSteps, target.Step)));
            if (target.Step.NodeType == "condition" && _draft.ExitStepId != target.Step.Id)
            {
                choices.Add(new("当前判断 · 是", target.Step.TrueSteps, bg));
                choices.Add(new("当前判断 · 否", target.Step.FalseSteps, bg));
            }
            if (IsTrigger(target.Step))
            {
                choices.Add(new("当前触发器 · 触发时执行", target.Step.FireSteps, true));
                choices.Add(new("当前触发器 · 每次目标任务尝试前", target.Step.BeforeAttemptSteps, true));
            }
            if (!ReferenceEquals(target.Chain, _draft.Steps))
                choices.Add(new("当前节点所在子链", target.Chain, bg));
        }
        _location.ItemsSource = choices; _location.SelectedIndex = 0;
        if (slot is null) { _parameters.Children.Add(new TextBlock { Text = "从右侧添加节点，或选择左侧节点编辑参数。" }); return; }
        var s = slot.Step;
        _parameters.Children.Add(new TextBlock { Text = Label(s), FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E8C96D") });
        Text("节点名称", s.Name, v => s.Name = v);
        Check("启用此节点", s.Enabled, v => s.Enabled = v);
        if (IsTrigger(s)) TriggerParameters(s);
        else ConditionOrAction(s, s.Kind);
    }
    private static bool Contains(IEnumerable<StartupStep> chain, StartupStep target)
        => chain.Any(s => ReferenceEquals(s, target) || Contains(s.TrueSteps.Concat(s.FalseSteps).Concat(s.FireSteps).Concat(s.BeforeAttemptSteps), target));

    private void RefreshCatalog()
    {
        _catalog.Children.Clear();
        var background = (_location.SelectedItem as ChainChoice)?.Background == true;
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
        if (_location.SelectedItem is not ChainChoice choice) return;
        var s = StartupStepKinds.Create(kind);
        if (IsTrigger(s)) s.TriggerScope = "round";
        var at = choice.Chain == _draft.Steps && _draft.ExitStepId is { } exit
            ? choice.Chain.FindIndex(x => x.Id == exit) : -1;
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
        { _error.Text = "请选择主链里已启用的判断节点作为出口。"; return; }
        if (p.Step.TrueSteps.Count != 0 || p.Step.FalseSteps.Count != 0)
        { _error.Text = "此判断已有内部子链，请先保留到其他节点；出口的是/否直接交给车道。"; return; }
        p.Chain.Remove(p.Step); p.Chain.Add(p.Step); _draft.ExitStepId = p.Step.Id; _error.Text = ""; RefreshTree(p.Step.Id);
    }

    private static void ThemeCombo(ComboBox combo)
    {
        combo.SetResourceReference(FrameworkElement.StyleProperty, "GoldComboBox");
        combo.SetResourceReference(ItemsControl.ItemContainerStyleProperty, "GoldComboItem");
    }

    private void Text(string label, string value, Action<string> change)
    {
        var line = Row(label);
        var box = new TextBox { Text = value, MinWidth = 190, MaxWidth = 550, Margin = new Thickness(4, 2, 0, 2) };
        AutomationProperties.SetName(box, label);
        box.TextChanged += (_, _) => change(box.Text); line.Children.Add(box);
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
            if (int.TryParse(box.Text, out var n) && n >= min && n <= max) { _numberErrors.Remove(key); change(n); }
            else _numberErrors[key] = $"{label}须为 {min}–{max} 的整数。";
        };
        line.Children.Add(box);
        _checks.Add(() => int.TryParse(box.Text, out var n) && n >= min && n <= max ? null : $"{label}须为 {min}–{max} 的整数。");
    }
    private void Check(string label, bool value, Action<bool> change)
    {
        var check = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 4, 0, 4), Foreground = Foreground };
        check.Checked += (_, _) => change(true); check.Unchecked += (_, _) => change(false); _parameters.Children.Add(check);
    }
    private void Choose(string label, string[] names, int selected, Action<int> change)
    {
        var combo = new ComboBox { ItemsSource = names, SelectedIndex = Math.Clamp(selected, 0, names.Length - 1), MinWidth = 165 };
        AutomationProperties.SetName(combo, label);
        combo.SelectionChanged += (_, _) => { if (combo.SelectedIndex >= 0) change(combo.SelectedIndex); };
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
        void Save() => change($"{hour.SelectedIndex:00}:{minute.SelectedIndex:00}");
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
            combo.SelectionChanged += (_, _) => values[index] = combo.SelectedIndex == 0 ? null : combo.SelectedIndex - 1;
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
                    c.Checked += (_, _) => { if (!s.Weekdays.Contains(day)) s.Weekdays.Add(day); }; c.Unchecked += (_, _) => s.Weekdays.Remove(day); wrap.Children.Add(c);
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
        Choose("有效范围", ["本轮计划", "本流程行", "指定任务的本次执行"], Array.IndexOf(scopes, s.TriggerScope), i => { s.TriggerScope = scopes[i]; Select(_selected); });
        if (s.TriggerScope == "task")
        {
            var combo = new ComboBox { ItemsSource = _resources, DisplayMemberPath = "Ref.Config", MinWidth = 180, SelectedItem = _resources.FirstOrDefault(n => n.NodeId == s.TargetNodeId) };
            combo.SelectionChanged += (_, _) => { if (combo.SelectedItem is WorkflowNode n) s.TargetNodeId = n.NodeId; };
            ThemeCombo(combo);
            AutomationProperties.SetName(combo, "绑定任务");
            Row("绑定任务").Children.Add(combo);
        }
        switch (s.Kind)
        {
            case StartupStepKinds.LogTrigger: Text("日志包含", s.LogKeyword, v => s.LogKeyword = v); Check("命中后继续监听", s.WatchRepeat, v => s.WatchRepeat = v); break;
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
        _parameters.Children.Add(new TextBlock { Text = "在右侧「插入到」选择触发子链或每次尝试初始化，再添加节点。范围结束即撤销。", Foreground = Brush("#C9BED5"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
    }

    private void Apply()
    {
        if (_requireExit && string.IsNullOrWhiteSpace(_draft.ExitStepId))
        { _error.Text = "车道需要是 / 否出口：请选择判断节点并点击“设为出口判断”。"; return; }
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
