using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
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
                var toB = await Switch(target.Id);
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
                Assert.Equal(3, bMember.ExpectedHoeingPlayers);
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

    private sealed class Server(WebApplication application) : IAsyncDisposable
    {
        public string Url => application.Urls.Single();
        public RoomManager Manager => application.Services.GetRequiredService<RoomManager>();

        public static async Task<Server> StartAsync()
        {
            Assert.True(AssistantDataDirectory.IsExplicit, "必须使用隔离 NEXUSBGI_DATA_ROOT。");
            var root = Path.Combine(AssistantDataDirectory.Root, "four-member-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton(_ => new RoomManager(50));
            builder.Services.AddSingleton<GatewaySessionTracker>();
            builder.Services.AddSingleton<GatewayBroadcaster>();
            builder.Services.AddSingleton<RoomPhaseObserver>();
            builder.Services.AddSingleton<IClientCapabilityLookup, GatewayCapabilityLookup>();
            builder.Services.AddSingleton<RoomOperations>();
            builder.Services.AddSingleton<GatewayDispatcher>();
            builder.Services.AddSignalR();
            var application = builder.Build();
            application.MapHub<GatewayHub>("/gateway");
            application.MapHub<CoordinatorHub>("/hub");
            await application.StartAsync();
            return new Server(application);
        }

        public async ValueTask DisposeAsync()
        {
            await application.StopAsync();
            await application.DisposeAsync();
        }
    }
}
