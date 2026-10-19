using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class IncidentRetentionTests
{
    [Fact]
    public void Settings_OldJsonUses50_AndCurrentRemainsACopy()
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Combine(fixture.Root, "dodoco_settings.json"),
            "{\"incidentBufferSeconds\":20,\"muteAll\":true}");

        var service = new DodocoSettingsService(fixture.Root);
        Assert.Equal(50, service.Current.IncidentRetentionCount);
        Assert.Equal(20, service.Current.IncidentBufferSeconds);
        Assert.True(service.Current.MuteAll);

        var copy = service.Current;
        copy.IncidentRetentionCount = 123;
        Assert.Equal(50, service.Current.IncidentRetentionCount);
    }

    [Theory]
    [InlineData(123)]
    [InlineData(0)]
    public void Settings_CustomCountAndUnlimitedPersistAcrossReload(int count)
    {
        using var fixture = new TestDirectory();
        var service = new DodocoSettingsService(fixture.Root);

        service.Update(settings => settings.IncidentRetentionCount = count);

        Assert.Equal(count, service.Current.IncidentRetentionCount);
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.Root, "dodoco_settings.json")));
        Assert.Equal(count, json.RootElement.GetProperty("incidentRetentionCount").GetInt32());
        Assert.Equal(count, new DodocoSettingsService(fixture.Root).Current.IncidentRetentionCount);
    }

    [Theory]
    [InlineData(-1, 50)]
    [InlineData(int.MinValue, 50)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void Settings_LoadNormalizesNegativeCount_WithoutChangingValidCounts(int stored, int expected)
    {
        using var fixture = new TestDirectory();
        File.WriteAllText(Path.Combine(fixture.Root, "dodoco_settings.json"),
            JsonSerializer.Serialize(new { incidentRetentionCount = stored }));

        Assert.Equal(expected, new DodocoSettingsService(fixture.Root).Current.IncidentRetentionCount);
    }

    [Fact]
    public void Prune_MixedLayoutsKeepsLatestCount_AndPreservesUnrelatedDirectoriesAndFiles()
    {
        using var fixture = new TestDirectory();
        var old = new[]
        {
            Incident(fixture.Root, "2026-10-16/20261016_120000_old"),
            Incident(fixture.Root, "2026-10-17/20261017_120000_old"),
            Incident(fixture.Root, "20261018_100000_flat"),
            Incident(fixture.Root, "2026-10-18/20261018_120000_layered"),
        };
        var recent = new[]
        {
            Incident(fixture.Root, "20261019_100000_flat"),
            Incident(fixture.Root, "2026-10-19/20261019_110000_layered"),
            Incident(fixture.Root, "2026-10-19/20261019_120000_layered"),
        };
        var unrelatedRoot = Path.Combine(fixture.Root, "notes");
        var unrelatedDate = Path.Combine(fixture.Root, "2026-10-17", "keep-me");
        Directory.CreateDirectory(unrelatedRoot);
        Directory.CreateDirectory(unrelatedDate);
        var rootFile = Path.Combine(fixture.Root, "README.txt");
        var dateFile = Path.Combine(fixture.Root, "2026-10-18", "README.txt");
        File.WriteAllText(rootFile, "keep root file");
        File.WriteAllText(dateFile, "keep date file");
        File.WriteAllText(Path.Combine(unrelatedDate, "notes.txt"), "keep unrelated directory");

        IncidentSnapshotService.PruneOldIncidents(fixture.Root, 3);

        Assert.All(old, path => Assert.False(Directory.Exists(path), path));
        Assert.All(recent, path => Assert.True(Directory.Exists(path), path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "2026-10-16")));
        Assert.True(Directory.Exists(unrelatedRoot));
        Assert.Equal("keep unrelated directory", File.ReadAllText(Path.Combine(unrelatedDate, "notes.txt")));
        Assert.Equal("keep root file", File.ReadAllText(rootFile));
        Assert.Equal("keep date file", File.ReadAllText(dateFile));
    }

    [Fact]
    public void Prune_ZeroPreservesAllEventsAndEmptyDateDirectories()
    {
        using var fixture = new TestDirectory();
        var events = new[]
        {
            Incident(fixture.Root, "20261017_120000_flat"),
            Incident(fixture.Root, "2026-10-18/20261018_120000_layered"),
            Incident(fixture.Root, "2026-10-19/20261019_120000_layered"),
        };
        var emptyDate = Path.Combine(fixture.Root, "2026-10-16");
        Directory.CreateDirectory(emptyDate);

        IncidentSnapshotService.PruneOldIncidents(fixture.Root, 0);

        Assert.All(events, path => Assert.True(File.Exists(Path.Combine(path, "trigger.json")), path));
        Assert.True(Directory.Exists(emptyDate));
    }

    [Theory]
    [InlineData(50)]
    [InlineData(-1)]
    public void Prune_DefaultAndInvalidCountsKeep50NewestEvents(int count)
    {
        using var fixture = new TestDirectory();
        Assert.Equal(50, new DodocoSettings().IncidentRetentionCount);
        var start = new DateTime(2026, 10, 19, 12, 0, 0);
        var events = Enumerable.Range(0, 52)
            .Select(index => Incident(fixture.Root, $"{start.AddSeconds(index):yyyyMMdd_HHmmss}_rule"))
            .ToArray();

        IncidentSnapshotService.PruneOldIncidents(fixture.Root, count);

        Assert.All(events.Take(2), path => Assert.False(Directory.Exists(path), path));
        Assert.All(events.Skip(2), path => Assert.True(Directory.Exists(path), path));
        Assert.Equal(50, Directory.GetDirectories(fixture.Root).Length);
    }

    [Fact]
    public void Prune_ActiveEventsDoNotConsumeCompletedEventAllowance()
    {
        using var fixture = new TestDirectory();
        var active = Incident(fixture.Root, "2026-10-17/20261017_120000_active");
        File.Delete(Path.Combine(active, "trigger.json"));
        File.WriteAllText(Path.Combine(active, "frame_-01.0.jpg"), "unfinished frame fixture");
        var old = Incident(fixture.Root, "20261018_120000_old");
        var newest = Incident(fixture.Root, "2026-10-19/20261019_120000_newest");

        IncidentSnapshotService.PruneOldIncidents(fixture.Root, 1,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { active });

        Assert.True(File.Exists(Path.Combine(active, "frame_-01.0.jpg")));
        Assert.True(Directory.Exists(newest));
        Assert.False(Directory.Exists(old));
    }

    // 单独在新 testhost 中运行；只创建设置弹窗，不启动主窗口、BGI 或房间连接。
    [Fact]
    public async Task SettingsWindow_RealShowEditRejectsInvalidCount_SavesAndCancels()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Application? app = null;
            try
            {
                Assert.Null(Application.Current);
                app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var settings = new DodocoSettings { IncidentRetentionCount = 50 };
                var saved = DriveDialog(app, settings, dialog =>
                {
                    var input = Assert.IsType<TextBox>(dialog.FindName("RetentionInput"));
                    var save = Assert.IsType<Button>(dialog.FindName("SaveButton"));
                    var error = Assert.IsType<TextBlock>(dialog.FindName("ErrorText"));
                    dialog.UpdateLayout();
                    Assert.Equal("50", input.Text);
                    AssertWithinContent(dialog, input);
                    AssertWithinContent(dialog, save);
                    AssertWithinContent(dialog, Assert.IsType<Button>(dialog.FindName("CancelButton")));
                    foreach (var invalid in new[] { "-1", "1.5", "", "2147483648" })
                    {
                        input.Text = invalid;
                        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Assert.True(dialog.IsVisible);
                        Assert.Null(dialog.Result);
                        Assert.False(string.IsNullOrWhiteSpace(error.Text));
                    }
                    input.Text = "123";
                    save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Assert.True(saved.HasValue);
                Assert.Equal(123, saved.Value.RetentionCount);
                Assert.Equal(settings.IncidentBufferSeconds, saved.Value.BufferSeconds);
                Assert.Equal(settings.IncidentCaptureIntervalSeconds, saved.Value.IntervalSeconds);
                Assert.Equal(settings.IncidentPreSeconds, saved.Value.PreSeconds);
                Assert.Equal(settings.IncidentPostSeconds, saved.Value.PostSeconds);

                var unlimited = DriveDialog(app, new DodocoSettings { IncidentRetentionCount = 0 }, dialog =>
                {
                    Assert.Equal("0", Assert.IsType<TextBox>(dialog.FindName("RetentionInput")).Text);
                    Assert.IsType<Button>(dialog.FindName("SaveButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Assert.True(unlimited.HasValue);
                Assert.Equal(0, unlimited.Value.RetentionCount);

                var cancelled = DriveDialog(app, new DodocoSettings { IncidentRetentionCount = 123 }, dialog =>
                {
                    var input = Assert.IsType<TextBox>(dialog.FindName("RetentionInput"));
                    Assert.Equal("123", input.Text);
                    input.Text = "1";
                    Assert.IsType<Button>(dialog.FindName("CancelButton"))
                        .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                });
                Assert.Null(cancelled);
                done.TrySetResult();
            }
            catch (Exception error) { done.TrySetException(error); }
            finally { app?.Shutdown(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(thread.Join(TimeSpan.FromSeconds(2)), "设置弹窗 STA 线程未退出。");
    }

    private static (int BufferSeconds, double IntervalSeconds, int PreSeconds, int PostSeconds, int RetentionCount)?
        DriveDialog(Application app, DodocoSettings settings, Action<IncidentSettingsWindow> action)
    {
        Exception? failure = null;
        var elapsed = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Normal, app.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        timer.Tick += (_, _) =>
        {
            var dialog = app.Windows.OfType<IncidentSettingsWindow>().SingleOrDefault();
            if (dialog is not { IsLoaded: true, IsVisible: true })
            {
                if (elapsed.Elapsed < TimeSpan.FromSeconds(5)) return;
                timer.Stop();
                failure = new TimeoutException("等待实际事发录像设置弹窗超时。");
                foreach (var window in app.Windows.OfType<Window>().ToArray()) window.Close();
                return;
            }
            timer.Stop();
            try { action(dialog); }
            catch (Exception error)
            {
                failure = error;
                if (dialog.IsVisible) dialog.Close();
            }
        };
        timer.Start();
        try
        {
            var result = IncidentSettingsWindow.ShowEdit(settings);
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }
        finally { timer.Stop(); }
    }

    private static string Incident(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "trigger.json"), "{}");
        return path;
    }

    private static void AssertWithinContent(Window window, FrameworkElement element)
    {
        var content = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
        Assert.True(element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0);
        var bounds = element.TransformToAncestor(content)
            .TransformBounds(new Rect(new Point(), element.RenderSize));
        Assert.True(bounds.Left >= -0.5 && bounds.Top >= -0.5
            && bounds.Right <= content.ActualWidth + 0.5 && bounds.Bottom <= content.ActualHeight + 0.5,
            $"设置控件超出窗口内容区：控件={bounds}，内容区={content.ActualWidth}x{content.ActualHeight}");
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "IncidentRetention-" + Guid.NewGuid().ToString("N"));
        public TestDirectory() => Directory.CreateDirectory(Root);
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
