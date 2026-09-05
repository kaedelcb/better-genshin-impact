using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using MultiplayerHoeingAssistant.Helpers;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

/// <summary>
/// 嘟嘟可页面代码后置。只放 MVVM 绑定做不到的交互：
/// - 实时日志：RichTextBox 按级别上色渲染（VM 的 VisibleEntries 仍是数据模型，集合变化映射成段落增删），
///   自动跟尾 / 用户上翻暂停跟尾 / 滚回底部恢复；文本天然支持跨行拖选与复制；
/// - 日志浏览：虚拟化 ListBox 承载整文件全量视图（多选 + Ctrl+C 复制、关键字高亮走绑定），
///   VM 的 NavigateRequested 事件驱动滚动定位（滚到底 / 选中并居中目标行）；
/// - 桌面监控：点击大图/按钮打开放大窗口。
/// </summary>
public partial class DodocoPage : UserControl
{
    private bool _hooked;
    private ScrollViewer? _realtimeScroller;
    private ScrollViewer? _browserScroller;
    private static readonly LevelToBrushConverter LevelBrush = new();
    private Brush _dimBrush = Brushes.Gray;
    private Brush _waterBrush = Brushes.LightSkyBlue;

    public DodocoPage()
    {
        InitializeComponent();
        RealtimeLog.Document = new FlowDocument { PagePadding = new Thickness(0) };
        // 时间选择框弹窗里点「现在/完成」= 点「按时间搜索」
        StartTimePicker.Submitted += (_, _) =>
            (DataContext as DodocoViewModel)?.Browser.SearchTimeRangeCommand.Execute(null);
        EndTimePicker.Submitted += (_, _) =>
            (DataContext as DodocoViewModel)?.Browser.SearchTimeRangeCommand.Execute(null);
        // DataContext 由 MainWindow 在 XAML 中绑定注入（DodocoViewModel），Loaded 时挂渲染/滚动事件
        Loaded += (_, _) => HookViewEvents();
    }

    private void HookViewEvents()
    {
        if (_hooked || DataContext is not DodocoViewModel vm) return;
        _dimBrush = TryFindResource("Dim") as Brush ?? _dimBrush;
        _waterBrush = TryFindResource("Water") as Brush ?? _waterBrush;
        vm.VisibleEntries.CollectionChanged += OnVisibleEntriesChanged;
        RebuildRealtimeDocument(vm);
        vm.Browser.NavigateRequested += OnBrowserNavigate;

        RealtimeLog.ApplyTemplate();
        _realtimeScroller = FindScrollViewer(RealtimeLog);
        if (_realtimeScroller != null) _realtimeScroller.ScrollChanged += RealtimeLog_ScrollChanged;
        BrowserList.ApplyTemplate();
        _browserScroller = FindScrollViewer(BrowserList);
        _hooked = true;
    }

    // ========== 实时日志：RichTextBox 渲染 ==========

    /// <summary>VM 可见集合变化 → 段落增删。约定：新增只发生在尾部、裁剪只发生在头部、筛选/切来源是 Reset。</summary>
    private void OnVisibleEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        var doc = RealtimeLog.Document;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Reset:
                RebuildRealtimeDocument(vm);
                break;
            case NotifyCollectionChangedAction.Add:
                if (e.NewItems != null)
                    foreach (var item in e.NewItems)
                        if (item is LogEntry entry) doc.Blocks.Add(RenderEntry(entry));
                FollowTailIfNeeded(vm);
                break;
            case NotifyCollectionChangedAction.Remove:
                // 环形缓冲超容从头部裁剪（RemoveAt(0)），同步摘掉开头段落
                var removeCount = e.OldItems?.Count ?? 0;
                for (var i = 0; i < removeCount && doc.Blocks.FirstBlock != null; i++)
                    doc.Blocks.Remove(doc.Blocks.FirstBlock);
                break;
        }
    }

    private void RebuildRealtimeDocument(DodocoViewModel vm)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var entry in vm.VisibleEntries)
            doc.Blocks.Add(RenderEntry(entry));
        RealtimeLog.Document = doc;
        FollowTailIfNeeded(vm);
    }

    /// <summary>渲染一条日志为段落：时间（暗）+ 级别 + [实例]（水色）+ 来源与消息（按级别上色）。</summary>
    private Paragraph RenderEntry(LogEntry entry)
    {
        var levelBrush = LevelBrush.Convert(entry.Level, typeof(Brush), null, CultureInfo.InvariantCulture) as Brush
                         ?? Brushes.White;
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 1) };
        p.Inlines.Add(new Run($"{entry.Time:HH:mm:ss.fff} ") { Foreground = _dimBrush });
        p.Inlines.Add(new Run($"{entry.Level} ") { Foreground = levelBrush, FontWeight = FontWeights.SemiBold });
        p.Inlines.Add(new Run($"[{entry.Instance ?? "未知"}] ") { Foreground = _waterBrush });
        p.Inlines.Add(new Run($"{entry.Source}  {entry.Message}") { Foreground = levelBrush });
        return p;
    }

    private void FollowTailIfNeeded(DodocoViewModel vm)
    {
        if (vm.AutoFollow) RealtimeLog.ScrollToEnd();
    }

    /// <summary>实时日志滚动：新内容到达且跟尾开启时滚到底；用户上翻暂停跟尾，滚回底部恢复。</summary>
    private void RealtimeLog_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        var sv = e.OriginalSource as ScrollViewer ?? _realtimeScroller;
        if (sv == null) return;

        if (e.ExtentHeightChange > 0)
        {
            if (vm.AutoFollow) RealtimeLog.ScrollToEnd();
        }
        else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
        {
            var atBottom = sv.VerticalOffset >= sv.ScrollableHeight - 2;
            if (vm.AutoFollow != atBottom) vm.AutoFollow = atBottom;
        }
    }

    // ========== 日志浏览：虚拟化 ListBox 全量视图（整文件在内存，自由滚动）+ 精确定位 ==========

    /// <summary>VM 定位请求：null=滚到末尾看最新；否则滚动+选中+居中目标行（项级滚动，VerticalOffset 以项为单位）。</summary>
    private void OnBrowserNavigate(int? lineIndex)
    {
        if (BrowserList.Items.Count == 0) return;
        if (lineIndex is not { } idx)
        {
            BrowserList.ScrollIntoView(BrowserList.Items[^1]);
            return;
        }
        if (idx < 0 || idx >= BrowserList.Items.Count) return;
        var item = BrowserList.Items[idx];
        BrowserList.ScrollIntoView(item);
        BrowserList.SelectedItem = item;
        // ScrollIntoView 只保证可见；布局完成后把目标行挪到视口中部（刚整载完 ViewportHeight 可能未就绪，推迟一拍）
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Background, () =>
        {
            var sv = _browserScroller ??= FindScrollViewer(BrowserList);
            if (sv != null && sv.ViewportHeight > 0)
                sv.ScrollToVerticalOffset(Math.Max(0, idx - sv.ViewportHeight / 2));
        });
        BrowserList.Focus();
    }

    /// <summary>查看区 Ctrl+C 复制选中行原文（一行一条）。</summary>
    private void BrowserList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        CopySelectedOnCtrlC(list, e, item => ((LogLineItem)item).Text);
    }

    // ========== 实时日志：回到最新 ==========

    /// <summary>「⇩ 最新」按钮：恢复自动跟尾并滚到末尾。</summary>
    private void ScrollToLatest_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        vm.AutoFollow = true;
        RealtimeLog.ScrollToEnd();
    }

    // ========== 搜索结果：复制 ==========
    // 结果行是纯 TextBlock（高亮渲染用），行选中交给 ListBox 默认行为；
    // Ctrl+C 走列表级 PreviewKeyDown 复制整行。

    private void SearchResults_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ListBox list) return;
        CopySelectedOnCtrlC(list, e, item => ((SearchResultItem)item).FullText);
    }

    /// <summary>Ctrl+C 复制选中行的完整文本（一行一条）。</summary>
    private static void CopySelectedOnCtrlC(ListBox list, KeyEventArgs e, Func<object, string> format)
    {
        if (e.Key != Key.C || Keyboard.Modifiers != ModifierKeys.Control) return;
        if (list.SelectedItems.Count == 0) return;
        var sb = new StringBuilder();
        foreach (var item in list.SelectedItems) sb.AppendLine(format(item));
        try
        {
            Clipboard.SetText(sb.ToString().TrimEnd());
            e.Handled = true;
        }
        catch { /* 剪贴板被占用时静默 */ }
    }

    // ========== 桌面监控：点击放大 ==========

    private void MonitorImage_Click(object sender, MouseButtonEventArgs e) => OpenMonitorZoom();

    private void MonitorZoom_Click(object sender, RoutedEventArgs e) => OpenMonitorZoom();

    /// <summary>打开当前帧的放大窗口（滚轮缩放/双击适应；窗口内可再保存图片）。</summary>
    private void OpenMonitorZoom()
    {
        if (DataContext is not DodocoViewModel vm || vm.Monitor.CurrentImage == null) return;
        var win = new ImageZoomWindow(
            vm.Monitor.CurrentImage,
            vm.Monitor.CurrentJpegBytes,
            vm.Monitor.CurrentFrameInfo,
            vm.Monitor.SuggestedFrameFileName)
        { Owner = Window.GetWindow(this) };
        win.Show();
    }

    // ========== 可视树工具 ==========

    /// <summary>可视树查找控件内部的 ScrollViewer。</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found != null) return found;
        }
        return null;
    }
}
