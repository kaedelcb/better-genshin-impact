using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 奥黛塔任务状态详情面板：锄地数据任务状态全部信息（配置组/任务/线路/脚本线路/锄地进度/上线信息/好感时间轮次）。
/// 独立于宠物窗口：可拖动（标题栏）、可调大小（边缘 + 右下角 grip）、可单独关闭（✕ 写回设置），
/// 位置/大小/透明度均持久化。数据每秒拉取（UI 线程，单线程契约）。
/// </summary>
public partial class PetStatusPanelWindow : Window
{
    private readonly PetViewModel _vm;
    private readonly DispatcherTimer _refresh;

    public PetStatusPanelWindow(PetViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Opacity = vm.PanelOpacity;
        ApplyMinimalMode();
        // 设置页透明度滑条实时生效（原先只在构造时读一次，调了没反应）
        vm.PropertyChanged += OnVmPropertyChanged;
        var (w, h) = vm.GetPanelSize();
        Width = w;
        Height = h;
        SourceInitialized += (_, _) =>
        {
            var (x, y, _) = _vm.GetPanelRestorePosition();
            Left = x;
            Top = y;
            // 免打扰穿透模式下任务面板同样整窗穿透（与宠物窗口行为一致，解锁走托盘子菜单/设置页）
            ApplyClickThrough(_vm.ClickThrough);
        };
        LocationChanged += (_, _) => NotifyLayout();
        SizeChanged += (_, _) => NotifyLayout();

        // 行内容垂直居中：隐藏空行后内容不满一屏时上下留白均衡（内容超高则回顶对齐+滚动）
        RowsScroll.SizeChanged += (_, _) => UpdateRowsAlignment();
        RowsHost.SizeChanged += (_, _) => UpdateRowsAlignment();

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => RebuildRows();
        Loaded += (_, _) => { RebuildRows(); _refresh.Start(); };
        Closed += (_, _) =>
        {
            _refresh.Stop();
            vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PetViewModel.PanelOpacity))
            Opacity = _vm.PanelOpacity;
        else if (e.PropertyName == nameof(PetViewModel.PanelMinimal))
            ApplyMinimalMode();
    }

    /// <summary>极简模式：高度随内容自适应（SizeToContent=Height，禁用高度缩放边）；
    /// 退出时恢复记忆高度。宽度两种模式下都可拖调。</summary>
    private void ApplyMinimalMode()
    {
        if (_vm.PanelMinimal)
        {
            // 三个坑都得清：①显式 Height 会让 SizeToContent 失效；②MinHeight=200 顶住收缩；
            // ③居中逻辑曾把容器高度锁成旧视口高（见 UpdateRowsAlignment）
            SizeToContent = SizeToContent.Height;
            Height = double.NaN;
            MinHeight = 0;
            RowsHostWrap.Height = double.NaN;
            foreach (var edge in new[] { EdgeN, EdgeS, EdgeNW, EdgeNE, EdgeSW, EdgeSE })
                edge.Visibility = Visibility.Collapsed;
        }
        else
        {
            SizeToContent = SizeToContent.Manual;
            MinHeight = 200;
            foreach (var edge in new[] { EdgeN, EdgeS, EdgeNW, EdgeNE, EdgeSW, EdgeSE })
                edge.Visibility = Visibility.Visible;
            var (_, h) = _vm.GetPanelSize();
            if (h > 100 && h < 2000)
                Height = h;
        }
    }

    // ========== 透明度循环按钮（标题栏 ◐）：1.0 → 0.85 → 0.7 → 0.55 → 0.4 → 1.0，写回 PanelOpacity 持久化 ==========

    private static readonly double[] OpacitySteps = [1.0, 0.85, 0.7, 0.55, 0.4];

    private void OnOpacityCycleClick(object sender, RoutedEventArgs e)
    {
        var current = StepsIndex(_vm.PanelOpacity);
        _vm.PanelOpacity = OpacitySteps[(current + 1) % OpacitySteps.Length];
    }

    private static int StepsIndex(double value)
    {
        var idx = Array.IndexOf(OpacitySteps, Math.Round(value, 2));
        return idx >= 0 ? idx : 0; // 非档位值（滑条自由拖出）从头循环
    }

    // ========== 手动缩放（AllowsTransparency 窗口无原生非客户区，WindowChrome/ResizeGrip 均不生效） ==========

    private System.Windows.Point _resizeStartScreen;
    private double _startLeft, _startTop, _startW, _startH;
    private string? _resizeEdge;
    private double _dpiScaleX = 1, _dpiScaleY = 1;

    private void OnResizeEdgeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.Shapes.Rectangle rect || rect.Tag is not string edge) return;
        _resizeEdge = edge;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
        {
            var t = source.CompositionTarget.TransformToDevice;
            _dpiScaleX = t.M11;
            _dpiScaleY = t.M22;
        }
        _resizeStartScreen = PointToScreen(e.GetPosition(this));
        _startLeft = Left;
        _startTop = Top;
        _startW = ActualWidth;
        _startH = ActualHeight;
        rect.CaptureMouse();
        rect.MouseMove += OnResizeEdgeMove;
        rect.MouseLeftButtonUp += OnResizeEdgeUp;
    }

    private void OnResizeEdgeMove(object sender, MouseEventArgs e)
    {
        if (_resizeEdge == null) return;
        var now = PointToScreen(e.GetPosition(this));
        var dx = (now.X - _resizeStartScreen.X) / _dpiScaleX;
        var dy = (now.Y - _resizeStartScreen.Y) / _dpiScaleY;

        if (_resizeEdge.Contains('E'))
            Width = Math.Max(MinWidth, _startW + dx);
        if (_resizeEdge.Contains('S'))
            Height = Math.Max(MinHeight, _startH + dy);
        if (_resizeEdge.Contains('W'))
        {
            var newW = Math.Max(MinWidth, _startW - dx);
            Left = _startLeft + (_startW - newW);
            Width = newW;
        }
        if (_resizeEdge.Contains('N'))
        {
            var newH = Math.Max(MinHeight, _startH - dy);
            Top = _startTop + (_startH - newH);
            Height = newH;
        }
    }

    private void OnResizeEdgeUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement el)
        {
            el.ReleaseMouseCapture();
            el.MouseMove -= OnResizeEdgeMove;
            el.MouseLeftButtonUp -= OnResizeEdgeUp;
        }
        _resizeEdge = null;
        // LocationChanged/SizeChanged 已触发 NotifyLayout，位置/尺寸自动写回设置
    }

    private void NotifyLayout()
    {
        if (double.IsNaN(Left) || double.IsNaN(ActualWidth)) return;
        _vm.NotifyPanelLayoutChanged(Left, Top, ActualWidth, ActualHeight);
    }

    // ========== 免打扰穿透模式（WS_EX_TRANSPARENT：整窗点击穿透，与 PetWindow 同款） ==========

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    /// <summary>应用/解除整窗点击穿透（VM 侧统一入口 ApplyClickThrough）。</summary>
    internal void ApplyClickThrough(bool enabled)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        ex = enabled ? (ex | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex);
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 1)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* 竞争忽略 */ }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => _vm.PanelEnabled = false;

    /// <summary>内容矮于视口 → 容器占满视口高、行内容垂直居中；高于视口 → 容器高度自适应、顶对齐（滚动可看全部）。
    /// 注意 ScrollViewer 以无限高度测量内容，子元素自身 VerticalAlignment=Center 对不齐视口，必须显式设高度。</summary>
    private void UpdateRowsAlignment()
    {
        // 极简模式高度由 SizeToContent 决定，禁用"容器高度=视口"逻辑——
        // 那会和 SizeToContent 形成反馈循环把窗口高度锁死
        if (_vm.PanelMinimal)
        {
            RowsHostWrap.Height = double.NaN;
            return;
        }
        var viewportHeight = RowsScroll.ViewportHeight;
        if (double.IsNaN(viewportHeight) || viewportHeight <= 0) return;
        if (RowsHost.ActualHeight < viewportHeight)
        {
            RowsHostWrap.Height = viewportHeight;
            RowsHost.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            RowsHostWrap.Height = double.NaN;
            RowsHost.VerticalAlignment = VerticalAlignment.Top;
        }
    }

    private void RebuildRows()
    {
        List<KeyValuePair<string, string>> rows;
        try
        {
            rows = _vm.BuildPanelRows();
        }
        catch (Exception ex)
        {
            // 行构建异常直接在面板上显示，杜绝"静默空白面板"
            rows = [new("面板异常", ex.Message)];
        }
        RowsHost.Children.Clear();
        foreach (var row in rows)
        {
            var grid = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = row.Key,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xC9, 0x6D)),
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(label, 0);

            var value = new TextBlock
            {
                Text = row.Value,
                FontSize = 12,
                Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xEA, 0xDD)),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Top
            };
            Grid.SetColumn(value, 1);

            grid.Children.Add(label);
            grid.Children.Add(value);
            RowsHost.Children.Add(grid);
        }
    }
}
