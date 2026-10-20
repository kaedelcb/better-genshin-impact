using System.Reflection;
using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed partial class FourMemberFinishEntryTests
{
    private static async Task RunChangedRoomCancellationEntryAsync(bool replyLost)
    {
        string[] uids = ["100000041", "100000042", "100000043", "100000044"];
        await using var server = await IsolatedCoordinator.StartAsync("changed-room-" + replyLost);
        var room = "ORIGINAL-" + Guid.NewGuid().ToString("N")[..8];
        var clients = Enumerable.Range(0, 4).Select(_ => new SignalRClient()).ToArray();
        SignalRClient? originalControl = null;
        MainViewModel? vm = null;
        CoordinatedBatchSnapshot? originalBatch = null;
        try
        {
            for (var i = 0; i < clients.Length; i++)
            {
                await clients[i].ConnectAsync(server.Url, room, "isolated-cancel", uids[i], "隔离成员" + i,
                    uids.ToList(), clientInstanceId: Guid.NewGuid().ToString("N"), bypassSystemProxy: true);
                await clients[i].ReportControlStatusAsync(new ControlStatus
                {
                    PlayerUid = uids[i], PlayerName = "隔离成员" + i, BgiStatus = "running",
                    ConfigGroups = ["隔离绑定"], OnlineHoeingGroupNames = ["隔离绑定"],
                    OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 4,
                    HoeingCompletionAction = new("stop", "group", "", 0),
                });
            }
            vm = new MainViewModel();
            Set(vm, "_config", new AssistConfig
            {
                ServerUrl = server.Url, PlayerUid = uids[0], PlayerName = "隔离遥控发起者",
                TeamUids = uids.ToList(), ControlRoomPassword = "isolated-cancel", ObserverMode = true,
                ClientInstanceId = Guid.NewGuid().ToString("N"), DisclaimerAccepted = true, BypassSystemProxy = true,
            });
            vm.RoomCode = room;
            originalControl = await ConnectProductionClientAsync(vm);
            vm.BeforeCoordinatedBeginReplyForTest = async accepted =>
            {
                originalBatch = accepted;
                vm.RoomCode = room + "-new";
                originalControl.RetireForReplacement();
                await originalControl.DisposeAsync(); // Force production fallback to original-room remote cleanup.
                if (replyLost) throw new IOException("old room accepted but reply was lost after switch");
            };
            var begin = typeof(MainViewModel).GetMethod("BeginCoordinatedWithContinuationAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
            var request = Guid.NewGuid().ToString("N");
            var work = (Task<CoordinatedBatchSnapshot>)begin.Invoke(vm, [request, uids, true, ""])!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.NotNull(originalBatch);
            var cancelled = (await clients[1].GetCurrentCoordinatedBatchAsync())!;
            Assert.Equal(originalBatch!.BatchId, cancelled.BatchId);
            Assert.Equal("aborted", cancelled.Phase);
            Assert.Equal("manual_reset", cancelled.Reason);
            Assert.True(cancelled.CleanupComplete); // None of the four members had registered for execution.
            Assert.Null(Active(vm));
            // Temporary remote cleanup did not evict any real member or change its instance identity.
            var next = await clients[1].BeginCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids,
                DateTimeOffset.UtcNow.AddMinutes(1));
            Assert.NotEqual(cancelled.BatchId, next.BatchId);
            Assert.Equal(uids, next.ParticipantUids);
            await clients[1].ResetCoordinatedBatchAsync(Guid.NewGuid().ToString("N"), uids, next.Generation, next.BatchId);
            await using var newRoom = new SignalRClient();
            await newRoom.ConnectAsync(server.Url, room + "-new", "isolated-cancel", uids[0], "新房间观察",
                uids.ToList(), isRemote: true, clientInstanceId: Guid.NewGuid().ToString("N"), bypassSystemProxy: true);
            Assert.Null(await newRoom.GetCurrentCoordinatedBatchAsync());
        }
        finally
        {
            if (vm is not null)
            {
                vm.BeforeCoordinatedBeginReplyForTest = null;
                Set(vm, "_disposing", true);
                StopFixtureTimers(vm);
                var host = (TaskCenterHost?)typeof(MainViewModel)
                    .GetField("_taskCenterHost", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
                if (host is not null) await host.ShutdownAsync();
            }
            if (originalControl is not null) await originalControl.DisposeAsync();
            foreach (var client in clients) await client.DisposeAsync();
        }
    }
}
