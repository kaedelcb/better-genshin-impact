using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.ViewModels;

namespace MultiplayerHoeingAssistant.Views;

public partial class RoomOrchestrationPage : UserControl
{
    private readonly DispatcherTimer _refreshTimer;
    private readonly Dictionary<(string Id, int Occurrence), CardContainer> _cards = new();
    private string? _lastProjection;
    private Window? _owner;
    private Point? _dragStart;
    private double _dragStartOffset;
    private bool _dragging;
    private bool _refreshing;
    private bool _editing;
    private bool _updatingMonitorTargets;
    private MainViewModel? _monitorViewModel;

    private sealed record CardContainer(ContentPresenter Element, string Signature);

    public RoomOrchestrationPage()
    {
        InitializeComponent();
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(750) };
        _refreshTimer.Tick += (_, _) => RefreshPresentation();
        PresentationRoot.DataContext = RoomOrchestrationPresentation.Empty;
        Loaded += (_, _) =>
        {
            _owner = Window.GetWindow(this);
            if (_owner is not null) _owner.Deactivated += OwnerDeactivated;
            UpdateRefreshTimer();
        };
        Unloaded += (_, _) =>
        {
            StopDrag();
            _refreshTimer.Stop();
            StopMonitor();
            if (_owner is not null) _owner.Deactivated -= OwnerDeactivated;
            _owner = null;
        };
        IsVisibleChanged += (_, _) => UpdateRefreshTimer();
        DataContextChanged += (_, _) =>
        {
            StopMonitor();
            _lastProjection = null;
            UpdateRefreshTimer();
        };
        RoomGraphScrollViewer.LostKeyboardFocus += (_, _) =>
        {
            if (!RoomGraphScrollViewer.IsKeyboardFocusWithin) StopDrag();
        };
    }

    private void UpdateRefreshTimer()
    {
        if (IsLoaded && IsVisible)
        {
            RefreshPresentation();
            _refreshTimer.Start();
        }
        else
        {
            _refreshTimer.Stop();
            StopMonitor();
            StopDrag();
        }
    }

    private void StopMonitor()
    {
        _monitorViewModel?.StopRoomOrchestrationMonitor();
        _monitorViewModel = null;
    }

    private void UpdateMonitorTargets(MainViewModel vm)
    {
        if (!vm.IsObserverMode)
        {
            StopMonitor();
            MonitorTargetRow.Visibility = Visibility.Collapsed;
            return;
        }
        _monitorViewModel = vm;
        _ = vm.RefreshRoomOrchestrationMonitorAsync();
        var choices = vm.OrchestrationMonitorTargets;
        var selected = vm.SelectedOrchestrationMonitorTarget;
        _updatingMonitorTargets = true;
        try
        {
            if (MonitorTargetSelector.ItemsSource is not MainViewModel.OrchestrationMonitorTarget[] old || !old.SequenceEqual(choices))
                MonitorTargetSelector.ItemsSource = choices;
            MonitorTargetSelector.SelectedItem = choices.FirstOrDefault(t => selected is not null && t.Uid == selected.Uid
                && t.InstanceId == selected.InstanceId && t.MachineId == selected.MachineId);
            MonitorTargetRow.Visibility = choices.Length > 1 || choices.Length == 1 && MonitorTargetSelector.SelectedItem is null
                ? Visibility.Visible : Visibility.Collapsed;
        }
        finally { _updatingMonitorTargets = false; }
    }

    private void MonitorTargetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingMonitorTargets || DataContext is not MainViewModel vm
            || MonitorTargetSelector.SelectedItem is not MainViewModel.OrchestrationMonitorTarget target) return;
        vm.SelectOrchestrationMonitorTarget(target);
        RefreshPresentation();
    }

    internal void RefreshPresentation()
    {
        if (_refreshing || _editing || !IsVisible || _dragStart.HasValue || DataContext is not MainViewModel vm) return;
        _refreshing = true;
        try
        {
            UpdateMonitorTargets(vm);
            var presentation = vm.GetRoomOrchestrationPresentation();
            var signature = JsonSerializer.Serialize(presentation);
            if (signature == _lastProjection) return;

            // Reuse the ScrollViewer and unchanged node presenters. When text above the current
            // viewport grows, preserve the first visible room and its offset within that room.
            var oldOffset = RoomGraphScrollViewer.VerticalOffset;
            var anchor = CaptureScrollAnchor(oldOffset);
            PresentationRoot.DataContext = presentation;
            EmptyGraphText.Text = vm.IsObserverMode ? "正在等待所选执行端返回房间配置。不会显示监控端的本地任务配置。"
                : vm.Config is null ? "正在读取本机房间设置…" : "没有可展示的房间档案。可通过助手房间的「管理房间」添加。";
            EmptyGraphText.Visibility = presentation.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            var wanted = new HashSet<(string, int)>();
            var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
            var index = 1; // EmptyGraphText is the first, permanent child.
            foreach (var card in presentation.Cards)
            {
                var occurrence = occurrences.GetValueOrDefault(card.Id);
                occurrences[card.Id] = occurrence + 1;
                var key = (card.Id, occurrence);
                wanted.Add(key);
                var cardSignature = JsonSerializer.Serialize(card);
                if (!_cards.TryGetValue(key, out var container))
                {
                    var element = new ContentPresenter
                    {
                        Content = card, ContentTemplate = (DataTemplate)FindResource("OrchestrationRoom"),
                        HorizontalAlignment = HorizontalAlignment.Stretch, Focusable = false
                    };
                    container = new(element, cardSignature);
                    _cards.Add(key, container);
                }
                else if (container.Signature != cardSignature)
                {
                    container.Element.Content = card;
                    _cards[key] = container with { Signature = cardSignature };
                }
                var previousIndex = RoomNodesHost.Children.IndexOf(container.Element);
                if (previousIndex != index)
                {
                    if (previousIndex >= 0) RoomNodesHost.Children.RemoveAt(previousIndex);
                    RoomNodesHost.Children.Insert(index, container.Element);
                }
                index++;
            }
            foreach (var key in _cards.Keys.Where(k => !wanted.Contains(k)).ToArray())
            {
                RoomNodesHost.Children.Remove(_cards[key].Element);
                _cards.Remove(key);
            }
            // Never navigate to a newly executing node. Native vertical scrolling remains the
            // authority, including keyboard and touch scrolling; only correct a changed anchor.
            RoomGraphScrollViewer.UpdateLayout();
            var offset = oldOffset;
            if (anchor is { } saved && _cards.TryGetValue(saved.Key, out var anchoredCard))
                offset = anchoredCard.Element.TranslatePoint(new Point(), RoomNodesHost).Y + saved.OffsetInCard;
            offset = Math.Clamp(offset, 0, RoomGraphScrollViewer.ScrollableHeight);
            if (Math.Abs(offset - RoomGraphScrollViewer.VerticalOffset) > 0.5)
                RoomGraphScrollViewer.ScrollToVerticalOffset(offset);
            _lastProjection = signature;
        }
        catch (InvalidOperationException ex)
        {
            // A room profile may be replaced by a confirmed configuration update between reads.
            // Do not repaint it as a successful/empty execution; retry the actual snapshot.
            if (_lastProjection is null) vm.AddLog("任务编排暂未能读取房间状态：" + ex.Message);
        }
        finally { _refreshing = false; }
    }

    private ((string Id, int Occurrence) Key, double OffsetInCard)? CaptureScrollAnchor(double offset)
    {
        if (offset <= 0.5) return null;
        foreach (var entry in _cards)
        {
            var element = entry.Value.Element;
            if (!RoomNodesHost.Children.Contains(element) || element.ActualHeight <= 0) continue;
            var top = element.TranslatePoint(new Point(), RoomNodesHost).Y;
            if (top <= offset && offset < top + element.ActualHeight)
                return (entry.Key, offset - top);
        }
        return null;
    }

    // The main window has a normal minimum width; high-DPI work areas can be smaller. Reflow
    // metadata below the task list rather than shrinking text or creating horizontal scrolling.
    private void RoomContentLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Grid grid) ApplyRoomContentWidth(grid, grid.ActualWidth);
    }

    private void RoomContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Grid grid && e.WidthChanged) ApplyRoomContentWidth(grid, e.NewSize.Width);
    }

    private static void ApplyRoomContentWidth(Grid grid, double width)
    {
        if (width <= 0 || grid.Children.Count != 2 || grid.Children[0] is not StackPanel tasks
            || grid.Children[1] is not Border meta) return;
        var narrow = width < 495;
        Grid.SetColumnSpan(tasks, narrow ? 2 : 1);
        Grid.SetRow(meta, narrow ? 1 : 0);
        Grid.SetColumn(meta, narrow ? 0 : 1);
        Grid.SetColumnSpan(meta, narrow ? 2 : 1);
        meta.Margin = narrow ? new Thickness(0, 9, 0, 0) : new Thickness(11, 0, 0, 0);
        meta.Padding = narrow ? new Thickness(0, 8, 0, 0) : new Thickness(11, 0, 0, 0);
        meta.BorderThickness = narrow ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
    }

    private async void EditRoomClick(object sender, RoutedEventArgs e)
    {
        if (_editing || sender is not Button { DataContext: RoomOrchestrationCard card }
            || !card.CanEdit || DataContext is not MainViewModel vm || Window.GetWindow(this) is not Window owner) return;
        StopDrag();
        e.Handled = true;
        _editing = true;
        try { await vm.EditRoomOrchestrationAsync(card.Id, owner); }
        finally
        {
            _editing = false;
            RefreshPresentation();
        }
    }

    private void GraphMouseDown(object sender, MouseButtonEventArgs e)
    {
        StopDrag();
        if (RoomGraphScrollViewer.ScrollableHeight <= 0 || e.ChangedButton != MouseButton.Left
            || IsInteractiveDragOrigin(e.OriginalSource as DependencyObject, RoomGraphScrollViewer)) return;
        _dragStart = e.GetPosition(RoomGraphScrollViewer);
        _dragStartOffset = RoomGraphScrollViewer.VerticalOffset;
        // Do not capture or consume a click until the movement actually crosses the threshold.
    }

    private void GraphMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } start) return;
        if (e.LeftButton != MouseButtonState.Pressed || RoomGraphScrollViewer.ScrollableHeight <= 0)
        {
            StopDrag();
            return;
        }
        var delta = e.GetPosition(RoomGraphScrollViewer).Y - start.Y;
        if (!_dragging)
        {
            if (Math.Abs(delta) < Math.Max(6d, SystemParameters.MinimumVerticalDragDistance)) return;
            if (!Mouse.Capture(RoomGraphScrollViewer, CaptureMode.Element)) { StopDrag(); return; }
            _dragging = true;
            RoomGraphScrollViewer.Cursor = Cursors.ScrollNS;
        }
        RoomGraphScrollViewer.ScrollToVerticalOffset(Math.Clamp(_dragStartOffset - delta, 0, RoomGraphScrollViewer.ScrollableHeight));
        e.Handled = true;
    }

    private void GraphMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) e.Handled = true;
        StopDrag();
        RefreshPresentation();
    }

    private void GraphLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_dragging && !ReferenceEquals(Mouse.Captured, RoomGraphScrollViewer)) StopDrag();
    }

    private void GraphMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_dragging) StopDrag();
    }

    private void GraphPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_dragStart.HasValue) return;
        StopDrag();
        e.Handled = true;
        RefreshPresentation();
    }

    private void OwnerDeactivated(object? sender, EventArgs e) => StopDrag();

    private void StopDrag()
    {
        _dragStart = null;
        _dragging = false;
        if (ReferenceEquals(Mouse.Captured, RoomGraphScrollViewer)) Mouse.Capture(null);
        RoomGraphScrollViewer.ClearValue(CursorProperty);
    }

    internal static bool IsInteractiveDragOrigin(DependencyObject? origin, DependencyObject canvas)
    {
        // A button template can have a Path or a text Run as OriginalSource. Walk to the
        // owning interactive control, including the scrollbar's arrow/Thumb regions.
        for (var element = origin; element is not null && !ReferenceEquals(element, canvas); element = ParentOf(element))
        {
            if (element is ButtonBase or Selector or TextBoxBase or PasswordBox or ScrollBar or Thumb or Slider or Hyperlink)
                return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject element) => element switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element),
        ContentElement content => ContentOperations.GetParent(content) ?? (content as FrameworkContentElement)?.Parent,
        _ => LogicalTreeHelper.GetParent(element)
    };
}
