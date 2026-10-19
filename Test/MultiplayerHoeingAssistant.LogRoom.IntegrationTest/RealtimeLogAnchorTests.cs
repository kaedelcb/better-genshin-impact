using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

public sealed class RealtimeLogAnchorTests
{
    [Fact]
    public async Task AnchorSkipsDisconnectedView_WhileReadingAndLiveRefreshKeepWorking()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                MainWindow? window = null;
                try
                {
                    var config = new AssistConfig
                    {
                        DisclaimerAccepted = true, ObserverMode = true,
                        PlayerUid = "isolated-monitor", ServerUrl = "http://127.0.0.1:1",
                        BgiPath = Path.Combine(AssistantDataDirectory.Root, "isolated-bgi.exe")
                    };
                    new AssistConfigManager().Save(config);
                    var main = new MainViewModel();
                    typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                        .SetValue(main, config);
                    main.CurrentPage = AppPage.Dodoco;
                    window = new MainWindow(main)
                    {
                        Width = 1180, Height = 820, ShowActivated = false, ShowInTaskbar = false,
                        WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000
                    };
                    var errors = new List<Exception>();
                    app.DispatcherUnhandledException += (_, args) =>
                    {
                        errors.Add(args.Exception);
                        args.Handled = true;
                    };
                    window.Show();
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var page = All(window).OfType<DodocoPage>().Single();
                    var log = Assert.IsType<RichTextBox>(page.FindName("RealtimeLog"));
                    var vm = window.Dodoco;
                    const string uid = "isolated-executor";
                    main.Members.Add(new MemberViewModel { PlayerUid = uid, PlayerName = "隔离执行端", Online = true });
                    vm.SelectedSource = vm.LogSources.Single(source => source.Key == uid);
                    var append = typeof(DodocoViewModel).GetMethod("AddRemoteEntries", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var capture = typeof(DodocoPage).GetMethod("CaptureRealtimeScrollAnchor", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var sequence = 0;
                    List<LogEntry> Entries(int count) => Enumerable.Range(0, count).Select(_ =>
                        new LogEntry(DateTime.Now, LogLevels.Inf, "isolated", "anchor-probe",
                            $"line-{sequence++:D5} " + new string('测', 32), null, sequence, "isolated.log")).ToList();
                    void Append(int count) => append.Invoke(vm, [uid, Entries(count)]);
                    async Task Settled()
                    {
                        var deadline = DateTime.UtcNow.AddSeconds(15);
                        while (vm.IsLogLoading || !Matches(log, vm))
                        {
                            if (errors.Count > 0) throw new AggregateException(errors);
                            Assert.True(DateTime.UtcNow < deadline, "实时日志未在限定时间追平当前来源。");
                            await Task.Delay(15);
                        }
                        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                        Assert.Empty(errors);
                    }

                    Append(500);
                    await Settled();
                    vm.AutoFollow = false;
                    vm.ClearViewCommand.Execute(null);
                    // 真实“清空”入口同步重建 Document；在下一次布局之前检查这一合法过渡状态。
                    var scope = Assert.IsAssignableFrom<Visual>(typeof(System.Windows.Controls.Primitives.TextBoxBase)
                        .GetProperty("RenderScope", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(log));
                    Assert.False(log.IsAncestorOf(scope));
                    Assert.False(vm.AutoFollow);
                    // 原实现会在此从 GetPositionFromPoint 抛出“Visual 不是此 Visual 的后代”。
                    Assert.Null(capture.Invoke(page, [vm]));

                    Append(500);
                    await Settled();
                    var document = log.Document;
                    var reading = document.Blocks.Cast<Paragraph>().ElementAt(250);
                    log.Selection.Select(reading.ContentStart, reading.ContentEnd);
                    var selectedText = log.Selection.Text;
                    log.ScrollToVerticalOffset(log.ExtentHeight / 2);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    vm.AutoFollow = false;
                    var readingTop = reading.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top;
                    for (var i = 0; i < 4; i++)
                    {
                        Append(12);
                        await Settled();
                        Assert.Same(document, log.Document);
                        Assert.False(vm.AutoFollow);
                        Assert.Equal(selectedText, log.Selection.Text);
                        Assert.Contains(reading, document.Blocks.Cast<Paragraph>());
                        Assert.InRange(reading.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top,
                            readingTop - 2, readingTop + 2);
                    }

                    Append(24);
                    await app.Dispatcher.InvokeAsync(() =>
                    {
                        Assert.True((bool)typeof(DodocoPage).GetField("_rendering", BindingFlags.Instance | BindingFlags.NonPublic)!
                            .GetValue(page)!, "尾批必须在前一批尚未完成排版时到达。");
                        Append(12);
                    }, DispatcherPriority.Background);
                    await Settled();
                    Assert.Equal(selectedText, log.Selection.Text);
                    Assert.InRange(reading.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top,
                        readingTop - 2, readingTop + 2);

                    // 让筛选、切页和小/大批次按 Dispatcher 自然时序交错，不主动 UpdateLayout。
                    for (var i = 0; i < 12; i++)
                    {
                        if (i % 3 == 0) { vm.ShowInf = false; vm.ShowInf = true; }
                        main.CurrentPage = i % 4 == 0 ? AppPage.Home : AppPage.Dodoco;
                        vm.SelectedTabIndex = i % 4 == 1 ? 2 : 0;
                        Append(i % 3 == 0 ? 1 : i % 3 == 1 ? 8 : 24);
                        await Task.Delay(20);
                    }
                    main.CurrentPage = AppPage.Dodoco;
                    vm.SelectedTabIndex = 0;
                    await Settled();
                    vm.SelectedSource = vm.LogSources.Single(source => source.Key == DodocoViewModel.LocalSourceKey);
                    await Settled();
                    Assert.Empty(vm.VisibleEntries);
                    vm.SelectedSource = vm.LogSources.Single(source => source.Key == uid);
                    await Settled();
                    Assert.Equal(500, vm.VisibleEntries.Count);
                    Assert.Empty(errors);
                    done.TrySetResult();
                }
                catch (Exception error) { done.TrySetException(error); }
                finally
                {
                    try { window?.Dodoco.Dispose(); window?.Hide(); } catch { }
                    app.Shutdown();
                }
            }));
            app.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(75));
    }

    private static bool Matches(RichTextBox log, DodocoViewModel vm)
    {
        var paragraphs = log.Document.Blocks.Cast<Paragraph>().ToArray();
        return paragraphs.Length == vm.VisibleEntries.Count && paragraphs.Select((paragraph, index) =>
            new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Contains(vm.VisibleEntries[index].Message)).All(value => value);
    }

    private static IEnumerable<DependencyObject> All(DependencyObject root)
    {
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in All(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
