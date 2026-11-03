using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.Model;
using BetterGenshinImpact.Service;
using Microsoft.ClearScript.V8;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoPathingTests;

[Collection("GlobalUserRoot")]
public sealed class PickupWaypointRouteEntryTests
{
    private const string RouteFile = "pickup-entry.json";
    private const string RouteJson = """
        {
          "info": { "name": "pickup-entry" },
          "positions": [
            { "id": 1, "x": 100, "y": 200, "type": "target", "action": "combat_script",
              "action_params": "万叶 attack", "pickup_target_count": "2/1" },
            { "id": 2, "x": 104, "y": 200, "type": "path" }
          ]
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectGroupEntryPreparesFileWithTheGroupsPickupSetting(bool enabled)
    {
        using var fixture = new RouteFixture();
        var project = BuildProject("Pathing", enabled);

        var prepared = project.PrepareDirectPathing(fixture.Root);
        Assert.True(prepared.HasValue);
        var (task, executor) = prepared.GetValueOrDefault();

        Assert.Same(project.GroupInfo!.Config.PathingConfig, executor.PartyConfig);
        Assert.Equal(enabled, executor.PartyConfig.EnablePickupTargetCount);
        Assert.False(executor.PartyConfig.SkipPartySwitch);
        Assert.Equal(RouteFile, task.FileName);
        AssertRoute(task);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JavaScriptRunFilePassesTheGroupsPickupSettingAndParsedRouteToTheExecutor(bool enabled)
    {
        using var fixture = new RouteFixture();
        var project = BuildProject("Javascript", enabled);
        var calls = new List<(PathExecutor Executor, PathingTask Task, string? ActiveRoute)>();
        var pathing = new AutoPathingScript(fixture.Root, project.GetJavaScriptPathingConfig(), (executor, task) =>
        {
            calls.Add((executor, task, ScriptRouteProgress.CurrentRouteName));
            return Task.CompletedTask;
        });
        // AAA 也通过这个宿主对象调用 runFile。只载入最小脚本，避免初始化需要游戏窗口的其它宿主。
        using var engine = new V8ScriptEngine(
            V8ScriptEngineFlags.UseCaseInsensitiveMemberBinding | V8ScriptEngineFlags.EnableTaskPromiseConversion);
        engine.AddHostObject("pathingScript", pathing);

        var running = engine.Evaluate("(async () => { await pathingScript.runFile('pickup-entry.json'); })()");
        await Assert.IsAssignableFrom<Task>(running).WaitAsync(TimeSpan.FromSeconds(10));

        var (executor, task, activeRoute) = Assert.Single(calls);
        Assert.Equal(RouteFile, activeRoute);
        Assert.Equal(enabled, executor.PartyConfig.EnablePickupTargetCount);
        Assert.True(executor.PartyConfig.SkipPartySwitch);
        Assert.NotSame(project.GroupInfo!.Config.PathingConfig, executor.PartyConfig);
        Assert.False(project.GroupInfo.Config.PathingConfig.SkipPartySwitch);
        AssertRoute(task);
    }

    private static ScriptGroupProject BuildProject(string type, bool enabled)
    {
        var group = new ScriptGroup
        {
            Config = new ScriptGroupConfig
            {
                PathingConfig = new PathingPartyConfig { EnablePickupTargetCount = enabled },
            },
            Projects = [new ScriptGroupProject { Type = type, Name = RouteFile }],
        };
        return Assert.Single(ScriptGroup.FromJson(group.ToJson()).Projects);
    }

    private static void AssertRoute(PathingTask task)
    {
        Assert.Equal(2, task.Positions.Count);
        Assert.Equal("combat_script", task.Positions[0].Action);
        Assert.Equal(new PickupTarget(2, 1), task.Positions[0].PickupTargetCount);
        Assert.Null(task.Positions[1].PickupTargetCount);
    }

    private sealed class RouteFixture : IDisposable
    {
        // GetCountryName 只读取 Config。替换当前测试进程内的值，不启动 App/配置服务或落盘。
        private static readonly PropertyInfo ConfigProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;
        private static readonly FieldInfo CancellationInstance = typeof(Singleton<CancellationContext>)
            .GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)!;
        private readonly CancellationContext _fixtureCancellation = new();
        private readonly object? _originalCancellation;
        private readonly object? _originalConfig;
        private readonly string _originalStartupPath;

        internal RouteFixture()
        {
            Root = Directory.CreateTempSubdirectory("pickup-entry-").FullName;
            File.WriteAllText(Path.Combine(Root, RouteFile), RouteJson);
            _originalStartupPath = Global.StartUpPath;
            _originalConfig = ConfigProperty.GetValue(null);
            _originalCancellation = CancellationInstance.GetValue(null);
            Global.StartUpPath = Root;
            ConfigProperty.SetValue(null, new AllConfig());
            CancellationInstance.SetValue(null, _fixtureCancellation);
        }

        internal string Root { get; }

        public void Dispose()
        {
            ScriptRouteProgress.Clear();
            CancellationInstance.SetValue(null, _originalCancellation);
            _fixtureCancellation.Clear();
            ConfigProperty.SetValue(null, _originalConfig);
            Global.StartUpPath = _originalStartupPath;
            File.Delete(Path.Combine(Root, RouteFile));
            Directory.Delete(Root);
        }
    }
}
