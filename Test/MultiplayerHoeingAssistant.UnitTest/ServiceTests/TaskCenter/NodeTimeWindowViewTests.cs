using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Globalization;
using System.Xml.Linq;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>隔离计划目录中的真实 WPF 入口：选择、确认、保存、总开关与实时显示。</summary>
[Collection("TaskCenterHeavyE2E")]
public sealed class NodeTimeWindowViewTests
{
    [Fact]
    public Task RowPopup_SelectsRangeWithoutTyping_AndPersistsOnlyAfterApply() => OnSta(async () =>
    {
        var host = CreateHost();
        host.SaveFlow(Document(), null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var view = new BasicTaskCenterView { DataContext = panel };
        var window = TestWindow(view);
        try
        {
            window.Show();
            await Layout(window);
            panel.StopAutoRefresh();
            Assert.False(panel.Editing!.NodeTimeWindowEnabled);
            Assert.Null(Saved(host).Nodes[0].TimeWindow);
            Assert.Contains("全天", panel.ResourceNodes.Single().TimeWindowDisplay);
            Assert.False(panel.ResourceNodes.Single().IsOutsideTimeWindow);
            Snapshot(window, "01-disabled-all-day");

            Click(TimeButton(view));
            var popup = (Popup)view.FindName("NodeTimeWindowPopup");
            await Until(window, () => popup.IsOpen);
            var start = (TimeOfDayPicker)view.FindName("NodeTimeWindowStartPicker");
            var end = (TimeOfDayPicker)view.FindName("NodeTimeWindowEndPicker");
            Assert.False(start.ShowSeconds);
            Assert.False(end.ShowSeconds);
            Assert.True(start.CommitOnSubmit);
            Assert.True(end.CommitOnSubmit);
            Assert.Empty(Descendants<TextBox>(popup.Child));

            // Choosing a column is provisional; closing a picker or the editor does not save it.
            var initial = start.Value;
            await OpenPicker(start, window);
            ((ListBox)start.FindName("HourList")).SelectedIndex = 18;
            await Layout(window);
            Assert.Equal(initial, start.Value);
            Assert.Null(Saved(host).Nodes[0].TimeWindow);
            ((ToggleButton)start.FindName("DropButton")).IsChecked = false;
            popup.IsOpen = false;

            Click(TimeButton(view));
            await Until(window, () => popup.IsOpen);
            await Select(start, 22, 15, window, popup);
            await Select(end, 6, 45, window, popup);
            Assert.Null(Saved(host).Nodes[0].TimeWindow);
            RenderSnapshot((FrameworkElement)popup.Child, "02-edit-range-popup");
            Click((Button)view.FindName("ApplyNodeTimeWindowButton"));
            await Until(window, () => Saved(host).Nodes[0].TimeWindow?.End == "06:45");
            Assert.False(popup.IsOpen);
            Assert.Equal("22:15", Saved(host).Nodes[0].TimeWindow!.Start);
            Assert.Contains("22:15", panel.ResourceNodes.Single().TimeWindowDisplay);
            Assert.Contains("06:45", panel.ResourceNodes.Single().TimeWindowDisplay);
            Assert.False(Saved(host).NodeTimeWindowEnabled);
            await Layout(window);
            Snapshot(window, "03-disabled-saved-range");

            var toggle = (ToggleButton)view.FindName("NodeTimeWindowToggle");
            toggle.IsChecked = true;
            await Until(window, () => Saved(host).NodeTimeWindowEnabled);
            panel.RefreshTimeWindows(new DateTimeOffset(2026, 10, 25, 22, 30, 0, TimeSpan.FromHours(8)));
            await Layout(window);
            Snapshot(window, "04-enabled-inside-range");
            panel.RefreshTimeWindows(new DateTimeOffset(2026, 10, 25, 12, 0, 0, TimeSpan.FromHours(8)));
            await Layout(window);
            Snapshot(window, "05-enabled-outside-range");

            // The next popup must bind the replacement edit model created by automatic saving.
            Click(TimeButton(view));
            await Until(window, () => popup.IsOpen);
            Assert.Equal("22:15", start.Value);
            Assert.Equal("06:45", end.Value);
            Click((Button)view.FindName("ClearNodeTimeWindowButton"));
            await Until(window, () => Saved(host).Nodes[0].TimeWindow is null);
            Assert.Contains("全天", panel.ResourceNodes.Single().TimeWindowDisplay);
            Assert.True(Saved(host).NodeTimeWindowEnabled);
            toggle.IsChecked = false;
            await Until(window, () => !Saved(host).NodeTimeWindowEnabled);
        }
        finally
        {
            panel.StopAutoRefresh();
            window.Close();
            await host.ShutdownAsync();
        }
    });

    [Fact]
    public Task VisibleRow_ReevaluatesTimeWithoutReopeningOrSaving_AndRemainsEditable() => OnSta(async () =>
    {
        var host = CreateHost();
        var document = Document();
        document.NodeTimeWindowEnabled = true;
        document.Nodes[0].TimeWindow = new() { Start = "08:00", End = "09:00" };
        host.SaveFlow(document, null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var view = new BasicTaskCenterView { DataContext = panel };
        var window = TestWindow(view);
        try
        {
            window.Show();
            await Layout(window);
            panel.StopAutoRefresh(); // Injected times stay deterministic while the same visible row is inspected.
            var editing = panel.Editing!;
            var node = panel.ResourceNodes.Single();
            var list = (ListBox)view.FindName("ResourceList");
            var item = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            var before = host.Workflows.LoadSnapshot("wf-node-time-window").Revision;
            var clock = new DateTimeOffset(2026, 10, 25, 7, 59, 59, TimeSpan.FromHours(8));

            panel.RefreshTimeWindows(clock);
            await Layout(window);
            Assert.True(node.IsOutsideTimeWindow);
            var outsideText = node.TimeWindowStatusText;
            var outsideOpacity = RowOpacity(item);
            var outsideBackground = RowBackground(item);
            Assert.True(outsideOpacity <= 0.45, "时段外名称应明显降亮，同时保留时段标记的可读性。");
            AssertNeutralOutsideBackground(outsideBackground);
            var badge = OutsideBadge(item);
            Assert.True(badge.IsVisible);
            Assert.Contains(Descendants<TextBlock>(badge), text => text.Text == "时段外");
            Assert.True(TimeButton(view).IsEnabled);
            Click(TimeButton(view));
            var popup = (Popup)view.FindName("NodeTimeWindowPopup");
            await Until(window, () => popup.IsOpen);
            Assert.True(popup.IsOpen);
            popup.IsOpen = false;

            panel.RefreshTimeWindows(clock.AddSeconds(1));
            await Layout(window);
            Assert.Same(editing, panel.Editing);
            Assert.Same(node, panel.ResourceNodes.Single());
            Assert.False(node.IsOutsideTimeWindow);
            Assert.NotEqual(outsideText, node.TimeWindowStatusText);
            Assert.Equal(1.0, RowOpacity(item));
            Assert.Equal(Visibility.Collapsed, badge.Visibility);
            Assert.True(ColorDistance(outsideBackground, RowBackground(item)) >= 30,
                "进入时段后应恢复原有行背景，不能只改变一个近似色的小圆点。");

            panel.RefreshTimeWindows(clock.AddHours(1).AddSeconds(1));
            await Layout(window);
            Assert.True(node.IsOutsideTimeWindow);
            Assert.Equal(outsideOpacity, RowOpacity(item));
            Assert.True(RowOpacity(item) <= 0.45, "到达结束时刻后应再次明显降低任务名称亮度。");
            Assert.True(badge.IsVisible);
            Assert.Equal(outsideBackground, RowBackground(item));
            Assert.False(editing.HasUnsavedChanges);
            await Task.Delay(450);
            await Layout(window);
            Assert.Equal(before, host.Workflows.LoadSnapshot("wf-node-time-window").Revision);
            Assert.False(editing.HasUnsavedChanges);
            Assert.Empty(host.Runs.List());
        }
        finally
        {
            panel.StopAutoRefresh();
            window.Close();
            await host.ShutdownAsync();
        }
    });

    [Fact]
    public Task RangeAndGlobalSwitch_UndoAndCopyPreserveIndependentSettingsAndUnknownFields() => OnSta(async () =>
    {
        var host = CreateHost();
        var document = Document();
        document.Nodes[0].TimeWindow = new()
        {
            Start = "22:15", End = "06:45",
            ExtensionData = new() { ["futureOption"] = JsonSerializer.SerializeToElement("preserved") },
        };
        host.SaveFlow(document, null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        try
        {
            var editing = panel.Editing!;
            var node = editing.Nodes.Single();
            node.SetTimeWindow("08:00", "09:30");
            Assert.True(editing.UndoSchedule());
            Assert.Equal("22:15", node.TimeWindowStart);
            Assert.Equal("06:45", node.TimeWindowEnd);
            Assert.Equal("preserved", node.TimeWindow!.ExtensionData!["futureOption"].GetString());
            editing.NodeTimeWindowEnabled = true;
            Assert.True(editing.UndoSchedule());
            Assert.False(editing.NodeTimeWindowEnabled);

            editing.SelectedNode = node;
            editing.DuplicateSelectedNodeCommand.Execute(null);
            var copy = editing.Nodes[1];
            Assert.NotEqual(node.NodeId, copy.NodeId);
            Assert.Equal(node.TimeWindowStart, copy.TimeWindowStart);
            Assert.Equal(node.TimeWindowEnd, copy.TimeWindowEnd);
            Assert.Equal("preserved", copy.TimeWindow!.ExtensionData!["futureOption"].GetString());
            copy.SetTimeWindow("10:00", "11:00");
            Assert.Equal("22:15", node.TimeWindowStart);
            Assert.Equal("06:45", node.TimeWindowEnd);
        }
        finally
        {
            panel.StopAutoRefresh();
            await host.ShutdownAsync();
        }
    });

    [Fact]
    public Task SixTaskPlan_At1120And1180And1380_KeepsHeaderActionsAndNamesReadable() => OnSta(async () =>
    {
        var host = CreateHost();
        var document = Document();
        document.Name = "日常计划";
        document.NodeTimeWindowEnabled = true;
        string[] names = ["清体", "狗粮", "单机-小怪-锄地", "好感任务", "采集", "至冬材料"];
        WorkflowTimeWindow?[] windows =
        [
            new() { Start = "08:00", End = "11:00" },
            new() { Start = "12:00", End = "18:00" },
            new() { Start = "22:00", End = "06:00" },
            null,
            new() { Start = "09:00", End = "10:00" },
            new() { Start = "08:00", End = "20:00" },
        ];
        document.Nodes = names.Select((name, index) => new WorkflowNode
        {
            NodeId = "n-preview-" + index, Kind = "resource.oneDragonConfig",
            Ref = new() { Config = name }, TimeWindow = windows[index],
            Strategies = index == 3
                ? [new() { Kind = "prerequisite.account", Params = new() { ["uid"] = JsonSerializer.SerializeToElement("123456789") } }]
                : [],
            ExtensionData = index == 5
                ? new() { ["planDisabled"] = JsonSerializer.SerializeToElement(true) } : null,
        }).ToList();
        host.SaveFlow(document, null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var view = new BasicTaskCenterView { DataContext = panel };
        var window = TestWindow(view);
        window.SizeToContent = SizeToContent.Width;
        try
        {
            window.Show();
            await Layout(window);
            panel.StopAutoRefresh();
            panel.RefreshTimeWindows(new DateTimeOffset(2026, 10, 25, 10, 0, 0, TimeSpan.FromHours(8)));
            Assert.Equal(6, panel.ResourceNodes.Count);
            Assert.Contains(panel.ResourceNodes, node => node.IsOutsideTimeWindow);
            Assert.Contains(panel.ResourceNodes, node => !node.IsOutsideTimeWindow);
            Assert.Equal("已停用", panel.ResourceNodes[5].TimeWindowStatusText);

            foreach (var width in new[] { 1120, 1180, 1380 })
            {
                view.Width = width;
                await Layout(window);
                Assert.InRange(view.ActualWidth, width - 0.5, width + 0.5);
                Snapshot(window, $"08-header-switch-six-tasks-{width}");
                AssertCompactHeaderAndActions(view);
                var list = (ListBox)view.FindName("ResourceList");
                foreach (var node in panel.ResourceNodes)
                {
                    var item = Assert.IsType<ListBoxItem>(list.ItemContainerGenerator.ContainerFromItem(node));
                    var row = Descendants<Grid>(item).Single(grid => ReferenceEquals(grid.Tag, node));
                    Assert.True(row.ActualHeight <= 40.5,
                        $"{width}px 下 {node.DisplayName} 的行高应保留原有紧凑布局：{row.ActualHeight:F1}px。");
                    var cells = row.Children.OfType<FrameworkElement>().OrderBy(Grid.GetColumn).ToArray();
                    Assert.Equal(6, cells.Length);
                    var bounds = cells.Select(cell => Bounds(cell, list)).ToArray();
                    for (var column = 1; column < bounds.Length; column++)
                        Assert.True(bounds[column - 1].Right <= bounds[column].Left + 0.5,
                            $"{width}px 下 {node.DisplayName} 的相邻列发生重叠。");
                    var timeButton = Descendants<Button>(item).Single(button => button.Name == "NodeTimeWindowButton");
                    var timeBounds = Bounds(timeButton, list);
                    Assert.True(timeBounds.Left >= 0 && timeBounds.Right <= list.ActualWidth + 0.5,
                        $"{width}px 下 {node.DisplayName} 的时段按钮超出列表可视宽度。");
                    Assert.True(timeButton.ActualHeight <= 32.5,
                        "时段按钮高度应与日期、账号单元格保持一致。");
                    var rangeText = Descendants<TextBlock>(timeButton).Single(text => text.Text == node.TimeWindowDisplay);
                    Assert.Equal(TextWrapping.NoWrap, rangeText.TextWrapping);
                    Assert.True(rangeText.DesiredSize.Width <= rangeText.ActualWidth + 0.5,
                        "时间段范围应在同一行完整显示。");
                    Assert.DoesNotContain(Descendants<TextBlock>(timeButton), text =>
                        BindingOperations.GetBindingExpression(text, TextBlock.TextProperty)?.ParentBinding.Path?.Path
                            == nameof(NodeEditVm.TimeWindowStatusText));
                    var nameText = Descendants<TextBlock>(cells[1]).Single(text => text.Name == "ResourceNameText");
                    Assert.Equal(node.DisplayName, nameText.Text);
                    Assert.True(nameText.ActualWidth >= FullTextWidth(nameText, node.DisplayName) - 0.5,
                        $"{width}px 下配置单名称被截断：{node.DisplayName}；可用={nameText.ActualWidth:F1}px，全文={FullTextWidth(nameText, node.DisplayName):F1}px。");
                    var settings = Descendants<Button>(item).Single(button => button.Name == "ResourceSettingsButton");
                    Assert.True(settings.ActualWidth <= 24.5,
                        $"设置按钮不应被继承的最小宽度撑大：{settings.ActualWidth:F1}px。");
                    Assert.True(row.ColumnDefinitions[4].ActualWidth <= 96.5, "UID/账号列应让出多余占位。");
                    var accountText = Descendants<TextBlock>(cells[4]).Single(text => text.Text == node.AccountDisplay);
                    Assert.True(accountText.ActualWidth >= FullTextWidth(accountText, node.AccountDisplay) - 0.5,
                        "当前账号值应完整显示。");
                    var accountChrome = Assert.IsType<Border>(VisualTreeHelper.GetParent(accountText));
                    var accountContentWidth = accountChrome.ActualWidth - accountChrome.Padding.Left - accountChrome.Padding.Right
                        - accountChrome.BorderThickness.Left - accountChrome.BorderThickness.Right;
                    Assert.True(accountContentWidth >= FullTextWidth(accountText, "123456789") - 0.5,
                        "账号内容区应容纳同字体下的九位UID，即使当前展示值按原规则进行了掩码。");
                    var badge = OutsideBadge(item);
                    Assert.Equal(node.IsOutsideTimeWindow, badge.IsVisible);
                    if (node.IsOutsideTimeWindow)
                    {
                        AssertNeutralOutsideBackground(RowBackground(item));
                        Assert.True(RowOpacity(item) <= 0.45);
                        Assert.True(timeButton.IsEnabled, "时段外仍应允许编辑时段。");
                    }
                }

                var toggle = (ToggleButton)view.FindName("NodeTimeWindowToggle");
                Assert.True(toggle.IsVisible);
                AssertWithinView(toggle, view);
            }
        }
        finally
        {
            panel.StopAutoRefresh();
            window.Close();
            await host.ShutdownAsync();
        }
    });

    private static Rect Bounds(FrameworkElement element, Visual ancestor)
        => element.TransformToAncestor(ancestor).TransformBounds(new Rect(element.RenderSize));

    private static void AssertWithinView(FrameworkElement element, FrameworkElement view)
    {
        var bounds = Bounds(element, view);
        Assert.True(bounds.Width > 0 && bounds.Height > 0);
        Assert.True(bounds.Left >= 0 && bounds.Top >= 0
            && bounds.Right <= view.ActualWidth + 0.5 && bounds.Bottom <= view.ActualHeight + 0.5,
            "时段总开关应完整留在计划表可视范围内。");
    }

    private static TaskCenterHost CreateHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "node-time-window-ui-" + Guid.NewGuid().ToString("N"));
        return new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
    }

    private static WorkflowDocument Document() => new()
    {
        WorkflowId = "wf-node-time-window", Name = "时间段计划",
        Nodes = [new() { NodeId = "n-resource", Kind = "resource.oneDragonConfig", Ref = new() { Config = "日常任务" } }],
    };

    private static WorkflowDocument Saved(TaskCenterHost host)
        => host.Workflows.LoadSnapshot("wf-node-time-window").Document;

    private static Button TimeButton(BasicTaskCenterView view)
        => Descendants<Button>((ListBox)view.FindName("ResourceList"))
            .Single(button => button.Name == "NodeTimeWindowButton");

    private static double RowOpacity(ListBoxItem item)
    {
        var name = Descendants<TextBlock>(item).Single(text => text.Name == "ResourceNameText");
        double opacity = 1;
        for (DependencyObject? current = name; current is not null && !ReferenceEquals(current, item);
             current = VisualTreeHelper.GetParent(current))
            if (current is UIElement element) opacity *= element.Opacity;
        return opacity;
    }

    private static FrameworkElement OutsideBadge(ListBoxItem item)
        => Descendants<FrameworkElement>(item).Single(element => element.Name == "OutsideTimeWindowBadge");

    private static Color RowBackground(ListBoxItem item)
    {
        var chrome = Assert.IsType<Border>(item.Template.FindName("Row", item));
        return Assert.IsType<SolidColorBrush>(chrome.Background).Color;
    }

    private static void AssertNeutralOutsideBackground(Color color)
    {
        var channels = new[] { (int)color.R, color.G, color.B };
        Assert.True(color.A >= 224 && channels.Max() - channels.Min() <= 16,
            $"时段外应使用清晰的中性灰背景，而非近似正常紫色或低透明叠层：{color}。");
    }

    private static int ColorDistance(Color left, Color right)
        => Math.Abs(left.R - right.R) + Math.Abs(left.G - right.G) + Math.Abs(left.B - right.B);

    private static double FullTextWidth(TextBlock text, string value)
        => new FormattedText(value, CultureInfo.InvariantCulture, text.FlowDirection,
            new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, text.FontStretch),
            text.FontSize, Brushes.White, VisualTreeHelper.GetDpi(text).PixelsPerDip).WidthIncludingTrailingWhitespace;

    private static void AssertCompactHeaderAndActions(BasicTaskCenterView view)
    {
        var header = Assert.IsType<Grid>(view.FindName("ResourceTableHeader"));
        var actions = Assert.IsType<ScrollViewer>(view.FindName("PlanActionsScrollViewer"));
        var toggle = Assert.IsType<ToggleButton>(view.FindName("NodeTimeWindowToggle"));
        Assert.Contains(toggle, Descendants<ToggleButton>(header));
        Assert.DoesNotContain(toggle, Descendants<ToggleButton>(actions));
        var toggleBounds = Bounds(toggle, header);
        var timeColumnLeft = header.ColumnDefinitions.Take(3).Sum(column => column.ActualWidth);
        Assert.True(toggleBounds.Left >= timeColumnLeft - 0.5
            && toggleBounds.Right <= timeColumnLeft + header.ColumnDefinitions[3].ActualWidth + 0.5,
            "时段总开关应在执行时段标题列内。");
        Assert.True(header.ActualHeight <= 22.5, "表头应保持单行，不增加独立控制栏。");
        Assert.True(actions.ScrollableWidth <= 0.5,
            $"上方操作按钮不应横向溢出或需要滚动：{actions.ScrollableWidth:F1}px。");
        Assert.NotEqual(Visibility.Visible, actions.ComputedHorizontalScrollBarVisibility);
        string[] labels = ["+任务", "复制", "修改", "删除", "导入配置", "导出配置", "重置状态", "启动单任务", "⏵⏵ 连续一条龙"];
        var buttons = Descendants<Button>(actions).Where(button => labels.Contains(button.Content as string)).ToArray();
        Assert.Equal(labels, buttons.Select(button => button.Content as string));
        foreach (var button in buttons)
        {
            var bounds = Bounds(button, actions);
            Assert.True(button.IsVisible && bounds.Left >= -0.5 && bounds.Right <= actions.ViewportWidth + 0.5,
                $"操作按钮应完整显示：{button.Content}；边界={bounds}；视口宽度={actions.ViewportWidth:F1}px。");
        }
    }

    private static Window TestWindow(FrameworkElement content, [CallerFilePath] string sourcePath = "")
    {
        var window = new Window
        {
            Width = 1380, Height = 800, FontSize = 13,
            WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000,
            ShowInTaskbar = false, ShowActivated = false,
        };
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NODE_TIME_WINDOW_SNAPSHOT_DIR")))
        {
            window.Content = content;
            return window;
        }

        // The production theme lives in these owners, rather than a separate Themes dictionary.
        // Extract only their resource markup; constructing the real shell would start unrelated services.
        var sourceRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "../../../.."));
        var application = XDocument.Load(Path.Combine(sourceRoot, "MultiplayerHoeingAssistant", "App.xaml"));
        var shell = XDocument.Load(Path.Combine(sourceRoot, "MultiplayerHoeingAssistant", "Views", "MainWindow.xaml"));
        window.Resources.MergedDictionaries.Add(OwnerResources(application, "Application.Resources"));
        window.Resources.MergedDictionaries.Add(OwnerResources(shell, "Window.Resources"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var backgroundMarkup = shell.Root!.Element(presentation + "Window.Background")!.Elements().Single();
        var background = (Brush)XamlReader.Parse(backgroundMarkup.ToString());
        window.Background = background;
        window.Content = new Border { Background = background, Child = content };
        return window;
    }

    private static ResourceDictionary OwnerResources(XDocument document, string elementName)
    {
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = document.Root!.Element(presentation + elementName)!;
        var namespaces = document.Root.Attributes().Where(attribute => attribute.IsNamespaceDeclaration)
            .Select(attribute =>
            {
                var value = attribute.Value;
                if (value.StartsWith("clr-namespace:", StringComparison.Ordinal)
                    && !value.Contains(";assembly=", StringComparison.OrdinalIgnoreCase))
                    value += ";assembly=MultiplayerHoeingAssistant";
                return new XAttribute(attribute.Name, value);
            });
        var dictionary = new XElement(presentation + "ResourceDictionary",
            namespaces, resources.Elements().Select(element => new XElement(element)));
        return (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
    }

    private static void Snapshot(Window window, string name)
        => RenderSnapshot((FrameworkElement)window.Content, name);

    private static void RenderSnapshot(FrameworkElement element, string name)
    {
        var directory = Environment.GetEnvironmentVariable("NODE_TIME_WINDOW_SNAPSHOT_DIR");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(Path.GetFullPath(directory));
        element.UpdateLayout();
        var width = Math.Max(1, (int)Math.Ceiling(element.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(element.ActualHeight));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(directory, name + ".png"));
        encoder.Save(stream);
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private static async Task OpenPicker(TimeOfDayPicker picker, Window window)
    {
        ((ToggleButton)picker.FindName("DropButton")).IsChecked = true;
        await Until(window, () => ((Popup)picker.FindName("PickerPopup")).IsOpen);
        await Layout(window);
    }

    private static async Task Select(TimeOfDayPicker picker, int hour, int minute, Window window, Popup parent)
    {
        var before = picker.Value;
        await OpenPicker(picker, window);
        Assert.Equal(Visibility.Collapsed, ((StackPanel)picker.FindName("SecondsColumn")).Visibility);
        ((ListBox)picker.FindName("HourList")).SelectedIndex = hour;
        ((ListBox)picker.FindName("MinuteList")).SelectedIndex = minute;
        Assert.Equal(before, picker.Value);
        Assert.True(parent.IsOpen);
        Click(Descendants<Button>(((Popup)picker.FindName("PickerPopup")).Child)
            .Single(button => Equals(button.Content, "完成")));
        await Layout(window);
        Assert.Equal($"{hour:D2}:{minute:D2}", picker.Value);
        Assert.False(((Popup)picker.FindName("PickerPopup")).IsOpen);
        Assert.True(parent.IsOpen);
    }

    private static async Task Layout(Window window)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static async Task Until(Window window, Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(6);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < end, "隔离时间段编辑或自动保存未完成。");
            await Layout(window);
            await Task.Delay(10);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }

    private static async Task OnSta(Func<Task> action)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.InvokeAsync(async () =>
            {
                try { await action(); done.TrySetResult(); }
                catch (Exception error) { done.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)), "隔离 WPF 调度线程未退出。");
    }
}
