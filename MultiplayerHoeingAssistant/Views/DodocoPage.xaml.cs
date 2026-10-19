using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
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
    private int _renderVersion;
    private bool _rendering;
    private bool _renderDirty;
    private bool _syncQueued;
    private bool _restoringScroll;
    private int _scrollVersion;
    private readonly List<LogEntry> _renderedEntries = new();

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

    private void AssistLogRoom_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        var owner = Window.GetWindow(this);
        var dialog = new RemoteLogRoomWindow(vm.Browser) { Owner = owner };
        if (owner != null) dialog.Resources.MergedDictionaries.Add(owner.Resources);
        dialog.ShowDialog();
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

    /// <summary>实时变化合帧增量同步；只有筛选/切来源等显式 Reset 才重建文档。</summary>
    private void OnVisibleEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is not DodocoViewModel vm) return;
        if (e.Action == NotifyCollectionChangedAction.Reset && !vm.VisibleEntries.IsLiveRefresh)
        {
            RebuildRealtimeDocument(vm);
            return;
        }
        if (_rendering) { _renderDirty = true; return; }
        QueueRealtimeSync(vm);
    }

    private void QueueRealtimeSync(DodocoViewModel vm)
    {
        if (_syncQueued) return;
        _syncQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _syncQueued = false;
            if (_rendering) { _renderDirty = true; return; }
            SyncRealtimeDocument(vm);
        });
    }

    /// <summary>保留窗口重叠部分的段落和选区，只摘掉过期头部、分批追加新尾部。</summary>
    private void SyncRealtimeDocument(DodocoViewModel vm)
    {
        var version = ++_renderVersion;
        var entries = vm.VisibleEntries.ToArray();
        var first = entries.Length == 0 ? -1
            : _renderedEntries.FindIndex(entry => ReferenceEquals(entry, entries[0]));
        var retained = first < 0 ? 0 : _renderedEntries.Count - first;
        if (retained > entries.Length || Enumerable.Range(0, retained)
                .Any(i => !ReferenceEquals(_renderedEntries[first + i], entries[i])))
            retained = 0;
        var removeCount = _renderedEntries.Count - retained;
        var anchor = CaptureRealtimeScrollAnchor(vm);
        var doc = RealtimeLog.Document;
        _renderDirty = false;
        _rendering = true;
        RealtimeLog.BeginChange();
        try
        {
            for (var i = 0; i < removeCount; i++) doc.Blocks.Remove(doc.Blocks.FirstBlock!);
            _renderedEntries.RemoveRange(0, removeCount);
        }
        finally { RealtimeLog.EndChange(); }
        RenderRealtimeChunk(vm, doc, entries, retained, version, anchor);
    }

    private void RebuildRealtimeDocument(DodocoViewModel vm)
    {
        var version = ++_renderVersion; // 切来源/改筛选时取消旧批次，防止旧日志混进新来源
        var entries = vm.VisibleEntries.ToArray();
        var doc = new FlowDocument { PagePadding = new Thickness(0) };
        RealtimeLog.Document = doc;
        _renderedEntries.Clear();
        _renderDirty = false;
        _rendering = entries.Length > 0;
        vm.LogLoadProgress = entries.Length == 0 ? 100 : 0;
        vm.IsLogLoading = _rendering;
        if (_rendering)
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => RenderRealtimeChunk(vm, doc, entries, 0, version));
    }

    /// <summary>每轮只排版少量段落，让 WPF 在批次之间处理输入、绘制进度和新日志。</summary>
    private void RenderRealtimeChunk(DodocoViewModel vm, FlowDocument doc, LogEntry[] entries, int start, int version,
        (TextPointer Position, double Top, int ScrollVersion)? anchor = null)
    {
        if (version != _renderVersion) return;
        var end = Math.Min(start + 12, entries.Length);
        RealtimeLog.BeginChange();
        try
        {
            for (var i = start; i < end; i++)
            {
                doc.Blocks.Add(RenderEntry(entries[i]));
                _renderedEntries.Add(entries[i]);
            }
        }
        finally { RealtimeLog.EndChange(); }
        if (vm.IsLogLoading) vm.LogLoadProgress = entries.Length == 0 ? 100 : end * 100 / entries.Length;
        if (end < entries.Length)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background,
                () => RenderRealtimeChunk(vm, doc, entries, end, version, anchor));
            return;
        }
        _rendering = false;
        vm.IsLogLoading = false;
        FollowTailIfNeeded(vm);
        RestoreRealtimeScrollAnchor(vm, anchor, version);
        // 初次排版期间到达的新日志也只增量追平，不能再次清文档、重启加载。
        // 先让本轮布局和 Loaded 优先级的阅读位置恢复完成，再追平积压日志。
        if (_renderDirty) QueueRealtimeSync(vm);
    }

    private (TextPointer Position, double Top, int ScrollVersion)? CaptureRealtimeScrollAnchor(DodocoViewModel vm)
    {
        if (vm.AutoFollow || !CanReadRealtimeGeometry()) return null;
        var position = RealtimeLog.GetPositionFromPoint(new Point(4, 4), snapToText: true);
        if (position == null) return null;
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        return rect.IsEmpty ? null : (position, rect.Top, _scrollVersion);
    }

    private bool CanReadRealtimeGeometry()
    {
        if (!RealtimeLog.IsLoaded || !RealtimeLog.IsVisible
            || !RealtimeLog.IsMeasureValid || !RealtimeLog.IsArrangeValid
            || !RealtimeLog.Document.ContentStart.HasValidLayout) return false;
        // Document 或模板重建后，新文本视图可能尚未接入视觉树。
        // GetPositionFromPoint 会先 TransformToDescendant，不能用它来试探布局是否就绪。
        var host = RealtimeLog.Template?.FindName("PART_ContentHost", RealtimeLog);
        Visual? scope = host switch
        {
            ScrollViewer { Content: Visual content } => content,
            Decorator { Child: Visual child } => child,
            _ => null
        };
        return scope is not null && RealtimeLog.IsAncestorOf(scope);
    }

    private void RestoreRealtimeScrollAnchor(DodocoViewModel vm,
        (TextPointer Position, double Top, int ScrollVersion)? anchor, int version)
    {
        if (anchor is not { } saved) return;
        bool IsCurrent() => version == _renderVersion && !vm.AutoFollow
            && saved.ScrollVersion == _scrollVersion && RealtimeLog.IsLoaded && RealtimeLog.IsVisible
            && saved.Position.IsInSameDocument(RealtimeLog.Document.ContentStart);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!IsCurrent()) return;
            _restoringScroll = true;
            try
            {
                RealtimeLog.UpdateLayout();
                if (!IsCurrent() || !CanReadRealtimeGeometry() || !saved.Position.HasValidLayout) return;
                var rect = saved.Position.GetCharacterRect(LogicalDirection.Forward);
                if (!IsCurrent()) return;
                if (!rect.IsEmpty)
                    RealtimeLog.ScrollToVerticalOffset(RealtimeLog.VerticalOffset + rect.Top - saved.Top);
                RealtimeLog.UpdateLayout();
            }
            finally { _restoringScroll = false; }
        });
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
        if (_restoringScroll || DataContext is not DodocoViewModel vm) return;
        var sv = e.OriginalSource as ScrollViewer ?? _realtimeScroller;
        if (sv == null) return;

        if (e.ExtentHeightChange != 0)
        {
            if (vm.AutoFollow) RealtimeLog.ScrollToEnd();
        }
        else if (e.VerticalChange != 0 && e.ViewportHeightChange == 0)
        {
            _scrollVersion++;
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

    private void MonitorTile_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MonitorTile tile } || DataContext is not DodocoViewModel vm) return;
        vm.Monitor.SelectedMonitor = vm.Monitor.MonitorOptions.FirstOrDefault(option => option.Key == tile.Key);
    }

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
