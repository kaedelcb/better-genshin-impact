using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RoomOrchestrationMonitorProcessTests
{
    [Fact]
    public async Task IsolatedMonitorReadsExecutorProfilesAndSavesOnlyTheChosenInactiveRoom()
    {
        Assert.True(AssistantDataDirectory.IsExplicit, "监控端验证必须使用隔离数据目录");
        var root = Path.Combine(AssistantDataDirectory.Root, "orchestration-executor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var uid = "123456789";
        var secret = Guid.NewGuid().ToString("N");
        const string server = "https://orchestration-fixture.example.test";
        var a = new AssistantRoomProfile { Id = "executor-a", Name = "执行甲", ServerUrl = server, Password = secret,
            TeamUids = [uid, "234567890"], ExpectedHoeingPlayers = 2, OnlineHoeingGroupNames = ["甲任务"], OnlineHoeingGroupTypes = ["group"] };
        a.RoomCode = AssistConfigManager.GenerateControlRoomCode(a.TeamUids);
        var b = new AssistantRoomProfile { Id = "executor-b", Name = "执行乙", ServerUrl = server, Password = secret,
            TeamUids = [uid, "345678901"], ExpectedHoeingPlayers = 4, OnlineHoeingGroupNames = ["乙任务"], OnlineHoeingGroupTypes = ["group"] };
        b.RoomCode = AssistConfigManager.GenerateControlRoomCode(b.TeamUids);
        var bgi = Path.Combine(root, "BetterGI.exe");
        File.WriteAllBytes(bgi, []);
        File.WriteAllText(Path.Combine(root, "mistletoe-package.json"), "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
        var config = new AssistConfig { PlayerUid = uid, PlayerName = "隔离执行端", ClientInstanceId = "fixture-executor-" + Guid.NewGuid().ToString("N"),
            BgiPath = bgi, StandaloneMode = true, DisclaimerAccepted = true, RoomProfiles = [a, b], ActiveRoomProfileId = a.Id, LocalRoomProfileId = a.Id };
        a.ApplyTo(config);
        var configPath = Path.Combine(root, "assistant-config.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(config));
        var exe = Environment.GetEnvironmentVariable("NEXUSBGI_TEST_ASSISTANT_EXE");
        Assert.True(!string.IsNullOrEmpty(exe) && File.Exists(exe), "使用本次隔离构建产物启动执行端");
        var start = new ProcessStartInfo(exe!, "--no-auto-launch") { UseShellExecute = false, CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(exe!)! };
        start.Environment["NEXUSBGI_DATA_ROOT"] = root;
        var temp = Directory.CreateDirectory(Path.Combine(root, "temp")).FullName;
        start.Environment["TMP"] = temp; start.Environment["TEMP"] = temp;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("隔离执行端未启动");
        var monitor = new MainViewModel { RoomCode = a.RoomCode };
        var monitorProfile = new AssistantRoomProfile { Id = "monitor-only", Name = "监控端本地镜像", RoomCode = a.RoomCode,
            ServerUrl = server, Password = secret, TeamUids = [.. a.TeamUids], OnlineHoeingGroupNames = ["本地镜像不应展示"] };
        var monitorConfig = new AssistConfig { PlayerUid = uid, PlayerName = "隔离监控端", ObserverMode = true, StandaloneMode = true,
            RoomProfiles = [monitorProfile], ActiveRoomProfileId = monitorProfile.Id, ShowRoomOrchestration = true };
        monitorProfile.ApplyTo(monitorConfig);
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(monitor, monitorConfig);
        var before = JsonSerializer.Serialize(monitorConfig);
        try
        {
            RemoteCenterTarget? target = null;
            for (var attempt = 0; attempt < 15 && !child.HasExited && target is null; attempt++)
            {
                target = (await monitor.FindLocalCenterExecutorsAsync()).SingleOrDefault(t => t.ClientInstanceId == config.ClientInstanceId);
                if (target is null) await Task.Delay(300);
            }
            Assert.NotNull(target);
            Assert.True(monitor.CanSelectRoomOrchestration);
            Assert.Empty(monitor.GetRoomOrchestrationPresentation().Cards); // never use monitor mirror
            await monitor.RefreshRoomOrchestrationMonitorAsync();
            var graph = monitor.GetRoomOrchestrationPresentation();
            Assert.Equal(new[] { a.Id, b.Id }, graph.Cards.Select(c => c.Id));
            Assert.Contains(graph.Cards, c => c.Id == b.Id && c.CanEdit);
            Assert.DoesNotContain(graph.Cards, c => c.Name == monitorProfile.Name);
            var read = await monitor.RequestRemoteCenterAsync(target!, "room.orchestration.edit", b.Id);
            Assert.Equal("ok", read.Status);
            Assert.DoesNotContain(bgi, read.PayloadJson!);
            Assert.DoesNotContain(secret, read.PayloadJson!);
            var edit = JsonSerializer.Deserialize<RemoteOrchestrationEdit>(read.PayloadJson!)!;
            Assert.Equal(b.Id, edit.Snapshot.Key.ProfileId);
            Assert.Equal("乙任务", Assert.Single(edit.Snapshot.Names));
            Assert.Equal(a.Id, Assert.Single(edit.Choices).TargetProfileId);
            Assert.Equal("", edit.Snapshot.BgiPath);
            var choice = Assert.Single(edit.Choices);
            var rejectedTime = await monitor.RequestRemoteCenterAsync(target!, "room.orchestration.save",
                new RemoteOrchestrationSaveRequest(b.Id, new(Schedule: "invalid"), null), edit.Revision);
            Assert.Equal("rejected", rejectedTime.Status);
            Assert.Contains("HH:mm", rejectedTime.Message);
            Assert.Equal("rejected", (await monitor.RequestRemoteCenterAsync(target!, "operation.get", rejectedTime.OperationId)).Status);
            var change = new RemoteOrchestrationSaveRequest(b.Id,
                new RoomOrchestrationEditChanges(ExpectedPlayers: 3, Schedule: "19:30",
                    Completion: new TaskConflictPolicySettings { Policy = TaskConflictPolicy.Stop, RoomSwitch = choice }), null);
            var saved = await monitor.RequestRemoteCenterAsync(target!, "room.orchestration.save", change, edit.Revision,
                "room-orchestration-" + Guid.NewGuid().ToString("N"), expectedMachineId: target!.MachineId);
            Assert.True(saved.Status == "ok", saved.Status + ": " + saved.Message);
            var receipt = await monitor.RequestRemoteCenterAsync(target!, "operation.get", saved.OperationId);
            Assert.Equal("ok", receipt.Status);
            var persisted = JsonSerializer.Deserialize<AssistConfig>(File.ReadAllText(configPath))!;
            Assert.Equal(a.Id, persisted.ActiveRoomProfileId);
            Assert.Equal(2, persisted.ExpectedHoeingPlayers);
            Assert.Equal("甲任务", Assert.Single(persisted.OnlineHoeingGroupNames));
            var changed = persisted.RoomProfiles.Single(p => p.Id == b.Id);
            Assert.Equal(3, changed.ExpectedHoeingPlayers);
            Assert.Equal("19:30", changed.ScheduledOnlineTime);
            Assert.Equal(a.Id, changed.OnlineHoeingRoomSwitchCompletion?.TargetProfileId);
            Assert.Equal("乙任务", Assert.Single(changed.OnlineHoeingGroupNames));
            Assert.Equal(before, JsonSerializer.Serialize(monitorConfig));
            var stale = await monitor.RequestRemoteCenterAsync(target!, "room.orchestration.save",
                change with { Changes = new(ExpectedPlayers: 1) }, edit.Revision);
            Assert.Equal("rejected", stale.Status);
            Assert.Equal(3, JsonSerializer.Deserialize<AssistConfig>(File.ReadAllText(configPath))!.RoomProfiles.Single(p => p.Id == b.Id).ExpectedHoeingPlayers);
            var ghost = await monitor.RequestRemoteCenterAsync(target!, "room.orchestration.edit", monitorProfile.Id);
            Assert.Equal("rejected", ghost.Status);
            // Exercise the very backend used by the WPF editor, not only individual protocol calls.
            var (remoteEdit, backend) = await monitor.ReadMonitorRoomOrchestrationEditorAsync(b.Id);
            Assert.Equal(3, remoteEdit.Snapshot.ExpectedPlayers);
            await backend.Save(new RoomOrchestrationEditChanges(ExpectedPlayers: 4), null, null, CancellationToken.None);
            Assert.Equal(4, JsonSerializer.Deserialize<AssistConfig>(File.ReadAllText(configPath))!.RoomProfiles.Single(p => p.Id == b.Id).ExpectedHoeingPlayers);
            Assert.Equal(before, JsonSerializer.Serialize(monitorConfig));
            monitor.StopRoomOrchestrationMonitor();
            var disconnected = monitor.GetRoomOrchestrationPresentation();
            Assert.All(disconnected.Cards, c => { Assert.False(c.Current); Assert.False(c.CanEdit); Assert.Equal("unknown", c.Status.Kind);
                Assert.Equal("进度待核对", c.ProgressText); Assert.False(c.HasConfirmedProgress); });
            Assert.Equal("unknown", disconnected.CurrentStatus.Kind);
        }
        finally
        {
            monitor.StopRoomOrchestrationMonitor();
            if (!child.HasExited) { child.Kill(); child.WaitForExit(10000); } // only this test-owned process
        }
    }
}
