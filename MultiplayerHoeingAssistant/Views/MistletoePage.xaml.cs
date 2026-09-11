using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 槲寄生 · 调度器页面代码后置。
/// 只承担两件事（MVVM 不便纯 XAML 表达的部分）：
/// 1. 节点卡片的拖拽重排（DragDrop）——支持同链换位与跨链移动（主链 ↔ 条件的是/否子链），
///    落点在卡片上按上下半区换算插入位次，落点在链空白区（分支列/主链面板）追加到链尾；
///    环检测（条件节点拖进自己的子链）在 ViewModel 的 MoveStepTo 里拒绝。
/// 2. 「浏览…」文件选择框与卡片点击展开/收起编辑器。
/// 3. 节点拖拽到列表上下边缘时让 ScrollViewer 自动滚动（原生 DragDrop 不会触发滚动）。
/// 拖拽只发生在节点手柄（⠿）上，卡片其余区域点击 = 展开/收起参数编辑器。
/// </summary>
public partial class MistletoePage : UserControl
{
    private MistletoeViewModel? Vm => DataContext as MistletoeViewModel;

    private Point _dragStartPoint;
    private StartupStepViewModel? _dragCandidate;

    public MistletoePage()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    // ================= 流程图节点跳转定位 =================

    private MistletoeViewModel? _revealSubscribed;

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_revealSubscribed != null) _revealSubscribed.StepRevealRequested -= OnStepRevealRequested;
        _revealSubscribed = Vm;
        if (_revealSubscribed != null) _revealSubscribed.StepRevealRequested += OnStepRevealRequested;
    }

    private void OnStepRevealRequested(StartupStepViewModel vm)
    {
        // 分支区域刚展开、目标卡片可能刚从不折叠变为可见，等布局完成后再找容器滚动
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
        {
            FindElementByDataContext(this, vm)?.BringIntoView();
        }));
    }

    /// <summary>在可视树里按 DataContext 广度优先找元素（编辑器节点树未虚拟化，容器均已生成）。</summary>
    private static FrameworkElement? FindElementByDataContext(DependencyObject root, object dataContext)
    {
        var queue = new Queue<DependencyObject>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var cur = queue.Dequeue();
            if (cur is FrameworkElement fe && ReferenceEquals(fe.DataContext, dataContext)) return fe;
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(cur);
            for (var i = 0; i < count; i++) queue.Enqueue(System.Windows.Media.VisualTreeHelper.GetChild(cur, i));
        }
        return null;
    }

    // ================= 拖拽重排 =================

    private void DragHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = (sender as FrameworkElement)?.DataContext as StartupStepViewModel;
        _dragStartPoint = e.GetPosition(null);
        // 标记预览事件已处理，抑制配对的冒泡 MouseLeftButtonDown 到达节点卡（否则会误触发展开/收起）
        e.Handled = true;
    }

    private void DragHandle_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragCandidate == null || e.LeftButton != MouseButtonState.Pressed) return;
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _dragStartPoint.X) < 6 && Math.Abs(pos.Y - _dragStartPoint.Y) < 6) return;

        var dragged = _dragCandidate;
        _dragCandidate = null;
        if (sender is FrameworkElement handle)
        {
            // DoDragDrop 是阻塞式模态循环（内部仍在泵消息，DispatcherTimer 照常走），
            // 拖拽期间开启边缘自动滚动，结束（落下/取消）后关闭
            StartDragAutoScroll();
            try
            {
                DragDrop.DoDragDrop(handle, new DataObject(typeof(StartupStepViewModel), dragged), DragDropEffects.Move);
            }
            finally
            {
                StopDragAutoScroll();
            }
        }
    }

    private void NodeCard_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartupStepViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    /// <summary>落在节点卡上：插入到目标卡片前/后（按落点上下半区）。</summary>
    private void NodeCard_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartupStepViewModel))) return;
        if (e.Data.GetData(typeof(StartupStepViewModel)) is not StartupStepViewModel dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not StartupStepViewModel target) return;
        if (Vm == null) return;

        var card = (FrameworkElement)sender;
        if (ReferenceEquals(dragged, target))
        {
            e.Handled = true;
            return;
        }

        var dropPos = e.GetPosition(card);
        var insertAfter = dropPos.Y > card.ActualHeight / 2;

        var targetChain = target.OwnerChain;
        var targetIndex = targetChain.Steps.IndexOf(target);
        if (targetIndex < 0) return;
        if (insertAfter) targetIndex++;

        // 同链前移的位次换算在 MoveStepTo 内统一处理
        Vm.MoveStepTo(dragged, targetChain, targetIndex);
        e.Handled = true;
    }

    /// <summary>链空白区（分支列 / 主链面板）悬停：接受本页节点。</summary>
    private void Chain_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(typeof(StartupStepViewModel)))
        {
            e.Effects = DragDropEffects.Move;
            // 不设 Handled：让落点更深处的节点卡优先处理
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
    }

    /// <summary>落在链空白区：追加到该链末尾（sender 的 DataContext 是 StepChainViewModel）。</summary>
    private void Chain_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(StartupStepViewModel))) return;
        if (e.Data.GetData(typeof(StartupStepViewModel)) is not StartupStepViewModel dragged) return;
        if ((sender as FrameworkElement)?.DataContext is not StepChainViewModel chain) return;
        if (Vm == null) return;

        // 拖拽项已在该链末尾时无操作
        if (ReferenceEquals(dragged.OwnerChain, chain) && chain.Steps.Count > 0
            && ReferenceEquals(chain.Steps[^1], dragged))
        {
            e.Handled = true;
            return;
        }

        Vm.MoveStepTo(dragged, chain, chain.Steps.Count);
        e.Handled = true;
    }

    // ================= 拖拽时自动滚动 =================

    /// <summary>
    /// WPF 原生 DragDrop 期间滚轮/边缘悬停都不会让 ScrollViewer 滚动。
    /// 在 DoDragDrop 阻塞期间挂一个定时器：用 Win32 GetCursorPos 拿真实光标位置
    /// （WPF 的 Mouse.GetPosition 在 OLE 拖拽期间返回陈旧坐标，不能用），
    /// 光标靠近 FlowScroller 上下边缘就按距离比例持续向该方向滚动；DoDragDrop 返回即停。
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _dragScrollTimer;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Win32Point lpPoint);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Win32Point { public int X; public int Y; }

    private void StartDragAutoScroll()
    {
        _dragScrollTimer ??= CreateDragScrollTimer();
        _dragScrollTimer.Start();
    }

    private void StopDragAutoScroll()
    {
        _dragScrollTimer?.Stop();
    }

    private System.Windows.Threading.DispatcherTimer CreateDragScrollTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        timer.Tick += (_, _) =>
        {
            if (!GetCursorPos(out var pt)) return;
            var p = FlowScroller.PointFromScreen(new Point(pt.X, pt.Y));

            // 光标横向偏离滚动条太远（拖出窗口了）就不滚
            if (p.X < -40 || p.X > FlowScroller.ActualWidth + 40) return;

            const double edge = 48;   // 上下边缘触发区高度
            const double maxStep = 8; // 每拍最大滚动像素（越贴近边缘滚得越快）
            double velocity = 0;
            if (p.Y < edge)
                velocity = -maxStep * Math.Min(edge - p.Y, edge) / edge;
            else if (p.Y > FlowScroller.ViewportHeight - edge && p.Y < FlowScroller.ViewportHeight + 40)
                velocity = maxStep * Math.Min(p.Y - (FlowScroller.ViewportHeight - edge), edge) / edge;
            if (velocity != 0)
                FlowScroller.ScrollToVerticalOffset(FlowScroller.VerticalOffset + velocity);
        };
        return timer;
    }

    // ================= 卡片点击展开/收起编辑器 =================

    private void NodeCard_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StartupStepViewModel vm) return;
        Vm?.ToggleSelectCommand.Execute(vm);
        e.Handled = true;
    }

    // ================= 程序路径浏览 =================

    private void BrowsePath_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StartupStepViewModel vm) return;
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "可执行程序 (*.exe)|*.exe|所有文件 (*.*)|*.*",
            Title = "选择要启动的程序",
        };
        if (dlg.ShowDialog() == true)
        {
            vm.Path = dlg.FileName;
        }
    }
}
