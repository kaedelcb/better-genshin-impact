using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest;

public sealed class AssistantRoomProfileTests
{
    [Fact]
    public void LegacyRoomKeepsCodeBindingsAndAlreadyFiredSchedule()
    {
        var date = new DateTime(2026, 11, 10);
        var config = new AssistConfig
        {
            PlayerUid = "111111111", TeamUids = ["222222222", "111111111"], ControlRoomPassword = "test-password",
            OnlineHoeingGroupNames = ["A路线"], OnlineHoeingGroupTypes = ["group"],
            ScheduledOnlineTime = "12:00", ScheduledOnlineRequiresConfirmation = true, ScheduledOnlineLastFireDate = date
        };
        var oldCode = AssistConfigManager.GenerateControlRoomCode(config.TeamUids);
        Assert.True(config.EnsureRoomProfiles());
        Assert.False(config.EnsureRoomProfiles());
        var profile = Assert.Single(config.RoomProfiles);
        Assert.Equal(oldCode, config.EffectiveRoomCode);
        Assert.Equal(date, profile.ScheduledOnlineLastFireDate);
        Assert.True(profile.ScheduledOnlineRequiresConfirmation);
        Assert.Equal("A路线", Assert.Single(profile.OnlineHoeingGroupNames));
    }

    [Fact]
    public void OldProfilesInheritSharedServerWithoutOverwritingExplicitAddresses()
    {
        var config = new AssistConfig { ServerUrl = "https://original.example.test/gateway-bgi" };
        var inherited = new AssistantRoomProfile { Id = "old", RoomCode = "ABCDEF" };
        var explicitServer = new AssistantRoomProfile { Id = "other", RoomCode = "ABCDEF", ServerUrl = "https://other.example.test" };
        config.RoomProfiles.AddRange([inherited, explicitServer]);
        Assert.True(config.FillLegacyRoomServerUrls());
        Assert.False(config.FillLegacyRoomServerUrls());
        explicitServer.ApplyTo(config);
        Assert.Equal("https://other.example.test", config.ServerUrl);
        Assert.Equal("https://original.example.test/gateway-bgi", config.GetRoomServerUrl(inherited));
        inherited.ApplyTo(config);
        Assert.Equal("https://original.example.test/gateway-bgi", config.ServerUrl);
    }

    [Theory]
    [InlineData("https://ROOMS.example.test/app/hub/", "https://rooms.example.test:443/app", true)]
    [InlineData("https://rooms.example.test/app/sub/../hub", "https://rooms.example.test/app", true)]
    [InlineData("https://rooms.example.test/app", "https://rooms.example.test/App", false)]
    [InlineData("http://localhost:5010", "http://localhost:5011", false)]
    public void ServerIdentityNormalizesAliasesButKeepsDifferentEndpoints(string first, string second, bool same)
        => Assert.Equal(same, RoomServerAddress.Same(first, second));

    [Fact]
    public void ApplyingAnotherRoomDoesNotChangeOldRoomOrGlobalIdentity()
    {
        var config = new AssistConfig { PlayerUid = "111111111", BgiPath = "bgi.exe" };
        var a = new AssistantRoomProfile { Id = "fixture-a", Name = "房间 A", RoomCode = "AAAAAA", TeamUids = ["111111111", "222222222"],
            OnlineHoeingGroupNames = ["A"], ScheduledOnlineTime = "10:00", ScheduledOnlineLastFireDate = new DateTime(2026, 11, 10) };
        var b = new AssistantRoomProfile { Id = "fixture-b", Name = "房间 B", RoomCode = "BBBBBZ", TeamUids = ["111111111", "333333333"],
            OnlineHoeingGroupNames = ["B"], ScheduledOnlineTime = "18:30", ScheduledOnlineRequiresConfirmation = true };
        config.RoomProfiles.AddRange([a, b]);
        a.ApplyTo(config);
        config.ActiveRoomProfileId = a.Id;
        Assert.Equal(a.RoomCode, config.EffectiveRoomCode);
        b.ApplyTo(config);
        config.ActiveRoomProfileId = b.Id;
        config.OnlineHoeingGroupNames.Add("B收尾");
        b.Capture(config);
        var roundTrip = JsonSerializer.Deserialize<AssistConfig>(JsonSerializer.Serialize(config))!;
        Assert.Equal("BBBBBZ", roundTrip.EffectiveRoomCode);
        Assert.Equal("A", Assert.Single(roundTrip.RoomProfiles[0].OnlineHoeingGroupNames));
        Assert.Equal(new[] { "B", "B收尾" }, roundTrip.RoomProfiles[1].OnlineHoeingGroupNames.ToArray());
        Assert.True(roundTrip.ScheduledOnlineRequiresConfirmation);
        Assert.Equal("111111111", roundTrip.PlayerUid);
        Assert.Equal("bgi.exe", roundTrip.BgiPath);
    }

    [Fact]
    public async Task RoomSwitchChoices_UpdatesPopupListBoxWithoutGeneratorMismatch_OnAddEditRemoveAndSwitch()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            Window? window = null;
            Popup? popup = null;
            MainViewModel? vm = null;
            try
            {
                var roomA = new AssistantRoomProfile { Id = "room-a", Name = "默认房间", RoomCode = "AAAAAA" };
                var roomB = new AssistantRoomProfile { Id = "room-b", Name = "周末房间", RoomCode = "BBBBBB" };
                var config = new AssistConfig
                {
                    StandaloneMode = true,
                    PlayerUid = "111111111",
                    ActiveRoomProfileId = roomA.Id,
                    RoomProfiles = [roomA, roomB]
                };
                vm = new MainViewModel();
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                typeof(MainViewModel).GetField("_config", flags)!.SetValue(vm, config);
                var notifyRoomProfiles = typeof(MainViewModel).GetMethod("NotifyRoomProfiles", flags)!;
                notifyRoomProfiles.Invoke(vm, null);

                var list = new ListBox
                {
                    Name = "AssistantRoomSelector",
                    SelectedValuePath = "Id",
                    MaxHeight = 224,
                    Width = 300
                };
                list.SetBinding(ItemsControl.ItemsSourceProperty, new Binding(nameof(MainViewModel.RoomSwitchChoices)));
                list.SetBinding(Selector.SelectedValueProperty, new Binding("ActiveRoomProfile.Id") { Mode = BindingMode.OneWay });

                popup = new Popup { StaysOpen = false, Child = list };
                var host = new StackPanel();
                host.Children.Add(popup);
                window = new Window
                {
                    DataContext = vm,
                    Content = host,
                    Width = 360,
                    Height = 260,
                    ShowActivated = false,
                    ShowInTaskbar = false,
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -20000,
                    Top = -20000
                };
                window.Show();

                void FlushUi()
                {
                    var frame = new DispatcherFrame();
                    dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
                    Dispatcher.PushFrame(frame);
                }

                void ReopenAndMeasure()
                {
                    popup.IsOpen = false;
                    FlushUi();
                    popup.IsOpen = true;
                    list.SetCurrentValue(Selector.SelectedValueProperty, vm.ActiveRoomProfile?.Id);
                    list.UpdateLayout();
                    FlushUi();
                }

                // 1. 首次打开弹窗（2 个房间），建立 ItemContainerGenerator 计数 = 2
                ReopenAndMeasure();
                Assert.Equal(2, list.Items.Count);
                Assert.Same(roomA, list.SelectedItem);

                // 2. 模拟「管理房间」新增第 3 个房间后重开弹窗：旧实现原地 Add 到 List<T>，引发 InvalidOperationException
                popup.IsOpen = false;
                FlushUi();
                var roomC = new AssistantRoomProfile { Id = "room-c", Name = "新增房间", RoomCode = "CCCCCC" };
                config.RoomProfiles.Add(roomC);
                notifyRoomProfiles.Invoke(vm, null);
                ReopenAndMeasure();
                Assert.Equal(3, list.Items.Count);
                Assert.Same(roomA, list.SelectedItem);

                // 3. 模拟「管理房间」编辑第 2 个房间（替换为新实例）后重开弹窗：列表应展示新名称且无残留旧对象
                popup.IsOpen = false;
                FlushUi();
                var editedB = new AssistantRoomProfile { Id = "room-b", Name = "周末房间-已改名", RoomCode = "B22222" };
                config.RoomProfiles[1] = editedB;
                notifyRoomProfiles.Invoke(vm, null);
                ReopenAndMeasure();
                Assert.Equal(3, list.Items.Count);
                Assert.Same(editedB, list.Items[1]);
                Assert.Equal("周末房间-已改名", ((AssistantRoomProfile)list.Items[1]).Name);

                // 4. 模拟「管理房间」移除第 3 个房间（3 → 2）后重开弹窗：不能出现计数不一致异常
                popup.IsOpen = false;
                FlushUi();
                config.RoomProfiles.RemoveAt(2);
                notifyRoomProfiles.Invoke(vm, null);
                ReopenAndMeasure();
                Assert.Equal(2, list.Items.Count);
                Assert.Same(roomA, list.SelectedItem);

                // 5. 弹窗保持打开时切换激活房间 + 通知刷新：不抛异常且选中项同步到目标房间
                config.ActiveRoomProfileId = editedB.Id;
                notifyRoomProfiles.Invoke(vm, null);
                list.UpdateLayout();
                FlushUi();
                Assert.Same(editedB, list.SelectedItem);

                // 6. 弹窗保持打开时新增房间但激活房间未变：不抛异常且不把当前选中项清空
                var roomD = new AssistantRoomProfile { Id = "room-d", Name = "四号房间", RoomCode = "DDDDDD" };
                config.RoomProfiles.Add(roomD);
                notifyRoomProfiles.Invoke(vm, null);
                list.UpdateLayout();
                FlushUi();
                Assert.Equal(3, list.Items.Count);
                Assert.Same(editedB, list.SelectedItem);

                // 7. 未调用 NotifyRoomProfiles 的原地列表修改（如保存失败回滚窗口期）不得破坏已绑定快照
                config.RoomProfiles.Add(new AssistantRoomProfile { Id = "temp", Name = "临时", RoomCode = "TTTTTT" });
                list.UpdateLayout();
                FlushUi();
                Assert.Equal(3, list.Items.Count);
                config.RoomProfiles.RemoveAt(config.RoomProfiles.Count - 1);

                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally
            {
                if (popup is not null) popup.IsOpen = false;
                window?.Close();
                vm?.Shutdown();
                dispatcher.InvokeShutdown();
            }
        }) { IsBackground = true, Name = "room-switch-choices-wpf-test" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
}
