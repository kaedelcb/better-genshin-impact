using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using OneDragonMigration.Core;
using Xunit;
using Envelope = BgiCoordinatorServer.Gateway.GatewayEnvelope;
using Protocol = BgiCoordinatorServer.Gateway.GatewayProtocol;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

/// <summary>四条真实本地网关连接；不启动游戏、不接触运行中的助手或真实 User。</summary>
public sealed class FourMemberConnectionRecoveryGatewayTests
{
    [Fact]
    public async Task LocalSettingsCanChangeWhileConnectedAndReconnectToUpdatedRoom()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离助手配置目录");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(new Action(async () =>
                {
                    var owner = new Window { Width = 100, Height = 100, Left = -20000, Top = -20000, ShowInTaskbar = false };
                    app.MainWindow = owner;
                    owner.Show();
                    try { await VerifyAsync(app); finished.SetResult(); }
                    catch (Exception ex) { finished.SetException(ex); }
                    finally { app.Shutdown(); }
                }));
                app.Run();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true, Name = "connected-local-settings" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(100));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        static async Task VerifyAsync(Application app)
        {
            await using var server = await Server.StartAsync();
            const string uid = "111111111";
            const string password = "fixture-password";
            var oldUids = new List<string> { uid, "222222222" };
            var newUids = new List<string> { uid, "333333333" };
            var oldCode = AssistConfigManager.GenerateControlRoomCode(oldUids);
            var newCode = AssistConfigManager.GenerateControlRoomCode(newUids);
            await using var peer = new SignalRClient();
            await peer.ConnectAsync(server.Url, newCode, password, "333333333", "隔离队友", newUids, bypassSystemProxy: true);
            var home = new AssistantRoomProfile { Id = "local", Name = "本机房间", RoomCode = oldCode,
                ServerUrl = server.Url, Password = password, TeamUids = oldUids };
            var config = new AssistConfig { PlayerUid = uid, PlayerName = "隔离测试", ClientInstanceId = "settings-test",
                BypassSystemProxy = true, RoomProfiles = [home], ActiveRoomProfileId = home.Id, LocalRoomProfileId = home.Id };
            home.ApplyTo(config);
            var vm = new MultiplayerHoeingAssistant.ViewModels.MainViewModel();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = vm.GetType();
            type.GetField("_config", flags)!.SetValue(vm, config);
            type.GetField("_configManager", flags)!.SetValue(vm, new AssistConfigManager());
            vm.RoomCode = oldCode;
            try
            {
                await (Task)type.GetMethod("ConnectSignalRAsync", flags)!.Invoke(vm, null)!;
                Assert.True(vm.IsConnected);
                var oldClient = type.GetField("_signalRClient", flags)!.GetValue(vm);
                // 模拟保存时已有刷新在飞：新设置必须排队，而不能丢失重连请求。
                var refreshing = type.GetField("_refreshRunning", flags)!;
                refreshing.SetValue(vm, 1);
                Exception? dialogFailure = null;
                app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    var dialog = app.Windows.OfType<MultiplayerHoeingAssistant.Views.SettingsWindow>().Single();
                    try
                    {
                        ((TextBox)dialog.FindName("TeamUidsBox")).Text = string.Join(",", newUids);
                        ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        if (dialog.IsVisible) throw new InvalidOperationException("连接中修改成员后应允许保存");
                    }
                    catch (Exception ex) { dialogFailure = ex; if (dialog.IsVisible) dialog.Close(); }
                }));
                vm.OpenRoomSettingsCommand.Execute(null);
                if (dialogFailure is not null) throw dialogFailure;
                Assert.False(vm.IsConnected);
                Assert.Equal(newCode, vm.RoomCode);
                refreshing.SetValue(vm, 0);
                await WaitUntilAsync(() => Task.FromResult(vm.IsConnected && vm.RoomCode == newCode
                    && type.GetField("_signalRClient", flags)!.GetValue(vm) is SignalRClient current
                    && !ReferenceEquals(current, oldClient) && current.IsConnected));
                var saved = new AssistConfigManager().Load();
                Assert.Equal(newCode, saved.LocalRoomProfile!.RoomCode);
                Assert.Equal(newUids, saved.TeamUids);
                Assert.DoesNotContain(server.Manager.GetControlRoomPlayers("CTRL_" + oldCode), p => p.PlayerUid == uid && p.Online);
                Assert.Contains(server.Manager.GetControlRoomPlayers("CTRL_" + newCode), p => p.PlayerUid == uid && p.Online);

                // 目标房间拒绝密码时不能保留旧房间作为新设置的成功连接。
                var rejectedUids = new List<string> { uid, "444444444" };
                var rejectedCode = AssistConfigManager.GenerateControlRoomCode(rejectedUids);
                await using var rejectedPeer = new SignalRClient();
                await rejectedPeer.ConnectAsync(server.Url, rejectedCode, "other-password", "444444444", "隔离队友", rejectedUids, bypassSystemProxy: true);
                SaveMembers(rejectedUids);
                await WaitUntilAsync(() => Task.FromResult(vm.CommandLogsText.Contains("刷新失败") && !vm.IsConnected));
                Assert.Equal(rejectedCode, vm.RoomCode);
                Assert.DoesNotContain(server.Manager.GetControlRoomPlayers("CTRL_" + rejectedCode), p => p.PlayerUid == uid && p.Online);

                // 失败后的再次保存应取消旧重试，只连接最后一次设置。
                SaveMembers(newUids);
                await WaitUntilAsync(() => Task.FromResult(vm.IsConnected && vm.RoomCode == newCode
                    && type.GetField("_signalRClient", flags)!.GetValue(vm) is SignalRClient current && current.IsConnected));
                Assert.Equal(newCode, new AssistConfigManager().Load().LocalRoomProfile!.RoomCode);

                // 两次连续保存发生在重连还未取得执行权时，第二次必须接管并连接最终设置。
                var reconnectGate = (SemaphoreSlim)type.GetField("_settingsReconnectGate", flags)!.GetValue(vm)!;
                await reconnectGate.WaitAsync();
                try
                {
                    SaveMembers(oldUids);
                    SaveMembers(newUids);
                    Assert.False(vm.IsConnected);
                    Assert.True((int)type.GetField("_settingsReconnectPending", flags)!.GetValue(vm)! >= 2);
                }
                finally { reconnectGate.Release(); }
                await WaitUntilAsync(() => Task.FromResult(vm.IsConnected && vm.RoomCode == newCode
                    && type.GetField("_signalRClient", flags)!.GetValue(vm) is SignalRClient current && current.IsConnected));
                Assert.Equal(newCode, new AssistConfigManager().Load().LocalRoomProfile!.RoomCode);

                void SaveMembers(List<string> members)
                {
                    Exception? error = null;
                    app.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                    {
                        var dialog = app.Windows.OfType<MultiplayerHoeingAssistant.Views.SettingsWindow>().Single();
                        try
                        {
                            ((TextBox)dialog.FindName("TeamUidsBox")).Text = string.Join(",", members);
                            ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            if (dialog.IsVisible) throw new InvalidOperationException("连接中修改成员后应允许保存");
                        }
                        catch (Exception ex) { error = ex; if (dialog.IsVisible) dialog.Close(); }
                    }));
                    vm.OpenRoomSettingsCommand.Execute(null);
                    if (error is not null) throw error;
                }
            }
            finally { vm.Shutdown(); }
        }
    }

    [Fact]
    public async Task AssistantSwitchesServersWithIdenticalRoomCodesAndReturnsToOriginAfterRejection()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离助手配置目录");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(new Action(async () =>
                {
                    try { await VerifyAsync(); finished.SetResult(); }
                    catch (Exception ex) { finished.SetException(ex); }
                    finally { app.Shutdown(); }
                }));
                app.Run();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true, Name = "isolated-room-server-switch" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(85));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        static async Task VerifyAsync()
        {
            await using var a = await Server.StartAsync();
            await using var b = await Server.StartAsync();
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            const string player = "111111111";
            const string peer = "222222222";
            const string code = "A12345";
            var list = new List<string> { player, peer };
            var source = new AssistantRoomProfile { Id = "cross-a", Name = "A", ServerUrl = a.Url, RoomCode = code, Password = "isolated-a",
                TeamUids = list, ExpectedHoeingPlayers = 2, OnlineHoeingGroupNames = ["A 的绑定"], OnlineHoeingGroupTypes = ["group"] };
            var target = new AssistantRoomProfile { Id = "cross-b", Name = "B", ServerUrl = b.Url, RoomCode = code, Password = "isolated-b",
                TeamUids = list, ExpectedHoeingPlayers = 3, OnlineHoeingGroupNames = ["B 的绑定"], OnlineHoeingGroupTypes = ["group"] };
            await using var peerA = new SignalRClient();
            await using var peerB = new SignalRClient();
            await peerA.ConnectAsync(a.Url, code, source.Password, peer, "A 队友", list, cancellationToken: budget.Token);
            await peerB.ConnectAsync(b.Url, code, target.Password, peer, "B 队友", list, cancellationToken: budget.Token);
            var vm = new MultiplayerHoeingAssistant.ViewModels.MainViewModel();
            source.OnlineHoeingCompletionPolicy = "switchRoom";
            source.OnlineHoeingRoomSwitchCompletion = new RoomSwitchCompletion(source.Id, a.Url, code, target.Id, b.Url, code, target.Name);
            var config = new AssistConfig { PlayerUid = player, PlayerName = "隔离测试", ClientInstanceId = "cross-server-test",
                RoomProfiles = [source, target], ActiveRoomProfileId = source.Id, ShowRoomOrchestration = true, ViewedRoomProfileId = source.Id };
            source.ApplyTo(config);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = vm.GetType();
            type.GetField("_config", flags)!.SetValue(vm, config);
            type.GetField("_configManager", flags)!.SetValue(vm, new AssistConfigManager());
            vm.RoomCode = code;
            try
            {
                await (Task)type.GetMethod("ConnectSignalRAsync", flags)!.Invoke(vm, null)!;
                Assert.Contains(vm.Members, member => member.PlayerName == "A 队友");
                // The monitor uses its own saved credentials to observe each room; these
                // connections must never appear as an extra executor or replace the actual member.
                // Exercise the internal helper without changing production visibility for a test.
                var linkType = type.Assembly.GetType("MultiplayerHoeingAssistant.Services.RoomOrchestrationMonitorLink", true)!;
                IAsyncDisposable Monitor(AssistantRoomProfile profile, string server) => (IAsyncDisposable)Activator.CreateInstance(linkType,
                    flags, null, [profile, server, player, "隔离监控端", true], null)!;
                void ConnectMonitor(IAsyncDisposable link) => linkType.GetMethod("EnsureConnected", flags)!.Invoke(link, null);
                bool Ready(IAsyncDisposable link) => (bool)linkType.GetProperty("IsReady", flags)!.GetValue(link)!;
                RemoteCenterTarget[] Targets(IAsyncDisposable link) => (RemoteCenterTarget[])linkType.GetMethod("Targets", flags)!.Invoke(link, null)!;
                await using var monitorA = Monitor(source, a.Url);
                await using var monitorB = Monitor(target, b.Url);
                ConnectMonitor(monitorA); ConnectMonitor(monitorB);
                await WaitUntilAsync(() => Task.FromResult(Ready(monitorA) && Ready(monitorB) && Targets(monitorA).Length == 1));
                var selectedInstance = Assert.Single(Targets(monitorA)).ClientInstanceId;
                Assert.Equal(config.ClientInstanceId, selectedInstance);
                Assert.Empty(Targets(monitorB));
                var switchRoom = type.GetMethod("SwitchAssistantRoomForFlowAsync", flags)!;
                Task<FlowRoomSwitchResult> Switch(string id) =>
                    (Task<FlowRoomSwitchResult>)switchRoom.Invoke(vm, [id, null, budget.Token])!;
                // A real switch waiting for a shared room effect must not reject a profile-only
                // save. The subsequently confirmed target must consume the newly saved fields.
                var configManager = (AssistConfigManager)type.GetField("_configManager", flags)!.GetValue(vm)!;
                Assert.True(configManager.TrySave(config));
                var heldRead = (IDisposable)type.GetMethod("TryEnterAssistantRoomEffect", flags)!.Invoke(vm, null)!;
                Task<RemoteCenterReply> Orchestrate(RemoteCenterRequest req) => (Task<RemoteCenterReply>)type
                    .GetMethod("ExecuteRoomOrchestrationRemoteOperationAsync", flags)!.Invoke(vm, [req, null, null])!;
                Task<FlowRoomSwitchResult> switching;
                try
                {
                    switching = Switch(target.Id);
                    await WaitUntilAsync(() => Task.FromResult(vm.RoomSwitchStatus.StartsWith("等待切换到", StringComparison.Ordinal)));
                    Assert.False(switching.IsCompleted);
                    var opened = await Orchestrate(new RemoteCenterRequest { Operation = "room.orchestration.edit",
                        TargetInstanceId = config.ClientInstanceId, PayloadJson = JsonSerializer.Serialize(target.Id) });
                    Assert.Equal("ok", opened.Status);
                    using var edit = JsonDocument.Parse(opened.PayloadJson!);
                    var savedDuringWait = await Orchestrate(new RemoteCenterRequest { Operation = "room.orchestration.save",
                        TargetInstanceId = config.ClientInstanceId, ExpectedRevision = edit.RootElement.GetProperty("Revision").GetString(),
                        PayloadJson = JsonSerializer.Serialize(new { ProfileId = target.Id, Changes = new { ExpectedPlayers = 4 } }) });
                    Assert.True(savedDuringWait.Status == "ok", savedDuringWait.Message);
                    Assert.Equal(source.Id, config.ActiveRoomProfileId);
                    Assert.Equal(2, config.ExpectedHoeingPlayers);
                    Assert.Equal(4, configManager.Load().RoomProfiles.Single(p => p.Id == target.Id).ExpectedHoeingPlayers);
                }
                finally { heldRead.Dispose(); }
                var toB = await switching;
                Assert.True(toB.Success, toB.Message);
                Assert.Equal(b.Url, config.ServerUrl);
                Assert.Equal(code, vm.RoomCode);
                Assert.True(config.ShowRoomOrchestration);
                Assert.Equal(source.Id, config.ViewedRoomProfileId);
                Assert.Equal("resume", config.OnlineHoeingCompletionPolicy);
                Assert.Equal(target.Id, config.RoomProfiles.Single(p => p.Id == source.Id).OnlineHoeingRoomSwitchCompletion?.TargetProfileId);
                Assert.DoesNotContain(vm.Members, member => member.PlayerName == "A 队友");
                Assert.Contains(vm.Members, member => member.PlayerName == "B 队友");
                Assert.DoesNotContain(a.Manager.GetControlRoomPlayers("CTRL_" + code), p => p.PlayerUid == player && p.Online);
                var bMember = Assert.Single(b.Manager.GetControlRoomPlayers("CTRL_" + code).Where(p => p.PlayerUid == player && p.Online));
                Assert.Equal(4, bMember.ExpectedHoeingPlayers);
                Assert.Equal("B 的绑定", Assert.Single(bMember.OnlineHoeingGroupNames));
                await WaitUntilAsync(() => Task.FromResult(Targets(monitorA).Length == 0 && Targets(monitorB).Any(t => t.ClientInstanceId == selectedInstance)));
                Assert.Equal(bMember.ClientInstanceId, Assert.Single(Targets(monitorB)).ClientInstanceId);
                Assert.DoesNotContain(b.Manager.GetControlRoomPlayers("CTRL_" + code), p => p.PlayerName == "隔离监控端");
                // A second same-UID instance must not overwrite the chosen instance. A synthetic
                // machine identity on this isolated gateway fixture also exercises the network
                // center request/reply path rather than the same-machine named pipe path.
                await using (var secondExecutor = new MultiplayerHoeingAssistant.Services.Gateway.MhaGatewayClient())
                {
                    const string secondInstance = "cross-server-second", secondMachine = "isolated-other-machine";
                    var delivered = new TaskCompletionSource<RemoteCommand>(TaskCreationOptions.RunContinuationsAsynchronously);
                    secondExecutor.EnvelopeReceived += envelope =>
                    {
                        if (envelope.Name != "control.remoteCommand" || envelope.Get<RemoteCommand>("command") is not { } cmd
                            || cmd.Cmd != RemoteCenterProtocol.RequestCommand) return;
                        delivered.TrySetResult(cmd);
                    };
                    await secondExecutor.ConnectAsync(b.Url, true, budget.Token);
                    var joined = await secondExecutor.InvokeCommandAsync("control.joinRoom", new { roomCode = code,
                        password = target.Password, playerUid = player, playerName = "隔离第二执行端", allowedUids = list,
                        clientInstanceId = secondInstance, machineId = secondMachine }, ct: budget.Token);
                    // Non-prepared joins can confirm through the full member event rather than a response payload.
                    Assert.NotEqual(false, joined.Payload?["success"]?.GetValue<bool>());
                    await WaitUntilAsync(() => Task.FromResult(Targets(monitorB).Length == 2));
                    Assert.Contains(Targets(monitorB), t => t.ClientInstanceId == selectedInstance);
                    var remote = Assert.Single(Targets(monitorB).Where(t => t.ClientInstanceId == secondInstance));
                    Assert.Equal(secondMachine, remote.MachineId);
                    var requestId = Guid.NewGuid().ToString("N");
                    var request = new RemoteCenterRequest { Operation = "room.orchestration.get", TargetInstanceId = secondInstance, ExpectedMachineId = secondMachine };
                    var responseTask = (Task<RemoteCenterReply>)linkType.GetMethod("RequestAsync", flags)!.Invoke(monitorB, [remote, request, requestId, budget.Token])!;
                    var received = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(8), budget.Token);
                    Assert.Equal(requestId, received.CommandId);
                    Assert.Equal(secondInstance, received.TargetInstanceId);
                    var response = RemoteCenterReply.Ok(new { fixture = true }); response.TargetInstanceId = secondInstance;
                    await secondExecutor.InvokeCommandAsync("control.sendCommand", new { command = RemoteCenterProtocol.Reply(received, player, "隔离第二执行端", response) }, ct: budget.Token);
                    Assert.Equal("ok", (await responseTask).Status);
                }
                await WaitUntilAsync(() => Task.FromResult(Targets(monitorB).Length == 1));
                var toA = await Switch(source.Id);
                Assert.True(toA.Success, toA.Message);
                Assert.Equal(a.Url, config.ServerUrl);
                Assert.Equal("switchRoom", config.OnlineHoeingCompletionPolicy);
                Assert.Equal(target.Id, config.OnlineHoeingRoomSwitchCompletion?.TargetProfileId);
                Assert.True(config.ShowRoomOrchestration);
                await WaitUntilAsync(() => Task.FromResult(Targets(monitorB).Length == 0 && Targets(monitorA).Any(t => t.ClientInstanceId == selectedInstance)));
                target.Password = "incorrect";
                var rejected = await Switch(target.Id);
                Assert.False(rejected.Success);
                Assert.False(rejected.Unknown, rejected.Message);
                Assert.Equal(source.Id, config.ActiveRoomProfileId);
                Assert.Equal(a.Url, config.ServerUrl);
                Assert.True(vm.IsConnected);
                Assert.Contains(vm.Members, member => member.PlayerName == "A 队友");
                Assert.DoesNotContain(vm.Members, member => member.PlayerName == "B 队友");
                var saved = new AssistConfigManager().Load();
                Assert.Equal(a.Url, saved.ServerUrl);
                Assert.Equal(b.Url, saved.RoomProfiles.Single(p => p.Id == target.Id).ServerUrl);
            }
            finally { vm.Shutdown(); }
        }
    }

    // 真实网页 → 两个不同服务器的同号房间 → 本人真实助手；只编辑房间设置和手动切房。
    // 与其他 WPF 集成用例一样独立按名称筛选运行，需独立空数据根。
    [Fact]
    public async Task AuthorizedBrowserEditsCrossServerRoomAndChecksOriginalSwitchReceipt()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离 NEXUSBGI_DATA_ROOT。");
        Assert.True(!Directory.Exists(AssistantDataDirectory.Root) || !Directory.EnumerateFileSystemEntries(AssistantDataDirectory.Root).Any(),
            "本用例需要专用空助手数据根，不能覆盖已有测试或用户配置。");
        var repository = FindRoomBrowserSourceRoot();
        var webRoot = Path.Combine(repository, "BgiCoordinatorServer", "wwwroot");
        var script = Path.Combine(repository, "BgiCoordinatorServer.Tests", "Web", "cross-room-assistant-browser.py");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(new Action(async () =>
                {
                    try { await VerifyAsync(); finished.SetResult(); }
                    catch (Exception ex) { finished.SetException(ex); }
                    finally { app.Shutdown(); }
                }));
                app.Run();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true, Name = "isolated-cross-room-web" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromMinutes(3));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));

        async Task VerifyAsync()
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(155));
            await using var a = await Server.StartAsync(webRoot);
            await using var b = await Server.StartAsync(webRoot);
            Assert.NotEqual(a.Url, b.Url);
            const string uid = "111111111", aPeerUid = "222222222", bPeerUid = "333333333";
            const string code = "A12345", instance = "cross-room-web-executor";
            const string note = "隔离网页保存的乙房备注";
            var source = new AssistantRoomProfile { Id = "web-a", Name = "甲房档案", ServerUrl = a.Url, RoomCode = code,
                Password = "isolated-web-a", TeamUids = [uid, aPeerUid], ExpectedHoeingPlayers = 2,
                OnlineHoeingGroupNames = ["甲房绑定"], OnlineHoeingGroupTypes = ["group"] };
            var target = new AssistantRoomProfile { Id = "web-b", Name = "乙房档案", ServerUrl = b.Url, RoomCode = code,
                Password = "isolated-web-b", TeamUids = [uid, bPeerUid], ExpectedHoeingPlayers = 3,
                OnlineHoeingGroupNames = ["乙房绑定"], OnlineHoeingGroupTypes = ["group"] };
            var aToB = new RoomSwitchCompletion(source.Id, a.Url, code, target.Id, b.Url, code, target.Name);
            var bToA = new RoomSwitchCompletion(target.Id, b.Url, code, source.Id, a.Url, code, source.Name);
            source.OnlineHoeingCompletionPolicy = "switchRoom";
            source.OnlineHoeingRoomSwitchCompletion = aToB;
            await using var peerA = new SignalRClient();
            await using var peerB = new SignalRClient();
            await peerA.ConnectAsync(a.Url, code, source.Password, aPeerUid, "甲房队友", source.TeamUids,
                bypassSystemProxy: true, cancellationToken: budget.Token);
            await peerB.ConnectAsync(b.Url, code, target.Password, bPeerUid, "乙房队友", target.TeamUids,
                bypassSystemProxy: true, cancellationToken: budget.Token);

            // 首连会触发真实状态采集。使用独立包标记隔离 ext/v2 管道，绝不连到本机运行中的 BGI。
            var bgiDirectory = Directory.CreateDirectory(Path.Combine(AssistantDataDirectory.Root, "offline-bgi-" + Guid.NewGuid().ToString("N"))).FullName;
            File.WriteAllText(Path.Combine(bgiDirectory, InstallationPipeScope.MarkerFile),
                "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
            using (var ipc = new IpcClient(bgiDirectory)) Assert.Contains(".install-", ipc.GetPipeName());
            var bgiPath = Path.Combine(bgiDirectory, "BetterGI.exe");
            var config = new AssistConfig { PlayerUid = uid, PlayerName = "隔离网页执行端", ClientInstanceId = instance,
                BgiPath = bgiPath, BypassSystemProxy = true, RoomProfiles = [source, target], ActiveRoomProfileId = source.Id,
                LocalRoomProfileId = source.Id, ViewedRoomProfileId = source.Id, ShowRoomOrchestration = true };
            source.ApplyTo(config);
            var vm = new MultiplayerHoeingAssistant.ViewModels.MainViewModel();
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var type = vm.GetType();
            Assert.Null(type.GetMethod("ResolveSamePackageBgiPath", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [AppContext.BaseDirectory]));
            var configManager = new AssistConfigManager();
            Assert.True(configManager.TrySave(config));
            type.GetField("_config", flags)!.SetValue(vm, config);
            type.GetField("_configManager", flags)!.SetValue(vm, configManager);
            vm.RoomCode = code;
            var ledgerPath = Path.Combine(AssistantDataDirectory.Root, "remote-center-operation-results.json");
            try
            {
                await (Task)type.GetMethod("ConnectSignalRAsync", flags)!.Invoke(vm, null)!;
                Assert.True(vm.IsConnected);
                // With no BGI installed at the fixture path the first real status collection
                // may spend several bounded IPC attempts before publishing the room bindings.
                var firstStatusDeadline = Stopwatch.StartNew();
                while (!a.Manager.GetControlRoomPlayers("CTRL_" + code).Any(p => p.PlayerUid == uid && p.Online
                    && p.ExpectedHoeingPlayers == 2 && p.OnlineHoeingGroupNames.SequenceEqual(source.OnlineHoeingGroupNames)))
                {
                    Assert.True(firstStatusDeadline.Elapsed < TimeSpan.FromSeconds(35), "真实助手首轮状态未确认：" + vm.CommandLogsText);
                    await Task.Delay(100, budget.Token);
                }
                var machine = Assert.Single(a.Manager.GetControlRoomPlayers("CTRL_" + code).Where(p => p.PlayerUid == uid && p.Online)).MachineId;
                Assert.False(string.IsNullOrWhiteSpace(machine));
                // 由生产授权库新签一次性授权码，浏览器须通过实际授权 UI 兑换；不预置浏览器凭据。
                var owners = type.GetProperty("WebOwners", flags)!.GetValue(vm)!;
                var (ownerCode, _) = ((string, DateTimeOffset))owners.GetType().GetMethod("CreateCode", flags)!.Invoke(owners, null)!;

                var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("BGI_ROOM_WEB_PYTHON") ?? "python")
                {
                    UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AssistantDataDirectory.Root,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                start.ArgumentList.Add("-I");
                start.ArgumentList.Add("-B");
                start.ArgumentList.Add("-X");
                start.ArgumentList.Add("utf8");
                start.ArgumentList.Add(script);
                using var browser = new Process { StartInfo = start };
                Assert.True(browser.Start(), "未能启动 Python Playwright 浏览器用例。");
                var browserError = browser.StandardError.ReadToEndAsync();
                try
                {
                    // 仅本次临时夹具的地址、房间密码和授权码，经 stdin 传入，不把凭据写到命令行。
                    await browser.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { a = a.Url, b = b.Url,
                        aPassword = source.Password, bPassword = target.Password, ownerCode }));
                    await browser.StandardInput.FlushAsync();
                    await ReadStage("before-switch");
                    AssertRoomState(switched: false);
                    var profileReceipt = AssertSaved("room.profiles.save", "room-profiles-web-");
                    var orchestrationReceipt = AssertSaved("room.orchestration.save", "room-orchestration-web-");
                    Assert.Equal(profileReceipt.Request.BrowserId, orchestrationReceipt.Request.BrowserId);
                    Assert.Empty(b.BrowserTrace!.Requests.Where(r => r.Operation is "room.profiles.save" or "room.orchestration.save" or "room.switch.start"));
                    Assert.Empty(a.BrowserTrace!.Requests.Where(r => r.Operation == "room.switch.start"));
                    await ContinueBrowser();

                    // 浏览器观察到同一实例从乙房重新出现才报告这个阶段；真实切房回执仍须落盘终结。
                    var switched = await ReadStage("switched");
                    var original = switched.GetProperty("operationId").GetString()!;
                    var switchRequest = Assert.Single(a.BrowserTrace.Requests.Where(r => r.Operation == "room.switch.start"));
                    AssertOwnerRequest(switchRequest, target.Id);
                    Assert.Equal(profileReceipt.Request.BrowserId, switchRequest.BrowserId);
                    Assert.StartsWith("room-switch-web-", original);
                    Assert.EndsWith("-" + switchRequest.Id, original);
                    await WaitUntilAsync(() => Task.FromResult(ReadReceipt(original)?.Status is { } status && status != "accepted"));
                    var terminal = ReadReceipt(original);
                    Assert.NotNull(terminal);
                    Assert.Equal("ok", terminal.Status);
                    Assert.Equal(original, terminal.OperationId);
                    using (var data = JsonDocument.Parse(terminal.PayloadJson!))
                        Assert.Equal(target.Id, data.RootElement.GetProperty("activeProfileId").GetString());
                    AssertRoomState(switched: true);
                    await ContinueBrowser();

                    await ReadStage("complete");
                    await browser.WaitForExitAsync(budget.Token);
                    Assert.True(browser.ExitCode == 0, await browserError);
                    var checkedRequest = Assert.Single(b.BrowserTrace.Requests.Where(r => r.Operation == "operation.get"
                        && r.Argument == original && b.BrowserTrace.Replies.Any(reply => reply.Id == r.Id && reply.Delivered && reply.Status == "ok")));
                    AssertOwnerRequest(checkedRequest, original, requiresRevision: false);
                    Assert.Equal(switchRequest.BrowserId, checkedRequest.BrowserId);
                    var checkedReply = Assert.Single(b.BrowserTrace.Replies.Where(r => r.Id == checkedRequest.Id && r.Delivered));
                    Assert.Equal(original, checkedReply.OperationId);
                    Assert.Equal(instance, checkedReply.TargetInstanceId);
                    Assert.Equal("ok", checkedReply.Status);
                    AssertRoomState(switched: true);
                    var requests = a.BrowserTrace.Requests.Concat(b.BrowserTrace.Requests).ToArray();
                    Assert.Single(requests.Where(r => r.Operation == "web.authorize"));
                    Assert.Single(requests.Where(r => r.Operation == "room.switch.start"));
                    Assert.Single(requests.Where(r => r.Operation == "room.profiles.save"));
                    Assert.Single(requests.Where(r => r.Operation == "room.orchestration.save"));
                    Assert.DoesNotContain(requests, r => r.ExplicitRefresh || r.Operation is not ("web.identity" or "web.authorize"
                        or "room.profiles.get" or "room.profiles.legacyCode" or "room.profiles.save" or "room.orchestration.get"
                        or "room.orchestration.edit" or "room.orchestration.names" or "room.orchestration.catalog" or "room.orchestration.layout"
                        or "room.orchestration.save" or "room.switch.start" or "room.switch.status" or "operation.get" or "bgi.update.hint"));
                    Assert.DoesNotContain(a.BrowserTrace.Names.Concat(b.BrowserTrace.Names), name => name is
                        "control.batch.begin" or "control.sendCommand" or "control.reportOnlineEvent");
                    using var ledger = JsonDocument.Parse(File.ReadAllText(ledgerPath));
                    Assert.Equal(3, ledger.RootElement.EnumerateObject().Count());
                }
                finally
                {
                    // Only the process launched above and its own headless browser are cleaned up.
                    if (!browser.HasExited) browser.Kill(entireProcessTree: true);
                    await browser.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }

                async Task<JsonElement> ReadStage(string expected)
                {
                    var line = await browser.StandardOutput.ReadLineAsync(budget.Token);
                    if (line is null)
                    {
                        await browser.WaitForExitAsync(budget.Token);
                        throw new InvalidOperationException("浏览器未到达阶段 " + expected + ": " + await browserError);
                    }
                    using var json = JsonDocument.Parse(line);
                    Assert.True(json.RootElement.GetProperty("stage").GetString() == expected, "浏览器阶段未通过：" + line);
                    return json.RootElement.Clone();
                }
                async Task ContinueBrowser()
                {
                    await browser.StandardInput.WriteLineAsync("continue");
                    await browser.StandardInput.FlushAsync();
                }
                (BrowserOwnerRequest Request, string Id) AssertSaved(string operation, string prefix)
                {
                    var request = Assert.Single(a.BrowserTrace!.Requests.Where(r => r.Operation == operation));
                    AssertOwnerRequest(request, target.Id);
                    var reply = Assert.Single(a.BrowserTrace.Replies.Where(r => r.Id == request.Id && r.Delivered));
                    Assert.Equal(instance, reply.TargetInstanceId);
                    Assert.Equal("ok", reply.Status);
                    Assert.NotNull(reply.OperationId);
                    Assert.StartsWith(prefix, reply.OperationId);
                    Assert.EndsWith("-" + request.Id, reply.OperationId);
                    var receipt = ReadReceipt(reply.OperationId);
                    Assert.NotNull(receipt);
                    Assert.Equal("ok", receipt.Status);
                    Assert.Equal(reply.OperationId, receipt.OperationId);
                    return (request, reply.OperationId);
                }
                void AssertOwnerRequest(BrowserOwnerRequest request, string argument, bool requiresRevision = true)
                {
                    Assert.Equal(code, request.RoomCode);
                    Assert.Equal(uid, request.TargetUid);
                    Assert.Equal(instance, request.TargetInstanceId);
                    Assert.Equal(instance, request.RequestTargetInstanceId);
                    Assert.Equal(machine, request.ExpectedMachineId);
                    Assert.Equal(request.Id, request.InnerId);
                    Assert.Equal(argument, request.Argument);
                    Assert.False(string.IsNullOrEmpty(request.BrowserId));
                    Assert.True(request.HasBrowserSecret);
                    if (requiresRevision) Assert.False(string.IsNullOrEmpty(request.ExpectedRevision));
                }
            }
            finally { vm.Shutdown(); }

            RemoteCenterReply? ReadReceipt(string id)
            {
                if (!File.Exists(ledgerPath)) return null;
                using var stream = File.Open(ledgerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var data = JsonDocument.Parse(stream);
                return data.RootElement.TryGetProperty(id, out var entry)
                    ? entry.GetProperty("Reply").Deserialize<RemoteCenterReply>() : null;
            }

            void AssertRoomState(bool switched)
            {
                VerifyConfig(config);
                VerifyConfig(new AssistConfigManager().Load());
                Assert.True(vm.IsConnected);
                Assert.Equal(code, vm.RoomCode);
                Assert.Contains(vm.Members, m => m.PlayerUid == (switched ? bPeerUid : aPeerUid) && m.Online);
                Assert.DoesNotContain(vm.Members, m => m.PlayerUid == (switched ? aPeerUid : bPeerUid) && m.Online);
                Assert.False(vm.IsBgiRunning);
                var aMembers = a.Manager.GetControlRoomPlayers("CTRL_" + code);
                var bMembers = b.Manager.GetControlRoomPlayers("CTRL_" + code);
                Assert.Equal(switched ? new[] { aPeerUid } : new[] { uid, aPeerUid },
                    aMembers.Where(p => p.Online).Select(p => p.PlayerUid).OrderBy(x => x).ToArray());
                Assert.Equal(switched ? new[] { uid, bPeerUid } : new[] { bPeerUid },
                    bMembers.Where(p => p.Online).Select(p => p.PlayerUid).OrderBy(x => x).ToArray());
                var member = Assert.Single((switched ? bMembers : aMembers).Where(p => p.PlayerUid == uid && p.Online));
                Assert.Equal(instance, member.ClientInstanceId);
                Assert.Equal(switched ? 4 : 2, member.ExpectedHoeingPlayers);
                Assert.Equal(switched ? "乙房绑定" : "甲房绑定", Assert.Single(member.OnlineHoeingGroupNames));
                Assert.All(aMembers.Concat(bMembers), p =>
                {
                    Assert.False(p.OnlineReady);
                    Assert.False(p.TaskRunning);
                    Assert.False(p.AutoHoeingRunning);
                    Assert.True(string.IsNullOrEmpty(p.HoeingBatchId));
                    Assert.Equal(0, p.OnlineEventGeneration);
                    Assert.Empty(p.OnlineHistory);
                });
                Assert.Null(a.Operations.CurrentCoordinatedBatch(GatewayHandlerContext.V3(Assert.Single(aMembers.Where(p => p.PlayerUid == aPeerUid && p.Online)).ConnectionId)));
                Assert.Null(b.Operations.CurrentCoordinatedBatch(GatewayHandlerContext.V3(Assert.Single(bMembers.Where(p => p.PlayerUid == bPeerUid && p.Online)).ConnectionId)));
                Assert.False(Directory.Exists(Path.Combine(bgiDirectory, "User")));

                void VerifyConfig(AssistConfig saved)
                {
                    Assert.Equal(2, saved.RoomProfiles.Count);
                    Assert.Equal(source.Id, saved.LocalRoomProfileId);
                    Assert.Equal(source.Id, saved.ViewedRoomProfileId);
                    Assert.True(saved.ShowRoomOrchestration);
                    Assert.Equal(switched ? target.Id : source.Id, saved.ActiveRoomProfileId);
                    Assert.Equal(switched ? b.Url : a.Url, saved.ServerUrl);
                    Assert.Equal(switched ? 4 : 2, saved.ExpectedHoeingPlayers);
                    Assert.Equal(switched ? target.Password : source.Password, saved.ControlRoomPassword);
                    Assert.Equal(switched ? bToA : aToB, saved.OnlineHoeingRoomSwitchCompletion);
                    Assert.Equal(bgiPath, saved.BgiPath);
                    var savedA = saved.RoomProfiles.Single(p => p.Id == source.Id);
                    var savedB = saved.RoomProfiles.Single(p => p.Id == target.Id);
                    Assert.Equal(a.Url, savedA.ServerUrl);
                    Assert.Equal(b.Url, savedB.ServerUrl);
                    Assert.Equal(code, savedA.RoomCode);
                    Assert.Equal(code, savedB.RoomCode);
                    Assert.Equal(source.Password, savedA.Password);
                    Assert.Equal(target.Password, savedB.Password);
                    Assert.Equal(source.TeamUids, savedA.TeamUids);
                    Assert.Equal(target.TeamUids, savedB.TeamUids);
                    Assert.Equal("", savedA.Note);
                    Assert.Equal(note, savedB.Note);
                    Assert.Equal(2, savedA.ExpectedHoeingPlayers);
                    Assert.Equal(4, savedB.ExpectedHoeingPlayers);
                    Assert.Equal("甲房绑定", Assert.Single(savedA.OnlineHoeingGroupNames));
                    Assert.Equal("乙房绑定", Assert.Single(savedB.OnlineHoeingGroupNames));
                    Assert.Equal("switchRoom", savedA.OnlineHoeingCompletionPolicy);
                    Assert.Equal("switchRoom", savedB.OnlineHoeingCompletionPolicy);
                    Assert.Equal(aToB, savedA.OnlineHoeingRoomSwitchCompletion);
                    Assert.Equal(bToA, savedB.OnlineHoeingRoomSwitchCompletion);
                    Assert.Empty(savedA.ScheduledOnlineTime);
                    Assert.Empty(savedB.ScheduledOnlineTime);
                }
            }
        }
    }

    private static string FindRoomBrowserSourceRoot()
    {
        static bool HasInputs(string root) => File.Exists(Path.Combine(root, "BgiCoordinatorServer", "wwwroot", "index.html"))
            && File.Exists(Path.Combine(root, "BgiCoordinatorServer.Tests", "Web", "cross-room-assistant-browser.py"));
        if (Environment.GetEnvironmentVariable("BGI_ROOM_WEB_SOURCE_ROOT") is { Length: > 0 } explicitRoot)
        {
            Assert.True(Path.IsPathFullyQualified(explicitRoot) && HasInputs(explicitRoot), "BGI_ROOM_WEB_SOURCE_ROOT 必须指向当前完整源码根。");
            return Path.GetFullPath(explicitRoot);
        }
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (HasInputs(directory.FullName)) return directory.FullName;
        throw new InvalidOperationException("找不到真实 wwwroot 和浏览器用例，请指定 BGI_ROOM_WEB_SOURCE_ROOT。");
    }

    [Fact]
    public async Task ControlRoomSwitchConfirmsBusinessResultsAndReturnsToOriginalRoom()
    {
        await using var server = await Server.StartAsync();
        const string uid = "111111111";
        const string password = "fixture-only";
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = budget.Token;
        await using var creator = await Connect();
        var a = await CreateRoom();
        var b = await CreateRoom();
        Assert.NotEqual(a, b);
        await using var source = await Connect();
        await PrepareAndReady(source, a, "A 的绑定");
        var left = await source.InvokeCommandAsync("control.leaveRoom", new { roomCode = a }, ct: ct);
        Assert.True(left.Payload!["success"]!.GetValue<bool>());

        await using var target = await Connect();
        var rejected = await target.InvokeCommandAsync("control.joinRoom", new
        { roomCode = b, playerUid = uid, playerName = uid, password = "错误密码", requireReady = true }, ct: ct);
        Assert.False(rejected.Payload!["success"]!.GetValue<bool>());
        await PrepareAndReady(target, b, "B 的绑定");
        var accepted = await target.InvokeCommandAsync("control.reportOnlineEvent",
            new { roomCode = b, generation = 1, isOnlineReady = true }, ct: ct);
        Assert.True(accepted.Payload!["success"]!.GetValue<bool>());
        var busy = await target.InvokeCommandAsync("control.leaveRoom", new { roomCode = b }, ct: ct);
        Assert.True(busy.Payload!["busy"]!.GetValue<bool>());
        var withdrawn = await target.InvokeCommandAsync("control.reportOnlineEvent",
            new { roomCode = b, generation = 1, isOnlineReady = false }, ct: ct);
        Assert.True(withdrawn.Payload!["success"]!.GetValue<bool>());
        left = await target.InvokeCommandAsync("control.leaveRoom", new { roomCode = b }, ct: ct);
        Assert.True(left.Payload!["success"]!.GetValue<bool>());
        await using var back = await Connect();
        await PrepareAndReady(back, a, "A 的绑定");
        Assert.Single(server.Manager.GetControlRoomPlayers("CTRL_" + a).Where(p => p.Online));
        Assert.DoesNotContain(server.Manager.GetControlRoomPlayers("CTRL_" + b), p => p.Online);

        async Task<MultiplayerHoeingAssistant.Services.Gateway.MhaGatewayClient> Connect()
        {
            var client = new MultiplayerHoeingAssistant.Services.Gateway.MhaGatewayClient();
            await client.ConnectAsync(server.Url, false, ct);
            return client;
        }
        async Task<string> CreateRoom()
        {
            var created = await creator.InvokeCommandAsync("control.createRoom", new
            { playerUid = uid, password, allowedUids = new[] { uid, "222222222" } }, ct: ct);
            Assert.True(created.Payload!["success"]!.GetValue<bool>());
            return created.GetString("roomCode");
        }
        async Task PrepareAndReady(MultiplayerHoeingAssistant.Services.Gateway.MhaGatewayClient client, string roomCode, string binding)
        {
            var prepared = await client.InvokeCommandAsync("control.joinRoom", new
            { roomCode, playerUid = uid, playerName = uid, password, requireReady = true }, ct: ct);
            Assert.True(prepared.Payload!["success"]!.GetValue<bool>());
            Assert.True(prepared.Get<ControlRoomPlayersUpdate>("playersUpdate")!.Full);
            Assert.DoesNotContain(server.Manager.GetControlRoomPlayers("CTRL_" + roomCode), p => p.Online);
            var status = new ControlStatus { RoomCode = roomCode, PlayerUid = uid, PlayerName = uid,
                ExpectedHoeingPlayers = 2, OnlineHoeingGroupNames = [binding], OnlineHoeingGroupTypes = ["group"] };
            var ready = await client.InvokeCommandAsync("control.readyRoom", new { status }, ct: ct);
            Assert.True(ready.Payload!["success"]!.GetValue<bool>());
            var active = Assert.Single(ready.Get<ControlRoomPlayersUpdate>("playersUpdate")!.Players!.Where(p => p.Online));
            Assert.Equal(binding, Assert.Single(active.OnlineHoeingGroupNames));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalBatchSurvivesMemberReconnect_AndTerminalCannotBeRevived(bool stopAfterReconnect)
    {
        await using var server = await Server.StartAsync();
        var room = "recovery-" + Guid.NewGuid().ToString("N");
        var uids = Enumerable.Range(0, 4).Select(i => "isolated-member-" + i).ToArray();
        var instances = uids.Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var tokens = uids.Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        var clients = new SignalRClient[4];
        const string group = "隔离四人锄地";
        try
        {
            for (var i = 0; i < 4; i++)
            {
                clients[i] = new SignalRClient();
                await ConnectAsync(i);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = uids[i], BgiStatus = "running",
                    ConfigGroups = [group], OnlineHoeingGroupNames = [group],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4
                });
            }
            var request = Guid.NewGuid().ToString("N");
            var batch = await clients[0].BeginCoordinatedBatchAsync(request, uids,
                DateTimeOffset.UtcNow.AddMinutes(1));
            foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
                for (var i = 0; i < 4; i++) batch = await UpdateAsync(i, stage);
            Assert.Equal("running", batch.Phase);
            Assert.Equal(0, batch.Attempt);

            // 真实断开并以同执行实例、原 token 重新入房，不能变成第二个批次。
            await clients[0].DisposeAsync();
            clients[0] = new SignalRClient();
            await ConnectAsync(0);
            var restored = await UpdateAsync(0, "takeoverConfirmed");
            Assert.Equal(batch.BatchId, restored.BatchId);
            Assert.Equal("running", restored.Phase);
            Assert.Equal(0, restored.Index);
            Assert.Equal(0, restored.Attempt);
            Assert.Equal(4, server.Manager.GetControlRoomPlayers("CTRL_" + room).Count);
            var duplicate = await clients[0].BeginCoordinatedBatchAsync(request, uids,
                DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.Equal(batch.BatchId, duplicate.BatchId);
            Assert.Equal(batch.Generation, duplicate.Generation);

            if (stopAfterReconnect)
            {
                await clients[3].SendRemoteCommandAsync(new RemoteCommand
                {
                    Cmd = "stop", Target = ["*"], CommandId = Guid.NewGuid().ToString("N"),
                    ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(20),
                    Params = new() { ["stopScope"] = "all" }
                });
                await WaitUntilAsync(async () => (await clients[0].GetCurrentCoordinatedBatchAsync())?.Phase == "aborted");
            }
            else
            {
                for (var i = 0; i < 4; i++) batch = await UpdateAsync(i, "takeoverConfirmed", "succeeded");
                Assert.Equal("completed", batch.Phase);
            }

            // 停止/完成后，重连和迟到的成功回执均只能读取原终态。
            var terminal = (await clients[0].GetCurrentCoordinatedBatchAsync())!;
            await clients[0].DisposeAsync();
            clients[0] = new SignalRClient();
            await ConnectAsync(0);
            for (var i = 0; i < 4; i++)
            {
                var late = await UpdateAsync(i, "takeoverConfirmed", "succeeded");
                Assert.Equal(terminal.BatchId, late.BatchId);
                Assert.Equal(terminal.Phase, late.Phase);
                Assert.Equal(terminal.Index, late.Index);
                Assert.Equal(terminal.Attempt, late.Attempt);
            }
            Assert.Equal(stopAfterReconnect ? "user_stop_all" : "", terminal.Reason);
            Assert.Equal(terminal.BatchId, (await clients[0].BeginCoordinatedBatchAsync(request,
                uids, DateTimeOffset.UtcNow.AddMinutes(1))).BatchId);

            Task ConnectAsync(int i) => clients[i].ConnectAsync(server.Url, room, "isolated-only",
                uids[i], uids[i], uids.ToList(), clientInstanceId: instances[i], bypassSystemProxy: true)
                .WaitAsync(TimeSpan.FromSeconds(10));

            Task<MultiplayerHoeingAssistant.Services.CoordinatedBatchSnapshot> UpdateAsync(
                int i, string stage, string result = "")
                => clients[i].UpdateCoordinatedBatchAsync(batch.Generation, tokens[i], batch.BatchId,
                    "group", 0, 0, result, CancellationToken.None, stage)
                    .WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            foreach (var client in clients)
                if (client is not null) await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task FourGameConnectionsRequireAllArrivals_AndGraceReconnectKeepsMemberIdentity()
    {
        await using var server = await Server.StartAsync();
        var connections = new HubConnection[4];
        var events = Enumerable.Range(0, 4).Select(_ => new ConcurrentQueue<string>()).ToArray();
        var arrivals = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        const string syncId = "isolated-route_strict_2";
        string? roomCode = null;
        try
        {
            for (var i = 0; i < 4; i++) connections[i] = await NewGameConnectionAsync(i);
            var created = await SendAsync(connections[0], Protocol.Names.RoomCreate,
                new { playerName = "member-0", playerUid = "member-0", expectedPlayerCount = 4, reportedVersion = "0.1.0+isolated" });
            roomCode = created.Payload!["roomCode"]!.GetValue<string>();
            for (var i = 1; i < 4; i++)
                Assert.True((await SendAsync(connections[i], Protocol.Names.RoomJoin,
                    new { playerName = "member-" + i, playerUid = "member-" + i, reportedVersion = "0.1.0+isolated" }, roomCode))
                    .Payload!["success"]!.GetValue<bool>());
            await SendAsync(connections[0], Protocol.Names.RoomMarkStarted, null, roomCode);

            for (var i = 0; i < 3; i++)
                await SendAsync(connections[i], Protocol.Names.SyncWaitForAllPlayers,
                    new { syncId, syncProgress = 1L }, roomCode);
            Assert.All(arrivals, arrived => Assert.False(arrived.Task.IsCompleted));
            await SendAsync(connections[3], Protocol.Names.SyncWaitForAllPlayers,
                new { syncId, syncProgress = 1L }, roomCode);
            await Task.WhenAll(arrivals.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(events, seen => Assert.Contains(syncId, seen));

            var oldConnection = connections[1].ConnectionId!;
            await connections[1].DisposeAsync();
            await WaitUntilAsync(() =>
            {
                var room = server.Manager.GetRoom(roomCode)!;
                lock (room) return Task.FromResult(room.GracePendingMembers.ContainsKey(oldConnection));
            });
            // 心跳已老，但宽限仍有效：走真实清退入口也不能提前踢人。
            var beforeReconnect = server.Manager.GetRoom(roomCode)!;
            lock (beforeReconnect)
                beforeReconnect.Players.Single(p => p.ConnectionId == oldConnection).LastHeartbeat = DateTime.UtcNow.AddSeconds(-35);
            typeof(RoomManager).GetMethod("RemoveDeadPlayers", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(server.Manager, [TimeSpan.FromSeconds(30), DateTime.UtcNow]);
            Assert.Equal(4, beforeReconnect.Players.Count);

            connections[1] = await NewGameConnectionAsync(1);
            Assert.True((await SendAsync(connections[1], Protocol.Names.RoomJoin,
                new { playerName = "member-1", playerUid = "member-1", reportedVersion = "0.1.0+isolated" }, roomCode))
                .Payload!["success"]!.GetValue<bool>());
            var restored = server.Manager.GetRoom(roomCode)!;
            lock (restored)
            {
                Assert.Equal(4, restored.Players.Count);
                Assert.DoesNotContain(restored.Players, p => p.ConnectionId == oldConnection);
                Assert.Contains(restored.Players, p => p.ConnectionId == connections[1].ConnectionId);
                Assert.False(restored.GracePendingMembers.ContainsKey(oldConnection));
            }
            await SendAsync(connections[1], Protocol.Names.SessionHeartbeat, null, roomCode);
            Assert.Equal(roomCode, server.Manager.GetRoomByConnectionId(connections[1].ConnectionId!).RoomCode);
        }
        finally
        {
            // 先结束成员，最后结束房主，避免测试收尾误报房主先退。
            foreach (var i in new[] { 1, 2, 3, 0 })
                if (connections[i] is not null) await connections[i].DisposeAsync();
        }

        async Task<HubConnection> NewGameConnectionAsync(int index)
        {
            var connection = new HubConnectionBuilder().WithUrl(server.Url + "/gateway").Build();
            connection.On<Envelope>("evt", envelope =>
            {
                if (envelope.Name != "sync.allArrived") return;
                // Requests use syncId; the AllArrived event carries syncPointId.
                var id = envelope.Payload?["syncPointId"]?.GetValue<string>();
                if (id is null) return;
                events[index].Enqueue(id);
                if (id == syncId) arrivals[index].TrySetResult();
            });
            await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await SendAsync(connection, Protocol.Names.SessionHello,
                new { clientKind = "bgi", protocolVersion = Protocol.ProtocolVersion, capabilities = Array.Empty<string>() }, query: true);
            return connection;
        }
    }

    [Fact]
    public async Task V2WorldPlanSkipsAB_RequiresReconnectedDWorldArrival_AndRetiresStartupAbort()
    {
        await using var server = await Server.StartAsync();
        var connections = new HubConnection[4];
        var uids = new[] { "loopback-A", "loopback-B", "loopback-C", "loopback-D" };
        var tickets = new (string PlanId, string Ticket)[4];
        var arrived = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        var closed = Enumerable.Range(0, 4)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        string? targetCode = null;
        const string version = "0.1.0+isolated-world-plan-v2";
        try
        {
            for (var i = 0; i < 4; i++) connections[i] = await ConnectV2Async(i);
            var source = await SendAsync(connections[0], Protocol.Names.RoomCreate,
                new { playerName = uids[0], playerUid = uids[0], expectedPlayerCount = 4, reportedVersion = version });
            var sourceCode = source.Payload!["roomCode"]!.GetValue<string>();
            await SendAsync(connections[0], Protocol.Names.RoomSetConfig,
                new { config = new { multiWorldEnabled = true, multiWorldCount = 4, partyTimeoutSeconds = 60 } },
                sourceCode);
            for (var i = 1; i < 4; i++)
                Assert.True((await SendAsync(connections[i], Protocol.Names.RoomJoin,
                    new { playerName = uids[i], playerUid = uids[i], reportedVersion = version }, sourceCode))
                    .Payload!["success"]!.GetValue<bool>());
            var begin = await SendAsync(connections[0], Protocol.Names.WorldPlanBegin, new { forceLegacy = false }, sourceCode);
            tickets[0] = ReadAdmission(begin);
            for (var i = 1; i < 4; i++)
            {
                tickets[i] = ReadAdmission(await SendAsync(connections[i], Protocol.Names.WorldPlanAcquire, null, sourceCode, query: true));
                Assert.Equal(tickets[0].PlanId, tickets[i].PlanId);
            }
            var counts = new[] { 0, 0, 2, 1 };
            for (var i = 0; i < 4; i++)
            {
                var report = await SendAsync(connections[i], Protocol.Names.WorldPlanReport,
                    new { planId = tickets[i].PlanId, ticket = tickets[i].Ticket, known = true, remainingCount = counts[i] });
                Assert.Equal(i == 3 ? "Prepared" : "Planning", Stage(report));
            }
            for (var i = 0; i < 4; i++)
            {
                var ack = await SendAsync(connections[i], Protocol.Names.WorldPlanAck, Credential(i));
                Assert.Equal(i == 3 ? "Committed" : "Prepared", Stage(ack));
                if (i == 3)
                {
                    var remaining = ack.Payload!["snapshot"]!["remainingHostUids"]!.AsArray()
                        .Select(n => n!.GetValue<string>()).ToArray();
                    Assert.Equal(new[] { uids[2], uids[3] }, remaining);
                    Assert.Equal(4, ack.Payload["snapshot"]!["participantUids"]!.AsArray().Count);
                }
            }
            for (var i = 0; i < 4; i++)
            {
                var released = await SendAsync(connections[i], Protocol.Names.WorldPlanLeaveSource, Credential(i));
                Assert.Equal(i == 3, released.Payload!["snapshot"]!["sourceReleased"]!.GetValue<bool>());
            }
            Assert.Null(server.Manager.GetRoom(sourceCode));
            var target = await SendAsync(connections[2], Protocol.Names.WorldPlanCreateTarget, Credential(2));
            targetCode = target.Payload!["snapshot"]!["targetRoomCode"]!.GetValue<string>();
            Assert.NotEqual(sourceCode, targetCode);
            var targetSession = target.Payload["snapshot"]!["targetRoomSessionId"]!.GetValue<string>();
            Assert.Equal(targetSession, server.Manager.GetRoom(targetCode)!.SessionId);
            for (var i = 0; i < 2; i++)
                Assert.True((await SendAsync(connections[i], Protocol.Names.RoomJoin,
                    new { playerName = uids[i], playerUid = uids[i], reportedVersion = version }, targetCode))
                    .Payload!["success"]!.GetValue<bool>());
            await SendAsync(connections[2], Protocol.Names.RoomReportHostReady, null, targetCode);
            for (var i = 0; i < 3; i++)
            {
                Assert.Equal("Committed", Stage(await SendAsync(connections[i], Protocol.Names.WorldPlanValidateTarget,
                    Credential(i), targetCode, query: true)));
                Assert.True((await SendAsync(connections[i], Protocol.Names.WorldReportJoined, null, targetCode))
                    .Payload!["ack"]!.GetValue<bool>());
            }
            var count = await SendAsync(connections[2], Protocol.Names.RoomGetState,
                new { section = "worldJoinedCount" }, targetCode, query: true);
            Assert.Equal(3, count.Payload!["value"]!.GetValue<int>());
            await ExpectErrorAsync(connections[2], Protocol.Names.RoomMarkStarted,
                new { requireWorldPlanRoster = true }, "world_plan_roster_not_ready", targetCode);
            await Task.Delay(150); // Let real local evt delivery drain before asserting that no all-worlds signal was sent.
            Assert.All(arrived, signal => Assert.False(signal.Task.IsCompleted));
            Assert.False(server.Manager.GetRoom(targetCode)!.IsStarted);

            // D loses the source connection after obtaining its private ticket. A new connection must recover this same plan.
            await connections[3].DisposeAsync();
            connections[3] = await ConnectV2Async(3);
            var recovered = await SendAsync(connections[3], Protocol.Names.WorldPlanGet, Credential(3), query: true);
            Assert.Equal("Committed", Stage(recovered));
            Assert.Equal(targetCode, recovered.Payload!["snapshot"]!["targetRoomCode"]!.GetValue<string>());
            Assert.True((await SendAsync(connections[3], Protocol.Names.RoomJoin,
                new { playerName = uids[3], playerUid = uids[3], reportedVersion = version }, targetCode))
                .Payload!["success"]!.GetValue<bool>());
            Assert.Equal("Committed", Stage(await SendAsync(connections[3], Protocol.Names.WorldPlanValidateTarget,
                Credential(3), targetCode, query: true)));
            await ExpectErrorAsync(connections[2], Protocol.Names.RoomMarkStarted,
                new { requireWorldPlanRoster = true }, "world_plan_roster_not_ready", targetCode);
            Assert.All(arrived, signal => Assert.False(signal.Task.IsCompleted));
            await SendAsync(connections[3], Protocol.Names.WorldReportJoined, null, targetCode);
            await Task.WhenAll(arrived.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            var marked = await SendAsync(connections[2], Protocol.Names.RoomMarkStarted,
                new { requireWorldPlanRoster = true }, targetCode);
            Assert.True(marked.Payload!["ack"]!.GetValue<bool>());
            Assert.True(server.Manager.GetRoom(targetCode)!.IsStarted);

            // 生产故障顺序：新连接先到，旧连接尚未被断线回调移入宽限期。
            // 不能放宽同 UID 在线保护；旧连接进入宽限后，重试应接回同一房间和世界准入。
            HubConnection? replacement = await ConnectV2Async(1);
            try
            {
                var oldId = connections[1].ConnectionId!;
                var originalRoom = server.Manager.GetRoom(targetCode)!;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    var denied = await replacement.InvokeAsync<Envelope>("Dispatch", new Envelope
                    {
                        Type = "command", Name = Protocol.Names.RoomJoin, RoomCode = targetCode,
                        RequestId = Guid.NewGuid().ToString("N"), SentAtUtc = DateTime.UtcNow,
                        Payload = Envelope.ToPayload(new { playerName = uids[1], playerUid = uids[1], reportedVersion = version })
                    }, timeout.Token);
                    Assert.False(denied.Payload!["success"]!.GetValue<bool>());
                    Assert.Equal("world_plan_duplicate_participant", denied.Payload["error"]!.GetValue<string>());
                    lock (originalRoom)
                    {
                        Assert.Equal(4, originalRoom.Players.Count);
                        Assert.Equal(oldId, originalRoom.Players.Single(p => p.PlayerUid == uids[1]).ConnectionId);
                        Assert.DoesNotContain(oldId, originalRoom.GracePendingMembers.Keys);
                    }
                }
                await connections[1].DisposeAsync();
                await WaitUntilAsync(() =>
                {
                    lock (originalRoom) return Task.FromResult(originalRoom.GracePendingMembers.ContainsKey(oldId));
                });
                Assert.True((await SendAsync(replacement, Protocol.Names.RoomJoin,
                    new { playerName = uids[1], playerUid = uids[1], reportedVersion = version }, targetCode))
                    .Payload!["success"]!.GetValue<bool>());
                connections[1] = replacement;
                replacement = null;
                lock (originalRoom)
                {
                    Assert.Same(originalRoom, server.Manager.GetRoom(targetCode));
                    Assert.Equal(targetSession, originalRoom.SessionId);
                    Assert.True(originalRoom.IsStarted);
                    Assert.Equal(4, originalRoom.Players.Count);
                    Assert.DoesNotContain(oldId, originalRoom.WorldJoinedSet);
                    Assert.Contains(connections[1].ConnectionId!, originalRoom.WorldJoinedSet);
                    Assert.False(originalRoom.GracePendingMembers.ContainsKey(oldId));
                }
                Assert.Null(server.Manager.GetRoomByConnectionId(oldId).Room);
                Assert.Same(originalRoom, server.Manager.GetRoomByConnectionId(connections[1].ConnectionId!).Room);
            }
            finally
            {
                if (replacement != null) await replacement.DisposeAsync();
            }
            await ExpectErrorAsync(connections[0], Protocol.Names.WorldPlanAbort, Credential(0), "world_plan_startup_retired");
            Assert.NotNull(server.Manager.GetRoom(targetCode));
            Assert.All(closed, signal => Assert.False(signal.Task.IsCompleted));
            await SendAsync(connections[2], Protocol.Names.RoomClose, null, targetCode);
            await Task.WhenAll(closed.Select(a => a.Task)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Null(server.Manager.GetRoom(targetCode));
        }
        finally
        {
            foreach (var i in new[] { 0, 1, 3, 2 })
                if (connections[i] is not null) await connections[i].DisposeAsync();
        }

        object Credential(int i) => new { planId = tickets[i].PlanId, ticket = tickets[i].Ticket };
        static string Stage(Envelope response) => response.Payload!["snapshot"]!["stage"]!.GetValue<string>();
        static (string PlanId, string Ticket) ReadAdmission(Envelope response)
        {
            Assert.Equal("Planning", Stage(response));
            return (response.Payload!["planId"]!.GetValue<string>(), response.Payload["ticket"]!.GetValue<string>());
        }

        async Task<HubConnection> ConnectV2Async(int index)
        {
            var connection = new HubConnectionBuilder().WithUrl(server.Url + "/gateway").Build();
            connection.On<Envelope>("evt", envelope =>
            {
                if (targetCode == null || envelope.RoomCode != targetCode) return;
                if (envelope.Name == "world.allJoined") arrived[index].TrySetResult();
                if (envelope.Name == "room.closed") closed[index].TrySetResult();
            });
            try
            {
                await connection.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
                var hello = await SendAsync(connection, Protocol.Names.SessionHello,
                    new { clientKind = "bgi", protocolVersion = Protocol.ProtocolVersion,
                        capabilities = new[] { Protocol.WorldPlanCapability } }, query: true);
                Assert.Contains(Protocol.WorldPlanCapability, hello.Payload!["capabilities"]!.AsArray().Select(n => n!.GetValue<string>()));
                return connection;
            }
            catch { await connection.DisposeAsync(); throw; }
        }

        static async Task ExpectErrorAsync(HubConnection connection, string name, object payload, string message, string? room = null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var response = await connection.InvokeAsync<Envelope>("Dispatch", new Envelope
            {
                Type = "command", Name = name, RequestId = Guid.NewGuid().ToString("N"), RoomCode = room,
                Payload = Envelope.ToPayload(payload), SentAtUtc = DateTime.UtcNow,
            }, timeout.Token);
            Assert.Equal(Protocol.ErrorCodes.InternalError, response.Payload!["error"]!["code"]!.GetValue<string>());
            Assert.Equal(message, response.Payload["error"]!["message"]!.GetValue<string>());
        }
    }

    private static async Task<Envelope> SendAsync(HubConnection connection, string name, object? payload,
        string? room = null, bool query = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var response = await connection.InvokeAsync<Envelope>(query ? "Query" : "Dispatch", new Envelope
        {
            Type = query ? "query" : "command", Name = name, RequestId = Guid.NewGuid().ToString("N"),
            RoomCode = room, Payload = Envelope.ToPayload(payload), SentAtUtc = DateTime.UtcNow
        }, timeout.Token);
        Assert.Null(response.Payload?["error"]);
        return response;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "隔离网关状态未在期限内收敛。");
            await Task.Delay(20);
        }
    }

    private sealed record BrowserOwnerRequest(string? Id, string? InnerId, string? Operation, string? RoomCode,
        string? TargetUid, string? TargetInstanceId, string? RequestTargetInstanceId, string? ExpectedMachineId,
        string? BrowserId, bool HasBrowserSecret, string? Argument, string? ExpectedRevision, bool ExplicitRefresh);
    private sealed record BrowserOwnerReply(string? Id, string? OperationId, string? Status, string? TargetInstanceId, bool Delivered);

    // Passive observation at the real hub: never replaces a response, mutates requests or retains passwords/secrets.
    private sealed class BrowserGatewayTrace : IHubFilter
    {
        public ConcurrentQueue<BrowserOwnerRequest> Requests { get; } = new();
        public ConcurrentQueue<BrowserOwnerReply> Replies { get; } = new();
        public ConcurrentQueue<string> Names { get; } = new();

        private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        private static bool True(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var result) && result;

        public async ValueTask<object?> InvokeMethodAsync(HubInvocationContext context, Func<HubInvocationContext, ValueTask<object?>> next)
        {
            if (context.HubMethodArguments.FirstOrDefault() is not Envelope env) return await next(context);
            Names.Enqueue(env.Name);
            if (env.Name == "mistletoe.request" && env.Payload is { } body && body["request"] is JsonObject request)
            {
                var argument = request["payload"] as JsonObject;
                Requests.Enqueue(new(Text(body["requestId"]), Text(request["requestId"]), Text(request["operation"]), Text(body["roomCode"]),
                    Text(body["targetUid"]), Text(body["targetInstanceId"]), Text(request["targetInstanceId"]), Text(request["expectedMachineId"]),
                    Text(request["browserId"]), !string.IsNullOrEmpty(Text(request["browserSecret"])),
                    Text(request["payload"]) ?? Text(argument?["profileId"]) ?? Text(argument?["ProfileId"]), Text(request["expectedRevision"]),
                    True(argument?["ExplicitRefresh"]) || True(argument?["explicitRefresh"])));
            }
            var result = await next(context);
            if (env.Name == "mistletoe.respond" && env.Payload is { } responseBody && responseBody["reply"] is JsonObject reply
                && result is Envelope response)
                Replies.Enqueue(new(Text(responseBody["requestId"]), Text(reply["operationId"]), Text(reply["status"]),
                    Text(reply["targetInstanceId"]), True(response.Payload?["accepted"])));
            return result;
        }
    }

    private sealed class Server(WebApplication application, BrowserGatewayTrace? trace = null) : IAsyncDisposable
    {
        public string Url => application.Urls.Single();
        public RoomManager Manager => application.Services.GetRequiredService<RoomManager>();
        public RoomOperations Operations => application.Services.GetRequiredService<RoomOperations>();
        public BrowserGatewayTrace? BrowserTrace => trace;

        public static async Task<Server> StartAsync(string? staticWebRoot = null)
        {
            Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离 NEXUSBGI_DATA_ROOT。");
            var root = Path.Combine(AssistantDataDirectory.Root, "four-member-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root, WebRootPath = staticWebRoot });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_ => new RoomManager(50));
            builder.Services.AddSingleton<GatewaySessionTracker>();
            builder.Services.AddSingleton<GatewayBroadcaster>();
            builder.Services.AddSingleton<RoomPhaseObserver>();
            builder.Services.AddSingleton<IClientCapabilityLookup, GatewayCapabilityLookup>();
            builder.Services.AddSingleton<RoomOperations>();
            builder.Services.AddSingleton<GatewayDispatcher>();
            BrowserGatewayTrace? trace = null;
            if (staticWebRoot is not null)
            {
                trace = new BrowserGatewayTrace();
                builder.Services.AddSingleton(trace);
                // 与 Program.cs 的真实网页跨域连接策略一致；仍走正常入房密码和本人授权。
                builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
            }
            builder.Services.AddSignalR(options =>
            {
                if (trace is not null)
                {
                    options.AddFilter<BrowserGatewayTrace>();
                    options.MaximumReceiveMessageSize = 10 * 1024 * 1024;
                }
            });
            var application = builder.Build();
            if (staticWebRoot is not null)
            {
                application.UseRouting();
                application.UseCors();
                // 该夹具没有管理端鉴权路由，不直接公开正式管理页面。
                application.Use(async (context, next) =>
                {
                    if (new[] { "/admin-login.html", "/admin-monitor.html", "/admin-routes.html", "/route-upload.html" }
                        .Contains(context.Request.Path.Value, StringComparer.OrdinalIgnoreCase))
                    {
                        context.Response.StatusCode = 404;
                        return;
                    }
                    await next(context);
                });
                application.UseDefaultFiles();
                application.UseStaticFiles();
            }
            application.MapHub<GatewayHub>("/gateway");
            application.MapHub<CoordinatorHub>("/hub");
            await application.StartAsync();
            return new Server(application, trace);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
