using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RemoteStartupReceiptTests
{
    [Theory]
    [InlineData("task-center-clears")]
    [InlineData("direct-terminal")]
    [InlineData("unknown")]
    public async Task SameMonitor_CanStartAgainAfterConfirmedReceipt_WithoutReconnecting(string mode)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            MistletoeViewModel? remote = null;
            LocalConfigEditChannel? pipe = null;
            try
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var uid = "receipt-" + Guid.NewGuid().ToString("N");
                var secret = Guid.NewGuid().ToString("N");
                var target = new RemoteCenterTarget { PlayerUid = uid, PlayerName = "隔离执行端",
                    ClientInstanceId = Guid.NewGuid().ToString("N"), ForceLocal = true, Online = true };
                var snapshot = new RemoteStartupSnapshot { Revision = "config-1", SchemesRevision = "schemes-1",
                    AutoLaunchRevision = "launch-1", Config = new StartupFlowConfig() };
                var completed = false;
                var calls = new List<string>();
                pipe = new LocalConfigEditChannel(() => secret, command =>
                {
                    var request = JsonSerializer.Deserialize<RemoteCenterRequest>(RemoteCenterProtocol.PayloadOf(command)!)!;
                    RemoteCenterReply reply;
                    switch (request.Operation)
                    {
                        case "startup.status":
                            reply = RemoteCenterReply.Ok(new RemoteStartupStatusSnapshot { IsRunning = false, StatusText = "已停止" });
                            break;
                        case "startup.get": reply = RemoteCenterReply.Ok(snapshot); break;
                        case "task.list": reply = RemoteCenterReply.Ok(new RemoteTaskListSnapshot()); break;
                        case "startup.run":
                            calls.Add(command.CommandId);
                            completed = mode == "direct-terminal";
                            reply = new RemoteCenterReply { Status = mode == "direct-terminal" ? "ok" : mode == "unknown" ? "unknown" : "accepted",
                                OperationId = command.CommandId, Message = "隔离启动回执" };
                            break;
                        case "operation.get":
                            reply = new RemoteCenterReply { Status = completed ? "ok" : mode == "unknown" ? "unknown" : "accepted",
                                OperationId = JsonSerializer.Deserialize<string>(request.PayloadJson!), Message = "原操作已完成" };
                            break;
                        default: reply = RemoteCenterReply.Rejected("不支持：" + request.Operation); break;
                    }
                    reply.TargetInstanceId = target.ClientInstanceId;
                    return Task.FromResult(RemoteCenterProtocol.Reply(command, uid, "隔离执行端", reply));
                }, _ => { }, pipeName: "startup-receipt-" + Guid.NewGuid().ToString("N"), channel: "center");
                var main = new MainViewModel();
                typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main,
                    new AssistConfig { ObserverMode = true, StandaloneMode = true, PlayerUid = uid,
                        TeamUids = [uid], ControlRoomPassword = secret });
                var endpoints = (ConcurrentDictionary<string, LocalConfigEditChannel.Endpoint>)typeof(MainViewModel)
                    .GetField("_localCenterEndpoints", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                endpoints[uid + ":" + target.ClientInstanceId] = pipe.Address;
                remote = new MistletoeViewModel(main, remoteTarget: target, remoteSnapshot: snapshot);
                ((DispatcherTimer)typeof(MistletoeViewModel).GetField("_bgiStatusRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(remote)!).Stop();
                remote.TaskCenter.StopAutoRefresh();
                Complete(Invoke(remote, "RefreshRemoteStartupStatusAsync"));
                Assert.True(remote.IsNotRunning);

                Complete(Invoke(remote, "RunRemoteStartupAsync"));
                Assert.Single(calls);
                if (mode != "direct-terminal")
                {
                    Assert.NotNull(main.PendingCenterOperation(target));
                    Assert.False(remote.IsNotRunning);
                    Complete(Invoke(remote, "RunRemoteStartupAsync"));
                    Assert.Single(calls); // 未知或正在处理的操作不能靠新请求重发。
                    completed = true;
                    if (mode == "task-center-clears")
                    {
                        // 真实任务中心刷新先消费同一个共享回执，启动中心随后才读状态。
                        Complete(Invoke(remote.TaskCenter, "RefreshRemoteAsync"));
                        Assert.Null(main.PendingCenterOperation(target));
                    }
                    Complete(Invoke(remote, "RefreshRemoteStartupStatusAsync"));
                }
                Assert.Null(main.PendingCenterOperation(target));
                Assert.True(remote.IsNotRunning, "共享回执已确认清除，启动中心不应保留等待标记");
                for (var cycle = 0; cycle < 3; cycle++)
                {
                    Complete(Invoke(remote, "RunRemoteStartupAsync"));
                    completed = true;
                    Complete(Invoke(remote.TaskCenter, "RefreshRemoteAsync"));
                    Complete(Invoke(remote, "RefreshRemoteStartupStatusAsync"));
                    Assert.True(remote.IsNotRunning);
                    Assert.Null(main.PendingCenterOperation(target));
                }
                Assert.Equal(4, calls.Count);
                Assert.Equal(4, calls.Distinct().Count());
                done.SetResult();
            }
            catch (Exception ex) { done.SetException(ex); }
            finally { remote?.StopRemoteRefresh(); pipe?.Dispose(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }

    private static Task Invoke(object owner, string method) => (Task)owner.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(owner, null)!;

    private static void Complete(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(10);
        }
        Assert.True(task.IsCompleted, "隔离请求未在限定时间完成");
        task.GetAwaiter().GetResult();
    }
}
