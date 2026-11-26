using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Hubs;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.LogRoom.IntegrationTest;

/// <summary>真实网关上的只读跟随登录，不读取实际账号配置，也不运行 BGI。</summary>
public sealed class RoomFollowObserverGatewayTests
{
    [Fact]
    public async Task KnownRoomUsesNormalObserverLoginAndRejectedRoomNeverReplacesExecutor()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须设置隔离 NEXUSBGI_DATA_ROOT。");
        var root = Path.Combine(AssistantDataDirectory.Root, "room-follow-gateway-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new RoomManager(12));
        builder.Services.AddSingleton<GatewaySessionTracker>();
        builder.Services.AddSingleton<GatewayBroadcaster>();
        builder.Services.AddSingleton<RoomPhaseObserver>();
        builder.Services.AddSingleton<RoomOperations>();
        builder.Services.AddSingleton<GatewayDispatcher>();
        builder.Services.AddSignalR();
        await using var server = builder.Build();
        server.MapHub<GatewayHub>("/gateway");
        await server.StartAsync();
        var url = server.Urls.Single();
        const string room = "follow-integration";
        const string uid = "fixture-owner";
        var password = Guid.NewGuid().ToString("N");
        var manager = server.Services.GetRequiredService<RoomManager>();
        await using var executor = new SignalRClient();
        await executor.ConnectAsync(url, room, password, uid, "测试执行端", [],
            clientInstanceId: "fixture-executor").WaitAsync(TimeSpan.FromSeconds(15));
        var initial = Assert.Single(manager.GetControlRoomPlayers("CTRL_" + room));

        await using (var rejected = NewObserver(url, room, Guid.NewGuid().ToString("N"), uid))
        {
            Call(rejected, "Start");
            await UntilAsync(() => Read<bool>(Call(rejected, "Capture")!, "AuthenticationRequired"));
            Assert.Null(Read<SignalRClient?>(Call(rejected, "Capture")!, "Client"));
            Assert.Equal(initial.ConnectionId, Assert.Single(manager.GetControlRoomPlayers("CTRL_" + room)).ConnectionId);
        }

        await using (var accepted = NewObserver(url, room, password, uid))
        {
            Call(accepted, "Start");
            await UntilAsync(() => Read<SignalRClient?>(Call(accepted, "Capture")!, "Client") is not null);
            var observation = Call(accepted, "Capture")!;
            var client = Assert.IsType<SignalRClient>(Read<SignalRClient?>(observation, "Client"));
            Assert.Equal(room, Read<string>(client, "CurrentRoomCode"));
            Assert.False(Read<bool>(observation, "AuthenticationRequired"));
            var players = Read<IReadOnlyList<MultiplayerHoeingAssistant.Models.ControlRoomPlayer>>(observation, "Players");
            Assert.Equal(uid, Assert.Single(players).PlayerUid);
            Assert.Equal(initial.ConnectionId, Assert.Single(manager.GetControlRoomPlayers("CTRL_" + room)).ConnectionId);
        }
        Assert.True(executor.IsConnected);
        Assert.Equal(initial.ConnectionId, Assert.Single(manager.GetControlRoomPlayers("CTRL_" + room)).ConnectionId);
    }

    [Fact]
    public async Task DesktopFollowSwitchesConfirmedRoomsAndRestoresSavedLoginAndViewOnDisable()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须设置隔离 NEXUSBGI_DATA_ROOT。");
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(new Action(async () =>
                {
                    try { await VerifyDesktopRoundTripAsync(); finished.TrySetResult(); }
                    catch (Exception ex) { finished.TrySetException(ex); }
                    finally { app.Shutdown(); }
                }));
                app.Run();
            }
            catch (Exception ex) { finished.TrySetException(ex); }
        }) { IsBackground = true, Name = "room-follow-roundtrip" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(110));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static async Task VerifyDesktopRoundTripAsync()
    {
        var root = Path.Combine(AssistantDataDirectory.Root, "room-follow-roundtrip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new RoomManager(12));
        builder.Services.AddSingleton<GatewaySessionTracker>();
        builder.Services.AddSingleton<GatewayBroadcaster>();
        builder.Services.AddSingleton<RoomPhaseObserver>();
        builder.Services.AddSingleton<RoomOperations>();
        builder.Services.AddSingleton<GatewayDispatcher>();
        builder.Services.AddSignalR();
        await using var server = builder.Build();
        server.MapHub<GatewayHub>("/gateway");
        await server.StartAsync();
        var url = server.Urls.Single();
        const string uid = "roundtrip-owner", roomA = "roundtrip-A", roomB = "roundtrip-B";
        var password = Guid.NewGuid().ToString("N");
        await using var memberA = new SignalRClient();
        await using var memberB = new SignalRClient();
        await memberA.ConnectAsync(url, roomA, password, uid, "测试房间甲", [], clientInstanceId: "fixture-A", bypassSystemProxy: true);
        await memberB.ConnectAsync(url, roomB, password, uid, "测试房间乙", [], clientInstanceId: "fixture-B", bypassSystemProxy: true);
        var machine = typeof(MainViewModel).Assembly.GetType("MultiplayerHoeingAssistant.Services.HostMachineIdentity", true)!
            .GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null) as string;
        var grant = RoomFollowGrant.Create("", true, uid, "roundtrip-publisher", machine!);
        await using var publisher = new RoomFollowPublisher(grant, true);
        await publisher.StartAsync();
        RoomFollowSnapshot State(string activeId, string viewedId, bool confirmed = true) => new()
        {
            Uid = uid, InstanceId = grant.Invitation.InstanceId, MachineId = grant.Invitation.MachineId, Confirmed = confirmed,
            ActiveId = activeId, ViewedId = viewedId, Profiles =
            [
                new() { Id = "source-a", Name = "共享房间甲", ServerUrl = url, RoomCode = roomA, TeamUids = [uid] },
                new() { Id = "source-b", Name = "共享房间乙", ServerUrl = url, RoomCode = roomB, TeamUids = [uid] },
                new() { Id = "source-c", Name = "未认证房间", ServerUrl = url, RoomCode = "roundtrip-C", TeamUids = [uid] }
            ]
        };
        var a = new AssistantRoomProfile { Id = "local-a", Name = "本地甲", ServerUrl = url, RoomCode = roomA, Password = password, TeamUids = [uid] };
        var b = new AssistantRoomProfile { Id = "local-b", Name = "本地乙", ServerUrl = url, RoomCode = roomB, Password = password, TeamUids = [uid] };
        var config = new AssistConfig { PlayerUid = uid, PlayerName = "测试监控端", ClientInstanceId = "roundtrip-monitor", ObserverMode = true,
            BypassSystemProxy = true, DisclaimerAccepted = true, RoomProfiles = [a, b], ActiveRoomProfileId = a.Id,
            LocalRoomProfileId = a.Id, ViewedRoomProfileId = a.Id };
        a.ApplyTo(config);
        var manager = new AssistConfigManager();
        Assert.True(manager.TrySave(config));
        var before = manager.ReadRawJson();
        var vm = new MainViewModel();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        vm.GetType().GetField("_config", flags)!.SetValue(vm, config);
        vm.GetType().GetField("_configManager", flags)!.SetValue(vm, manager);
        vm.GetType().GetField("_roomFollowInitialized", flags)!.SetValue(vm, true);
        vm.RoomCode = roomA;
        try
        {
            await (Task)Call(vm, "ConnectSignalRAsync")!;
            Assert.True(vm.IsConnected);
            publisher.Update(State("source-b", "source-a"));
            await (Task)Call(vm, "ReplaceRoomFollowInvitationAsync", grant.Invitation.Encode())!;
            await (Task)Call(vm, "SetRoomFollowingEnabledAsync", true)!;
            await UntilAsync(() => vm.IsRoomFollowing && vm.IsConnected && vm.RoomCode == roomB && vm.ViewedRoomProfileId == "source-a");
            Assert.Equal("source-b", vm.ActiveRoomProfile!.Id);
            Assert.Equal("共享房间乙", vm.ActiveRoomProfile.Name);
            Assert.Equal(before, manager.ReadRawJson());
            publisher.Update(State("source-a", AssistConfig.AllRoomsViewId, confirmed: false));
            await UntilAsync(() => !vm.IsConnected && vm.RoomCode.Length == 0);
            publisher.Update(State("source-a", AssistConfig.AllRoomsViewId));
            await UntilAsync(() => vm.IsConnected && vm.RoomCode == roomA && vm.IsViewingAllRooms);
            publisher.Update(State("source-c", "source-c"));
            await UntilAsync(() => !vm.IsConnected && vm.RoomCode.Length == 0 && vm.RoomFollowStatus.Contains("待授权"));
            Assert.Equal(before, manager.ReadRawJson());
            await (Task)Call(vm, "SetRoomFollowingEnabledAsync", false)!;
            await UntilAsync(() => !vm.IsRoomFollowing && vm.IsConnected && vm.RoomCode == roomA);
            Assert.Equal("local-a", vm.ActiveRoomProfile!.Id);
            Assert.Equal("local-a", vm.ViewedRoomProfileId);
            Assert.Equal(new[] { "local-a", "local-b" }, vm.Config!.RoomProfiles.Select(p => p.Id));
            Assert.Equal(before, manager.ReadRawJson());
            Assert.True(memberA.IsConnected && memberB.IsConnected);
        }
        finally { vm.Shutdown(); }
    }

    private static IAsyncDisposable NewObserver(string server, string room, string password, string uid)
    {
        // The production observer-session type is internal to the WPF assembly. This integration
        // assembly intentionally exercises its real transport via reflection, not a mock adapter.
        var assembly = typeof(MainViewModel).Assembly;
        var kind = assembly.GetType("MultiplayerHoeingAssistant.ViewModels.RoomFollowRoomCredential", throwOnError: true)!;
        var session = assembly.GetType("MultiplayerHoeingAssistant.ViewModels.RoomFollowObserverSession", throwOnError: true)!;
        var credential = Activator.CreateInstance(kind, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [server, room, password, uid, "测试监控端", "fixture-monitor", true], null)!;
        return (IAsyncDisposable)Activator.CreateInstance(session, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [credential], null)!;
    }

    private static object? Call(object target, string method, params object?[] args) => target.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.Invoke(target, args);

    private static T Read<T>(object target, string property) => (T)target.GetType()
        .GetProperty(property, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(target)!;

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(25);
        Assert.True(condition(), "真实观察者房间连接未在限定时间内确认预期状态。");
    }
}
