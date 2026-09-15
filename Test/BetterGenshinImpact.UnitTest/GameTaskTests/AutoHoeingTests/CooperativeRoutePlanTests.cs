using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Rerun;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public class CooperativeRoutePlanTests
{
    /// <summary>
    /// PathingTaskInfo 的字段初始化会读取 TaskContext.Instance().Config（真实运行期配置服务），
    /// 单测环境没有该服务 → 构造 PathingTask 会抛“Config未初始化”。这里在类型初始化时注入
    /// 一个默认 AllConfig，使计划冻结逻辑可在纯单测中验证；不启动任何游戏/UI 组件。
    /// ConfigService.Config 的 setter 是 private，故沿用本仓库测试既有的反射注入模式。
    /// </summary>
    static CooperativeRoutePlanTests()
    {
        var field = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        field?.SetValue(null, new BetterGenshinImpact.Core.Config.AllConfig());
    }

    private static PathingTask Task(params Waypoint[] points) => new()
    {
        FileName = "route_a.json", FullPath = "original/route_a.json",
        Info = new() { MapMatchMethod = "SIFT", Description = "frozen", Tags = ["original"] },
        Positions = points.ToList()
    };

    private static Waypoint Point(string type = "path", string? action = null,
        string? sync = null, double x = 0) => new()
    { Type = type, Action = action, SyncPointId = sync, X = x };

    [Fact]
    public void CreateTaskReturnsIndependentFrozenCopiesIncludingIgnoredPaths()
    {
        var task = Task(Point(action: "fight", sync: "meet"));
        task.Positions[0].PointExtParams.AutoFight = new() { MoraValues = [200] };
        var plan = CooperativeRoutePlan.FromTask(new RouteInfo(), task, 4, 30);
        task.Info.Tags[0] = "changed";
        task.Positions.Clear();
        var first = plan.CreateTask();
        first.Info.Tags[0] = "mutated";
        first.Positions[0].PointExtParams.AutoFight!.MoraValues![0] = 999;
        first.FileName = "changed.json";
        var second = plan.CreateTask();
        Assert.Equal("original", second.Info.Tags[0]);
        Assert.Equal(200, second.Positions[0].PointExtParams.AutoFight!.MoraValues![0]);
        Assert.Equal("route_a.json", second.FileName);
        Assert.Equal("original/route_a.json", second.FullPath);
        Assert.False(plan.Manifest.Eligible);
        Assert.Equal("4:route", plan.Manifest.RouteId);
    }

    [Fact]
    public void ManualVariantsKeepSemanticIdsDespiteInsertedWaypoints()
    {
        var a = Task(Point("teleport"), Point(sync: "ready"), Point(action: "fight"));
        var b = Task(Point("teleport", x: 100), Point(x: 200),
            Point(sync: "ready", x: 300), Point(action: "fight", x: 400));
        b.FileName = "route_b.json";
        var pa = CooperativeRoutePlan.FromTask(new(), a, 2, 30);
        var pb = CooperativeRoutePlan.FromTask(new(), b, 2, 30);
        Assert.Equal(pa.Manifest.RouteId, pb.Manifest.RouteId);
        Assert.Equal(pa.Manifest.Checkpoints.Select(p => p.Id), pb.Manifest.Checkpoints.Select(p => p.Id));
        Assert.Equal(pa.SyncPointIds[1], pb.SyncPointIds[2]);
        Assert.Equal(pa.FightPointIds[2], pb.FightPointIds[3]);
        Assert.NotEqual(pa.Manifest.RouteId,
            CooperativeRoutePlan.FromTask(new(), a, 3, 30).Manifest.RouteId);
    }

    [Fact]
    public void LogicalIdAloneEnablesManualModeAndExplicitTeleportTakesPrecedence()
    {
        var task = Task(Point("teleport", sync: "arrival"), Point(x: 100), Point(action: "fight"));
        task.LogicalRouteId = "logical";
        var plan = CooperativeRoutePlan.FromTask(new(), task, 0, 0);
        Assert.Single(plan.SyncPointIds);
        Assert.Equal("s:0:sync:arrival", plan.SyncPointIds[0]);
        Assert.Equal("0:logical", plan.Manifest.RouteId);
        task.Positions[0].SyncPointId = null;
        var noMarks = CooperativeRoutePlan.FromTask(new(), task, 0, 0);
        Assert.Single(noMarks.SyncPointIds);
        Assert.Equal("s:0:tp", noMarks.SyncPointIds[0]);
    }

    [Fact]
    public void AutoSelectionMatchesResolverAndDeduplicatesTeleportFallback()
    {
        var task = Task(Point("teleport"), Point(x: 100), Point(action: "fight"),
            Point("teleport"), Point(action: "fight"), Point(action: "fight"));
        var segments = CooperativeRoutePlan.ConvertWaypointsForTrack(task.Positions, task);
        var expected = new HashSet<int>();
        for (var s = 0; s < segments.Count; s++)
        {
            foreach (var match in new SyncPointResolver().ResolveWithIndex(segments[s], 30))
                if (match.syncPoint != null && match.syncPointIdx >= 0)
                    expected.Add(s * 10000 + match.syncPointIdx);
            for (var w = 0; w < segments[s].Count; w++)
                if (segments[s][w].Type == "teleport") expected.Add(s * 10000 + w);
        }
        var plan = CooperativeRoutePlan.FromTask(new(), task, 0, 30);
        Assert.Equal(expected.Order(), plan.SyncPointIds.Keys.Order());
        // 键 10000（段1 wp0）既是传送点、又被解析器当作"无合法集合点时回退到最近传送点"的结果。
        // 与 legacy BuildSyncPointMapAuto 一致：解析器先写入 auto id，随后的传送点补写遇到同键即跳过，
        // 因此该键的规范 ID 是 auto 序列号而不是 tp。两端跑同一 builder，ID 只需确定且共享。
        Assert.Equal("s:1:auto:0", plan.SyncPointIds[10000]);
        Assert.Equal(3, plan.FightPointIds.Count);
        Assert.Equal(plan.Manifest.Checkpoints.Count,
            plan.Manifest.Checkpoints.Select(p => p.Id).Distinct().Count());
    }

    [Fact]
    public void ConverterPreservesSegmentationAndPointMetadata()
    {
        var task = Task(Point(), Point("teleport"), Point("teleport", action: "fight"));
        task.Positions[2].PointExtParams.MonsterTag = "elite";
        task.Positions[2].PointExtParams.EnableMonsterLootSplit = true;
        task.Positions[2].PointExtParams.AutoFight = new() { RewardType = "mora" };
        var segments = CooperativeRoutePlan.ConvertWaypointsForTrack(task.Positions, task);
        Assert.Equal(new[] { 1, 1, 1 }, segments.Select(s => s.Count));
        Assert.Equal("elite", segments[2][0].MonsterTag);
        Assert.True(segments[2][0].EnableMonsterLootSplit);
        Assert.Same(task.Positions[2].PointExtParams.AutoFight, segments[2][0].AutoFight);
        Assert.Single(CooperativeRoutePlan.ConvertWaypointsForTrack([], task));
        Assert.Empty(CooperativeRoutePlan.FromTask(new(), Task(), 0, 30).Manifest.Checkpoints);
    }

    [Fact]
    public void BuildFreezesMergedRuntimeTaskAndNeverReloadsDeletedSource()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "route_a.json");
            File.WriteAllText(path, JsonSerializer.Serialize(Task(Point("teleport")), PathRecorder.JsonOptions));
            File.WriteAllText(Path.Combine(directory, "control.json5"),
                """{"global_cover":{"info":{"description":"merged","enable_monster_loot_split":true}}}""");
            var plan = CooperativeRoutePlan.Build(new RouteInfo { FileName = "route_a.json", FullPath = path }, 7, 30);
            Directory.Delete(directory, true);
            var copy = plan.CreateTask();
            Assert.Equal("merged", copy.Info.Description);
            Assert.True(copy.Positions[0].PointExtParams.EnableMonsterLootSplit);
            Assert.Equal(path, copy.FullPath);
            Assert.Equal("route_a.json", copy.FileName);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
