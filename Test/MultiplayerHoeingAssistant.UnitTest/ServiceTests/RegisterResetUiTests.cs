using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

[Collection("RegisterBankWpf")]
public sealed class RegisterResetUiTests
{
    [Fact]
    public Task RealSettingsButtonsResetOneAndAllWithoutSavingDraftOrClosingWindow() => OnSta(async () =>
    {
        var root = Path.Combine(Path.GetTempPath(), "register-reset-ui-" + Guid.NewGuid().ToString("N"));
        using var service = new RegisterBankService(Path.Combine(root, "registers.json"), startTimer: false);
        var initial = service.GetSnapshot().Entries[0];
        service.Configure(new() { Number = 1, Name = "原名称", ResetMode = RegisterBank.AfterChange,
            DelaySeconds = 3600, ExpectedSettingsRevision = initial.SettingsRevision });
        service.Set(new() { Values = Enumerable.Repeat<int?>(1, RegisterBank.Count).ToList() });
        var original = service.GetSnapshot().Entries[0]; var requests = new List<int?>();
        var bank = new RegisterBankViewModel(c => Task.FromResult(service.Configure(c)), n =>
        { requests.Add(n); return Task.FromResult(service.Reset(n)); });
        bank.Apply(service.GetSnapshot());
        var strip = new RegisterStripView { DataContext = bank };
        var owner = new Window { Content = strip, Width = 620, Height = 160, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(owner);
        RegisterSettingsWindow? dialog = null;
        try
        {
            owner.Show(); await Layout(owner);
            dialog = new RegisterSettingsWindow(bank, bank.Cells[0].Entry, strip) { Owner = owner };
            dialog.Show(); await Layout(dialog);
            var vm = Assert.IsType<RegisterSettingsViewModel>(dialog.DataContext);
            var oldStatus = vm.CurrentState;
            var name = Descendants<TextBox>(dialog).Single(b => AutomationProperties.GetName(b) == "寄存器名称");
            name.Text = "未保存的草稿";
            var rule = Descendants<ComboBox>(dialog).Single(c => AutomationProperties.GetName(c) == "重置规则");
            rule.SelectedIndex = 1;
            var reset = Assert.IsType<Button>(dialog.FindName("ResetButton"));
            var resetAll = Assert.IsType<Button>(dialog.FindName("ResetAllButton"));
            Assert.True(reset.IsEnabled); Assert.True(resetAll.IsEnabled);
            Click(reset); await Layout(dialog);
            Assert.True(dialog.IsVisible);
            Assert.Equal(new int?[] { 1 }, requests);
            Assert.Equal(0, service.GetSnapshot().Entries[0].Value);
            Assert.All(service.GetSnapshot().Entries.Skip(1), e => Assert.Equal(1, e.Value));
            Assert.Equal("0", bank.Cells[0].ValueText);
            Assert.NotEqual(oldStatus, vm.CurrentState); Assert.Contains("0", vm.CurrentState);
            Assert.Equal("未保存的草稿", name.Text); Assert.Equal(1, rule.SelectedIndex);
            Assert.Equal(original.Name, service.GetSnapshot().Entries[0].Name);
            Assert.Equal(original.ResetMode, service.GetSnapshot().Entries[0].ResetMode);
            Assert.Equal(original.SettingsRevision, service.GetSnapshot().Entries[0].SettingsRevision);
            Click(resetAll); await Layout(dialog);
            Assert.True(dialog.IsVisible);
            Assert.Equal(new int?[] { 1, null }, requests);
            Assert.All(bank.Cells, c => Assert.Equal("0", c.ValueText));
            Assert.All(service.GetSnapshot().Entries, e => Assert.Equal(0, e.Value));
            Assert.Equal(original.ResetMode, service.GetSnapshot().Entries[0].ResetMode);
            Capture(dialog, "register-manual-reset-settings");
            Click(Assert.IsType<Button>(dialog.FindName("CancelButton"))); await Layout(owner);
            Assert.Equal("原名称", service.GetSnapshot().Entries[0].Name);
        }
        finally
        {
            if (dialog?.IsVisible == true) dialog.Close(); owner.Close(); service.Dispose();
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    });

    [Fact]
    public Task ResetButtonsWaitForAcknowledgmentAndRepeatedClicksDoNotSendAgain() => OnSta(async () =>
    {
        var pending = new TaskCompletionSource<RegisterBankSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = 0;
        var snapshot = new RegisterBankSnapshot { Available = true,
            Entries = Enumerable.Range(1, RegisterBank.Count).Select(n => new RegisterEntry { Number = n, Value = 1 }).ToList() };
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), _ => { requests++; return pending.Task; });
        bank.Apply(snapshot);
        var strip = new RegisterStripView { DataContext = bank };
        var owner = new Window { Content = strip, Width = 620, Height = 160, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(owner);
        RegisterSettingsWindow? dialog = null;
        try
        {
            owner.Show(); await Layout(owner);
            dialog = new RegisterSettingsWindow(bank, bank.Cells[0].Entry, strip) { Owner = owner };
            dialog.Show(); await Layout(dialog);
            var reset = Assert.IsType<Button>(dialog.FindName("ResetButton"));
            var all = Assert.IsType<Button>(dialog.FindName("ResetAllButton"));
            Click(reset); await Layout(dialog);
            Assert.True(bank.IsBusy);
            Assert.False(reset.IsEnabled); Assert.False(all.IsEnabled);
            Assert.False(Assert.IsType<Button>(dialog.FindName("SaveButton")).IsEnabled);
            // Raising Click bypasses WPF hit testing; the handler still must reject a duplicate.
            Click(reset); Click(all); Assert.Equal(1, requests);
            Assert.Equal("1", bank.Cells[0].ValueText);
            pending.SetResult(new RegisterBankSnapshot { Available = true,
                Entries = Enumerable.Range(1, RegisterBank.Count).Select(n => new RegisterEntry { Number = n, Value = n == 1 ? 0 : 1 }).ToList() });
            for (var i = 0; i < 20 && bank.IsBusy; i++) { await Task.Delay(10); await Layout(dialog); }
            Assert.False(bank.IsBusy); Assert.True(reset.IsEnabled); Assert.Equal("0", bank.Cells[0].ValueText);
            Assert.True(dialog.IsVisible);
        }
        finally
        {
            if (!pending.Task.IsCompleted) pending.TrySetException(new InvalidOperationException("test ended"));
            if (dialog?.IsVisible == true) { await Layout(dialog); dialog.Close(); }
            owner.Close();
        }
    });

    [Fact]
    public Task StripHasTenUnchangedCellsAndAnAllResetButton() => OnSta(async () =>
    {
        int? target = 99; var calls = 0;
        var bank = new RegisterBankViewModel(_ => throw new InvalidOperationException(), n =>
        {
            target = n; calls++;
            return Task.FromResult(new RegisterBankSnapshot { Available = true,
                Entries = Enumerable.Range(1, RegisterBank.Count).Select(x => new RegisterEntry { Number = x }).ToList() });
        });
        bank.Apply(new() { Available = true, Entries = Enumerable.Range(1, RegisterBank.Count).Select(n => new RegisterEntry { Number = n, Value = 1 }).ToList() });
        var strip = new RegisterStripView { DataContext = bank, IsCompact = true };
        var window = new Window { Content = strip, Width = 440, Height = 160, ShowInTaskbar = false, ShowActivated = false, Left = -20000, Top = -20000 };
        FormalWpfTheme.Apply(window);
        try
        {
            window.Show(); await Layout(window);
            var cells = Descendants<Button>(strip).Where(b => b.DataContext is RegisterCellViewModel).ToArray();
            Assert.Equal(10, cells.Length);
            Assert.All(cells, b => { Assert.Equal(18, b.ActualWidth); Assert.Equal(18, b.ActualHeight); });
            var all = Assert.IsType<Button>(strip.FindName("ResetAllButton"));
            Assert.True(all.IsVisible); Assert.True(all.IsEnabled);
            Click(all); await Layout(window);
            Assert.Equal(1, calls); Assert.Null(target);
            Assert.All(bank.Cells, c => Assert.Equal("0", c.ValueText));
            Capture(window, "register-manual-reset-strip");
            bank.Apply(null); await Layout(window);
            Assert.False(all.IsEnabled); Assert.All(cells, b => Assert.False(b.IsEnabled));
        }
        finally { window.Close(); }
    });

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static async Task Layout(Window window) { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T match) yield return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static void Capture(Window window, string name)
    {
        var folder = Environment.GetEnvironmentVariable("BGI_COMPOSITION_UI_SCREENSHOTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var visual = window.Content as FrameworkElement ?? window;
        var bmp = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            var rect = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight);
            context.DrawRectangle(window.Background, null, rect);
            context.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, rect);
        }
        bmp.Render(drawing); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bmp));
        using var output = File.Create(Path.Combine(folder, name + ".png")); png.Save(output);
    }
    private static Task OnSta(Func<Task> action)
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
        return Await();
        async Task Await() { await done.Task.WaitAsync(TimeSpan.FromSeconds(45)); Assert.True(thread.Join(TimeSpan.FromSeconds(3))); }
    }
}
