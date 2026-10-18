using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Views;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

[Collection("TaskCenterPipeTransport")]
public sealed class ConfigTransferWindowIntegrationTests
{
    [Fact]
    public async Task RealWindow_ImportsReloadsAndKeepsEdits_ThenRestoresDiskAndMemoryOnStartupRegistrationFailure()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须给此独立测试进程指定新的 NEXUSBGI_DATA_ROOT");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            MainWindow? window = null;
            MainViewModel? main = null;
            var oldIpc = IpcClient.PipeNameOverrideForTest;
            var oldExt = BgiExternalClient.PipeNameOverrideForTest;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var unreachable = "config-transfer-no-bgi-" + Guid.NewGuid().ToString("N");
                IpcClient.PipeNameOverrideForTest = unreachable;
                BgiExternalClient.PipeNameOverrideForTest = unreachable;
                var app = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                Assert.True(app.Dispatcher.CheckAccess(), "窗口集成测试请单独在新测试进程运行");
                var root = AssistantDataDirectory.Root;
                Assert.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, Path.GetFullPath(LocalPeerSyncService.SharedDir));
                var fixture = Path.Combine(Path.GetTempPath(), "ConfigTransferWindow-" + Guid.NewGuid().ToString("N"));
                var source = Path.Combine(fixture, "source");
                var package = Path.Combine(fixture, "settings.zip");
                var failedPackage = Path.Combine(fixture, "registration-failure.zip");
                Seed(root, "local", "local-instance", 480, 140);
                Seed(source, "imported", "foreign-instance", 1920, 225);
                var exported = new ConfigTransferService(source).Export(package);
                Assert.True(exported.Success, exported.Message);
                var manager = new AssistConfigManager();
                main = new MainViewModel();
                Set(main, "_configManager", manager);
                Set(main, "_config", manager.Load());
                Set(main, "_cacheManager", new MemberConfigCacheManager());
                var registrationCalls = new List<bool>();
                main.AutoStartupApply = (enabled, _, allowElevation) =>
                {
                    Assert.False(allowElevation);
                    registrationCalls.Add(enabled);
                    return new(!enabled, enabled ? "模拟注册失败" : "模拟恢复原登记");
                };
                window = new MainWindow(main) { ShowActivated = false, Left = -20000, Top = -20000 };
                app.MainWindow = window;
                main.CurrentPage = AppPage.Settings;
                window.Show();
                window.UpdateLayout();
                Assert.Same(window, Application.Current.MainWindow);
                var button = Find<Button>(window, b => Equals(b.Content, "导入全部设置"));
                Assert.NotNull(button);
                Assert.True(button!.IsVisible && button.IsEnabled);

                var imported = main.ImportSettingsWithRuntime(package);
                Assert.True(imported.Success, imported.Message);
                Assert.True(main.CanEditConfiguration && button.IsEnabled);
                Assert.Equal("imported", main.Config!.PlayerName);
                Assert.Equal("local-instance", main.Config.ClientInstanceId);
                Assert.Equal("imported", Assert.Single(window.Mistletoe.RootChain.Steps).Name);
                Assert.Contains(window.Mistletoe.Schemes, s => s.Scheme.Name == "imported");
                Assert.Contains(window.Mistletoe.TaskCenter.Flows, f => f.WorkflowId == "wf-imported");
                Assert.Contains(window.Dodoco.Watch.Rules, r => r.Name == "imported-rule");
                Assert.Equal(225, window.Pet.SizePx);
                Assert.False(window.Pet.Topmost);
                Assert.Empty(registrationCalls);
                Assert.False(window.Mistletoe.IsRunning);

                main.CurrentPage = AppPage.Mistletoe;
                window.Mistletoe.SelectedTabIndex = 0;
                window.UpdateLayout();
                Assert.True(Find<MistletoePage>(window)!.IsVisible);
                window.Mistletoe.RootChain.Steps[0].WaitSecondsText = "12";
                window.Mistletoe.SaveNow();
                window.Mistletoe.SchemeNameText = "continued-local-edit";
                window.Mistletoe.SaveSchemeCommand.Execute(null);
                window.Dodoco.MuteAll = true;
                window.Pet.SoundMuted = true;
                Assert.Equal(33, Read(root, "startup-flow.json")["delaySeconds"]!.GetValue<int>());
                Assert.Equal(12, Read(root, "startup-flow.json")["steps"]![0]!["waitSeconds"]!.GetValue<int>());
                Assert.Contains(Read(root, "startup-flow-schemes.json").AsArray(), s => s!["name"]!.GetValue<string>() == "imported");
                Assert.Contains(Read(root, "startup-flow-schemes.json").AsArray(), s => s!["name"]!.GetValue<string>() == "continued-local-edit");
                Assert.Equal(1920, Read(root, "dodoco_settings.json")["thumbnailWidth"]!.GetValue<int>());
                Assert.True(Read(root, "dodoco_settings.json")["muteAll"]!.GetValue<bool>());
                Assert.Equal(225, Read(root, "pet_settings.json")["sizePx"]!.GetValue<double>());
                Assert.False(Read(root, "pet_settings.json")["topmost"]!.GetValue<bool>());
                Assert.True(Read(root, "pet_settings.json")["soundMuted"]!.GetValue<bool>());

                var next = JsonSerializer.Deserialize<AssistConfig>(File.ReadAllText(Path.Combine(source, "assistant-config.json")))!;
                next.PlayerName = "must-roll-back";
                next.AutoLaunchOnBoot = true;
                Write(source, "assistant-config.json", next);
                exported = new ConfigTransferService(source).Export(failedPackage);
                Assert.True(exported.Success, exported.Message);
                var before = Snapshot(root);
                var failed = main.ImportSettingsWithRuntime(failedPackage);
                Assert.False(failed.Success);
                Assert.False(failed.RecoveryRequired, failed.Message);
                Assert.Equal(new[] { true, false }, registrationCalls);
                Assert.Equal("imported", main.Config!.PlayerName);
                Assert.False(main.Config.AutoLaunchOnBoot);
                Assert.True(main.CanEditConfiguration);
                Assert.Equal("12", Assert.Single(window.Mistletoe.RootChain.Steps).WaitSecondsText);
                Assert.True(window.Dodoco.MuteAll && window.Pet.SoundMuted);
                Assert.Equal(225, window.Pet.SizePx);
                var after = Snapshot(root);
                Assert.Equal(before.Keys.OrderBy(n => n), after.Keys.OrderBy(n => n));
                foreach (var item in before) Assert.Equal(item.Value, after[item.Key]);
                window.Dodoco.ShareLogInfoOnly = true;
                Assert.Equal(1920, Read(root, "dodoco_settings.json")["thumbnailWidth"]!.GetValue<int>());
                main.CurrentPage = AppPage.Settings;
                window.UpdateLayout();
                RenderIfRequested(window);
            }
            catch (Exception ex) { failure = ex; }
            finally
            {
                try
                {
                    if (window is not null)
                    {
                        window.Mistletoe.SetConfigTransferActive(true);
                        window.Pet.Shutdown();
                        window.Dodoco.Dispose();
                        window.Hide();
                    }
                    main?.Shutdown();
                }
                catch (Exception ex) { failure ??= ex; }
                finally
                {
                    IpcClient.PipeNameOverrideForTest = oldIpc;
                    BgiExternalClient.PipeNameOverrideForTest = oldExt;
                    if (failure is null) done.TrySetResult();
                    else done.TrySetException(failure);
                }
            }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static void Seed(string root, string name, string instance, int width, int petSize)
    {
        var startup = new StartupFlowConfig { Enabled = false, DelaySeconds = 33,
            Steps = [new StartupStep { Id = name + "-step", Name = name, Kind = StartupStepKinds.Wait, WaitSeconds = 7 }] };
        Write(root, "assistant-config.json", new AssistConfig { PlayerName = name, PlayerUid = "123456789",
            ClientInstanceId = instance, StandaloneMode = true, ServerUrl = "", GuardBgi = false,
            AutoLaunchOnBoot = false, AutoLaunchWithBgi = false, TeamUids = ["123456789"], DisclaimerAccepted = true });
        Write(root, "startup-flow.json", startup);
        Write(root, "startup-flow-schemes.json", new[] { new StartupFlowScheme { Name = name, Config = startup } });
        Write(root, "dodoco_settings.json", new DodocoSettings { ThumbnailWidth = width, MuteAll = false });
        Write(root, "dodoco_watch_rules.json", new { builtinStallSeeded = true, muteAll = false,
            rules = new[] { new WatchRule { Id = name + "-rule", Name = name + "-rule", Pattern = "never-a-real-log-match", Enabled = true } } });
        Write(root, "pet_settings.json", new PetSettings { Enabled = false, PanelEnabled = false,
            SizePx = petSize, Topmost = false, SoundMuted = false });
        Write(root, "member-config-cache.json", new MemberConfigCache { ConfigGroups = [name] });
        Write(root, "flows/wf-" + name + ".flow.json", new WorkflowDocument { WorkflowId = "wf-" + name,
            Name = name + "-plan", Nodes = [new WorkflowNode { NodeId = "end", Kind = "control.end" }] });
        File.WriteAllText(Path.Combine(root, ConfigTransferMistletoe.CurrentWorkflowFileName), "wf-" + name);
    }
    private static void Write<T>(string root, string name, T value)
    {
        var path = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value));
    }
    private static JsonNode Read(string root, string name) => JsonNode.Parse(File.ReadAllText(Path.Combine(root, name)))!;
    private static void Set(object target, string name, object value) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    private static Dictionary<string, byte[]> Snapshot(string root) => ConfigTransferService.SettingFileNames
        .Append(ConfigTransferMistletoe.CurrentWorkflowFileName).Select(n => Path.Combine(root, n))
        .Concat(Directory.GetFiles(Path.Combine(root, "flows"), "*.flow.json")).Append(LocalPeerSyncService.SharedRulesPath)
        .Where(File.Exists).ToDictionary(p => Path.GetRelativePath(root, p), File.ReadAllBytes);
    private static T? Find<T>(DependencyObject node, Func<T, bool>? predicate = null) where T : DependencyObject
    {
        if (node is T match && (predicate is null || predicate(match))) return match;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            if (Find<T>(VisualTreeHelper.GetChild(node, i), predicate) is { } child) return child;
        return null;
    }
    private static void RenderIfRequested(Window window)
    {
        var path = Environment.GetEnvironmentVariable("CONFIG_TRANSFER_UI_SNAPSHOT_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var bitmap = new RenderTargetBitmap(Math.Max(1, (int)window.ActualWidth), Math.Max(1, (int)window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); png.Save(stream);
    }
}
