using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Media;
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

/// <summary>
/// 隔离 WPF 重置命令及真实网关：旧测试只覆盖本机复位和远端 ACK，不能证明重置会刷新房间。
/// 三种连接条件下确认新连接/成员快照、复位回执和失败提示后结束；不启动 BGI 或访问真实 User。
/// 本类须在独立 testhost 中运行，WPF Application 只创建一次。
/// </summary>
public sealed class ResetRoomRefreshEntryTests
{
    [Fact]
    public async Task ResetCommand_RefreshesRoomBeforeReset_AndDoesNotClaimRefreshOnFailure()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "必须设置隔离 NEXUSBGI_DATA_ROOT。");
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Assert.Null(Application.Current);
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(app.Dispatcher));
                app.Dispatcher.InvokeAsync(async () =>
                {
                    var owner = new Window { Width = 100, Height = 100, Left = -20000, Top = -20000, ShowInTaskbar = false };
                    app.MainWindow = owner;
                    owner.Show();
                    try
                    {
                        await VerifyAsync(disconnect: false, rejectRefresh: false);
                        await VerifyAsync(disconnect: true, rejectRefresh: false);
                        await VerifyAsync(disconnect: true, rejectRefresh: true);
                        done.TrySetResult();
                    }
                    catch (Exception ex) { done.TrySetException(ex); }
                    finally { app.Shutdown(); }
                });
                app.Run();
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }) { IsBackground = true, Name = "ResetRoomRefreshEntry" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(100));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
    }

    private static async Task VerifyAsync(bool disconnect, bool rejectRefresh)
    {
        var root = Path.Combine(AssistantDataDirectory.Root, "reset-room-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = root });
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(_ => new RoomManager(50));
        builder.Services.AddSingleton<GatewaySessionTracker>();
        builder.Services.AddSingleton<GatewayBroadcaster>();
        builder.Services.AddSingleton<RoomPhaseObserver>();
        builder.Services.AddSingleton<RoomOperations>();
        builder.Services.AddSingleton<GatewayDispatcher>();
        builder.Services.AddSignalR();
        await using var server = builder.Build();
        server.MapHub<GatewayHub>("/gateway");
        server.MapHub<CoordinatorHub>("/hub");
        await server.StartAsync();
        const string uid = "refresh-owner";
        const string password = "isolated-reset-room";
        var room = "reset-room-" + Guid.NewGuid().ToString("N");
        var vm = new MainViewModel();
        var config = new AssistConfig
        {
            ServerUrl = server.Urls.Single(), PlayerUid = uid, PlayerName = "重置监控端",
            TeamUids = [uid], ControlRoomPassword = password, ObserverMode = true,
            DisclaimerAccepted = true, BypassSystemProxy = true,
            BgiPath = Path.Combine(root, "unused", "BetterGI.exe"),
        };
        Set(vm, "_config", config);
        vm.RoomCode = room;
        await using var executor = new SignalRClient();
        SignalRClient? old = null;
        DispatcherTimer? confirmTimer = null;
        var resetReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await executor.ConnectAsync(config.ServerUrl, room, password, uid, "实际执行端", [uid],
                clientInstanceId: "executor", bypassSystemProxy: true);
            executor.OnRemoteCommand += command =>
            {
                if (command.Cmd != "reset_state") return;
                _ = ReplyAsync(command);
            };
            async Task ReplyAsync(RemoteCommand command)
            {
                try
                {
                    Assert.NotSame(old, Get<SignalRClient?>(vm, "_signalRClient"));
                    await executor.SendRemoteCommandAsync(new RemoteCommand
                    {
                        Cmd = "ack", Sender = uid, SenderUid = uid, Target = [command.SenderUid],
                        CommandId = command.CommandId,
                        Params = new() { ["status"] = "success", ["message"] = "隔离执行端重置完成" },
                    });
                    resetReceived.TrySetResult();
                }
                catch (Exception ex) { resetReceived.TrySetException(ex); }
            }
            old = (SignalRClient)Call(vm, "CreateWiredSignalRClient")!;
            await ((Task)Call(vm, "ConnectSignalRWithConfigAsync", old, CancellationToken.None, "隔离初次入房", false)!)
                .WaitAsync(TimeSpan.FromSeconds(15));
            await UntilAsync(() => vm.Members.Any(m => m.PlayerUid == uid && m.Online));
            var self = vm.Members.Single(m => m.PlayerUid == uid);
            if (disconnect) await old.DisposeAsync();
            if (rejectRefresh)
            {
                // 拒绝入房必须抛出失败，不能把正常命令响应当作成功；直接客户端避免交互式错误弹窗。
                await using var rejected = new SignalRClient();
                var rejection = await Assert.ThrowsAnyAsync<IOException>(() => rejected.ConnectAsync(
                    config.ServerUrl, room, "wrong-password", uid, "错误密码", [uid], isRemote: true, bypassSystemProxy: true));
                Assert.Contains("加入控制房间失败", rejection.Message);
                // 正式重置入口验证服务器不可达，仍必须收口而不误报房间刷新成功。
                config.ServerUrl = "http://127.0.0.1:0";
            }
            // 人为放入过期 UI 状态；只有刷新取得完整房间快照后才会恢复。
            self.Online = false;
            self.PlayerName = "过期成员状态";
            var confirmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            confirmTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            confirmTimer.Tick += (_, _) =>
            {
                var dialog = Application.Current.Windows.Cast<Window>()
                    .FirstOrDefault(w => w.Title == "重置状态" && w.IsVisible);
                if (dialog == null) return;
                try
                {
                    Assert.Contains(Descendants(dialog).OfType<TextBlock>(), text => text.Text.Contains("先刷新房间连接和成员状态"));
                    var button = Assert.Single(Descendants(dialog).OfType<Button>().Where(b => Equals(b.Content, "确定")));
                    confirmTimer.Stop();
                    ((IInvokeProvider)new ButtonAutomationPeer(button)).Invoke();
                    confirmed.TrySetResult();
                }
                catch (Exception ex) { confirmTimer.Stop(); dialog.Close(); confirmed.TrySetException(ex); }
            };
            confirmTimer.Start();
            vm.ResetStateCommand.Execute(self);
            await confirmed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await UntilAsync(() => Get<SemaphoreSlim>(vm, "_resetRequestGate").CurrentCount == 1);
            if (rejectRefresh)
            {
                Assert.Contains("房间刷新未确认", vm.CommandLogsText);
                Assert.DoesNotContain("房间状态已刷新", vm.CommandLogsText);
                Assert.False(resetReceived.Task.IsCompleted);
            }
            else
            {
                await resetReceived.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.NotSame(old, Get<SignalRClient>(vm, "_signalRClient"));
                Assert.True(vm.IsConnected);
                Assert.True(vm.Members.Single(m => m.PlayerUid == uid).Online);
                Assert.NotEqual("过期成员状态", vm.Members.Single(m => m.PlayerUid == uid).PlayerName);
                Assert.Contains("房间状态已刷新", vm.CommandLogsText);
                Assert.Contains("服务端已撤销旧批次", vm.CommandLogsText);
                Assert.DoesNotContain("远端执行端未确认", vm.CommandLogsText);
            }
        }
        finally
        {
            confirmTimer?.Stop();
            Set(vm, "_disposing", true);
            Call(vm, "StopSignalRRecovery");
            Get<System.Threading.Timer?>(vm, "_retryTimer")?.Dispose();
            Get<System.Threading.Timer?>(vm, "_statusTimer")?.Dispose();
            if (Get<SignalRClient?>(vm, "_signalRClient") is { } current) await current.DisposeAsync();
            if (old != null) await old.DisposeAsync();
            await server.StopAsync();
        }
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(30), "等待房间刷新/重置结果超时");
            await Task.Delay(30);
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var next in Descendants(child)) yield return next;
        }
    }

    private static object? Call(object target, string name, params object?[] args)
        => typeof(MainViewModel).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
    private static T Get<T>(object target, string name)
        => (T)typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static void Set(object target, string name, object? value)
        => typeof(MainViewModel).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
}
