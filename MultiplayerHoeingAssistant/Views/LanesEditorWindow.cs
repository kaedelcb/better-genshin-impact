using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>车道是一个任务：在这里设置它自己的条件和两条路线，不要求用户创建出口流程。</summary>
public sealed class LanesEditorWindow : Window
{
    private readonly WorkflowLanesDefinition _draft;
    private readonly List<WorkflowNode> _resources;
    private readonly IReadOnlyList<StartupStep> _programs;
    private readonly List<StartupStep> _before;
    private readonly WorkflowFlowDefinition _decision;
    private readonly string _originalYes;
    private readonly string _originalNo;
    private readonly StackPanel _parameters = new();
    private readonly TextBlock _error = new() { Foreground = new SolidColorBrush(Color.FromRgb(245, 166, 151)), TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(235, 214, 157)) };
    private readonly TextBlock _yesPreview = Heading("");
    private readonly TextBlock _noPreview = Heading("");
    private readonly TextBlock _beforeSummary = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<string, StartupStep> _conditions = new(StringComparer.Ordinal);
    private readonly List<Func<string?>> _checks = [];
    private StartupStep? _condition;
    private readonly TextBox _name;
    private readonly TextBox _yes;
    private readonly TextBox _no;
    internal Func<WorkflowLanesDefinition, string?>? ApplyToPlan { get; set; }
    /// <summary>每条路线走完后的跳转目标选项（第一项为「继续下一行」）。为 null 时不显示（旧配对车道不支持跳转）。</summary>
    internal IReadOnlyList<MultiplayerHoeingAssistant.ViewModels.JumpTargetChoice>? JumpChoices { get; init; }
    internal ComboBox? YesJumpForTest { get; private set; }
    internal ComboBox? NoJumpForTest { get; private set; }
    public WorkflowLanesDefinition Result => _draft;

    public LanesEditorWindow(WorkflowLanesDefinition source, WorkflowFlowDefinition? decision,
        IEnumerable<WorkflowNode> resources, IReadOnlyList<StartupStep>? programs = null)
    {
        _draft = Copy(source); _resources = resources.ToList(); _programs = programs ?? [];
        _originalYes = source.YesName; _originalNo = source.NoName;
        var local = decision is null ? null : Copy(decision);
        _decision = local ?? new WorkflowFlowDefinition { Name = "车道条件" };
        _condition = local?.Steps.FirstOrDefault(s => s.Id == local.ExitStepId);
        _before = local?.Steps.Where(s => s.Id != local.ExitStepId).ToList() ?? [];
        if (_condition is not null) _conditions[_condition.Kind] = _condition;
        Title = "设置车道"; Width = 860; Height = 720; MinWidth = 680; MinHeight = 540;
        MaxHeight = SystemParameters.WorkArea.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        CompositionUiTheme.ApplyEditor(this);
        Background = B("#24203B"); Foreground = B("#F4F2FA"); FontSize = 13;
        FontFamily = new FontFamily("Microsoft YaHei UI");
        var root = new Grid { Margin = new Thickness(20), Background = Background };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        Content = root;
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var identity = new StackPanel { Width = 180 };
        _name = TextField(identity, "车道名称", _draft.Name);
        DockPanel.SetDock(identity, Dock.Right); header.Children.Add(identity);
        var intro = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        intro.Children.Add(Heading("设置车道", 20));
        intro.Children.Add(Note("执行到这里时判断一次，只执行选中的路线。"));
        header.Children.Add(intro); root.Children.Add(header);
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        CompositionUiTheme.StyleScroll(scroll); Grid.SetRow(scroll, 1); root.Children.Add(scroll);
        var workspace = new Grid();
        workspace.ColumnDefinitions.Add(new());
        workspace.ColumnDefinitions.Add(new() { Width = new GridLength(210) });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        workspace.RowDefinitions.Add(new() { Height = GridLength.Auto });
        scroll.Content = workspace;
        var body = new StackPanel();
        workspace.Children.Add(CompositionUiTheme.Panel(body));
        body.Children.Add(Heading("两条路线"));
        var names = new Grid { Margin = new Thickness(0, 5, 0, 12) };
        names.ColumnDefinitions.Add(new()); names.ColumnDefinitions.Add(new());
        var left = new StackPanel { Margin = new Thickness(0, 0, 9, 0) };
        var right = new StackPanel { Margin = new Thickness(9, 0, 0, 0) };
        names.Children.Add(left); Grid.SetColumn(right, 1); names.Children.Add(right); body.Children.Add(names);
        _yes = TextField(left, "条件满足时走", source.YesName is "是" or "" ? "路线 1" : source.YesName);
        _no = TextField(right, "其他情况走", source.NoName is "否" or "" ? "路线 2" : source.NoName);
        Loaded += (_, _) =>
        {
            if (JumpChoices is not { Count: > 0 } choices || YesJumpForTest is not null) return;
            YesJumpForTest = JumpCombo(left, choices, _draft.YesJumpTo, v => _draft.YesJumpTo = v);
            NoJumpForTest = JumpCombo(right, choices, _draft.NoJumpTo, v => _draft.NoJumpTo = v);
            body.Children.Insert(body.Children.IndexOf(names) + 1,
                Note("路线走完后可以跳到后面的行，中间的行本轮不执行；路线留空时判断后直接跳。"));
        };
        body.Children.Add(Heading("选择条件"));
        var kinds = TaskFlowNodeCatalog.All.Where(k => k.NodeType == "condition").ToArray();
        var choices = new[] { "请选择判断方式" }.Concat(kinds.Select(k => k.DisplayName)).ToArray();
        var choose = Combo(choices, _condition is null ? 0 : Array.FindIndex(kinds, k => k.Key == _condition.Kind) + 1);
        choose.MinWidth = 280; choose.HorizontalAlignment = HorizontalAlignment.Left;
        choose.Margin = new Thickness(0, 7, 0, 8); body.Children.Add(choose);
        body.Children.Add(_parameters);
        var summary = new StackPanel();
        summary.Children.Add(Heading("执行预览"));
        _preview.Margin = new Thickness(0, 7, 0, 13); summary.Children.Add(_preview);
        void Route(string when, TextBlock title, List<string> ids)
        {
            var route = new StackPanel();
            route.Children.Add(Note(when)); route.Children.Add(title);
            var names = ids.Select(id => _resources.FirstOrDefault(n => n.NodeId == id)?.Ref?.Config ?? "引用的任务不可用").ToList();
            var tasks = Note(names.Count == 0 ? "未添加任务" : string.Join("、", names.Take(3)) + (names.Count > 3 ? $" 等 {names.Count} 项" : ""));
            tasks.ToolTip = string.Join("\n", names); route.Children.Add(tasks);
            var tile = CompositionUiTheme.Panel(route); tile.Margin = new Thickness(0, 0, 0, 8); summary.Children.Add(tile);
        }
        Route("满足条件", _yesPreview, _draft.YesNodeIds); Route("其他情况", _noPreview, _draft.NoNodeIds);
        var after = Note("选中的路线结束后，继续下一项任务。"); summary.Children.Add(after);
        Loaded += (_, _) => { if (JumpChoices is { Count: > 0 }) after.Text = "选中的路线结束后，按「这条路线走完后」的设置继续。"; };
        var preview = CompositionUiTheme.Panel(summary, inset: true);
        preview.VerticalAlignment = VerticalAlignment.Top;
        workspace.Children.Add(preview);
        void ArrangePreview(bool stacked)
        {
            workspace.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 210);
            Grid.SetColumn(preview, stacked ? 0 : 1); Grid.SetRow(preview, stacked ? 1 : 0);
            preview.Margin = stacked ? new Thickness(0, 10, 0, 0) : new Thickness(10, 0, 0, 0);
        }
        ArrangePreview(false);
        workspace.SizeChanged += (_, e) => { if (e.WidthChanged) ArrangePreview(e.NewSize.Width < 690); };
        var advanced = new Expander { Header = "可选：判断之前先做几件事", Margin = new Thickness(0, 8, 0, 8), Foreground = Foreground };
        advanced.SetResourceReference(StyleProperty, "CompositionExpander");
        var before = new StackPanel { Margin = new Thickness(12, 6, 0, 0) };
        before.Children.Add(Note("这些步骤只属于当前车道，不会增加一个独立的流程任务。一般情况可以留空。"));
        before.Children.Add(_beforeSummary);
        before.Children.Add(Action("编辑判断前的步骤", () =>
        {
            var editor = new TaskFlowEditorWindow(new WorkflowFlowDefinition { Name = "判断前的步骤", Steps = _before }, _resources, allowedPrograms: _programs) { Owner = this, Title = "当前车道 · 判断前的步骤" };
            editor.ApplyToPlan = result =>
            {
                if (result.ExitStepId is not null) return "这里设置判断之前的步骤；选择车道的条件请在车道窗口中设置。";
                _before.Clear(); _before.AddRange(result.Steps); UpdatePreview(); return null;
            };
            editor.ShowDialog();
        }));
        advanced.Content = before; body.Children.Add(advanced);
        choose.SelectionChanged += (_, _) =>
        {
            _error.Text = "";
            if (choose.SelectedIndex <= 0) _condition = null;
            else
            {
                var key = kinds[choose.SelectedIndex - 1].Key;
                if (!_conditions.TryGetValue(key, out _condition)) _conditions[key] = _condition = StartupStepKinds.Create(key);
            }
            DrawParameters(); UpdatePreview();
        };
        _yes.TextChanged += (_, _) => UpdatePreview(); _no.TextChanged += (_, _) => UpdatePreview();
        var footer = new DockPanel { Margin = new Thickness(0, 12, 0, 0) }; Grid.SetRow(footer, 2); root.Children.Add(footer);
        var apply = Action("保存车道", Apply); CompositionUiTheme.StyleButton(apply, true);
        var cancel = Action("取消", () => DialogResult = false); cancel.IsCancel = true;
        DockPanel.SetDock(apply, Dock.Right); DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(apply); footer.Children.Add(cancel); footer.Children.Add(_error);
        DrawParameters(); UpdatePreview();
    }
    private static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;
    private static SolidColorBrush B(string color) => new((Color)ColorConverter.ConvertFromString(color));
    private static TextBlock Heading(string text, double size = 15) => new() { Text = text, FontSize = size, FontWeight = FontWeights.SemiBold, Foreground = B("#F2EAF7"), Margin = new Thickness(0, 7, 0, 4) };
    private static TextBlock Note(string text) => new() { Text = text, Foreground = B("#C6BDD7"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 5) };
    private static Button Action(string label, Action run)
    {
        var button = new Button { Content = label, Margin = new Thickness(4, 3, 4, 3), Padding = new Thickness(10, 5, 10, 5), HorizontalAlignment = HorizontalAlignment.Left };
        CompositionUiTheme.StyleButton(button); button.Click += (_, _) => run(); return button;
    }
    private static ComboBox Combo(IEnumerable<string> labels, int selected)
    {
        var box = new ComboBox { ItemsSource = labels.ToArray(), SelectedIndex = selected, MinWidth = 100, Margin = new Thickness(0, 3, 8, 3) };
        CompositionUiTheme.StyleCombo(box); return box;
    }
    private static ComboBox JumpCombo(Panel target, IReadOnlyList<MultiplayerHoeingAssistant.ViewModels.JumpTargetChoice> choices, string? current, Action<string?> set)
    {
        target.Children.Add(Note("这条路线走完后"));
        var box = new ComboBox { ItemsSource = choices, MinWidth = 100, Margin = new Thickness(0, 2, 8, 5) };
        CompositionUiTheme.StyleCombo(box);
        box.SelectedItem = choices.FirstOrDefault(c => c.NodeId == current) ?? choices[0];
        box.SelectionChanged += (_, _) => set((box.SelectedItem as MultiplayerHoeingAssistant.ViewModels.JumpTargetChoice)?.NodeId);
        target.Children.Add(box); return box;
    }
    private static TextBox TextField(Panel target, string label, string value)
    {
        target.Children.Add(Note(label));
        var box = new TextBox { Text = value, MinHeight = 29, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(0, 2, 8, 5),
            Background = B("#211D35"), Foreground = B("#F4F2FA"), BorderBrush = B("#756781") };
        target.Children.Add(box); return box;
    }
    private void DrawParameters()
    {
        _parameters.Children.Clear(); _checks.Clear();
        if (_condition is not { } step) { _parameters.Children.Add(Note("选择一种判断方式后，在这里设置具体条件。")); return; }
        void Choose(string label, string[] values, int index, Action<int> set)
        {
            _parameters.Children.Add(Note(label)); var box = Combo(values, index);
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) set(box.SelectedIndex); UpdatePreview(); };
            _parameters.Children.Add(box);
        }
        void Text(string label, string value, Action<string> set, bool required = false)
        {
            var box = TextField(_parameters, label, value); box.TextChanged += (_, _) => { set(box.Text.Trim()); UpdatePreview(); };
            if (required) _checks.Add(() => string.IsNullOrWhiteSpace(box.Text) ? "请填写" + label + "。" : null);
        }
        void Number(string label, int value, int min, int max, Action<int> set)
        {
            var box = TextField(_parameters, label, value.ToString());
            _checks.Add(() => int.TryParse(box.Text, out var number) && number >= min && number <= max ? null : label + $"请填写 {min}–{max}。");
            box.TextChanged += (_, _) => { if (int.TryParse(box.Text, out var n) && n >= min && n <= max) set(n); };
        }
        switch (step.Kind)
        {
            case StartupStepKinds.JudgeRegister:
                var settings = step.RegisterCondition ??= new();
                Choose("怎样匹配这些寄存器", ["下面选择的条件全部满足", "下面选择的条件任意一个满足"], settings.Match == "any" ? 1 : 0, i => settings.Match = i == 0 ? "all" : "any");
                var registers = new WrapPanel();
                for (var i = 0; i < RegisterBank.Count; i++)
                {
                    var index = i; var slot = new StackPanel { Width = 96, Margin = new Thickness(0, 3, 5, 3) };
                    slot.Children.Add(Note("R" + (i + 1)));
                    var box = Combo(["不参与", "等于 0", "等于 1"], settings.Values[i] is { } v ? v + 1 : 0);
                    box.SetResourceReference(StyleProperty, "CompositionRegisterChoice");
                    box.MinWidth = 0; box.ToolTip = "不参与判断、等于 0 或等于 1";
                    box.SelectionChanged += (_, _) => { settings.Values[index] = box.SelectedIndex == 0 ? null : box.SelectedIndex - 1; UpdatePreview(); };
                    slot.Children.Add(box); registers.Children.Add(slot);
                }
                _parameters.Children.Add(registers); break;
            case StartupStepKinds.Weekday:
                _parameters.Children.Add(Note("今天是哪些星期时，走上面的车道？"));
                var days = new WrapPanel();
                for (var i = 1; i <= 7; i++)
                {
                    var day = i; var check = new CheckBox { Content = "周" + "一二三四五六日"[i - 1], IsChecked = step.Weekdays.Contains(i), Margin = new Thickness(0, 6, 15, 6) };
                    CompositionUiTheme.StyleCheck(check);
                    check.Checked += (_, _) => { if (!step.Weekdays.Contains(day)) step.Weekdays.Add(day); UpdatePreview(); };
                    check.Unchecked += (_, _) => { step.Weekdays.Remove(day); UpdatePreview(); }; days.Children.Add(check);
                }
                _parameters.Children.Add(days); break;
            case StartupStepKinds.TimeRange:
                Time("开始时刻", step.TimeStart, x => step.TimeStart = x);
                Time("结束时刻", step.TimeEnd, x => step.TimeEnd = x);
                _parameters.Children.Add(Note("支持跨零点，例如 22:00 到次日 06:00。")); break;
            case StartupStepKinds.ManualConfirm:
                Text("询问内容", step.ConfirmMessage, x => step.ConfirmMessage = x, true);
                Number("等待秒数（0 表示一直等）", step.ConfirmTimeoutSeconds, 0, 86400, x => step.ConfirmTimeoutSeconds = x);
                Choose("超时没有回答时", ["走上面的车道", "走下面的车道"], step.ConfirmTimeoutGoTrue ? 0 : 1, i => step.ConfirmTimeoutGoTrue = i == 0); break;
            case StartupStepKinds.ProcessRunning:
                Text("进程名称", step.ProcessName, x => step.ProcessName = x, true);
                Choose("什么状态算满足", ["进程正在运行", "进程没有运行"], step.ExpectRunning ? 0 : 1, i => step.ExpectRunning = i == 0); break;
            case StartupStepKinds.BgiTaskName:
                Text("任务名称包含", step.TaskName, x => step.TaskName = x, true);
                Choose("名称匹配方式", ["包含上面的文字", "不包含上面的文字"], step.ExpectRunning ? 0 : 1, i => step.ExpectRunning = i == 0); break;
            default:
                Choose("什么状态算满足", step.Kind == StartupStepKinds.BgiTaskRunning ? ["BGI 正在执行任务", "BGI 当前空闲"] : ["正在运行", "没有运行"], step.ExpectRunning ? 0 : 1, i => step.ExpectRunning = i == 0);
                break;
        }
    }
    private void Time(string label, string text, Action<string> set)
    {
        _parameters.Children.Add(Note(label));
        var parsed = TimeOnly.TryParse(text, out var t) ? t : TimeOnly.MinValue;
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var hours = Combo(Enumerable.Range(0, 24).Select(x => x.ToString("00")), parsed.Hour);
        var minutes = Combo(Enumerable.Range(0, 60).Select(x => x.ToString("00")), parsed.Minute);
        hours.MinWidth = minutes.MinWidth = 74;
        void Changed(object? sender, SelectionChangedEventArgs args) { if (hours.SelectedIndex >= 0 && minutes.SelectedIndex >= 0) set($"{hours.SelectedIndex:00}:{minutes.SelectedIndex:00}"); UpdatePreview(); }
        hours.SelectionChanged += Changed; minutes.SelectionChanged += Changed;
        line.Children.Add(hours); line.Children.Add(Note("时")); line.Children.Add(minutes); line.Children.Add(Note("分")); _parameters.Children.Add(line);
    }
    private void UpdatePreview()
    {
        if (_yes is null || _no is null) return;
        _beforeSummary.Text = _before.Count == 0 ? "未设置额外步骤" : string.Join(" → ", _before.Select(s => string.IsNullOrWhiteSpace(s.Name) ? StartupStepKinds.Find(s.Kind)?.DisplayName ?? s.Kind : s.Name));
        var condition = _condition is null ? "尚未设置条件" : Describe(_condition);
        // Keep each comparison together when the preview column wraps.
        _preview.Text = _condition?.Kind == StartupStepKinds.JudgeRegister ? condition.Replace(" = ", "\u00a0=\u00a0") : condition; _yesPreview.Text = _yes.Text; _noPreview.Text = _no.Text;
    }
    internal static string Describe(StartupStep step) => step.Kind switch
    {
        StartupStepKinds.JudgeRegister => step.RegisterCondition?.Values.Any(v => v is not null) == true
            ? string.Join(step.RegisterCondition.Match == "any" ? " 或 " : " 且 ", step.RegisterCondition.Values.Select((v, i) => v is null ? null : $"R{i + 1} = {v}").Where(x => x is not null))
            : "尚未选择寄存器",
        StartupStepKinds.Weekday => step.Weekdays.Count == 0 ? "未选择星期（不会满足）" : string.Join("、", step.Weekdays.Order().Select(i => i is >= 1 and <= 7 ? "周" + "一二三四五六日"[i - 1] : "?")),
        StartupStepKinds.TimeRange => step.TimeStart + "–" + step.TimeEnd,
        StartupStepKinds.ManualConfirm => "人工选择“是”",
        StartupStepKinds.BgiRunning => step.ExpectRunning ? "BGI 正在运行" : "BGI 未运行",
        StartupStepKinds.GameRunning => step.ExpectRunning ? "游戏正在运行" : "游戏未运行",
        StartupStepKinds.ProcessRunning => step.ProcessName + (step.ExpectRunning ? " 正在运行" : " 未运行"),
        StartupStepKinds.BgiTaskRunning => step.ExpectRunning ? "BGI 正在执行任务" : "BGI 当前空闲",
        StartupStepKinds.BgiTaskName => "任务名" + (step.ExpectRunning ? "包含" : "不包含") + "“" + step.TaskName + "”",
        _ => (StartupStepKinds.Find(step.Kind)?.DisplayName ?? step.Kind) + (string.IsNullOrEmpty(step.TaskName) ? "" : "：" + step.TaskName)
    };
    private void Apply()
    {
        if (string.IsNullOrWhiteSpace(_name.Text) || string.IsNullOrWhiteSpace(_yes.Text) || string.IsNullOrWhiteSpace(_no.Text))
        { _error.Text = "请填写车道和两条路线的名称。"; return; }
        if (_condition is null) { _error.Text = "请选择怎样判断进入哪条车道。"; return; }
        foreach (var check in _checks) if (check() is { } error) { _error.Text = error; return; }
        try
        {
            if (_condition.Kind == StartupStepKinds.JudgeRegister) RegisterBank.ValidateSelection(_condition.RegisterCondition?.Values);
            _condition.Enabled = true; _condition.TrueSteps.Clear(); _condition.FalseSteps.Clear();
            _draft.Name = _name.Text.Trim(); _draft.YesName = _yes.Text.Trim(); _draft.NoName = _no.Text.Trim();
            if (_originalYes == "是" && _draft.YesName == "路线 1") _draft.YesName = _originalYes;
            if (_originalNo == "否" && _draft.NoName == "路线 2") _draft.NoName = _originalNo;
            _decision.Steps = _before.Append(_condition).ToList(); _decision.ExitStepId = _condition.Id;
            _draft.Decision = _decision;
            var invalid = TaskFlowNodeCatalog.Validate(_draft.Decision);
            if (invalid.Count > 0) { _error.Text = string.Join("；", invalid); return; }
            if (ApplyToPlan?.Invoke(_draft) is { } issue) { _error.Text = issue; return; }
            DialogResult = true;
        }
        catch (Exception ex) { _error.Text = ex.Message; }
    }
}
