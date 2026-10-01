using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.DpiAwareness;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.ViewModel;
using Microsoft.Extensions.Logging;
using Serilog.Sinks.RichTextBox.Abstraction;
using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using Vanara.PInvoke;

namespace BetterGenshinImpact.View;

/// <summary>
/// 覆盖在游戏窗口上的遮罩窗口，用于显示识别结果、日志、状态、指标、地图点位等。
/// 只能由 IMaskWindowHost 通过 DI 创建；View 层以外的代码不应引用本类型。
/// </summary>
public partial class MaskWindow : Window
{
    private readonly MaskWindowViewModel _viewModel;
    private readonly IRichTextBox _richTextBox;
    private readonly ILogger<MaskWindow> _logger;

    private MapLabelSearchWindow? _mapLabelSearchWindow;
    private CancellationTokenSource? _mapLabelCategorySelectCts;


    /// <summary>
    /// 日志框裁剪重入守卫：LogTextBoxTextChanged 内 RemoveAt/Clear 会再次触发 TextChanged
    /// 并同步重入本方法。置位期间重入直接 return，根除 RemoveAt→TextChanged→RemoveAt 递归栈溢出。
    /// TextChanged 在 WPF UI 线程（Dispatcher）上同步重入，普通 bool 即可，无需锁/volatile。
    /// </summary>
    private bool _isTrimmingLog;


    static MaskWindow()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(MaskWindow), new FrameworkPropertyMetadata(typeof(MaskWindow)));
    }

    public MaskWindow(MaskWindowViewModel viewModel, IRichTextBox richTextBox, ILogger<MaskWindow> logger)
    {
        _viewModel = viewModel;
        _richTextBox = richTextBox;
        _logger = logger;
        DataContext = viewModel;

        this.SetResourceReference(StyleProperty, typeof(MaskWindow));
        InitializeComponent();
        this.InitializeDpiAwareness();

        LogTextBox.TextChanged += LogTextBoxTextChanged;
        Loaded += OnLoaded;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _richTextBox.RichTextBox = LogTextBox;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        // 先设置窗口样式，再触发 SourceInitialized，保证 WindowClickThroughBehavior 最后应用点击穿透状态
        this.SetLayeredWindow();
        this.SetChildWindow();
        this.HideFromAltTab();
        base.OnSourceInitialized(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        LogTextBox.TextChanged -= LogTextBoxTextChanged;

        _mapLabelCategorySelectCts?.Cancel();
        _mapLabelCategorySelectCts?.Dispose();
        _mapLabelCategorySelectCts = null;

        if (_mapLabelSearchWindow != null)
        {
            _mapLabelSearchWindow.Close();
            _mapLabelSearchWindow = null;
        }

        base.OnClosed(e);
    }

    /// <summary>
    /// 点位选择器关闭时，同步隐藏它弹出的搜索窗口（搜索窗口是本视图自己持有的子窗口）
    /// </summary>
    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MaskWindowViewModel.IsMapPointPickerOpen)
            && !_viewModel.IsMapPointPickerOpen
            && _mapLabelSearchWindow != null)
        {
            _ = Dispatcher.InvokeAsync(() => _mapLabelSearchWindow?.Hide());
        }


    }

    private void LogTextBoxTextChanged(object sender, TextChangedEventArgs e)
    {
        // 重入守卫：裁剪中的 RemoveAt/Clear 会再次触发 TextChanged 并重入本方法，
        // 正在裁剪时立即返回，根除递归栈溢出（0xc00000fd）。
        if (_isTrimmingLog)
        {
            return;
        }

        _isTrimmingLog = true;
        try
        {
            // 行数批量裁剪：循环删除最旧行直到行数 <= 上限（替代原"每次只删 1 行"）。
            if (LogTextBox.Document.Blocks.FirstBlock is Paragraph p)
            {
                var removeCount = ComputeTrimCount(p.Inlines.Count, MaxLogParagraphLines);
                var inlines = (System.Collections.IList)p.Inlines;
                for (var i = 0; i < removeCount; i++)
                {
                    inlines.RemoveAt(0);
                }
            }

            // 总长度裁剪：超过上限清空（阈值语义不变；守卫保护下 Clear 触发的 TextChanged 被挡）。
            var textRange = new TextRange(LogTextBox.Document.ContentStart, LogTextBox.Document.ContentEnd);
            if (textRange.Text.Length > MaxLogTextLength)
            {
                LogTextBox.Document.Blocks.Clear();
            }
        }
        finally
        {
            _isTrimmingLog = false;
        }

        // ScrollToEnd 不修改文档、不触发 TextChanged，放守卫块外保证每次都滚动（与原行为一致）。
        LogTextBox.ScrollToEnd();
    }

    private void MapLabelSearchTextBox_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mapLabelSearchWindow == null)
        {
            _mapLabelSearchWindow = new MapLabelSearchWindow();
            _mapLabelSearchWindow.AttachViewModel(_viewModel);
        }

        var textbox = (FrameworkElement)sender;
        var point = textbox.PointToScreen(new Point(0, 0));
        var popupHeight = _mapLabelSearchWindow.ActualHeight > 0 ? _mapLabelSearchWindow.ActualHeight : _mapLabelSearchWindow.Height;

        _mapLabelSearchWindow.Left = point.X / DpiHelper.ScaleY;
        _mapLabelSearchWindow.Top = (point.Y - 4) / DpiHelper.ScaleY - popupHeight;

        if (!_mapLabelSearchWindow.IsVisible)
        {
            _mapLabelSearchWindow.Show();
        }

        _mapLabelSearchWindow.Topmost = true;
        _mapLabelSearchWindow.FocusSearch();

        e.Handled = true;
    }

    private void MapLabelCategoriesListView_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var container = ItemsControl.ContainerFromElement(MapLabelCategoriesListView, e.OriginalSource as DependencyObject) as ListViewItem;
        if (container == null)
        {
            return;
        }

        var item = MapLabelCategoriesListView.ItemContainerGenerator.ItemFromContainer(container) as MapLabelCategoryVm;
        if (item == null)
        {
            return;
        }

        if (ReferenceEquals(MapLabelCategoriesListView.SelectedItem, item))
        {
            return;
        }

        MapLabelCategoriesListView.SelectedItem = item;

        _mapLabelCategorySelectCts?.Cancel();
        _mapLabelCategorySelectCts?.Dispose();
        _mapLabelCategorySelectCts = new CancellationTokenSource();
        _ = SelectMapLabelCategoryAsync(item, _mapLabelCategorySelectCts.Token);
    }

    private async Task SelectMapLabelCategoryAsync(MapLabelCategoryVm item, CancellationToken ct)
    {
        try
        {
            await _viewModel.SelectMapLabelCategoryCommand.ExecuteAsync(item);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "切换地图标点分类时发生异常");
        }
    }

    public RichTextBox LogBox => LogTextBox;

    /// <summary>日志框单段落最大行数上限（裁剪阈值）。</summary>
    private const int MaxLogParagraphLines = 200;

    /// <summary>日志框文档最大文本长度上限（裁剪阈值）。</summary>
    private const int MaxLogTextLength = 10000;

    public static int ComputeTrimCount(int currentLineCount, int maxLines)
    {
        return Math.Max(0, currentLineCount - maxLines);
    }

}

file static class MaskWindowExtension
{
    public static void HideFromAltTab(this Window window)
    {
        var hWnd = new WindowInteropHelper(window).Handle;
        int style = User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE);
        style |= (int)User32.WindowStylesEx.WS_EX_TOOLWINDOW;
        User32.SetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE, style);
    }

    public static void SetLayeredWindow(this Window window)
    {
        var hWnd = new WindowInteropHelper(window).Handle;
        int style = User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE);
        style |= (int)User32.WindowStylesEx.WS_EX_TRANSPARENT;
        style |= (int)User32.WindowStylesEx.WS_EX_LAYERED;
        _ = User32.SetWindowLong(hWnd, User32.WindowLongFlags.GWL_EXSTYLE, style);
    }

    public static void SetChildWindow(this Window window)
    {
        var hWnd = new WindowInteropHelper(window).Handle;
        int style = User32.GetWindowLong(hWnd, User32.WindowLongFlags.GWL_STYLE);
        style |= (int)User32.WindowStyles.WS_CHILD;
        _ = User32.SetWindowLong(hWnd, User32.WindowLongFlags.GWL_STYLE, style);
    }
}
