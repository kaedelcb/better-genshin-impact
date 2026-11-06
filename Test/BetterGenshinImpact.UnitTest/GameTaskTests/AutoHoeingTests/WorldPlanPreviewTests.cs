using System.Reflection;
using System.Text.Json;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>Runs the production read-only preview against isolated route and account files.</summary>
public sealed class WorldPlanPreviewTests : IDisposable
{
    private const string Account = "world-plan-preview";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "BetterGI-worldplan-preview-" + Guid.NewGuid().ToString("N"));
    private readonly AutoHoeingTask _task;
    private readonly AutoHoeingConfig _config;

    static WorldPlanPreviewTests()
    {
        // PathingTaskInfo initializes some values from ConfigService, as in CooperativeRoutePlanTests.
        var configField = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (configField?.GetValue(null) == null) configField?.SetValue(null, new AllConfig());
    }

    public WorldPlanPreviewTests()
    {
        Directory.CreateDirectory(RouteDirectory);
        _config = new AutoHoeingConfig
        {
            AccountName = Account,
            OperationMode = "运行锄地路线",
            UseFixedDebugRoutes = true,
            FixedDebugRoutePath = RouteDirectory,
            GroupIndex = 1,
            PartyTimeoutSeconds = 30,
            MultiplayerEnabled = true,
            MultiWorldEnabled = true,
            MultiWorldResumeEnabled = true
        };
        _task = new AutoHoeingTask();
        SetField(_task, "_config", _config);
        SetField(_task, "_dataDir", _directory);
    }

    private string RouteDirectory => Path.Combine(_directory, "routes");
    private string CdPath => Path.Combine(_directory, "records", Account + ".json");

    [Fact]
    public void CompletedWorldHasNoPendingRoutes_AndPartialWorldKeepsOnlyUnfinishedRoutes()
    {
        AddRoutes("01-finished.json", "02-pending.json", "03-pending.json");
        WriteCd(("01-finished.json", DateTime.Now.AddDays(2)));
        Assert.Equal((true, 2), Preview()); // C interrupted after its first route.

        WriteCd(("01-finished.json", DateTime.Now.AddDays(2)),
            ("02-pending.json", DateTime.Now.AddDays(2)), ("03-pending.json", DateTime.Now.AddDays(2)));
        Assert.Equal((true, 0), Preview()); // A/B are skipped only on an explicit zero result.
    }

    [Fact]
    public void NewAccountWithNoCdFileHasWork_AndPreviewDoesNotCreateRecords()
    {
        AddRoutes("01.json", "02.json");
        Assert.Equal((true, 2), Preview());
        Assert.False(File.Exists(CdPath));
        Assert.False(Directory.Exists(Path.GetDirectoryName(CdPath)));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    [InlineData(4, 0)]
    public void ExplicitStartIndexBypassesCdAndKeepsTheConfiguredPrefix(int start, int expected)
    {
        AddRoutes("01.json", "02.json", "03.json");
        WriteCd(("01.json", DateTime.Now.AddDays(2)), ("02.json", DateTime.Now.AddDays(2)),
            ("03.json", DateTime.Now.AddDays(2)));
        _config.StartRouteIndex = start;
        Assert.Equal((true, expected), Preview());
    }

    [Fact]
    public void KeywordFilteringCombinesWithCdBeforeDecidingWhetherTheWorldIsEmpty()
    {
        AddRoutes("01-completed.json", "02-excluded.json", "03-excluded.json");
        WriteCd(("01-completed.json", DateTime.Now.AddDays(2)));
        _config.RouteFilterKeywords = "excluded";
        Assert.Equal((true, 0), Preview());
        _config.RouteFilterKeywords = "";
        Assert.Equal((true, 2), Preview());
    }

    [Fact]
    public void CdExpiringDuringPartyAssemblyDoesNotAuthorizeSkippingTheWorld()
    {
        AddRoutes("01.json");
        WriteCd(("01.json", DateTime.Now.AddSeconds(60)));
        Assert.Equal((true, 1), Preview());
    }

    [Fact]
    public void MissingRouteDirectoryIsUnknownInsteadOfCompleted()
    {
        _config.FixedDebugRoutePath = Path.Combine(_directory, "not-created");
        Assert.Equal((false, 0), Preview());
    }

    [Fact]
    public void EmptyPackageIsUnknownInsteadOfCompleted()
    {
        Assert.Equal((false, 0), Preview());
    }

    [Fact]
    public void NormalSelectionWithoutMonsterTableCannotAuthorizeSkippingTheWorld()
    {
        _config.UseFixedDebugRoutes = false;
        Directory.CreateDirectory(Path.Combine(_directory, "pathing"));
        Assert.False(File.Exists(Path.Combine(_directory, "assets", "monsterInfo.json")));
        Assert.Equal((false, 0), Preview());
    }

    [Fact]
    public void TargetModeWithLoadedMonsterTableButEmptyPathingDirectoryIsUnknown()
    {
        _config.UseFixedDebugRoutes = false;
        _config.OperationMode = "启用仅指定怪物模式";
        _config.TargetMonsters = "丘丘人";
        Directory.CreateDirectory(Path.Combine(_directory, "pathing"));
        var monsterFile = Path.Combine(_directory, "assets", "monsterInfo.json");
        Directory.CreateDirectory(Path.GetDirectoryName(monsterFile)!);
        File.WriteAllText(monsterFile, """[{"name":"丘丘人","type":"普通","moraRate":1.0}]""");
        var repository = (MonsterInfoRepository)typeof(AutoHoeingTask).GetField("_monsterRepo",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_task)!;
        repository.Load(monsterFile);
        Assert.Single(repository.GetAll());

        Assert.Equal((false, 0), Preview());
    }

    [Fact]
    public void JsonWithoutWaypointsIsUnknownEvenWithCompletedCd()
    {
        File.WriteAllText(Path.Combine(RouteDirectory, "01-empty-task.json"), "{}");
        WriteCd(("01-empty-task.json", DateTime.Now.AddDays(2)));
        Assert.Equal((false, 0), Preview());
    }

    [Fact]
    public async Task VariantPreviewUsesTheActualLocalVariantNameForCd()
    {
        Directory.CreateDirectory(Path.Combine(RouteDirectory, "A变体"));
        Directory.CreateDirectory(Path.Combine(RouteDirectory, "B变体"));
        AddRoutes(Path.Combine("A变体", "01_a.json"), Path.Combine("B变体", "01_b.json"));
        _config.VariantPreferences[Path.GetFileName(RouteDirectory)] = "B变体";
        await using var client = new CoordinatorClient();
        await using var coordinator = new MultiplayerCoordinator(client, new SyncPointResolver(), _config);
        SetField(_task, "_multiplayerCoordinator", coordinator);

        // A is the representative uploaded to peers, but this account actually executes B.
        WriteCd(("01_b.json", DateTime.Now.AddDays(2)));
        Assert.Equal((true, 0), Preview());
        WriteCd(("01_a.json", DateTime.Now.AddDays(2)));
        Assert.Equal((true, 1), Preview());
    }

    [Fact]
    public void MalformedRouteIsUnknownEvenWhenItsCdWouldHaveFilteredItOut()
    {
        AddRoutes("01-valid.json");
        File.WriteAllText(Path.Combine(RouteDirectory, "02-broken.json"), "{broken");
        WriteCd(("01-valid.json", DateTime.Now.AddDays(2)), ("02-broken.json", DateTime.Now.AddDays(2)));
        Assert.Equal((false, 0), Preview());
    }

    [Fact]
    public void MalformedCdIsUnknownInsteadOfAnEmptyCompletedWorld()
    {
        AddRoutes("01.json");
        Directory.CreateDirectory(Path.GetDirectoryName(CdPath)!);
        File.WriteAllText(CdPath, "[broken");
        Assert.Equal((false, 0), Preview());
        Assert.Equal("[broken", File.ReadAllText(CdPath));
    }

    [Fact]
    public void RepeatedPreviewDoesNotWriteCdOrRouteFiles()
    {
        AddRoutes("01-completed.json", "02-pending.json");
        WriteCd(("01-completed.json", DateTime.Now.AddDays(2)));
        var before = Directory.GetFiles(_directory, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, path => (Bytes: File.ReadAllBytes(path), Modified: File.GetLastWriteTimeUtc(path)));

        Assert.Equal((true, 1), Preview());
        Assert.Equal((true, 1), Preview());

        Assert.Equal(before.Keys.Order(), Directory.GetFiles(_directory, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, original) in before)
        {
            Assert.Equal(original.Bytes, File.ReadAllBytes(path));
            Assert.Equal(original.Modified, File.GetLastWriteTimeUtc(path));
        }
    }

    [Fact]
    public void SavedDisabledSwitchOverridesAnEnabledGlobalDefault()
    {
        var task = new AutoHoeingTask(settings: new Dictionary<string, object?>
        {
            ["multiplayerEnabled"] = true,
            ["multiWorldEnabled"] = true,
            ["multiWorldResumeEnabled"] = false
        });
        var config = new AutoHoeingConfig { MultiWorldResumeEnabled = true };
        SetField(task, "_config", config);
        Invoke(task, "ApplySettingsOverride");
        Assert.False(config.MultiWorldResumeEnabled);
        Assert.True(config.MultiWorldEnabled);
    }

    private (bool Known, int Count) Preview()
        => ((bool Known, int Count))Invoke(_task, "PreviewOwnWorld")!;

    private void AddRoutes(params string[] names)
    {
        const string validRoute = """
            {"info":{"name":"preview fixture","description":"预计用时60秒","bgi_version":""},
             "positions":[{"type":"teleport","x":0,"y":0},{"type":"path","x":1,"y":1}]}
            """;
        foreach (var name in names) File.WriteAllText(Path.Combine(RouteDirectory, name), validRoute);
    }

    private void WriteCd(params (string Name, DateTime Until)[] records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CdPath)!);
        File.WriteAllText(CdPath, JsonSerializer.Serialize(records.Select(record => new
        {
            fileName = record.Name,
            cdTime = record.Until.ToString("yyyy/M/d HH:mm:ss"),
            records = new[] { 60d }
        })));
    }

    private static void SetField(object instance, string name, object value)
        => typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);

    private static object? Invoke(object instance, string name)
        => typeof(AutoHoeingTask).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);

    public void Dispose()
    {
        // This class only creates this unique directory; never touch the real User records.
        var target = Path.GetFullPath(_directory);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath());
        if (!target.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("BetterGI-worldplan-preview-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test output path");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
    }
}
