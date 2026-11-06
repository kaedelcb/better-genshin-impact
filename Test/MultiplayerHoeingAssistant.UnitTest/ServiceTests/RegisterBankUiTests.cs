using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
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
            var title = Descendants<TextBlock>(basic).Single(t => t.Text == "计划表");
            var titleTop = title.TransformToAncestor(window).Transform(new Point());
            Assert.InRange(Math.Abs(titleTop.Y + title.ActualHeight / 2 - first.Y - 9), 0, 1);
            Assert.True(strip.IsCompact);
            var pane = (Border)basic.FindName("PlanListPane");
            var paneOrigin = pane.TransformToAncestor(window).Transform(new Point());
            Assert.True(first.X >= paneOrigin.X + pane.Padding.Left);
            Assert.True(last.X + 18 <= paneOrigin.X + pane.ActualWidth - pane.Padding.Right);
            var resetAll = Assert.IsType<Button>(strip.FindName("ResetAllButton"));
            var resetOrigin = resetAll.TransformToAncestor(window).Transform(new Point());
            Assert.True(resetOrigin.X + resetAll.ActualWidth <= paneOrigin.X + pane.ActualWidth - pane.Padding.Right);
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
        public int TaskCenterModeIndex { get; set; }
    }
}
