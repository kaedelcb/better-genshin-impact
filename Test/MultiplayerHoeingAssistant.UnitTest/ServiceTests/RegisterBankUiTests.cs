using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[CollectionDefinition("RegisterBankWpf", DisableParallelization = true)]
public sealed class RegisterBankWpfCollection;

[Collection("RegisterBankWpf")]
public sealed class RegisterBankUiTests
{
    [Fact]
    public Task StartupCenterUsesCompactTenCellsAlignedWithTitle() => OnSta(async () =>
    {
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException());
        bank.Apply(new() { Available = true, Entries = Enumerable.Range(1, RegisterBank.Count)
            .Select(n => new RegisterEntry { Number = n, Value = n is 1 or 10 ? 1 : 0 }).ToList() });
        var page = new MistletoePage { DataContext = new RegisterContext(bank) };
        var window = new Window { Content = page, Width = 980, Height = 760, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(window);
        try
        {
            window.Show(); await Layout(window);
            var strip = Descendants<RegisterStripView>(page).Single(s => s.IsVisible);
            Assert.Same(bank, strip.DataContext); Assert.True(strip.IsCompact);
            var cells = Descendants<Button>(strip).Where(b => b.DataContext is RegisterCellViewModel).ToArray();
            Assert.Equal(10, cells.Length);
            foreach (var width in new[] { 980, 1180 })
            {
                window.Width = width; await Layout(window);
                Assert.All(cells, b => { Assert.Equal(18, b.ActualWidth); Assert.Equal(18, b.ActualHeight); });
                Assert.Equal(Color.FromRgb(232, 201, 109), Assert.IsType<SolidColorBrush>(cells[0].BorderBrush).Color);
                Assert.Equal(Color.FromRgb(81, 75, 105), Assert.IsType<SolidColorBrush>(cells[1].BorderBrush).Color);
                var title = Descendants<TextBlock>(page).Single(t => t.Text == "启动流程");
                var titleCenter = title.TransformToAncestor(window).Transform(new Point(0, title.ActualHeight / 2));
                var firstCell = cells[0].TransformToAncestor(window).Transform(new Point(0, 9));
                var lastCell = cells[9].TransformToAncestor(window).Transform(new Point(0, 9));
                Assert.Equal(firstCell.Y, lastCell.Y, 1); Assert.InRange(Math.Abs(titleCenter.Y - firstCell.Y), 0, 1);
                Assert.True(cells[9].TransformToAncestor(window).Transform(new Point(18, 0)).X < window.ActualWidth);
                strip.BringIntoView(); await Layout(window);
                Capture(page, "register-compact-startup-" + width);
                Capture((FrameworkElement)VisualTreeHelper.GetParent(strip), "register-compact-startup-header-" + width);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task BasicPlanUsesTheSameTenCellsAndClickSettingsPersistNameAndSelectedTime() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "register-ui-" + Guid.NewGuid().ToString("N"));
        using var service = new RegisterBankService(Path.Combine(root, "registers.json"), startTimer: false);
        var bank = new RegisterBankViewModel(change => Task.FromResult(service.Configure(change)));
        bank.Apply(service.GetSnapshot());
        var host = new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => true, () => null);
        var panel = new TaskCenterPanelViewModel(host, autoRefresh: false);
        var basic = new BasicTaskCenterView { DataContext = panel };
        // Preserve the real ancestor binding used by BasicTaskCenterView, without starting a client or BGI.
        var page = new MistletoePage { DataContext = new RegisterContext(bank), Content = basic };
        var window = new Window { Content = page, Width = 980, Height = 760, ShowInTaskbar = false,
            ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(window);
        try
        {
            window.Show(); await Layout(window);
            var strip = Descendants<RegisterStripView>(basic).Single();
            Assert.Same(bank, strip.DataContext);
            var buttons = Descendants<Button>(strip).Where(b => b.DataContext is RegisterCellViewModel).ToArray();
            Assert.Equal(10, buttons.Length);
            Assert.All(buttons, b => { Assert.Equal(18, b.ActualWidth); Assert.Equal(18, b.ActualHeight); Assert.Equal("0", b.Content); });
            var first = buttons[0].TransformToAncestor(window).Transform(new Point());
            var last = buttons[9].TransformToAncestor(window).Transform(new Point());
            Assert.Equal(first.Y, last.Y, 1);
            var header = (Grid)basic.FindName("CurrentPlanHeader");
            var bounds = header.TransformToAncestor(window).TransformBounds(new Rect(header.RenderSize));
            var title = (TextBlock)basic.FindName("CurrentPlanTitleText");
            var titleBounds = title.TransformToAncestor(window).TransformBounds(new Rect(title.RenderSize));
            Assert.True(strip.IsCompact); Assert.True(first.X >= titleBounds.Right);
            Assert.InRange(first.Y, bounds.Top, bounds.Bottom - 18);
            var resetAll = Assert.IsType<Button>(strip.FindName("ResetAllButton"));
            var resetOrigin = resetAll.TransformToAncestor(window).Transform(new Point());
            Assert.True(resetOrigin.X + resetAll.ActualWidth <= bounds.Right + 1);
            Assert.Equal(first.Y, resetOrigin.Y, 1);
            Assert.DoesNotContain(Descendants<ComboBox>(basic), combo =>
                BindingOperations.GetBinding(combo, System.Windows.Controls.Primitives.Selector.SelectedIndexProperty)
                    ?.Path?.Path?.Contains("TaskCenterModeIndex") == true);

            var dialog = new RegisterSettingsWindow(bank, bank.Cells[0].Entry, strip) { Owner = window };
            var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            dialog.Loaded += async (_, _) =>
            {
                try
                {
                    await Layout(dialog);
                    var name = Descendants<TextBox>(dialog).Single(t => AutomationProperties.GetName(t) == "寄存器名称");
                    name.Text = "日志命中标记";
                    Combo(dialog, "重置规则").SelectedIndex = 2;
                    await Layout(dialog);
                    Assert.True(Combo(dialog, "延时重置秒数").IsVisible);
                    Assert.All(Descendants<ComboBox>(dialog), c => Assert.False(c.IsEditable));
                    Combo(dialog, "延时重置天数").SelectedItem = 1;
                    Combo(dialog, "延时重置小时").SelectedItem = 2;
                    Combo(dialog, "延时重置分钟").SelectedItem = 3;
                    Combo(dialog, "延时重置秒数").SelectedItem = 4;
                    await Layout(dialog);
                    ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    if (dialog.DataContext is RegisterSettingsViewModel { Error.Length: > 0 } failed)
                        throw new InvalidOperationException(failed.Error);
                    completed.TrySetResult();
                }
                catch (Exception ex) { completed.TrySetException(ex); dialog.Close(); }
            };
            Assert.True(dialog.ShowDialog());
            await completed.Task;
            var saved = service.GetSnapshot().Entries[0];
            Assert.Equal("日志命中标记", saved.Name);
            Assert.Equal(RegisterBank.AfterChange, saved.ResetMode);
            Assert.Equal(93784, saved.DelaySeconds);
            Assert.Contains("日志命中标记", bank.Cells[0].ToolTip);

            service.Set(new() { Values = Enumerable.Range(0, 10).Select(i => (int?)(i == 0 || i == 9 ? 1 : 0)).ToList() });
            bank.Apply(service.GetSnapshot()); await Layout(window);
            Assert.Equal("1", buttons[0].Content); Assert.Equal("1", buttons[9].Content);
            Assert.Contains("日志命中标记", AutomationProperties.GetName(buttons[0]));
            Assert.All(buttons, b => { Assert.Equal(18, b.ActualWidth); Assert.Equal(18, b.ActualHeight); });
            Capture(page, "register-compact-basic-980");
        }
        finally
        {
            window.Close(); panel.StopAutoRefresh(); await host.ShutdownAsync();
            service.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public void SelectionEditorKeepsNumbersWhenNamesChangeAndRejectsUnavailableRemoteState()
    {
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException());
        bank.Apply(new() { Available = true, Entries = Enumerable.Range(1, 10).Select(n => new RegisterEntry { Number = n }).ToList() });
        var settings = new RegisterWriteSettings();
        var row = new RegisterSelectionRowViewModel(bank.Cells[9], settings.Values, 9, false, () => { });
        row.SelectedIndex = 2;
        var snapshot = new RegisterBankSnapshot { Available = true,
            Entries = Enumerable.Range(1, 10).Select(n => new RegisterEntry { Number = n, Name = n == 10 ? "第十位名称" : "" }).ToList() };
        bank.Apply(snapshot);
        Assert.Equal(1, settings.Values[9]); Assert.Contains("第十位名称", row.Cell.Label);
        bank.Apply(null);
        Assert.False(row.Cell.Available); Assert.Equal("?", row.Cell.ValueText);
        Assert.Equal(1, settings.Values[9]);
    }

    private static void Capture(FrameworkElement visual, string name)
    {
        var folder = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var bounds = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(36, 32, 63)), null, bounds);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(Path.Combine(folder, name + ".png")); png.Save(output);
    }

    private static ComboBox Combo(DependencyObject root, string name)
        => Descendants<ComboBox>(root).Single(c => AutomationProperties.GetName(c) == name);
    private static async Task Layout(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
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
                catch (Exception ex) { done.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
        Assert.True(thread.Join(TimeSpan.FromSeconds(3)));
    }
    public sealed class RegisterContext(RegisterBankViewModel bank)
    {
        public RegisterBankViewModel Registers => bank;
        public int SelectedTabIndex => 0;
        public int TaskCenterModeIndex { get; set; }
    }
}
