using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 槲寄生 · 启动流程图弹窗（只读）：把整棵启动流程树画成流程图。
/// - 顺序流自上而下，条件节点分「是（左）/ 否（右）」两支，分支结束后汇合回主链；
/// - 定时触发器 / 电子狗的「到点·触发执行」链挂在节点右侧，虚线引出（异步触发，不属于顺序流）；
/// - 节点/分支直接绑定 <see cref="StartupStepViewModel"/> 的运行态，执行中打开会实时点亮走过的路径；
/// - 点击节点通过回调跳回启动中心编辑器定位；缩放（Ctrl+滚轮/按钮）与拖动平移支持大图浏览。
/// 图为打开时的结构快照：增删节点/拖拽改序后需重新打开刷新（运行态与参数摘要仍实时联动）。
/// </summary>
public partial class FlowChartWindow : Window
{
    // ---- 布局常量 ----
    private const double NodeW = 250;   // 节点卡宽
    private const double NodeH = 86;    // 节点卡高
    private const double VGap = 46;     // 链内相邻节点纵向间距（连线区）
    private const double HGap = 40;     // 是/否两支、节点与触发链的横向间距
    private const double BranchGapY = 42; // 条件节点底部到分支链顶部的间距
    private const double Pad = 30;      // 整图外边距

    // ---- 配色（与启动中心编辑器卡片同一套色系；窗口独立加载，不依赖主窗口资源字典，故写字面量） ----
    private static readonly Brush Gold = Frozen("#E8C96D");
    private static readonly Brush GoldText = Frozen("#F2E3B6");
    private static readonly Brush Anemo = Frozen("#6FD8CE");
    private static readonly Brush Pyro = Frozen("#E88A6F");
    private static readonly Brush Fail = Frozen("#EF5350");
    private static readonly Brush DimText = Frozen("#9C97C0");
    private static readonly Brush WhiteText = Frozen("#F4F2FA");
    private static readonly Brush CardBg = Frozen("#241d3d");
    private static readonly Brush EdgeNormal = Frozen("#809C97C0");
    private static readonly Brush EdgeDim = Frozen("#339C97C0");
    /// <summary>「是」分支出线的基础色（未执行时也带色，与「否」分支一眼区分）。</summary>
    private static readonly Brush TrueEdgeBase = Frozen("#806FD8CE");
    /// <summary>「否」分支出线的基础色。</summary>
    private static readonly Brush FalseEdgeBase = Frozen("#80E88A6F");
    private static readonly Brush CardEdge = Frozen("#40D4AF37");
    private static readonly Brush BadgeBgDefault = Frozen("#336FD8CE");
    private static readonly Brush ChipBg = Frozen("#299C97C0");

    private readonly Action<StartupStepViewModel> _onActivate;
    private readonly StepChainViewModel _root;
    /// <summary>图上节点卡索引（节点 VM → 卡片元素）：底部武装面板点击定位用；每次重建图时重建。</summary>
    private readonly Dictionary<StartupStepViewModel, FrameworkElement> _nodeByVm = new();
    /// <summary>节点类型强调色（卡片左侧色条 + 图标底色）：条件/动作各大类一眼可辨，色系沿用页面现有配色。</summary>
    private static string KindAccentHex(string kind) => kind switch
    {
        StartupStepKinds.TimeRange or StartupStepKinds.Weekday => "#E8C96D", // 时间类条件：金
        StartupStepKinds.BgiRunning or StartupStepKinds.GameRunning or StartupStepKinds.ProcessRunning
            or StartupStepKinds.BgiTaskRunning or StartupStepKinds.BgiTaskName => "#6FD8CE", // 状态类条件：青
        StartupStepKinds.ManualConfirm => "#B39DDB",                          // 人工确认：紫
        StartupStepKinds.StartBgi or StartupStepKinds.StartGame => "#48C87E", // 启动 BGI/游戏：绿
        StartupStepKinds.StartProgram => "#64B5F6",                           // 启动第三方程序：蓝
        StartupStepKinds.StopBgi or StartupStepKinds.KillProgram => "#EF5350",// 关闭类：红
        StartupStepKinds.RunCmd => "#9575CD",                                 // CMD：深紫
        StartupStepKinds.Wait => "#9C97C0",                                   // 等待：灰
        StartupStepKinds.TimerTrigger => "#FFC857",                           // 定时触发器：琥珀
        StartupStepKinds.Watchdog => "#E88A6F",                               // 电子狗：橙
        StartupStepKinds.LogTrigger => "#4FC3F7",                             // 日志触发器：浅蓝
        StartupStepKinds.EnterTaskCenter => "#48C87E",                        // 交接任务中心：绿（重点节点）
        StartupStepKinds.EndFlow => "#EF5350",                                // 结束流程：红（重点节点）
        _ => "#90A4AE",                                                       // 旧版遗留等：灰蓝
    };

    /// <summary>重点突出的节点（加粗描边 + 加宽色条）：流程的终点/交接点/异步触发点。</summary>
    private static bool IsEmphasized(string kind) => kind is StartupStepKinds.EndFlow
        or StartupStepKinds.EnterTaskCenter or StartupStepKinds.TimerTrigger or StartupStepKinds.Watchdog
        or StartupStepKinds.LogTrigger;
    private readonly List<StepChainViewModel> _subscribedChains = [];
    private bool _rebuildPending;

    private FlowChartWindow(StepChainViewModel root, Action<StartupStepViewModel> onActivate, Window? owner,
        System.Collections.ObjectModel.ObservableCollection<ArmedTimerViewModel> armedTimers,
        System.Collections.ObjectModel.ObservableCollection<ArmedWatchdogViewModel> armedWatchdogs,
        System.Collections.ObjectModel.ObservableCollection<ArmedLogTriggerViewModel> armedLogTriggers)
    {
        InitializeComponent();
        _root = root;
        _onActivate = onActivate;
        // 不设 Owner：Owned 子窗口永远压在主窗口之上，主窗口点不上来，跳转定位会看不见。
        // 独立窗口则与助手各自正常置前/退后。
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // 底部独立面板：已激活的定时器/电子狗/日志触发器（绑定运行态集合，挂载/撤下/触发实时刷新）
        TimerList.ItemsSource = armedTimers;
        WatchdogList.ItemsSource = armedWatchdogs;
        LogTriggerList.ItemsSource = armedLogTriggers;
        BuildChart(root);
        SubscribeStructureChanges();
        // 打开时按流程图实际宽度定窗口宽（不强制整图缩放可见；高度保留默认，纵向图一般更长，交给滚动）
        Width = Math.Clamp(ChartCanvas.Width + 60, MinWidth, 1500);
        Loaded += (_, _) => Scroller.ScrollToHome();
    }

    /// <summary>弹窗展示整条启动流程的流程图（非模态，可开着对照编辑器操作）。</summary>
    public static FlowChartWindow Show(StepChainViewModel root, Action<StartupStepViewModel> onActivate,
        System.Collections.ObjectModel.ObservableCollection<ArmedTimerViewModel> armedTimers,
        System.Collections.ObjectModel.ObservableCollection<ArmedWatchdogViewModel> armedWatchdogs,
        System.Collections.ObjectModel.ObservableCollection<ArmedLogTriggerViewModel> armedLogTriggers,
        Window? owner = null)
    {
        var o = owner ?? Application.Current?.MainWindow;
        if (o is not { IsLoaded: true, IsVisible: true }) o = null;
        var w = new FlowChartWindow(root, onActivate, o, armedTimers, armedWatchdogs, armedLogTriggers);
        w.Show();
        return w;
    }

    // ================= 底部武装面板：点击行定位到图上节点 =================

    /// <summary>武装面板「⌖ 定位」按钮：滚动到挂载该定时器/电子狗/日志触发器的图上节点并闪烁提示。</summary>
    private void ArmedLocate_Click(object sender, RoutedEventArgs e)
    {
        var step = (sender as FrameworkElement)?.DataContext switch
        {
            ArmedTimerViewModel t => t.Step,
            ArmedWatchdogViewModel w => w.Step,
            ArmedLogTriggerViewModel l => l.Step,
            _ => null,
        };
        if (step != null) LocateNode(step);
    }

    /// <summary>点击武装面板行（点在按钮上不触发）：滚动到挂载该定时器/电子狗/日志触发器的图上节点并闪烁提示。</summary>
    private void ArmedRow_Click(object sender, MouseButtonEventArgs e)
    {
        // 点在按钮上时不定位（按钮自己的命令优先）
        for (var d = e.OriginalSource as DependencyObject; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is ButtonBase) return;

        var step = (sender as FrameworkElement)?.DataContext switch
        {
            ArmedTimerViewModel t => t.Step,
            ArmedWatchdogViewModel w => w.Step,
            ArmedLogTriggerViewModel l => l.Step,
            _ => null,
        };
        if (step == null) return;
        LocateNode(step);
        e.Handled = true;
    }

    /// <summary>滚动到模型对应的图上节点并闪烁几下提示。
    /// 模型不在当前流程树（实例挂载自其他方案/旧配置）→ 弹窗说明；vm 在但图元素缺（结构刚改过还没重画完）→ 静默不跳。</summary>
    private void LocateNode(StartupStep model)
    {
        var vm = FindVm(_root, model);
        if (vm == null)
        {
            // 与启动中心「定位」按钮同口径：实例可能挂载自切换方案前的旧流程，不能静默无事发生
            MessageBox.Show(this,
                $"运行实例「{StartupFlowRunner.DisplayName(model, 0)}」不在当前启动流程中，无法在流程图上定位。\n\n它可能挂载自切换到其他方案前的流程、或流程配置被修改/恢复之前的版本；实例本身仍在正常运行。",
                "无法定位", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!_nodeByVm.TryGetValue(vm, out var el)) return;

        var center = el.TransformToVisual(ChartCanvas).Transform(new Point(el.ActualWidth / 2, el.ActualHeight / 2));
        var scale = Zoom.ScaleX;
        Scroller.ScrollToHorizontalOffset(Math.Max(0, center.X * scale - Scroller.ViewportWidth / 2));
        Scroller.ScrollToVerticalOffset(Math.Max(0, center.Y * scale - Scroller.ViewportHeight / 2));

        var anim = new System.Windows.Media.Animation.DoubleAnimation(0.25, 1.0, new Duration(TimeSpan.FromSeconds(0.45)))
        {
            AutoReverse = true,
            RepeatBehavior = new System.Windows.Media.Animation.RepeatBehavior(3),
        };
        // 动画结束后释放，恢复 Opacity 的绑定值（禁用半透明/未走分支灰显）
        anim.Completed += (_, _) => el.BeginAnimation(UIElement.OpacityProperty, null);
        el.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    /// <summary>按模型引用在 VM 树里递归定位节点（与 MistletoeViewModel.FindStepVm 同口径；窗口自带一份避免反查 VM 内部）。</summary>
    private static StartupStepViewModel? FindVm(StepChainViewModel chain, StartupStep model)
    {
        foreach (var vm in chain.Steps)
        {
            if (ReferenceEquals(vm.Model, model)) return vm;
            var hit = FindVm(vm.TrueChain, model) ?? FindVm(vm.FalseChain, model) ?? FindVm(vm.FireChain, model);
            if (hit != null) return hit;
        }
        return null;
    }

    // ================= 结构变更自动重画 =================

    /// <summary>订阅整棵树所有链的增删/移动（参数与运行态改动走绑定实时刷新，不需要重建）。</summary>
    private void SubscribeStructureChanges()
    {
        _subscribedChains.Clear();
        CollectChains(_root, _subscribedChains);
        foreach (var c in _subscribedChains)
            c.Steps.CollectionChanged += OnStructureChanged;
    }

    private void UnsubscribeStructureChanges()
    {
        foreach (var c in _subscribedChains)
            c.Steps.CollectionChanged -= OnStructureChanged;
        _subscribedChains.Clear();
    }

    private static void CollectChains(StepChainViewModel chain, List<StepChainViewModel> acc)
    {
        acc.Add(chain);
        foreach (var s in chain.Steps)
        {
            CollectChains(s.TrueChain, acc);
            CollectChains(s.FalseChain, acc);
            CollectChains(s.FireChain, acc);
        }
    }

    private void OnStructureChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // 连续改动（如恢复方案整树替换）合并成一次重建
        if (_rebuildPending) return;
        _rebuildPending = true;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, new Action(() =>
        {
            _rebuildPending = false;
            if (!IsLoaded) return;
            UnsubscribeStructureChanges();
            BuildChart(_root);
            SubscribeStructureChanges();
        }));
    }

    // ================= 图构建 =================

    /// <summary>一块已布局的子图：自身大小的画布 + 顶/底接入点（块内坐标）。LooseEnds 仅条件块有（两条分支的底部出口，由父链画汇合线）。Terminal=true 表示流程到此终止（如「结束流程」），父链不再往下画线。</summary>
    private sealed record Block(Canvas Panel, double Width, double Height, double TopX, double BottomX, List<Point>? LooseEnds, bool Terminal = false);

    private void BuildChart(StepChainViewModel root)
    {
        ChartCanvas.Children.Clear();
        _nodeByVm.Clear();
        var body = BuildChain(root);

        // 顶部「开始」标记
        var canvas = new Canvas();
        var startPill = new Border
        {
            Background = CardEdge, CornerRadius = new CornerRadius(13), Padding = new Thickness(14, 3, 14, 3),
            Child = new TextBlock { Text = "▶ 开始", FontSize = 11, Foreground = GoldText },
        };
        startPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var startW = startPill.DesiredSize.Width;
        var startH = startPill.DesiredSize.Height;

        var w = Math.Max(body.Width, startW);
        var bodyX = (w - body.Width) / 2;
        var bodyY = startH + VGap;
        Canvas.SetLeft(startPill, (w - startW) / 2);
        Canvas.SetTop(startPill, 0);
        canvas.Children.Add(startPill);
        DrawEdge(canvas, [new Point(w / 2, startH), new Point(bodyX + body.TopX, bodyY)], EdgeNormal, 1.4, false, true);
        Canvas.SetLeft(body.Panel, bodyX);
        Canvas.SetTop(body.Panel, bodyY);
        canvas.Children.Add(body.Panel);
        canvas.Width = w;
        canvas.Height = bodyY + body.Height;

        ChartCanvas.Children.Add(canvas);
        Canvas.SetLeft(canvas, Pad);
        Canvas.SetTop(canvas, Pad);
        ChartCanvas.Width = w + Pad * 2;
        ChartCanvas.Height = canvas.Height + Pad * 2;
    }

    /// <summary>布局一条链：节点自上而下，条件块之间画连线；条件块的 LooseEnds 在非末尾时画汇合线到下一个节点。</summary>
    private Block BuildChain(StepChainViewModel chain)
    {
        var items = chain.Steps.Select(vm => (vm, b: BuildNode(vm))).ToList();
        var width = items.Max(t => t.b.Width);

        var canvas = new Canvas();
        double y = 0;
        double prevExitX = 0, prevExitY = 0;
        var prevTerminal = false; // 上一块已终止（结束流程）：它与后续节点之间不画线
        for (var i = 0; i < items.Count; i++)
        {
            var b = items[i].b;
            var x = (width - b.Width) / 2;
            Canvas.SetLeft(b.Panel, x);
            Canvas.SetTop(b.Panel, y);
            canvas.Children.Add(b.Panel);

            if (i > 0 && !prevTerminal)
            {
                // 上一节点出口 → 本节点顶部（不同列时拐一次弯）
                var topX = x + b.TopX;
                if (Math.Abs(prevExitX - topX) < 0.5)
                    DrawEdge(canvas, [new Point(prevExitX, prevExitY), new Point(topX, y)], EdgeNormal, 1.4, false, true);
                else
                {
                    var midY = prevExitY + (y - prevExitY) / 2;
                    DrawEdge(canvas, [new Point(prevExitX, prevExitY), new Point(prevExitX, midY), new Point(topX, midY), new Point(topX, y)],
                        EdgeNormal, 1.4, false, true);
                }
            }

            if (b.LooseEnds is { Count: > 0 } looseEnds)
            {
                var exitX = x + b.BottomX;
                if (i < items.Count - 1)
                {
                    // 分支汇合：未终止的分支底部拐到中线，汇成下一个节点的来路（以「结束流程」收尾的分支不回汇）
                    var mergeY = y + b.Height + VGap * 0.55;
                    foreach (var p in looseEnds)
                    {
                        var ax = x + p.X;
                        var ay = y + p.Y;
                        DrawEdge(canvas, [new Point(ax, ay), new Point(ax, mergeY), new Point(exitX, mergeY)],
                            EdgeNormal, 1.1, false, false);
                    }
                    prevExitX = exitX;
                    prevExitY = mergeY;
                }
                else
                {
                    prevExitX = exitX;
                    prevExitY = y + b.Height;
                }
            }
            else
            {
                prevExitX = x + b.BottomX;
                prevExitY = y + b.Height;
            }
            prevTerminal = b.Terminal || (b.LooseEnds != null && b.LooseEnds.Count == 0);

            y += b.Height + VGap;
        }
        y -= VGap;

        var first = items[0].b;
        var last = items[^1].b;
        canvas.Width = width;
        canvas.Height = y;
        return new Block(canvas, width, y,
            (width - first.Width) / 2 + first.TopX,
            (width - last.Width) / 2 + last.BottomX,
            null,
            last.Terminal || (last.LooseEnds != null && last.LooseEnds.Count == 0));
    }

    /// <summary>布局一个节点（含条件分支 / 触发子链）。</summary>
    private Block BuildNode(StartupStepViewModel vm)
    {
        var card = BuildNodeCard(vm);
        var canvas = new Canvas();

        if (vm.IsCondition)
        {
            var trueB = BuildBranch(vm.TrueChain, vm, isTrue: true);
            var falseB = BuildBranch(vm.FalseChain, vm, isTrue: false);
            var total = trueB.Width + HGap + falseB.Width;
            var w = Math.Max(NodeW, total);
            var nodeCX = w / 2;
            var by = NodeH + BranchGapY;
            var tx = nodeCX - total / 2;
            var fx = tx + trueB.Width + HGap;

            Canvas.SetLeft(card, nodeCX - NodeW / 2);
            Canvas.SetTop(card, 0);
            canvas.Children.Add(card);
            Canvas.SetLeft(trueB.Panel, tx);
            Canvas.SetTop(trueB.Panel, by);
            canvas.Children.Add(trueB.Panel);
            Canvas.SetLeft(falseB.Panel, fx);
            Canvas.SetTop(falseB.Panel, by);
            canvas.Children.Add(falseB.Panel);

            // 条件 → 分支头（颜色/粗细随运行态：走过的分支点亮，未走的灰下去）
            DrawCondEdge(canvas, vm, isTrue: true,
                from: new Point(nodeCX, NodeH), to: new Point(tx + trueB.TopX, by), splitY: NodeH + BranchGapY / 2);
            DrawCondEdge(canvas, vm, isTrue: false,
                from: new Point(nodeCX, NodeH), to: new Point(fx + falseB.TopX, by), splitY: NodeH + BranchGapY / 2);

            var h = by + Math.Max(trueB.Height, falseB.Height);
            canvas.Width = w;
            canvas.Height = h;
            // 以「结束流程」收尾的分支不回汇合（流程到此终止）；两支都终止则整个条件块也是终点
            var looseEnds = new List<Point>();
            if (!trueB.Terminal) looseEnds.Add(new Point(tx + trueB.BottomX, by + trueB.Height));
            if (!falseB.Terminal) looseEnds.Add(new Point(fx + falseB.BottomX, by + falseB.Height));
            return new Block(canvas, w, h, nodeCX, nodeCX, looseEnds, trueB.Terminal && falseB.Terminal);
        }

        // 普通动作节点；定时触发器/电子狗/日志触发器有触发子链时挂右侧虚线引出
        Canvas.SetLeft(card, 0);
        Canvas.SetTop(card, 0);
        canvas.Children.Add(card);
        var w2 = NodeW;
        var h2 = NodeH;
        if ((vm.IsTimerTrigger || vm.IsWatchdog || vm.IsLogTrigger) && vm.FireChain.Steps.Count > 0)
        {
            var fireB = BuildChain(vm.FireChain);
            var fireX = NodeW + HGap;
            Canvas.SetLeft(fireB.Panel, fireX);
            Canvas.SetTop(fireB.Panel, 0);
            canvas.Children.Add(fireB.Panel);

            // 虚线：节点右缘 → 触发链首节点左缘（异步触发，不回汇合）
            var endX = fireX + fireB.TopX - NodeW / 2;
            DrawEdge(canvas, [new Point(NodeW, NodeH / 2), new Point(endX, NodeH / 2)], EdgeNormal, 1.3, true, true);
            var label = new TextBlock
            {
                Text = vm.IsTimerTrigger ? "⏰ 到点执行" : vm.IsWatchdog ? "🐕 触发执行" : "📜 触发执行",
                FontSize = 9.5, Foreground = DimText,
            };
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(label, NodeW + (endX - NodeW - label.DesiredSize.Width) / 2);
            Canvas.SetTop(label, NodeH / 2 - label.DesiredSize.Height - 2);
            canvas.Children.Add(label);

            w2 = fireX + fireB.Width;
            h2 = Math.Max(NodeH, fireB.Height);
        }
        canvas.Width = w2;
        canvas.Height = h2;
        // 「结束流程」是终点：下方不再出现任何连线（包括父链的汇合线/后继连线）
        return new Block(canvas, w2, h2, NodeW / 2, NodeW / 2, null, vm.Kind == StartupStepKinds.EndFlow);
    }

    /// <summary>条件的一条分支：非空则布局子链，空则放个「（空）」占位；子链整体绑定未走灰显。</summary>
    private Block BuildBranch(StepChainViewModel branch, StartupStepViewModel cond, bool isTrue)
    {
        Block b;
        if (branch.Steps.Count > 0)
        {
            b = BuildChain(branch);
        }
        else
        {
            var ph = new Border
            {
                Width = 90, Height = 26, CornerRadius = new CornerRadius(13),
                BorderBrush = EdgeNormal, BorderThickness = new Thickness(1),
                Child = new TextBlock
                {
                    Text = "（空）", FontSize = 10, Foreground = DimText,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                },
            };
            var c = new Canvas { Width = 90, Height = 26 };
            c.Children.Add(ph);
            b = new Block(c, 90, 26, 45, 45, null);
        }
        // 未走的分支整支灰显（与编辑器分支灰显同口径，跟随运行态实时变化）
        b.Panel.SetBinding(UIElement.OpacityProperty,
            new Binding(nameof(StartupStepViewModel.RunState)) { Source = cond, Converter = BranchOpacityConv.Instance, ConverterParameter = isTrue });
        return b;
    }

    /// <summary>条件节点到分支头的折线 + 「是/否」标注，绑定该条件的运行态变色。</summary>
    private void DrawCondEdge(Canvas canvas, StartupStepViewModel cond, bool isTrue, Point from, Point to, double splitY)
    {
        List<Point> pts;
        if (Math.Abs(from.X - to.X) < 0.5)
        {
            pts = [from, to];
        }
        else
        {
            pts = [from, new Point(from.X, splitY), new Point(to.X, splitY), to];
        }
        DrawEdge(canvas, pts, isTrue ? TrueEdgeBase : FalseEdgeBase, 1.4, false, true,
            stateSource: cond, branchParam: isTrue);

        var label = new TextBlock
        {
            Text = isTrue ? "是" : "否",
            FontSize = 10.5, FontWeight = FontWeights.SemiBold,
        };
        label.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(StartupStepViewModel.RunState)) { Source = cond, Converter = CondEdgeBrushConv.Instance, ConverterParameter = isTrue });
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        // 标注放在水平段的起点旁（无水平段则放竖线右侧）
        var lx = Math.Abs(from.X - to.X) < 0.5 ? from.X + 5 : Math.Min(from.X, to.X) + Math.Abs(to.X - from.X) / 2 - label.DesiredSize.Width / 2;
        Canvas.SetLeft(label, lx);
        Canvas.SetTop(label, splitY - label.DesiredSize.Height - 1);
        canvas.Children.Add(label);
    }

    // ================= 节点卡与连线绘制 =================

    /// <summary>节点卡：图标 + 显示名 + 类型徽标 + 运行态徽标 + 摘要行 + 判断依据行，全部绑定 VM 实时刷新。</summary>
    private FrameworkElement BuildNodeCard(StartupStepViewModel vm)
    {
        var accentHex = KindAccentHex(vm.Kind);
        var accent = Frozen(accentHex);
        var emphasized = IsEmphasized(vm.Kind);
        var card = new Border
        {            Width = NodeW, Height = NodeH,
            Background = CardBg,
            // 重点节点（结束流程/进入任务中心/定时触发器/电子狗）加粗描边突出
            BorderThickness = emphasized ? new Thickness(2) : new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8, 10, 8), Cursor = Cursors.Hand,
        };
        card.SetBinding(Border.BorderBrushProperty,
            new Binding(nameof(StartupStepViewModel.RunState)) { Source = vm, Converter = CardBorderConv.Instance, ConverterParameter = emphasized ? accentHex : null });
        card.SetBinding(UIElement.OpacityProperty,
            new Binding(nameof(StartupStepViewModel.Enabled)) { Source = vm, Converter = EnabledOpacityConv.Instance });
        card.SetBinding(FrameworkElement.ToolTipProperty, new Binding(nameof(StartupStepViewModel.Summary)) { Source = vm });
        // 鼠标松开且位移小于阈值才算点击（位移大说明是在拖动平移画布，不触发跳转）
        Point downPos = default;
        card.MouseLeftButtonDown += (_, e) => downPos = e.GetPosition(card);
        card.MouseLeftButtonUp += (_, e) =>
        {
            var d = e.GetPosition(card) - downPos;
            if (Math.Abs(d.X) < 6 && Math.Abs(d.Y) < 6) _onActivate(vm);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // 左侧类型色条：不同判断/动作一眼区分（重点节点色条加宽）
        var accentBar = new Border
        {
            Width = emphasized ? 4.5 : 3, CornerRadius = new CornerRadius(2), Background = accent,
            Margin = new Thickness(0, 2, 7, 2), VerticalAlignment = VerticalAlignment.Stretch,
        };
        grid.Children.Add(accentBar);

        var iconBg = new Border
        {
            Width = 26, Height = 26, CornerRadius = new CornerRadius(7), VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 8, 0),
            Background = Frozen("#40" + accentHex[1..]),
            Child = new TextBlock
            {
                FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Foreground = accent,
                Text = vm.Icon,
            },
        };
        Grid.SetColumn(iconBg, 1);
        grid.Children.Add(iconBg);

        var right = new StackPanel();
        // 行 1：名称 + 类型徽标 + 运行态徽标
        var row1 = new StackPanel { Orientation = Orientation.Horizontal };
        var name = new TextBlock
        {
            FontSize = 12.5, FontWeight = FontWeights.Medium, Foreground = WhiteText, VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 120,
        };
        name.SetBinding(TextBlock.TextProperty, new Binding(nameof(StartupStepViewModel.DisplayName)) { Source = vm });
        row1.Children.Add(name);
        var chip = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0),
            Background = ChipBg, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { FontSize = 9, Foreground = DimText, Text = vm.TypeName },
        };
        row1.Children.Add(chip);
        var badge = new Border
        {
            CornerRadius = new CornerRadius(5), Padding = new Thickness(5, 1, 5, 1), Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        badge.SetBinding(Border.BackgroundProperty,
            new Binding(nameof(StartupStepViewModel.RunState)) { Source = vm, Converter = BadgeBgConv.Instance });
        badge.SetBinding(UIElement.VisibilityProperty,
            new Binding(nameof(StartupStepViewModel.HasRunState)) { Source = vm, Converter = new BooleanToVisibilityConverter() });
        var badgeText = new TextBlock { FontSize = 9 };
        badgeText.SetBinding(TextBlock.TextProperty, new Binding(nameof(StartupStepViewModel.RunStateBadgeText)) { Source = vm });
        badgeText.SetBinding(TextBlock.ForegroundProperty,
            new Binding(nameof(StartupStepViewModel.RunState)) { Source = vm, Converter = BadgeFgConv.Instance });
        badge.Child = badgeText;
        row1.Children.Add(badge);
        right.Children.Add(row1);

        // 行 2：摘要（这个节点大概做什么）
        var summary = new TextBlock
        {
            FontSize = 10, Foreground = DimText, Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap, MaxHeight = 28,
        };
        summary.SetBinding(TextBlock.TextProperty, new Binding(nameof(StartupStepViewModel.Summary)) { Source = vm });
        right.Children.Add(summary);

        // 行 3：运行态附注（条件判断依据，有内容才显示）
        var note = new TextBlock
        {
            FontSize = 9.5, Foreground = GoldText, Margin = new Thickness(0, 2, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        note.SetBinding(TextBlock.TextProperty, new Binding(nameof(StartupStepViewModel.RunStateNote)) { Source = vm });
        note.SetBinding(UIElement.VisibilityProperty,
            new Binding(nameof(StartupStepViewModel.HasRunStateNote)) { Source = vm, Converter = new BooleanToVisibilityConverter() });
        right.Children.Add(note);

        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        card.Child = grid;
        _nodeByVm[vm] = card; // 底部武装面板点击定位用
        return card;
    }

    /// <summary>画一条折线边（可选箭头/虚线；给 stateSource 时颜色粗细绑定条件运行态）。</summary>
    private void DrawEdge(Canvas canvas, List<Point> pts, Brush brush, double thickness, bool dashed, bool arrow,
        StartupStepViewModel? stateSource = null, object? branchParam = null)
    {
        var line = new Polyline
        {
            Points = new PointCollection(pts),
            Stroke = brush, StrokeThickness = thickness,
            StrokeLineJoin = PenLineJoin.Round,
        };
        if (dashed) line.StrokeDashArray = new DoubleCollection { 4, 3 };
        if (stateSource != null)
        {
            line.SetBinding(Shape.StrokeProperty,
                new Binding(nameof(StartupStepViewModel.RunState)) { Source = stateSource, Converter = CondEdgeBrushConv.Instance, ConverterParameter = branchParam });
            line.SetBinding(Shape.StrokeThicknessProperty,
                new Binding(nameof(StartupStepViewModel.RunState)) { Source = stateSource, Converter = CondEdgeThicknessConv.Instance, ConverterParameter = branchParam });
        }
        canvas.Children.Add(line);

        if (arrow && pts.Count >= 2)
        {
            var head = MakeArrowHead(pts[^2], pts[^1], brush);
            if (stateSource != null)
            {
                head.SetBinding(Shape.FillProperty,
                    new Binding(nameof(StartupStepViewModel.RunState)) { Source = stateSource, Converter = CondEdgeBrushConv.Instance, ConverterParameter = branchParam });
            }
            canvas.Children.Add(head);
        }
    }

    private static Polygon MakeArrowHead(Point from, Point to, Brush fill)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.001) { dx = 0; dy = 1; len = 1; }
        var ux = dx / len; var uy = dy / len;       // 指向目标的单位向量
        const double s = 7;                          // 箭头大小
        var p1 = new Point(to.X - ux * s - uy * s * 0.55, to.Y - uy * s + ux * s * 0.55);
        var p2 = new Point(to.X - ux * s + uy * s * 0.55, to.Y - uy * s - ux * s * 0.55);
        return new Polygon { Points = new PointCollection { to, p1, p2 }, Fill = fill };
    }

    // ================= 缩放 / 平移 =================

    private double Scale => Zoom.ScaleX;

    private void SetScale(double s)
    {
        s = Math.Clamp(s, 0.2, 2.5);
        Zoom.ScaleX = Zoom.ScaleY = s;
        ZoomText.Text = $"{s * 100:0}%";
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetScale(Scale + 0.15);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetScale(Scale - 0.15);
    private void ZoomReset_Click(object sender, RoutedEventArgs e) => SetScale(1);
    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void FitToWindow()
    {
        var vw = Scroller.ViewportWidth;
        var vh = Scroller.ViewportHeight;
        if (vw <= 0 || vh <= 0 || ChartCanvas.Width <= 0 || ChartCanvas.Height <= 0) return;
        SetScale(Math.Min(vw / ChartCanvas.Width, vh / ChartCanvas.Height));
        Scroller.ScrollToHome();
    }

    private void Scroller_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetScale(Scale + (e.Delta > 0 ? 0.15 : -0.15));
        e.Handled = true;
    }

    private bool _panArmed;   // 已按下但尚未超过拖动阈值（不捕获鼠标，让节点点击正常冒泡）
    private bool _panning;    // 已超过阈值，正在拖动平移（此时才捕获）
    private Point _panStart;
    private double _panStartH, _panStartV;

    private void Pan_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _panArmed = true;
        _panStart = e.GetPosition(Scroller);
        _panStartH = Scroller.HorizontalOffset;
        _panStartV = Scroller.VerticalOffset;
        // 注意：不能在这里捕获鼠标——捕获会截走后续事件，节点卡片收不到 click（跳转失灵）
    }

    private void Pan_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_panArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(Scroller);
        if (!_panning)
        {
            if (Math.Abs(pos.X - _panStart.X) < 5 && Math.Abs(pos.Y - _panStart.Y) < 5) return;
            _panning = true;
            PanSurface.CaptureMouse();
        }
        Scroller.ScrollToHorizontalOffset(_panStartH - (pos.X - _panStart.X));
        Scroller.ScrollToVerticalOffset(_panStartV - (pos.Y - _panStart.Y));
    }

    private void Pan_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _panArmed = false;
        if (_panning)
        {
            _panning = false;
            PanSurface.ReleaseMouseCapture();
        }
    }

    // ================= 画笔/转换器 =================

    private static Brush Frozen(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFrom(hex)!;
        b.Freeze();
        return b;
    }

    /// <summary>节点卡描边色：运行态 → 颜色（与编辑器卡片描边同口径）；未运行时重点节点用类型色描边，普通节点用默认金线。</summary>
    private sealed class CardBorderConv : IValueConverter
    {
        public static readonly CardBorderConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value switch
        {
            NodeRunState.Running => Gold,
            NodeRunState.Success => Anemo,
            NodeRunState.CondTrue => Anemo,
            NodeRunState.CondFalse => Pyro,
            NodeRunState.Failed => Fail,
            NodeRunState.Skipped => EdgeNormal,
            // p 是该节点类型色（重点节点传入）：未执行时也带一圈类型色，突出终点/交接点/触发点
            _ => p is string hex ? Frozen("#99" + hex[1..]) : CardEdge,
        };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>条件分支边颜色：是=青系 / 否=橙系（未执行也有底色区分）；走过的点亮，没走的灰下去。</summary>
    private sealed class CondEdgeBrushConv : IValueConverter
    {
        public static readonly CondEdgeBrushConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        {
            var isTrue = p is true;
            return value switch
            {
                NodeRunState.CondTrue => isTrue ? Anemo : EdgeDim,
                NodeRunState.CondFalse => isTrue ? EdgeDim : Pyro,
                _ => isTrue ? TrueEdgeBase : FalseEdgeBase,
            };
        }
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>条件分支边粗细：走过的加粗，没走的细。</summary>
    private sealed class CondEdgeThicknessConv : IValueConverter
    {
        public static readonly CondEdgeThicknessConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        {
            var isTrue = p is true;
            return value switch
            {
                NodeRunState.CondTrue => isTrue ? 2.2 : 1.0,
                NodeRunState.CondFalse => isTrue ? 1.0 : 2.2,
                _ => 1.4,
            };
        }
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>分支整支灰显：条件已判断且没走这条支 → 半透明。</summary>
    private sealed class BranchOpacityConv : IValueConverter
    {
        public static readonly BranchOpacityConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
        {
            var isTrue = p is true;
            return value switch
            {
                NodeRunState.CondTrue => isTrue ? 1.0 : 0.35,
                NodeRunState.CondFalse => isTrue ? 0.35 : 1.0,
                _ => 1.0,
            };
        }
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>禁用节点半透明显示（与编辑器卡片同口径）。</summary>
    private sealed class EnabledOpacityConv : IValueConverter
    {
        public static readonly EnabledOpacityConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is true ? 1.0 : 0.45;
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>运行态徽标底色。</summary>
    private sealed class BadgeBgConv : IValueConverter
    {
        public static readonly BadgeBgConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value switch
        {
            NodeRunState.Running => Frozen("#40E8C96D"),
            NodeRunState.Failed => Frozen("#40EF5350"),
            NodeRunState.CondFalse => Frozen("#40E88A6F"),
            NodeRunState.Skipped => ChipBg,
            _ => BadgeBgDefault,
        };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }

    /// <summary>运行态徽标文字色。</summary>
    private sealed class BadgeFgConv : IValueConverter
    {
        public static readonly BadgeFgConv Instance = new();
        public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value switch
        {
            NodeRunState.Running => GoldText,
            NodeRunState.Failed => Fail,
            NodeRunState.CondFalse => Pyro,
            NodeRunState.Skipped => DimText,
            _ => Anemo,
        };
        public object ConvertBack(object v, Type t, object p, System.Globalization.CultureInfo c) => throw new NotSupportedException();
    }
}
