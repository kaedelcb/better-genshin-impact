using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 任务中心流程图（只读）：把当前计划表画成流程图。
/// - 计划行自上而下；流程画成带框的步骤链（内部判断分是/否两支、触发器的触发链挂右侧虚线）；
/// - 车道先画选择条件，再分「满足条件 / 其他情况」两条路线，路线结束后汇合到下一行；
/// - 按计划最近一次运行的本轮记录点亮走过的节点和路线，没走的路线灰显；
/// - 点击节点回到计划表定位（流程步骤打开流程编辑并选中该步骤）；Ctrl+滚轮缩放、拖动平移。
/// 结构或运行路径改变后自动重画；窗口只读运行记录，不写计划、不参与执行。
/// </summary>
public sealed class TaskCenterFlowChartWindow : Window
{
    private const double NodeW = 240, NodeH = 74, VGap = 42, HGap = 36, BranchGapY = 40, Pad = 30, FramePad = 14, FrameTitleH = 24;

    private static readonly Brush Gold = Frozen("#E8C96D"), GoldText = Frozen("#F2E3B6"), Anemo = Frozen("#6FD8CE"),
        Pyro = Frozen("#E88A6F"), Fail = Frozen("#EF5350"), DimText = Frozen("#9C97C0"), WhiteText = Frozen("#F4F2FA"),
        CardBg = Frozen("#241d3d"), EdgeNormal = Frozen("#809C97C0"), EdgeDim = Frozen("#339C97C0"),
        TrueEdgeBase = Frozen("#806FD8CE"), FalseEdgeBase = Frozen("#80E88A6F"), CardEdge = Frozen("#40D4AF37"),
        ChipBg = Frozen("#299C97C0"), FrameBg = Frozen("#1C1833");

    private readonly TaskCenterPanelViewModel _host;
    private readonly Action<string, string?> _locate;
    private readonly Canvas _chart = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Background = Brushes.Transparent };
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly ScrollViewer _scroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly Grid _panSurface = new() { Background = Brushes.Transparent };
    private readonly TextBlock _zoomText = new() { Text = "100%", FontSize = 11, Foreground = DimText, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), MinWidth = 40, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.Bold, Foreground = Frozen("#d4af37"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _runText = new() { FontSize = 11, Foreground = DimText, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _builtKey = "";
    private bool _sized;

    internal TaskCenterFlowChartWindow(TaskCenterPanelViewModel host, Action<string, string?> locate)
    {
        _host = host; _locate = locate;
        Title = "槲寄生 · 任务流程图";
        Width = 1080; Height = 740; MinWidth = 640; MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        Background = Frozen("#1a1a2e"); Foreground = Frozen("#e0e0e0");
        FontFamily = new FontFamily("Microsoft YaHei UI");
        Helpers.DpiAwarenessController.Initialize(this);

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var tools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(ToolButton("－", () => SetScale(_zoom.ScaleX - 0.15), "缩小", 30));
        tools.Children.Add(_zoomText);
        tools.Children.Add(ToolButton("＋", () => SetScale(_zoom.ScaleX + 0.15), "放大", 30));
        tools.Children.Add(ToolButton("1:1", () => SetScale(1), "实际大小", 0, 8));
        tools.Children.Add(ToolButton("⤢ 适应窗口", FitToWindow, "缩放到整图可见", 0, 6));
        DockPanel.SetDock(tools, Dock.Right); head.Children.Add(tools);
        var titles = new StackPanel(); titles.Children.Add(_title); titles.Children.Add(_runText);
        head.Children.Add(titles);
        root.Children.Add(head);

        _chart.LayoutTransform = _zoom;
        _panSurface.Children.Add(_chart);
        _panSurface.PreviewMouseLeftButtonDown += PanDown;
        _panSurface.PreviewMouseMove += PanMove;
        _panSurface.PreviewMouseLeftButtonUp += PanUp;
        _scroller.Content = _panSurface;
        _scroller.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetScale(_zoom.ScaleX + (e.Delta > 0 ? 0.15 : -0.15)); e.Handled = true;
        };
        var frame = new Border { Background = Frozen("#14122a"), CornerRadius = new CornerRadius(10), BorderBrush = CardEdge, BorderThickness = new Thickness(1), Child = _scroller };
        Grid.SetRow(frame, 1); root.Children.Add(frame);
        Content = root;

        _host.RunTraceChanged += Rebuild;
        _host.PropertyChanged += HostChanged;
        _watch.Tick += (_, _) => Rebuild();
        Loaded += (_, _) => { _watch.Start(); _scroller.ScrollToHome(); };
        Closed += (_, _) => { _watch.Stop(); _host.RunTraceChanged -= Rebuild; _host.PropertyChanged -= HostChanged; };
        Rebuild();
    }

    private void HostChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(TaskCenterPanelViewModel.Editing) or nameof(TaskCenterPanelViewModel.CurrentPlanTitle)) Rebuild();
    }

    /// <summary>结构或运行路径变化才重画（1 秒检查一次计划草稿，覆盖编辑器内的修改）。</summary>
    internal void Rebuild()
    {
        var editing = _host.Editing;
        string key;
        try { key = (editing is null ? "" : JsonSerializer.Serialize(editing.Draft.Nodes) + "|" + editing.Draft.Loop?.Mode + "|" + editing.NameText + "|" + editing.NodeTimeWindowEnabled)
                    + "|" + _host.RunTrace.Key; }
        catch { return; }
        if (key == _builtKey) return;
        _builtKey = key;
        Build(editing, _host.RunTrace);
        if (!_sized && _chart.Width > 0) { _sized = true; Width = Math.Clamp(_chart.Width + 60, MinWidth, 1500); }
    }

    // ================= 构建 =================

    private sealed record Block(Canvas Panel, double Width, double Height, double TopX, double BottomX, List<Point>? LooseEnds);

    private void Build(WorkflowEditVm? editing, TaskCenterRunTrace trace)
    {
        _chart.Children.Clear();
        if (editing is null)
        {
            _title.Text = "🗺  任务流程图（只读）";
            _runText.Text = "还没有选择计划表。";
            _chart.Width = _chart.Height = 0;
            return;
        }
        var doc = editing.Draft;
        _title.Text = $"🗺  任务流程图（只读） · {editing.NameText}";
        _runText.Text = !trace.HasRun ? "这张计划表还没有运行记录；运行后会点亮实际走过的路径。"
            : $"最近一次运行：{RunStateText(trace.State)} · 第 {trace.Round + 1} 轮" + (trace.IsActive ? " · 实时刷新" : "")
              + "　　亮色为本轮走过的节点与路线，灰色为没有走的路线。";

        var rows = editing.MainPlanNodes;
        var blocks = new List<Block>();
        foreach (var row in rows)
        {
            try { blocks.Add(BuildRow(doc, editing, row, trace)); }
            catch (Exception ex) { blocks.Add(Card("⚠", "无法绘制", "", ex.Message, "#EF5350", NodeRunState.None, 1, null)); }
        }
        if (blocks.Count == 0) blocks.Add(Pill("（计划表还是空的）", DimText));

        var start = Pill(trace.HasRun ? $"▶ 开始 · 第 {trace.Round + 1} 轮" : "▶ 开始", GoldText);
        var end = Pill(doc.Loop is null ? "■ 计划结束" : "↻ 进入下一轮", GoldText);
        var all = Chain([start, .. blocks, end]);
        Canvas.SetLeft(all.Panel, Pad); Canvas.SetTop(all.Panel, Pad);
        _chart.Children.Add(all.Panel);
        _chart.Width = all.Width + Pad * 2;
        _chart.Height = all.Height + Pad * 2;
    }

    private Block BuildRow(WorkflowDocument doc, WorkflowEditVm editing, NodeEditVm row, TaskCenterRunTrace trace)
    {
        var node = row.Model;
        var nodeState = trace.Node(node.NodeId);
        var opacity = row.IsEnabled ? 1.0 : 0.45;
        if (node.Flow is { } flow)
        {
            var inner = flow.Steps.Count == 0 ? Pill("暂无步骤", DimText) : Steps(node.NodeId, flow.Steps, flow.ExitStepId, trace);
            var f = Frame($"{row.ResourceIndex:00}  流程 · {row.DisplayName}" + Badge(nodeState, " "), inner, "#B39DDB", nodeState, () => _locate(node.NodeId, null));
            f.Panel.Opacity = opacity;
            return f;
        }
        if (node.Lanes is { } lanes)
        {
            var owner = WorkflowComposition.GetDecisionOwnerId(doc, node);
            var decision = WorkflowComposition.GetDecision(doc, node);
            var exit = decision?.Steps.FirstOrDefault(s => s.Id == decision.ExitStepId);
            var choice = trace.LaneChoice(doc, node);
            var laneState = choice switch { true => NodeRunState.CondTrue, false => NodeRunState.CondFalse, _ => nodeState };
            var yesName = lanes.YesName == "是" ? "路线 1" : lanes.YesName;
            var noName = lanes.NoName == "否" ? "路线 2" : lanes.NoName;
            var head = Card("⑂", $"{row.ResourceIndex:00}  {row.DisplayName}", "车道",
                exit is null ? "还没有设置选择条件" : "判断：" + LanesEditorWindow.Describe(exit), "#E8C96D", laneState, opacity,
                () => _locate(node.NodeId, null));
            var yes = LaneBranch(editing, lanes.YesNodeIds, trace);
            var no = LaneBranch(editing, lanes.NoNodeIds, trace);
            var split = Split(head, yes, no, "满足 → " + yesName, "其他 → " + noName, choice);
            split.Panel.Opacity = opacity;
            var before = decision?.Steps.Where(s => s.Id != decision.ExitStepId).ToList() ?? [];
            if (before.Count == 0) return split;
            var pre = Frame("判断前的步骤", Steps(owner, before, null, trace), "#9C97C0", NodeRunState.None,
                () => _locate(owner, before[0].Id));
            pre.Panel.Opacity = opacity;
            return Chain([pre, split]);
        }
        return Card("▶", $"{row.ResourceIndex:00}  {row.DisplayName}", row.KindName,
            TaskSummary(editing, row), "#48C87E", nodeState, opacity, () => _locate(node.NodeId, null));
    }

    private Block LaneBranch(WorkflowEditVm editing, List<string> ids, TaskCenterRunTrace trace)
    {
        if (ids.Count == 0) return Pill("（空，直接继续）", DimText);
        var items = new List<Block>();
        foreach (var (id, i) in ids.Select((id, i) => (id, i)))
        {
            var vm = editing.Nodes.FirstOrDefault(n => n.NodeId == id);
            items.Add(vm is null ? Card("⚠", "任务引用缺失", "", "请重新绑定", "#EF5350", NodeRunState.None, 1, null)
                : Card("▶", $"{i + 1:00}  {vm.DisplayName}", vm.KindName, TaskSummary(editing, vm),
                    "#48C87E", trace.Node(id), vm.IsEnabled ? 1 : 0.45, () => _locate(id, null)));
        }
        return Chain(items);
    }

    /// <summary>任务卡摘要：日期/账号/失败处理 + 执行时段（设置了时段才显示；计划总开关关闭时注明不生效）。</summary>
    private static string TaskSummary(WorkflowEditVm editing, NodeEditVm vm)
    {
        if (!vm.IsEnabled) return "已停用，执行时跳过";
        // 与计划表「UID/账号」列同口径：未绑定也写明，避免误以为漏显示；UID 按计划表同样掩码。
        var summary = vm.SettingsSummary + (vm.HasAccount ? "" : " · 未绑定账号");
        if (vm.HasTimeWindow)
            summary += " · 时段 " + WorkflowTimeWindowPolicy.Display(vm.TimeWindow)
                + (vm.HasTimeWindowOptions ? "（" + vm.TimeWindowOptionsText + "）" : "")
                + (editing.NodeTimeWindowEnabled ? "" : "（总开关关闭，不生效）");
        return summary;
    }

    /// <summary>流程内部步骤链：判断分是/否两支；触发器的触发链挂右侧虚线；车道选择条件不分支。</summary>
    private Block Steps(string owner, IReadOnlyList<StartupStep> steps, string? exitId, TaskCenterRunTrace trace)
    {
        var items = new List<Block>();
        foreach (var s in steps)
        {
            var state = trace.Step(owner, s.Id);
            var kind = StartupStepKinds.Find(s.Kind);
            var name = string.IsNullOrWhiteSpace(s.Name) ? kind?.DisplayName ?? s.Kind : s.Name;
            var summary = TaskFlowEditorWindow.DescribeStep(s);
            if (s.BeforeAttemptSteps.Count > 0) summary += $"；任务每次开始前执行 {s.BeforeAttemptSteps.Count} 个动作";
            var stepId = s.Id;
            var card = Card(kind?.Icon ?? "▶", name, s.Id == exitId ? "路线选择条件" : kind?.NodeType == "condition" ? "判断" : TaskFlowNodeCatalog.IsTrigger(s.Kind) ? "触发器" : "动作",
                summary, FlowChartWindow.KindAccentHex(s.Kind), state, s.Enabled ? 1 : 0.45, () => _locate(owner, stepId));
            if (s.NodeType == "condition" && s.Id != exitId)
            {
                var yes = s.TrueSteps.Count > 0 ? Steps(owner, s.TrueSteps, null, trace) : Pill("（空）", DimText);
                var no = s.FalseSteps.Count > 0 ? Steps(owner, s.FalseSteps, null, trace) : Pill("（空）", DimText);
                items.Add(Split(card, yes, no, "是", "否", state switch { NodeRunState.CondTrue => true, NodeRunState.CondFalse => false, _ => null }));
            }
            else if (TaskFlowNodeCatalog.IsTrigger(s.Kind) && s.FireSteps.Count > 0)
                items.Add(Side(card, Steps(owner, s.FireSteps, null, trace), s.Kind == StartupStepKinds.TimerTrigger ? "⏰ 到点执行" : s.Kind == StartupStepKinds.LogTrigger ? "📜 日志出现时" : "🐕 触发执行"));
            else items.Add(card);
        }
        return Chain(items);
    }

    // ================= 布局积木 =================

    /// <summary>纵向链：相邻块之间画连线；分支块的出口先汇合再接下一块。</summary>
    private Block Chain(IReadOnlyList<Block> items)
    {
        var width = items.Max(b => b.Width);
        var canvas = new Canvas();
        double y = 0, prevX = 0, prevY = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var b = items[i];
            var x = (width - b.Width) / 2;
            Canvas.SetLeft(b.Panel, x); Canvas.SetTop(b.Panel, y); canvas.Children.Add(b.Panel);
            if (i > 0)
            {
                var topX = x + b.TopX;
                if (Math.Abs(prevX - topX) < 0.5) Edge(canvas, [new(prevX, prevY), new(topX, y)], EdgeNormal, 1.4, false, true);
                else
                {
                    var mid = prevY + (y - prevY) / 2;
                    Edge(canvas, [new(prevX, prevY), new(prevX, mid), new(topX, mid), new(topX, y)], EdgeNormal, 1.4, false, true);
                }
            }
            prevX = x + b.BottomX; prevY = y + b.Height;
            if (b.LooseEnds is { Count: > 0 } ends && i < items.Count - 1)
            {
                var mergeY = y + b.Height + VGap * 0.55;
                foreach (var p in ends) Edge(canvas, [new(x + p.X, y + p.Y), new(x + p.X, mergeY), new(prevX, mergeY)], EdgeNormal, 1.1, false, false);
                prevY = mergeY;
            }
            y += b.Height + VGap;
        }
        y -= VGap;
        canvas.Width = width; canvas.Height = y;
        var first = items[0]; var last = items[^1];
        return new Block(canvas, width, y, (width - first.Width) / 2 + first.TopX, (width - last.Width) / 2 + last.BottomX,
            last.LooseEnds?.Select(p => new Point((width - last.Width) / 2 + p.X, y - last.Height + p.Y)).ToList());
    }

    /// <summary>判断块：头卡下分左右两支；chosen 为本轮选择（null=未判断），没走的一支灰显。</summary>
    private Block Split(Block head, Block yes, Block no, string yesLabel, string noLabel, bool? chosen)
    {
        var total = yes.Width + HGap + no.Width;
        var w = Math.Max(head.Width, total);
        var cx = w / 2;
        var by = head.Height + BranchGapY;
        var tx = cx - total / 2; var fx = tx + yes.Width + HGap;
        var canvas = new Canvas();
        Canvas.SetLeft(head.Panel, cx - head.Width / 2); canvas.Children.Add(head.Panel);
        Canvas.SetLeft(yes.Panel, tx); Canvas.SetTop(yes.Panel, by); canvas.Children.Add(yes.Panel);
        Canvas.SetLeft(no.Panel, fx); Canvas.SetTop(no.Panel, by); canvas.Children.Add(no.Panel);
        if (chosen is { } c) { yes.Panel.Opacity *= c ? 1 : 0.35; no.Panel.Opacity *= c ? 0.35 : 1; }
        var splitY = head.Height + BranchGapY / 2;
        BranchEdge(canvas, new(cx, head.Height), new(tx + yes.TopX, by), splitY, yesLabel, true, chosen);
        BranchEdge(canvas, new(cx, head.Height), new(fx + no.TopX, by), splitY, noLabel, false, chosen);
        var h = by + Math.Max(yes.Height, no.Height);
        canvas.Width = w; canvas.Height = h;
        return new Block(canvas, w, h, cx, cx, [new(tx + yes.BottomX, by + yes.Height), new(fx + no.BottomX, by + no.Height)]);
    }

    private void BranchEdge(Canvas canvas, Point from, Point to, double splitY, string text, bool isYes, bool? chosen)
    {
        var taken = chosen == isYes; var skipped = chosen is not null && !taken;
        var brush = taken ? (isYes ? Anemo : Pyro) : skipped ? EdgeDim : isYes ? TrueEdgeBase : FalseEdgeBase;
        Edge(canvas, Math.Abs(from.X - to.X) < 0.5 ? [from, to] : [from, new(from.X, splitY), new(to.X, splitY), to],
            brush, taken ? 2.2 : skipped ? 1.0 : 1.4, false, true);
        var label = new TextBlock { Text = text, FontSize = 10.5, FontWeight = FontWeights.SemiBold, Foreground = taken || chosen is null ? (isYes ? Anemo : Pyro) : EdgeDim };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, Math.Min(from.X, to.X) + Math.Abs(to.X - from.X) / 2 - label.DesiredSize.Width / 2);
        Canvas.SetTop(label, splitY - label.DesiredSize.Height - 1);
        canvas.Children.Add(label);
    }

    /// <summary>卡片右侧挂一条异步触发链（虚线引出，不回汇合）。</summary>
    private Block Side(Block card, Block chain, string text)
    {
        var canvas = new Canvas();
        canvas.Children.Add(card.Panel);
        var fireX = card.Width + HGap + 40;
        Canvas.SetLeft(chain.Panel, fireX); canvas.Children.Add(chain.Panel);
        var endX = fireX + chain.TopX - NodeW / 2;
        Edge(canvas, [new(card.Width, NodeH / 2), new(Math.Max(card.Width + 10, endX), NodeH / 2)], EdgeNormal, 1.3, true, true);
        var label = new TextBlock { Text = text, FontSize = 9.5, Foreground = DimText };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(label, card.Width + 6); Canvas.SetTop(label, NodeH / 2 - label.DesiredSize.Height - 2);
        canvas.Children.Add(label);
        var w = fireX + chain.Width; var h = Math.Max(card.Height, chain.Height);
        canvas.Width = w; canvas.Height = h;
        return new Block(canvas, w, h, card.TopX, card.BottomX, null);
    }

    /// <summary>带标题的框（流程、判断前的步骤）：标题可点击定位，框色随运行态。</summary>
    private Block Frame(string title, Block inner, string accentHex, NodeRunState state, Action onClick)
    {
        var w = Math.Max(inner.Width, NodeW) + FramePad * 2;
        var h = inner.Height + FramePad * 2 + FrameTitleH;
        var canvas = new Canvas { Width = w, Height = h };
        var border = new Border
        {
            Width = w, Height = h, CornerRadius = new CornerRadius(12), Background = FrameBg,
            BorderBrush = state == NodeRunState.None ? Frozen("#66" + accentHex[1..]) : StateBrush(state, accentHex),
            BorderThickness = new Thickness(state == NodeRunState.None ? 1 : 2),
        };
        canvas.Children.Add(border);
        var titleText = new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Frozen(accentHex), Cursor = Cursors.Hand,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = w - FramePad * 2, ToolTip = "点击回到计划表定位" };
        Clickable(titleText, onClick);
        Canvas.SetLeft(titleText, FramePad); Canvas.SetTop(titleText, 7); canvas.Children.Add(titleText);
        var ix = (w - inner.Width) / 2;
        Canvas.SetLeft(inner.Panel, ix); Canvas.SetTop(inner.Panel, FramePad + FrameTitleH); canvas.Children.Add(inner.Panel);
        return new Block(canvas, w, h, w / 2, w / 2, null);
    }

    private Block Pill(string text, Brush foreground)
    {
        var pill = new Border { Background = CardEdge, CornerRadius = new CornerRadius(13), Padding = new Thickness(14, 3, 14, 3),
            Child = new TextBlock { Text = text, FontSize = 11, Foreground = foreground } };
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var c = new Canvas { Width = pill.DesiredSize.Width, Height = pill.DesiredSize.Height };
        c.Children.Add(pill);
        return new Block(c, c.Width, c.Height, c.Width / 2, c.Width / 2, null);
    }

    private Block Card(string icon, string name, string type, string summary, string accentHex, NodeRunState state, double opacity, Action? onClick)
    {
        var accent = Frozen(accentHex);
        var card = new Border
        {
            Width = NodeW, Height = NodeH, Background = CardBg, CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 8, 10, 8),
            BorderBrush = state == NodeRunState.None ? CardEdge : StateBrush(state, accentHex),
            BorderThickness = new Thickness(state is NodeRunState.None or NodeRunState.Skipped ? 1 : 2),
            Opacity = opacity, ToolTip = summary, Cursor = onClick is null ? Cursors.Arrow : Cursors.Hand,
        };
        if (onClick is not null) Clickable(card, onClick);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border { Width = 3, CornerRadius = new CornerRadius(2), Background = accent, Margin = new Thickness(0, 2, 7, 2) });
        var iconBg = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 8, 0),
            Background = Frozen("#40" + accentHex[1..]),
            Child = new TextBlock { Text = icon, FontSize = 12, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        Grid.SetColumn(iconBg, 1); grid.Children.Add(iconBg);
        var right = new StackPanel();
        var row1 = new DockPanel();
        var badgeText = Badge(state, "");
        if (badgeText.Length > 0)
        {
            var badge = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                Background = BadgeBg(state), Child = new TextBlock { Text = badgeText, FontSize = 9, Foreground = BadgeFg(state) } };
            DockPanel.SetDock(badge, Dock.Right); row1.Children.Add(badge);
        }
        if (type.Length > 0)
        {
            var chip = new Border { CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0), Background = ChipBg, VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = type, FontSize = 9, Foreground = DimText } };
            DockPanel.SetDock(chip, Dock.Right); row1.Children.Add(chip);
        }
        row1.Children.Add(new TextBlock { Text = name, FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = WhiteText, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        right.Children.Add(row1);
        right.Children.Add(new TextBlock { Text = summary, FontSize = 10, Foreground = DimText, Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap, MaxHeight = 28, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(right, 2); grid.Children.Add(right);
        card.Child = grid;
        var c = new Canvas { Width = NodeW, Height = NodeH };
        c.Children.Add(card);
        return new Block(c, NodeW, NodeH, NodeW / 2, NodeW / 2, null);
    }

    /// <summary>位移小于阈值才算点击（大位移是在拖动平移画布）。</summary>
    private static void Clickable(FrameworkElement element, Action onClick)
    {
        Point down = default;
        element.MouseLeftButtonDown += (_, e) => down = e.GetPosition(element);
        element.MouseLeftButtonUp += (_, e) =>
        {
            var d = e.GetPosition(element) - down;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) onClick();
        };
    }

    private static void Edge(Canvas canvas, List<Point> pts, Brush brush, double thickness, bool dashed, bool arrow)
    {
        var line = new Polyline { Points = new PointCollection(pts), Stroke = brush, StrokeThickness = thickness, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
        if (dashed) line.StrokeDashArray = [4, 3];
        canvas.Children.Add(line);
        if (!arrow || pts.Count < 2) return;
        var (from, to) = (pts[^2], pts[^1]);
        var dx = to.X - from.X; var dy = to.Y - from.Y; var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) { dx = 0; dy = 1; len = 1; }
        var ux = dx / len; var uy = dy / len; const double s = 7;
        canvas.Children.Add(new Polygon
        {
            Points = [to, new(to.X - ux * s - uy * s * 0.55, to.Y - uy * s + ux * s * 0.55), new(to.X - ux * s + uy * s * 0.55, to.Y - uy * s - ux * s * 0.55)],
            Fill = brush, IsHitTestVisible = false,
        });
    }

    // ================= 运行态配色（与启动流程图同口径） =================

    private static string Badge(NodeRunState state, string prefix) => state switch
    {
        NodeRunState.Running => prefix + "执行中…",
        NodeRunState.Success => prefix + "✓ 完成",
        NodeRunState.Failed => prefix + "✗ 未完成",
        NodeRunState.Skipped => prefix + "已跳过",
        NodeRunState.CondTrue => prefix + "→ 是",
        NodeRunState.CondFalse => prefix + "→ 否",
        _ => "",
    };

    private static Brush StateBrush(NodeRunState state, string accentHex) => state switch
    {
        NodeRunState.Running => Gold,
        NodeRunState.Success or NodeRunState.CondTrue => Anemo,
        NodeRunState.CondFalse => Pyro,
        NodeRunState.Failed => Fail,
        NodeRunState.Skipped => EdgeNormal,
        _ => Frozen("#99" + accentHex[1..]),
    };

    private static Brush BadgeBg(NodeRunState state) => state switch
    {
        NodeRunState.Running => Frozen("#40E8C96D"),
        NodeRunState.Failed => Frozen("#40EF5350"),
        NodeRunState.CondFalse => Frozen("#40E88A6F"),
        NodeRunState.Skipped => ChipBg,
        _ => Frozen("#336FD8CE"),
    };

    private static Brush BadgeFg(NodeRunState state) => state switch
    {
        NodeRunState.Running => GoldText,
        NodeRunState.Failed => Fail,
        NodeRunState.CondFalse => Pyro,
        NodeRunState.Skipped => DimText,
        _ => Anemo,
    };

    private static string RunStateText(WorkflowRunState? state) => state switch
    {
        WorkflowRunState.Planned => "准备中",
        WorkflowRunState.Running => "运行中",
        WorkflowRunState.Waiting => "等待触发",
        WorkflowRunState.Paused => "已暂停",
        WorkflowRunState.Succeeded => "已完成",
        WorkflowRunState.Failed => "失败",
        WorkflowRunState.Cancelled => "已停止",
        WorkflowRunState.Interrupted => "已中断",
        WorkflowRunState.Unknown => "结果未确认",
        WorkflowRunState.Completing => "收尾中",
        WorkflowRunState.LocalWaitParking => "等待执行位置",
        WorkflowRunState.OnlineHoeingParking => "联机锄地中",
        _ => "—",
    };

    // ================= 缩放 / 平移 =================

    private void SetScale(double s)
    {
        s = Math.Clamp(s, 0.2, 2.5);
        _zoom.ScaleX = _zoom.ScaleY = s;
        _zoomText.Text = $"{s * 100:0}%";
    }

    private void FitToWindow()
    {
        if (_scroller.ViewportWidth <= 0 || _scroller.ViewportHeight <= 0 || _chart.Width <= 0 || _chart.Height <= 0) return;
        SetScale(Math.Min(_scroller.ViewportWidth / _chart.Width, _scroller.ViewportHeight / _chart.Height));
        _scroller.ScrollToHome();
    }

    private bool _panArmed, _panning;
    private Point _panStart;
    private double _panH, _panV;
    private void PanDown(object sender, MouseButtonEventArgs e)
    { _panArmed = true; _panStart = e.GetPosition(_scroller); _panH = _scroller.HorizontalOffset; _panV = _scroller.VerticalOffset; }
    private void PanMove(object sender, MouseEventArgs e)
    {
        if (!_panArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(_scroller);
        if (!_panning)
        {
            if (Math.Abs(pos.X - _panStart.X) < 5 && Math.Abs(pos.Y - _panStart.Y) < 5) return;
            _panning = true; _panSurface.CaptureMouse();
        }
        _scroller.ScrollToHorizontalOffset(_panH - (pos.X - _panStart.X));
        _scroller.ScrollToVerticalOffset(_panV - (pos.Y - _panStart.Y));
    }
    private void PanUp(object sender, MouseButtonEventArgs e)
    {
        _panArmed = false;
        if (_panning) { _panning = false; _panSurface.ReleaseMouseCapture(); }
    }

    private static Button ToolButton(string text, Action click, string tip, double width, double left = 0)
    {
        var b = new Button { Content = text, Height = 26, Padding = new Thickness(8, 0, 8, 0), Margin = new Thickness(left, 0, 0, 0), ToolTip = tip,
            Foreground = Brushes.White, Background = Frozen("#3a3a55"), BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12 };
        if (width > 0) b.Width = width;
        CompositionUiTheme.StyleButton(b);
        b.Click += (_, _) => click();
        return b;
    }

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFrom(hex)!;
        b.Freeze();
        return b;
    }
}
